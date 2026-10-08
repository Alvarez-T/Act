using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace YFex.Rules;

/// <summary>Who signed an <see cref="OverrideToken"/>.</summary>
public enum OverrideTokenKind : byte
{
    /// <summary>Issued online by the server (HMAC with a server secret) — D34.</summary>
    Server = 0,
    /// <summary>Issued offline by a POS device (ECDSA device key) — capped and always flagged on replay (D34).</summary>
    Device = 1,
}

/// <summary>
/// A manager approval bound to ONE decision outcome (09 §6): approver, permission, the decision
/// <see cref="Fingerprint"/>, validity window, and a signature. Changing anything the rules see (e.g. the discount
/// from 8% to 12%) changes the fingerprint, so the token no longer matches and a new approval is needed.
/// <see cref="KeyId"/> selects the verifying key (server key rotation / device id).
/// </summary>
public readonly record struct OverrideToken(OverrideTokenKind Kind, string ApproverId, string Permission, ulong Fingerprint,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string KeyId, ReadOnlyMemory<byte> Signature)
{
    /// <summary>Canonical bytes that are signed (everything but the signature).</summary>
    public byte[] SignedPayload() => Payload(Kind, ApproverId, Permission, Fingerprint, IssuedAt, ExpiresAt, KeyId);

    internal static byte[] Payload(OverrideTokenKind kind, string approver, string permission, ulong fingerprint,
        DateTimeOffset issuedAt, DateTimeOffset expiresAt, string keyId)
    {
        var enc = Encoding.UTF8;
        int len = 1 + 4 + enc.GetByteCount(approver) + 4 + enc.GetByteCount(permission) + 8 + 8 + 8 + 4 + enc.GetByteCount(keyId);
        var bytes = new byte[len];
        var span = bytes.AsSpan();
        span[0] = (byte)kind;
        int p = 1;
        p += WriteString(span[p..], approver);
        p += WriteString(span[p..], permission);
        BinaryPrimitives.WriteUInt64LittleEndian(span[p..], fingerprint); p += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[p..], issuedAt.UtcTicks); p += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[p..], expiresAt.UtcTicks); p += 8;
        WriteString(span[p..], keyId);
        return bytes;

        static int WriteString(Span<byte> dest, string s)
        {
            int n = Encoding.UTF8.GetBytes(s, dest[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(dest, n);
            return 4 + n;
        }
    }
}

/// <summary>Outcome of verifying a token against a decision's approval request.</summary>
public enum ApprovalCheck : byte
{
    Valid = 0,
    /// <summary>Unknown key or bad signature (tampering).</summary>
    InvalidSignature = 1,
    Expired = 2,
    /// <summary>The token approves another outcome (the situation changed after the approval).</summary>
    FingerprintMismatch = 3,
    /// <summary>The token grants another permission.</summary>
    PermissionMismatch = 4,
    /// <summary>Device (offline) token beyond the offline cap of the permission.</summary>
    OverOfflineCap = 5,
    /// <summary>No verifier accepts this token kind.</summary>
    UnsupportedKind = 6,
    /// <summary>The approver lacks the permission (checked by the pipeline with YFex.Authorization).</summary>
    ApproverNotAllowed = 7,
}

/// <summary>Result of a verification. <see cref="Flagged"/> = accepted but must be reviewed (offline approvals).</summary>
public readonly record struct ApprovalVerification(ApprovalCheck Check, bool Flagged = false)
{
    public bool IsValid => Check == ApprovalCheck.Valid;
}

/// <summary>Issues approval tokens (online: the server, executing <c>ApproveDecision</c> as the approver).</summary>
public interface IOverrideTokenIssuer
{
    OverrideToken Issue(string approverId, string permission, ulong fingerprint, DateTimeOffset now);
}

/// <summary>
/// Verifies an approval token for one approval request of a decision. The pipeline (Cqrs stage 4) also checks that the
/// approver holds the permission (YFex.Authorization) — verifiers only check cryptography, binding and caps.
/// </summary>
public interface IOverrideTokenVerifier
{
    ApprovalVerification Verify(in OverrideToken token, in ApprovalRequest request, ulong fingerprint, DateTimeOffset now);
}

