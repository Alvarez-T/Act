namespace YFex.Messaging.Rpc;

/// <summary>
/// Well-known service keys for storage DI registrations.
/// Backend packages register the raw (unencrypted) <see cref="IClientStorage"/> under
/// <see cref="Inner"/> so the encryption decorator can resolve it without circular loops.
/// </summary>
public static class StorageServiceKeys
{
    /// <summary>
    /// Key used to register the concrete, unencrypted <see cref="IClientStorage"/> implementation.
    /// The <c>AddYFexStorageEncryption</c> extension wraps the service at this key.
    /// </summary>
    public const string Inner = "yfex-storage-inner";
}
