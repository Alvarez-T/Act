using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using YFex.Cryptography;
using YFex.Persistence;

namespace YFex.Persistence.Encryption;

/// <summary>
/// <see cref="IKeyValueStore"/> decorator that seals every value with YFex.Cryptography (sealed value v3, AES-256-GCM). Keys stay
/// plain text to preserve prefix enumeration.
/// </summary>
/// <remarks>
/// <para>Every value is bound to where it lives: sealing context <c>"yfex.store.v1" [store name, key]</c> (length-prefixed), so a
/// value copied under another key or into another store fails authentication instead of decrypting (offline F2).</para>
/// <para>The storage key is the current key of a <see cref="KeyRing"/> (default ring <c>"device/storage"</c>): random, generated once,
/// kept wrapped by the root key (CNG/TPM on Windows), rotated yearly. Old keys keep opening their values (offline F1,
/// cryptography 06).</para>
/// <para>Earlier layouts are still read and re-sealed on their next write: <c>0x01</c> (no associated data) and <c>0x02</c> (offline
/// Phase 0, associated data <c>"yfex.store|" + store + "|" + key</c>), both with the storage key imported into the ring under
/// <see cref="LegacyStorageKeyId"/>.</para>
/// <para><see cref="GetAsync"/> returns <see langword="null"/> only for a missing or erased value; a value that cannot be opened
/// throws <see cref="StorageIntegrityException"/> (cryptography C10, ⚑K15). <see cref="TryGetAsync"/> returns the same as a value.
/// Failures also raise <see cref="ReadFailed"/>, are counted, and the first of each kind is logged as <c>StorageKeyMismatch</c>.</para>
/// </remarks>
public sealed class EncryptedKeyValueStore : IKeyValueStore
{
    /// <summary>Store name used when none is configured.</summary>
    public const string DefaultStoreName = "default";

    /// <summary>Default key ring of the storage key.</summary>
    public const string DefaultKeyRing = "device/storage";

    /// <summary>Sealing domain of stored values (cryptography 05 §9).</summary>
    public static readonly MessageDomain StoreDomain = new("yfex.store.v1");

    /// <summary>
    /// Id under which a storage key that predates key rings (the offline Phase 0 wrapped file, or a key supplied by the app) lives
    /// in the ring: the <c>0x01</c>/<c>0x02</c> layouts carry no key id, so they are opened with this key.
    /// </summary>
    public static readonly KeyId LegacyStorageKeyId = new(new Guid("0192a0c0-0000-7000-8000-73746f726167"));

    private const int StackLimit = 256;

    private readonly IKeyValueStore _inner;
    private readonly KeyRingProvider? _rings;
    private readonly KeyRingName _ringName;
    private readonly KeyRing? _fixedRing;
    private readonly ILogger? _logger;
    private readonly byte[] _legacyAssociatedDataPrefix;
    private int _authenticationFailureLogged;
    private int _otherFailureLogged;
    private long _unreadableValues;
    private long _legacyValuesRead;

    /// <summary>Seals with the current key of ring <paramref name="ring"/> from <paramref name="rings"/> (reloaded when another installation rotated).</summary>
    public EncryptedKeyValueStore(IKeyValueStore inner, KeyRingProvider rings, KeyRingName ring, string storeName = DefaultStoreName,
        ILogger? logger = null)
        : this(inner, storeName, logger)
    {
        _rings = rings ?? throw new ArgumentNullException(nameof(rings));
        _ringName = ring;
    }

    /// <summary>Seals with a fixed ring (tests, a key supplied by the app).</summary>
    public EncryptedKeyValueStore(IKeyValueStore inner, KeyRing ring, string storeName = DefaultStoreName, ILogger? logger = null)
        : this(inner, storeName, logger)
    {
        _fixedRing = ring ?? throw new ArgumentNullException(nameof(ring));
        _ringName = ring.Name;
    }

    private EncryptedKeyValueStore(IKeyValueStore inner, string storeName, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        _inner = inner;
        _logger = logger;
        StoreName = storeName;
        _legacyAssociatedDataPrefix = Encoding.UTF8.GetBytes("yfex.store|" + storeName + "|");
    }

