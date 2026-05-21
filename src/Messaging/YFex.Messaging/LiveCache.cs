namespace YFex.Messaging;

/// <summary>
/// Controls where <c>[Live]</c> property values are cached and how
/// invalidation is delivered back to subscribers.
/// </summary>
public enum LiveCache
{
    /// <summary>
    /// Default. Fusion <c>ComputedState</c> kept in-process.
    /// Invalidation is in-memory; no serialization required.
    /// </summary>
    Local = 0,

    /// <summary>
    /// Value is computed on the server and cached in the server-side
    /// Fusion registry. Clients receive a cached result plus Stl.Rpc
    /// invalidation pushes when the server's computed value changes.
    /// Requires <c>YFex.Messaging.Rpc</c> and the event/result types
    /// must be <c>[MemoryPackable]</c>.
    /// </summary>
    ServerShared = 1,

    /// <summary>
    /// Same as <see cref="ServerShared"/> but additionally serializes
    /// the last fetched value to client-local storage (IndexedDB / file).
    /// Survives process restart; shows stale value until reconnected.
    /// Requires <c>YFex.Messaging.Rpc</c> and <c>YFex.Messaging.Persistence</c>.
    /// </summary>
    ClientPersistent = 2,
}
