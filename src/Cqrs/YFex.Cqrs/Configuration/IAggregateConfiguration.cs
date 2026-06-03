namespace YFex.Cqrs.Configuration;

// ── Public user-facing interfaces ─────────────────────────────────────────────

/// <summary>
/// Non-generic base allowing <see cref="CompiledMessagingRegistry.Build"/> to iterate
/// configurations without knowing <c>TAggregate</c> at the call site.
/// <see cref="AggregateType"/> is the only public member — no registration metadata leaks here.
/// </summary>
public interface IAggregateConfiguration
{
    Type AggregateType { get; }
}

/// <summary>
/// EF-Core-style aggregate configuration. One file per aggregate covers all
/// queries, commands, and events.  Baseline — applies on both client and server.
/// </summary>
public interface IAggregateConfiguration<TAggregate> : IAggregateConfiguration
{
    void Configure(AggregateConfigurationBuilder<TAggregate> builder);

    // DIM: AggregateType is always typeof(TAggregate)
    Type IAggregateConfiguration.AggregateType => typeof(TAggregate);
}

/// <summary>Server-only overrides layered on top of the baseline.</summary>
public interface IServerAggregateConfiguration<TAggregate> : IAggregateConfiguration<TAggregate>
{
}

/// <summary>Client-only overrides layered on top of the baseline.</summary>
public interface IClientAggregateConfiguration<TAggregate> : IAggregateConfiguration<TAggregate>
{
}
