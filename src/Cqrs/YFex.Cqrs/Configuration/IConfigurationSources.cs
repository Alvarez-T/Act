using YFex.Cqrs.Registration;

namespace YFex.Cqrs.Configuration;

/// <summary>
/// Internal contract: a query configuration builder that can produce its typed registration metadata.
/// <see cref="AggregateConfigurationBuilder{TAggregate}"/> stores a typed list of these — no List&lt;object&gt;.
/// </summary>
internal interface IQueryConfigurationSource
{
    QueryRegistrationMetadata BuildMetadata();
}

/// <summary>
/// Internal contract: a command configuration builder that can produce its typed registration metadata.
/// </summary>
internal interface ICommandConfigurationSource
{
    CommandRegistrationMetadata BuildMetadata();
}
