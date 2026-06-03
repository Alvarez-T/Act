namespace YFex.Cqrs.Registration;

/// <summary>Plain data record for event-group linkage metadata.</summary>
internal sealed record EventGroupRegistrationMetadata
{
    public required Type[] EventTypes   { get; init; }
    public Type?           GroupUnion   { get; init; }
}
