namespace YFex.Cqrs;

/// <summary>
/// Placeholder for Plan 3's invalidation runtime (ActualLab.Fusion Invalidation.Begin scope).
/// Only the interface shape is defined here; the implementation lives in YFex.Messaging.Rpc.
/// </summary>
public interface IInvalidator
{
    void Invalidate<TQuery>(TQuery query) where TQuery : IQuery<object?>;
    void InvalidateAll<TQuery>() where TQuery : IQuery<object?>;
}

/// <summary>Context passed to cache invalidation logic at dispatch time.</summary>
public interface IInvalidationContext
{
    IInvalidator Invalidator { get; }
}
