namespace YFex.Rules.Dynamic;

/// <summary>Violation codes of dynamic-rule publish checks (paths are JSON paths into the spec: <c>when.and[1].&gt;=[0]</c>).</summary>
public static class DynamicRuleCodes
{
    public const string Malformed = "rules.dynamic.malformed";
    public const string CodeMismatch = "rules.dynamic.code-mismatch";
    public const string UnknownGroup = "rules.dynamic.unknown-group";
    public const string GroupClosed = "rules.dynamic.group-closed";
    public const string TierNotAllowed = "rules.dynamic.tier-not-allowed";
    public const string DirectiveNotAllowed = "rules.dynamic.directive-not-allowed";
    public const string OverCap = "rules.dynamic.over-cap";
    public const string UnknownField = "rules.dynamic.unknown-field";
    public const string FieldNotExposed = "rules.dynamic.field-not-exposed";
    public const string NoLines = "rules.dynamic.no-lines";
    public const string NestedAggregate = "rules.dynamic.nested-aggregate";
    public const string UnknownFunction = "rules.dynamic.unknown-function";
    public const string Unimplemented = "rules.dynamic.unimplemented";
    /// <summary>A function that depends on context (clock, user…) — rules must be replayable (Phase X, XD7).</summary>
    public const string NonDeterministic = "rules.dynamic.non-deterministic";
    public const string Arity = "rules.dynamic.arity";
    public const string Type = "rules.dynamic.type";
    public const string BadLiteral = "rules.dynamic.literal";
    public const string NoAmount = "rules.dynamic.no-amount";
    public const string TooLarge = "rules.dynamic.too-large";
    /// <summary>The spec names a permission id no catalog declares (authorization-consolidation ⚑Z2: ids resolved at publish).</summary>
    public const string UnknownPermission = "authorization.permission.unknown";
    public const string Missing = "rules.dynamic.missing";

    /// <summary>The expression binder's codes mapped to this namespace, so publish diagnostics are unchanged by Phase X (XD1).
    /// Unknown <c>$names</c> keep reporting <see cref="UnknownField"/> as before.</summary>
    internal static ExprDiagnosticCodes Expression { get; } = new()
    {
        Malformed = Malformed,
        UnknownField = UnknownField,
        FieldNotExposed = FieldNotExposed,
        NoItems = NoLines,
        NestedAggregate = NestedAggregate,
        UnknownFunction = UnknownFunction,
        Unimplemented = Unimplemented,
        NonDeterministic = NonDeterministic,
        Arity = Arity,
        Type = Type,
        BadLiteral = BadLiteral,
        UnknownSpecial = UnknownField,
        TooLarge = TooLarge,
    };
}
