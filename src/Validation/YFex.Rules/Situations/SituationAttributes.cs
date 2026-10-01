namespace YFex.Rules;

/// <summary>
/// Marks an immutable snapshot evaluated by rules (04 §1). Everything a rule may read MUST be here — rules never do
/// I/O, never read clocks, never read ambient context. The generator implements <see cref="ISituation{TSelf}"/>:
/// typed field handles (<c>Fields</c>), <c>Schema</c>, <c>Read</c>, <c>Hash</c>, <c>WriteTo</c>/<c>ReadFrom</c>, <c>DiffMask</c>.
/// <code>
/// [Situation("shipment")]
/// public readonly partial record struct Shipment(
///     [property: RuleTier] CarrierId Carrier, [property: RuleTier] HubId Hub,
///     [property: RuleEffectiveDate] DateOnly ShipDate,
///     [property: Requested] Mass DeclaredWeight, ImmutableArray&lt;Parcel&gt; Parcels);
/// </code>
/// </summary>
/// <param name="name">Schema name used by tables, dynamic rules and the catalog (<c>"shipment"</c>).</param>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class SituationAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// The single <see cref="DateOnly"/>/<see cref="DateTime"/> field whose value decides which rule versions, table versions
/// and parameter values are in effect (04 §10) — the business date of the operation, never the clock.
/// Without one, the latest published data is used.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class RuleEffectiveDateAttribute : Attribute;

/// <summary>
/// Declares the field as the next level of the app's rule-tier hierarchy (04 §9): the first <c>[RuleTier]</c> field is
/// tier 1, the next tier 2… (declaration order, at most <see cref="RuleTierPath.MaxDepth"/>). Rule data published at a
/// tier applies to every evaluation below it; the most specific tier wins. Names are the app's own (Tenant/Store,
/// Carrier/Hub, Company/Department…); tables and parameters opt in with <c>EditableAt = [nameof(…)]</c>.
/// A situation without tiers only has code data.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class RuleTierAttribute : Attribute;

/// <summary>
/// A value the actor ASKED for. After all stages the engine checks it against the resolved limits/options of the
/// same field and emits violations/approvals with path = field name (04 §7 step 6). Also valid on a rule set's
/// <see cref="DerivedField{TSituation,T}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class RequestedAttribute : Attribute;

/// <summary>
/// Pure computed situation property (from other fields only). Part of the schema/<c>Read</c> (tables may bind it),
/// excluded from <c>WriteTo</c> (recomputed on read).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DerivedAttribute : Attribute;

/// <summary><c>[Exposed(false)]</c> hides a field from table editors and dynamic rules. Default: exposed.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ExposedAttribute(bool exposed = true) : Attribute
{
    public bool Exposed { get; } = exposed;
}

/// <summary>Role of a situation field in the schema.</summary>
public enum FieldRole : byte { Normal = 0, Tier = 1, EffectiveDate = 2, Requested = 3, Derived = 4, Lines = 5 }
