namespace YFex.Cqrs.Registration;

/// <summary>
/// Typed snapshot of one aggregate's registration metadata.
/// Produced by <see cref="YFex.Cqrs.Configuration.AggregateConfigurationBuilder{TAggregate}.BuildRegistrations"/>
/// and consumed immediately by <see cref="CompiledMessagingRegistry"/>.Build().
/// Internal — never leaves the YFex.Cqrs assembly.
/// </summary>
internal sealed class AggregateRegistrations
{
    internal QueryRegistrationMetadata[]      Queries  { get; }
    internal CommandRegistrationMetadata[]    Commands { get; }
    internal EventGroupRegistrationMetadata[] Events   { get; }

    internal AggregateRegistrations(
        QueryRegistrationMetadata[]      queries,
        CommandRegistrationMetadata[]    commands,
        EventGroupRegistrationMetadata[] events)
    { Queries = queries; Commands = commands; Events = events; }
}
