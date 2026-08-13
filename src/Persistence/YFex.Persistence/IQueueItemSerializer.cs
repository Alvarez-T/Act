namespace YFex.Persistence;

/// <summary>
/// Converts queue items to and from their persisted string form.
/// Supplied to a generic <see cref="IDurableQueue{T}"/> implementation so the
/// store stays domain-agnostic; the domain library owns the serialization format.
/// </summary>
public interface IQueueItemSerializer<T> where T : class
{
    /// <summary>Serializes <paramref name="item"/> to a persistable string.</summary>
    string Serialize(T item);

    /// <summary>
    /// Deserializes a string produced by <see cref="Serialize"/>.
    /// Returns <see langword="null"/> when the payload is malformed and should be skipped.
    /// </summary>
    T? Deserialize(string data);
}
