namespace YFex.Cqrs;

/// <summary>Commands implementing this return <see cref="QueueableResult{T}"/> and are enqueued when offline.</summary>
public interface IQueueable
{
}

/// <summary>Queries implementing this participate in client-side persistent caching.</summary>
public interface ICacheable
{
}

/// <summary>Marker base for invalidation groups. User interfaces extending this become group tokens for fan-out invalidation.</summary>
public interface IInvalidationGroup
{
}

/// <summary>Marker base for event groups. User interfaces extending this become group tokens for Subscribe fan-out.</summary>
public interface IEventGroup
{
}
