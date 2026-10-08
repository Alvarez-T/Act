namespace YFex.Installations;

/// <summary>The installation of this process, published by its host and read by ecosystem libraries.</summary>
/// <remarks>
/// This is metadata, not a trust boundary. Cryptographic authorization must validate enrollment separately.
/// Hosts simulating multiple installations in one process should use explicit identities for those installations.
/// </remarks>
public static class LocalInstallation
{
    private static InstallationIdentity? _current;

    /// <summary>Null until the host publishes its installation.</summary>
    public static InstallationIdentity? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Raised synchronously after an unequal identity is published. Subscribe at startup and unsubscribe on shutdown.
    /// Under concurrent publishers, callbacks may run concurrently; use the event argument as the change snapshot.
    /// </summary>
    public static event Action<InstallationIdentity>? Changed;

    /// <summary>
    /// Publishes a snapshot atomically. Equal snapshots do not notify. Intended for the Installations runtime.
    /// Subscriber exceptions propagate after publication and do not roll back the snapshot.
    /// </summary>
    public static void Publish(InstallationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        InstallationIdentity? previous;
        do
        {
            previous = Volatile.Read(ref _current);
            if (Equals(previous, identity)) return;
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _current, identity, previous), previous));
        Changed?.Invoke(identity);
    }
}
