namespace YFex.Cqrs;

/// <summary>
/// Immutable cache options for a query — pure data, produced by
/// <see cref="Configuration.CacheConfigurationBuilder"/> and stored in
/// <see cref="Runtime.QueryPolicy"/>.
/// </summary>
public sealed record CachePolicy(
    TimeSpan? SlidingExpiration,
    TimeSpan? AbsoluteExpiration,
    TimeSpan? StaleAfter);
