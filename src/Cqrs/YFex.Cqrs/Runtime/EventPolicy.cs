namespace YFex.Cqrs.Runtime;

/// <summary>Pre-compiled event group linkage for one event union type.</summary>
public sealed record EventPolicy(Type? GroupUnionType);