    /// <summary>Name bound into every sealed value of this store.</summary>
    public string StoreName { get; }

    /// <summary>Values that could not be opened since this instance was created.</summary>
    public long UnreadableValues => Interlocked.Read(ref _unreadableValues);

    /// <summary>Values in a layout before sealed value v3 (0x01/0x02) read since this instance was created; each is re-sealed on its next write.</summary>
    public long LegacyValuesRead => Interlocked.Read(ref _legacyValuesRead);

    /// <summary>Raised for every value that could not be opened.</summary>
    public event Action<StorageReadFailure>? ReadFailed;

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        StoredValueResult result = await TryGetAsync(key, ct).ConfigureAwait(false);
        return result.Status switch
        {
            StoredValueStatus.Missing or StoredValueStatus.Erased => null,
            StoredValueStatus.Opened or StoredValueStatus.OpenedNeedsReseal => result.Value,
            _ => throw new StorageIntegrityException(StoreName, key, result.Status),
        };
    }

    /// <summary>Reads a value and reports what was found, without throwing for values that cannot be opened.</summary>
    public async ValueTask<StoredValueResult> TryGetAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[]? sealedValue = await _inner.GetAsync(key, ct).ConfigureAwait(false);
        if (sealedValue is null) return new StoredValueResult(StoredValueStatus.Missing, null);

        KeyRing ring = await RingAsync(ct).ConfigureAwait(false);
        StoredValueResult result = Open(ring, key, sealedValue);
        if (result.Status == StoredValueStatus.KeyUnknown && _rings is not null)
        {
            // Another installation sharing the ring store may have rotated: re-read the ring once (rate-limited) and retry.
            ring = await _rings.ReloadAsync(_ringName, ct).ConfigureAwait(false);
            result = Open(ring, key, sealedValue);
        }

        if (!result.IsOpened && result.Status != StoredValueStatus.Erased) ReportUnreadable(key, result.Status);
        return result;
    }

    public async ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        KeyRing ring = await RingAsync(ct).ConfigureAwait(false);
        await _inner.SetAsync(key, Seal(ring, key, value), ttl, ct).ConfigureAwait(false);
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default) => _inner.DeleteAsync(key, ct);

    /// <summary>True when a value exists and opens (a value that cannot be opened does not "exist" for readers).</summary>
    public async ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default)
        => (await TryGetAsync(key, ct).ConfigureAwait(false)).IsOpened;

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
        => _inner.GetKeysWithPrefixAsync(prefix, ct);

    public ValueTask ClearAsync(CancellationToken ct = default) => _inner.ClearAsync(ct);

    // ── internals ─────────────────────────────────────────────────────────────────

    private ValueTask<KeyRing> RingAsync(CancellationToken ct)
    {
        if (_fixedRing is not null) return ValueTask.FromResult(_fixedRing);
        return _rings!.TryGetCached(_ringName, out KeyRing? ring) ? ValueTask.FromResult(ring) : _rings.GetAsync(_ringName, ct);
    }

    private byte[] Seal(KeyRing ring, string key, byte[] value)
    {
        int length = SealContext.GetLength(StoreDomain, StoreName, key);
        byte[]? rented = null;
        Span<byte> buffer = length <= StackLimit ? stackalloc byte[StackLimit] : (rented = ArrayPool<byte>.Shared.Rent(length));
        try
        {
            return SealedValue.Seal(ring, value, SealContext.Write(buffer, StoreDomain, StoreName, key));
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private StoredValueResult Open(KeyRing ring, string key, byte[] sealedValue)
    {
        if (sealedValue.Length == 0) return new StoredValueResult(StoredValueStatus.Malformed, null);
        return sealedValue[0] switch
        {
            SealedValue.FormatVersion => OpenCurrent(ring, key, sealedValue),
            LegacyFormats.PersistenceVersion1 or LegacyFormats.PersistenceVersion2 => OpenLegacy(ring, key, sealedValue),
            _ => new StoredValueResult(StoredValueStatus.UnsupportedFormat, null),
        };
    }

    private StoredValueResult OpenCurrent(KeyRing ring, string key, byte[] sealedValue)
    {
        var plaintext = new byte[SealedValue.GetPlaintextLength(sealedValue.Length)];
        int length = SealContext.GetLength(StoreDomain, StoreName, key);
        byte[]? rented = null;
        Span<byte> buffer = length <= StackLimit ? stackalloc byte[StackLimit] : (rented = ArrayPool<byte>.Shared.Rent(length));
        try
        {
            OpenResult result = SealedValue.Open(ring, sealedValue, SealContext.Write(buffer, StoreDomain, StoreName, key), plaintext);
            return Map(result, plaintext);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private StoredValueResult OpenLegacy(KeyRing ring, string key, byte[] sealedValue)
    {
        if (!ring.TryGet(LegacyStorageKeyId, out SecretKey? legacyKey))
            return new StoredValueResult(ring.StatusOf(LegacyStorageKeyId) == KeyStatus.Destroyed ? StoredValueStatus.Erased : StoredValueStatus.KeyUnknown, null);

        var plaintext = new byte[LegacyFormats.GetPersistencePlaintextLength(sealedValue.Length)];
        OpenResult result;
        if (sealedValue[0] == LegacyFormats.PersistenceVersion1)
        {
            result = LegacyFormats.OpenPersistenceVersion1(legacyKey, sealedValue, plaintext);
        }
        else
        {
            int length = _legacyAssociatedDataPrefix.Length + Encoding.UTF8.GetByteCount(key);
            byte[]? rented = null;
            Span<byte> associatedData = length <= StackLimit ? stackalloc byte[StackLimit] : (rented = ArrayPool<byte>.Shared.Rent(length));
            try
            {
                associatedData = associatedData[..length];
                _legacyAssociatedDataPrefix.CopyTo(associatedData);
                Encoding.UTF8.GetBytes(key, associatedData[_legacyAssociatedDataPrefix.Length..]);
                result = LegacyFormats.OpenPersistenceVersion2(legacyKey, sealedValue, associatedData, plaintext);
            }
            finally
            {
                if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
            }
        }
        if (result.IsOpened) Interlocked.Increment(ref _legacyValuesRead);
        return Map(result, plaintext);
    }

    private static StoredValueResult Map(OpenResult result, byte[] plaintext)
    {
        if (result.TryGetValue(out Opened opened))
        {
            byte[] value = opened.Written == plaintext.Length ? plaintext : plaintext[..opened.Written];
            return new StoredValueResult(opened.NeedsReseal ? StoredValueStatus.OpenedNeedsReseal : StoredValueStatus.Opened, value);
        }
        StoredValueStatus status = result.Outcome switch
        {
            OpenOutcome.KeyDestroyed => StoredValueStatus.Erased,
            OpenOutcome.KeyUnknown => StoredValueStatus.KeyUnknown,
            OpenOutcome.AuthenticationFailed => StoredValueStatus.AuthenticationFailed,
            OpenOutcome.UnsupportedFormat => StoredValueStatus.UnsupportedFormat,
            OpenOutcome.AlgorithmUnavailable => StoredValueStatus.AlgorithmUnavailable,
            _ => StoredValueStatus.Malformed,
        };
        return new StoredValueResult(status, null);
    }

    private void ReportUnreadable(string key, StoredValueStatus status)
    {
        Interlocked.Increment(ref _unreadableValues);
        ReadFailed?.Invoke(new StorageReadFailure(StoreName, key, status));

        if (_logger is null) return;
        // Keys may carry identifiers: log the store and the kind once, never the key itself.
        ref int logged = ref status == StoredValueStatus.AuthenticationFailed ? ref _authenticationFailureLogged : ref _otherFailureLogged;
        if (Interlocked.Exchange(ref logged, 1) != 0) return;
        _logger.LogWarning(
            "StorageKeyMismatch: a value in encrypted store '{StoreName}' could not be opened ({Status}). The storage key may have " +
            "changed, the value may have been moved or tampered with, or it was written unencrypted. Reading it throws " +
            "StorageIntegrityException. Further occurrences of this kind are counted (UnreadableValues) but not logged.",
            StoreName, status);
    }
}
