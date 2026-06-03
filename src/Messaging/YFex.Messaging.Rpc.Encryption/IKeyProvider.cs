namespace YFex.Messaging.Rpc.Encryption;

/// <summary>
/// Provides the raw AES key material (exactly 32 bytes for AES-256).
/// Implementations handle platform-specific key derivation or retrieval.
/// </summary>
public interface IKeyProvider
{
    /// <summary>Returns a 32-byte AES-256 key. Called once at startup; result is cached by the caller.</summary>
    byte[] GetKey();
}
