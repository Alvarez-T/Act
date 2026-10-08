using System.Security.Cryptography;

namespace YFex.Persistence.Encryption;

/// <summary>What reading one value of an <see cref="EncryptedKeyValueStore"/> found.</summary>
public enum StoredValueStatus : byte
{
    /// <summary>No value under that key.</summary>
    Missing = 0,

    /// <summary>Authenticated and decrypted with the ring's current key.</summary>
    Opened = 1,

    /// <summary>Authenticated and decrypted, but sealed with an older key or an older layout (0x01/0x02): re-sealed on the next write.</summary>
    OpenedNeedsReseal = 2,

    /// <summary>Its key was destroyed (crypto-shredding): the value is gone for good and reads as missing.</summary>
    Erased = 3,

    /// <summary>Wrong key, value moved to another key or store, or tampered bytes.</summary>
    AuthenticationFailed = 4,

    /// <summary>Sealed with a key this installation does not have (another machine's store, a lost key ring).</summary>
    KeyUnknown = 5,

    /// <summary>Not a sealed value this version reads (written unencrypted, or by a newer version).</summary>
    UnsupportedFormat = 6,

    /// <summary>Too short or garbled to be a sealed value.</summary>
    Malformed = 7,

    /// <summary>Sealed with an algorithm this platform cannot run.</summary>
    AlgorithmUnavailable = 8,
}

/// <summary>Result of <see cref="EncryptedKeyValueStore.TryGetAsync"/>: the status and, when opened, the plaintext.</summary>
public readonly record struct StoredValueResult(StoredValueStatus Status, byte[]? Value)
{
    /// <summary>True for <see cref="StoredValueStatus.Opened"/> and <see cref="StoredValueStatus.OpenedNeedsReseal"/>.</summary>
    public bool IsOpened => Status is StoredValueStatus.Opened or StoredValueStatus.OpenedNeedsReseal;
}

/// <summary>A value of an <see cref="EncryptedKeyValueStore"/> that could not be opened.</summary>
public readonly record struct StorageReadFailure(string StoreName, string Key, StoredValueStatus Status);

/// <summary>
/// A stored value exists but cannot be opened: tampered, moved, unencrypted, or sealed with a key this installation does not have.
/// Raised by <see cref="EncryptedKeyValueStore.GetAsync"/> instead of returning <see langword="null"/>, so a lost storage key or a
/// tampered outbox is never mistaken for "nothing there" (cryptography C10, ⚑K15). <see cref="EncryptedKeyValueStore.TryGetAsync"/>
/// reports the same as a value. The message never contains the key (keys may carry identifiers); <see cref="Key"/> does.
/// </summary>
public sealed class StorageIntegrityException(string storeName, string key, StoredValueStatus status)
    : CryptographicException($"A value in encrypted store '{storeName}' cannot be opened ({status}). The storage key may have changed, " +
        "the value may have been moved or tampered with, or it was written unencrypted. See docs/internal/cryptography/09 §2.")
{
    public string StoreName { get; } = storeName;

    public string Key { get; } = key;

    public StoredValueStatus Status { get; } = status;
}
