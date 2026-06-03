namespace YFex.Cqrs.Configuration;

/// <summary>
/// Fluent builder for cache options, used inside
/// <see cref="QueryConfigurationBuilder{TQuery,TResult}.Cacheable(System.Action{CacheConfigurationBuilder})"/>.
/// Produces an immutable <see cref="CachePolicy"/> record.
/// </summary>
public sealed class CacheConfigurationBuilder
{
    private TimeSpan? _sliding;
    private TimeSpan? _absolute;
    private TimeSpan? _stale;

    public CacheConfigurationBuilder SlidingExpiration(TimeSpan d) { _sliding  = d; return this; }
    public CacheConfigurationBuilder AbsoluteExpiration(TimeSpan d) { _absolute = d; return this; }
    public CacheConfigurationBuilder StaleAfter(TimeSpan threshold) { _stale    = threshold; return this; }

    internal CachePolicy Build() => new(_sliding, _absolute, _stale);
}
