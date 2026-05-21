namespace YFex.Persistence;

/// <summary>
/// Opt-out marker. When a class-level <c>[Persist]</c> rule (or convention) would
/// include this property in the snapshot, apply <c>[NeverPersist]</c> to exclude it.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class NeverPersistAttribute : Attribute { }
