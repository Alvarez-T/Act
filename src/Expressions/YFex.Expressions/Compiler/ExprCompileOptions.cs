namespace YFex.Expressions;

/// <summary>
/// Violation codes the binder reports. Defaults are neutral (<c>expr.*</c>); a host maps them to its own namespace so its
/// published diagnostics never change (YFex.Rules.Dynamic passes its <c>rules.dynamic.*</c> codes — Phase X decision XD1).
/// </summary>
public sealed record ExprDiagnosticCodes
{
    public static ExprDiagnosticCodes Default { get; } = new();

    public string Malformed { get; init; } = "expr.malformed";
    public string UnknownField { get; init; } = "expr.unknown-field";
    public string FieldNotExposed { get; init; } = "expr.field-not-exposed";
    /// <summary>An item field / aggregate where the schema has no (default) collection.</summary>
    public string NoItems { get; init; } = "expr.no-items";
    public string NestedAggregate { get; init; } = "expr.nested-aggregate";
    public string UnknownFunction { get; init; } = "expr.unknown-function";
    /// <summary>A contract function nobody implemented in this host.</summary>
    public string Unimplemented { get; init; } = "expr.unimplemented";
    /// <summary>A non-deterministic function where <see cref="ExprCompileOptions.RequireDeterministic"/> is set.</summary>
    public string NonDeterministic { get; init; } = "expr.non-deterministic";
    public string Arity { get; init; } = "expr.arity";
    public string Type { get; init; } = "expr.type";
    public string BadLiteral { get; init; } = "expr.literal";
    /// <summary>A <c>$name</c> the host doesn't offer.</summary>
    public string UnknownSpecial { get; init; } = "expr.unknown-special";
    public string TooLarge { get; init; } = "expr.too-large";
}

/// <summary>Size limits that protect hosts from huge authored expressions (today's Rules.Dynamic values).</summary>
public sealed record ExprLimits(int MaxNodes = 500, int MaxDepth = 32)
{
    public static ExprLimits Default { get; } = new();
}

/// <summary>Everything an expression is checked against.</summary>
/// <param name="Schema">What <c>var</c>/<c>line</c> may read.</param>
/// <param name="Functions">What calls may use.</param>
public sealed record ExprCompileOptions(ExprSchema Schema, FunctionCatalog Functions)
{
    /// <summary>The <c>$name</c> context values the host offers.</summary>
    public ExprSpecials Specials { get; init; } = ExprSpecials.None;

    public ExprDiagnosticCodes Codes { get; init; } = ExprDiagnosticCodes.Default;

    public ExprLimits Limits { get; init; } = ExprLimits.Default;

    /// <summary>Reject functions with <see cref="ExprFunction.IsDeterministic"/> = false (hosts whose results must be replayable).</summary>
    public bool RequireDeterministic { get; init; }
}
