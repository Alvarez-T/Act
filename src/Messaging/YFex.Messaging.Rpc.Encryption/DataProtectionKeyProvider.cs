using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace YFex.Messaging.Rpc.Encryption;

/// <summary>
/// Derives a 32-byte AES key via <see cref="IDataProtector"/> from the OS key store
/// (DPAPI on Windows, Keychain on macOS, libsecret on Linux).
///
/// The protector produces a stable deterministic 256-bit key by HKDF-expanding
/// a DataProtection-protected seed value.
/// </summary>
internal sealed class DataProtectionKeyProvider : IKeyProvider
{
    private readonly IDataProtectionProvider _dpProvider;
    private byte[]? _cachedKey;

    public DataProtectionKeyProvider(IDataProtectionProvider dpProvider)
        => _dpProvider = dpProvider;

    public byte[] GetKey()
    {
        if (_cachedKey is not null) return _cachedKey;

        // Derive a stable key: protect a fixed seed, then HKDF-expand to 32 bytes.
        var protector = _dpProvider.CreateProtector("YFex.Messaging.Rpc.Encryption.v1");
        var seed = protector.Protect(Encoding.UTF8.GetBytes("yfex-storage-master-key-seed-v1"));

        _cachedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, seed,
            outputLength: 32,
            salt: null,
            info: Encoding.UTF8.GetBytes("YFex.AesGcm.StorageKey.v1"));
        return _cachedKey;
    }
}
