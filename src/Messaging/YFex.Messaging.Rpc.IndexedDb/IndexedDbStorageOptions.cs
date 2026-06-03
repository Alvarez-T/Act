namespace YFex.Messaging.Rpc.IndexedDb;

public sealed class IndexedDbStorageOptions
{
    /// <summary>
    /// Action taken when IndexedDB is unavailable (e.g. private-browsing mode on Safari).
    /// Default: <see cref="IndexedDbUnavailableAction.FallbackToMemory"/>.
    /// </summary>
    public IndexedDbUnavailableAction UnavailableAction { get; set; } =
        IndexedDbUnavailableAction.FallbackToMemory;
}

public enum IndexedDbUnavailableAction
{
    /// <summary>Silently fall back to volatile in-memory storage.</summary>
    FallbackToMemory,
    /// <summary>Throw <see cref="InvalidOperationException"/> so the caller knows persistence failed.</summary>
    Throw
}