/// <summary>
/// Online approvals (D34): HMAC-SHA256 over the token payload with a server secret. Keys are identified by
/// <c>KeyId</c> so they can rotate (sign with the current key, verify with any configured key).
/// <code>
/// var tokens = new HmacOverrideTokens(new Dictionary&lt;string, byte[]&gt; { ["k2"] = key2, ["k1"] = key1 }, currentKeyId: "k2",
///                                     lifetime: TimeSpan.FromMinutes(15));
/// </code>
/// </summary>
public sealed class HmacOverrideTokens : IOverrideTokenIssuer, IOverrideTokenVerifier
{
    private readonly FrozenDictionary<string, byte[]> _keys;
    private readonly string _currentKeyId;

    public HmacOverrideTokens(IReadOnlyDictionary<string, byte[]> keys, string currentKeyId, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!keys.TryGetValue(currentKeyId, out var current) || current.Length < 32)
            throw new ArgumentException("The current HMAC key must exist and have at least 32 bytes.", nameof(currentKeyId));
        _keys = keys.ToFrozenDictionary(StringComparer.Ordinal);
        _currentKeyId = currentKeyId;
        Lifetime = lifetime;
    }

    public TimeSpan Lifetime { get; }

    public OverrideToken Issue(string approverId, string permission, ulong fingerprint, DateTimeOffset now)
    {
        var expires = now + Lifetime;
        var payload = OverrideToken.Payload(OverrideTokenKind.Server, approverId, permission, fingerprint, now, expires, _currentKeyId);
        return new(OverrideTokenKind.Server, approverId, permission, fingerprint, now, expires, _currentKeyId,
            HMACSHA256.HashData(_keys[_currentKeyId], payload));
    }

    public ApprovalVerification Verify(in OverrideToken token, in ApprovalRequest request, ulong fingerprint, DateTimeOffset now)
    {
        if (token.Kind != OverrideTokenKind.Server) return new(ApprovalCheck.UnsupportedKind);
        if (!_keys.TryGetValue(token.KeyId, out var key)) return new(ApprovalCheck.InvalidSignature);
        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(key, token.SignedPayload(), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, token.Signature.Span)) return new(ApprovalCheck.InvalidSignature);
        return OverrideTokenChecks.Binding(in token, in request, fingerprint, now, checkExpiry: true);
    }
}

/// <summary>Per-permission cap of offline approvals (D34): the requested value an offline manager may approve.</summary>
/// <param name="Permission">E.g. <c>"sales.discount.override"</c>.</param>
/// <param name="MaxRequested">Highest requested value (the limit check's arg {1}) approvable offline; None = no offline approval.</param>
/// <param name="MaxAge">How long after issuing the token is accepted on replay.</param>
public readonly record struct OfflineApprovalCap(string Permission, RawValue MaxRequested, TimeSpan MaxAge);

/// <summary>Public keys of the provisioned devices (server side); revoke by removing the device.</summary>
public interface IDeviceKeyStore
{
    /// <summary>SubjectPublicKeyInfo (DER) of the device's ECDSA P-256 key, or null when unknown/revoked.</summary>
    byte[]? GetPublicKey(string deviceId);
}

/// <summary>
/// Offline approvals, device side (D34): the POS verified the manager locally (PIN / badge — app concern) and signs
/// with its own ECDSA P-256 key. <b>Security review required before production</b> (key extraction from a
/// compromised device, PIN brute force on cached hashes, permissions revoked while offline) — that's why the server
/// caps and flags every device token.
/// </summary>
public sealed class DeviceOverrideTokenIssuer(string deviceId, ECDsa privateKey, TimeSpan lifetime) : IOverrideTokenIssuer
{
    public OverrideToken Issue(string approverId, string permission, ulong fingerprint, DateTimeOffset now)
    {
        var expires = now + lifetime;
        var payload = OverrideToken.Payload(OverrideTokenKind.Device, approverId, permission, fingerprint, now, expires, deviceId);
        return new(OverrideTokenKind.Device, approverId, permission, fingerprint, now, expires, deviceId,
            privateKey.SignData(payload, HashAlgorithmName.SHA256));
    }
}

