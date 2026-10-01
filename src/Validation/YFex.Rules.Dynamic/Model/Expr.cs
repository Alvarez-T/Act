using System.Collections.Immutable;

namespace YFex.Rules.Dynamic;

/// <summary>Node kinds of a rule expression (05 §8, extended with arithmetic and functions).</summary>
public enum ExprKind : byte
{
    /// <summary>Literal: number, text, bool or null (<see cref="Expr.Literal"/>).</summary>
    Literal,
    /// <summary>Situation field <c>{"var":"Total"}</c> or special <c>{"var":"$base"}</c>.</summary>
    Var,
    /// <summary>Line field <c>{"line":"Category"}</c> — per-line rule, or inside an aggregate.</summary>
    Line,
    Add, Subtract, Multiply, Divide, Negate,
    Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual,
    And, Or, Not,
    /// <summary><c>{"in":[x, [v1, v2]]}</c> — literal list.</summary>
    In,
    /// <summary><c>{"between":[x, lo, hi]}</c> — inclusive.</summary>
    Between,
    /// <summary><c>{"isSet": x}</c> — not null.</summary>
    IsSet,
    /// <summary><c>{"if":[cond, then, else]}</c>.</summary>
    If,
    /// <summary><c>{"coalesce":[a, b, …]}</c> — first non-null.</summary>
    Coalesce,
    /// <summary>Aggregates over the situation's lines: <c>{"sum": expr}</c>, count, any, all, minOf, maxOf.</summary>
    Sum, Count, Any, All, MinOf, MaxOf,
    /// <summary>Function call <c>{"round":[x, 2]}</c> (any key that is not an operator).</summary>
    Call,
}

/// <summary>
/// Immutable expression tree of a dynamic rule. Authoring/interchange form only — never evaluated directly: the binder
/// type-checks it against the situation schema + function catalog and the compiler flattens it to a postfix program.
/// </summary>
public sealed class Expr
{
    private Expr(ExprKind kind, RawValue literal, string? name, ImmutableArray<Expr> args, ImmutableArray<RawValue> list)
    {
        Kind = kind;
        Literal = literal;
        Name = name;
        Args = args.IsDefault ? [] : args;
        List = list.IsDefault ? [] : list;
    }

    public ExprKind Kind { get; }
    /// <summary><see cref="ExprKind.Literal"/>: an untyped literal (numbers as Decimal/Int64, text, bool, None).</summary>
    public RawValue Literal { get; }
    /// <summary>Field name (<see cref="ExprKind.Var"/>, <see cref="ExprKind.Line"/>) or function name (<see cref="ExprKind.Call"/>).</summary>
    public string? Name { get; }
    public ImmutableArray<Expr> Args { get; }
    /// <summary><see cref="ExprKind.In"/>: the literal values.</summary>
    public ImmutableArray<RawValue> List { get; }

    public static Expr Lit(in RawValue value) => new(ExprKind.Literal, value, null, default, default);
    public static Expr Lit(decimal value) => Lit(RawValue.From(value));
    public static Expr Lit(string value) => Lit(RawValue.From(value));
    public static Expr Lit(bool value) => Lit(RawValue.From(value));
    public static Expr Null => Lit(RawValue.None);
    public static Expr Var(string name) => new(ExprKind.Var, default, name, default, default);
    public static Expr LineVar(string name) => new(ExprKind.Line, default, name, default, default);
    public static Expr Op(ExprKind kind, params ImmutableArray<Expr> args) => new(kind, default, null, args, default);
    public static Expr In(Expr value, params ImmutableArray<RawValue> list) => new(ExprKind.In, default, null, [value], list);
    public static Expr Call(string function, params ImmutableArray<Expr> args) => new(ExprKind.Call, default, function, args, default);

    /// <summary>Nodes in this tree (size limits).</summary>
    public int CountNodes()
    {
        int n = 1;
        foreach (var a in Args) n += a.CountNodes();
        return n;
    }

    public int Depth()
    {
        int d = 0;
        foreach (var a in Args) d = Math.Max(d, a.Depth());
        return d + 1;
    }

    public override string ToString() => ExprText.Render(this);
}

/// <summary>Directive families of a dynamic rule's <c>then</c> list.</summary>
public enum DirectiveSpecKind : byte { Deny, Warn, Limit, Grant, Decrease, Increase, Approval }

/// <summary>
/// One <c>then</c> entry (05 §8 DirectiveSpec, values may be expressions):
/// <list type="bullet">
/// <item><c>{"deny":{"path":"Installments","code":"tenant.x.low","args":[expr]}}</c> / <c>warn</c></item>
/// <item><c>{"limit":{"field":"Installments","min":expr,"max":expr,"exceed":"deny|warn|approval","permission":"…"}}</c></item>
/// <item><c>{"grant":{"field":"Payment","options":["Pix","Cash"]}}</c></item>
/// <item><c>{"decrease":{"target":"total|line","percent":expr | "amount":expr,"max":expr}}</c> / <c>increase</c></item>
/// <item><c>{"approval":{"permission":"…","path":"…","code":"…"}}</c></item>
/// </list>
/// </summary>
public sealed record DirectiveSpec(
    DirectiveSpecKind Kind,
    string? Field = null,
    string? Path = null,
    string? Code = null,
    ImmutableArray<Expr> Args = default,
    Expr? Min = null,
    Expr? Max = null,
    Exceed Exceed = Exceed.Deny,
    string? Permission = null,
    ImmutableArray<RawValue> Options = default,
    bool LineTarget = false,
    Expr? Percent = null,
    Expr? Amount = null);

/// <summary>
/// A runtime-authored rule (05 §8). <see cref="Messages"/>: culture (<c>""</c>, <c>"en"</c>, <c>"pt"</c>…) → message key →
/// template; key <c>"name"</c> is the rule's display name (message key = <see cref="Code"/>), other keys are the codes its
/// deny/warn/approval directives use. Versioned with the rule (10 §4) — old decisions show the name they had.
/// </summary>
public sealed record RuleSpec(
    string Code,
    string Group,
    int Priority,
    Expr? When,
    ImmutableArray<DirectiveSpec> Then,
    ImmutableDictionary<string, ImmutableDictionary<string, string>> Messages,
    string? Description = null);
