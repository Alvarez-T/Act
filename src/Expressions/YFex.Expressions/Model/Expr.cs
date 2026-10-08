using System.Collections.Immutable;

namespace YFex.Expressions;

/// <summary>Node kinds of an expression. Order is persisted nowhere, but keep it stable: tests and tools switch on it.</summary>
public enum ExprKind : byte
{
    /// <summary>Literal: number, text, bool or null (<see cref="Expr.Literal"/>).</summary>
    Literal,
    /// <summary>Top-level field <c>{"var":"Total"}</c> or special <c>{"var":"$base"}</c>.</summary>
    Var,
    /// <summary>Item field of the schema's default collection <c>{"line":"Category"}</c> — per-item expression, or inside an aggregate.</summary>
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
    /// <summary>Aggregates over a collection's items: <c>{"sum": expr}</c>, count, any, all, minOf, maxOf.</summary>
    Sum, Count, Any, All, MinOf, MaxOf,
    /// <summary>Function call <c>{"round":[x, 2]}</c> (any key that is not an operator).</summary>
    Call,
}

/// <summary>
/// Immutable expression tree. Authoring/interchange form only — never evaluated directly: <see cref="ExprBinder"/> type-checks it
/// against an <see cref="ExprSchema"/> + <see cref="FunctionCatalog"/> and <see cref="ExprCompiler"/> flattens it to an
/// <see cref="ExprProgram"/>. JSON form: <see cref="ExprJson"/>; readable text: <see cref="ExprText"/>.
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
