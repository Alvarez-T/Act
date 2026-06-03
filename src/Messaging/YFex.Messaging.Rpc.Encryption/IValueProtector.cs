namespace YFex.Messaging.Rpc.Encryption;

/// <summary>
/// Encrypts and decrypts raw byte payloads. Implementations are stateless after construction.
/// </summary>
public interface IValueProtector
{
    /// <summary>Returns an authenticated ciphertext blob. Never returns null.</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// Decrypts and authenticates <paramref name="ciphertext"/>.
    /// Returns <c>null</c> if the version tag is unknown (forward-compat) or authentication fails.
    /// </summary>
    byte[]? Unprotect(ReadOnlySpan<byte> ciphertext);
}