/// <summary>
/// Offline approvals, server side (D34): verifies the device signature, the fingerprint/permission binding and the
/// per-permission cap. Valid device tokens are always <see cref="ApprovalVerification.Flagged"/> (audit/review).
/// Expiry is not compared with the replay time (the device was offline) — <see cref="OfflineApprovalCap.MaxAge"/> is.
/// </summary>
public sealed class DeviceOverrideTokenVerifier : IOverrideTokenVerifier
{
    private readonly IDeviceKeyStore _keys;
    private readonly FrozenDictionary<string, OfflineApprovalCap> _caps;

    public DeviceOverrideTokenVerifier(IDeviceKeyStore keys, IEnumerable<OfflineApprovalCap> caps)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        var map = new Dictionary<string, OfflineApprovalCap>(StringComparer.Ordinal);
        foreach (var c in caps) map[c.Permission] = c;
        _caps = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public ApprovalVerification Verify(in OverrideToken token, in ApprovalRequest request, ulong fingerprint, DateTimeOffset now)
    {
        if (token.Kind != OverrideTokenKind.Device) return new(ApprovalCheck.UnsupportedKind);
        if (_keys.GetPublicKey(token.KeyId) is not { } spki) return new(ApprovalCheck.InvalidSignature);
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            if (!ecdsa.VerifyData(token.SignedPayload(), token.Signature.Span, HashAlgorithmName.SHA256))
                return new(ApprovalCheck.InvalidSignature);
        }
        var binding = OverrideTokenChecks.Binding(in token, in request, fingerprint, now, checkExpiry: false);
        if (!binding.IsValid) return binding;

        if (!_caps.TryGetValue(token.Permission, out var cap) || cap.MaxRequested.IsNone) return new(ApprovalCheck.OverOfflineCap);
        if (now - token.IssuedAt > cap.MaxAge || token.IssuedAt > token.ExpiresAt) return new(ApprovalCheck.Expired);
        // The requested value is arg {1} of the engine's limit violation; an explicit RequireApproval directive has none → not approvable offline.
        if (request.Reason.Args.Count < 2) return new(ApprovalCheck.OverOfflineCap);
        var requested = request.Reason.Args[1];
        if (!requested.TryCompareTo(cap.MaxRequested, out int c) || c > 0) return new(ApprovalCheck.OverOfflineCap);
        return new(ApprovalCheck.Valid, Flagged: true);
    }
}

/// <summary>Tries each verifier that supports the token's kind (typically HMAC + device).</summary>
public sealed class CompositeOverrideTokenVerifier(params IOverrideTokenVerifier[] verifiers) : IOverrideTokenVerifier
{
    public ApprovalVerification Verify(in OverrideToken token, in ApprovalRequest request, ulong fingerprint, DateTimeOffset now)
    {
        var last = new ApprovalVerification(ApprovalCheck.UnsupportedKind);
        foreach (var v in verifiers)
        {
            last = v.Verify(in token, in request, fingerprint, now);
            if (last.Check != ApprovalCheck.UnsupportedKind) return last;
        }
        return last;
    }
}

internal static class OverrideTokenChecks
{
    public static ApprovalVerification Binding(in OverrideToken token, in ApprovalRequest request, ulong fingerprint, DateTimeOffset now, bool checkExpiry)
    {
        if (token.Fingerprint != fingerprint) return new(ApprovalCheck.FingerprintMismatch);
        if (!string.Equals(token.Permission, request.Permission.Value, StringComparison.Ordinal)) return new(ApprovalCheck.PermissionMismatch);
        if (checkExpiry && now > token.ExpiresAt) return new(ApprovalCheck.Expired);
        return new(ApprovalCheck.Valid);
    }
}
