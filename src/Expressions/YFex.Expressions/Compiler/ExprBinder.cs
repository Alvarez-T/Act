using System.Globalization;
using YFex.Validation;

namespace YFex.Expressions;

/// <summary>
/// A type-checked node: names resolved to ids, literals already converted to the type they are compared/combined with. Hosts read
/// the public members (e.g. YFex.Rules builds trace templates from "field op literal" conditions) and pass the root to
/// <see cref="ExprCompiler.Emit"/>.
/// </summary>
public sealed class BoundExpression
{
    internal BoundExpression() { }

    public required ExprKind Kind { get; init; }
    public required ExprType Type { get; init; }
    /// <summary>The literal (after conversion) when <see cref="IsLiteral"/>.</summary>
    public RawValue Value { get; init; }
    /// <summary>The authored node (texts, traces).</summary>
    public required Expr Source { get; init; }
    public BoundExpression[] Args { get; init; } = [];
    public bool IsLiteral => Kind == ExprKind.Literal;
    /// <summary>A <c>$name</c> read (<see cref="ExprKind.Var"/>).</summary>
    public bool IsSpecial { get; init; }
    /// <summary>A plain top-level field read: <see cref="ExprKind.Var"/> and not <see cref="IsSpecial"/>.</summary>
    public bool IsField => Kind == ExprKind.Var && !IsSpecial;
    /// <summary>−1, or the collection this node reads per item outside any aggregate (propagated to the root).</summary>
    public int ItemCollection { get; init; } = -1;

    internal ushort FieldId { get; init; }
    internal ushort CollectionId { get; init; }
    internal byte SpecialSlot { get; init; }
    internal ExprFunction? Function { get; init; }
    internal RawValue[] Set { get; init; } = [];
}

/// <summary>
/// Binds expressions against an <see cref="ExprCompileOptions"/> (schema, functions, specials): resolves names, infers and checks
/// every type, converts literals (<c>"Pix"</c> → the enum value, <c>500</c> → Money 500 when compared with a Money field) and
/// reports problems as <see cref="Violation"/>s with the JSON path of the offending node. One binder per authored document (a rule
/// spec, a screen): <see cref="ItemCollection"/> accumulates over every <see cref="Bind(Expr, string)"/> call.
/// </summary>
public sealed class ExprBinder
{
    private readonly ExprSchema _schema;
    private readonly FunctionCatalog _functions;
    private readonly ExprDiagnosticCodes _codes;
    private readonly List<Violation> _errors;
    private bool _inAggregate;

    public ExprBinder(ExprCompileOptions options, List<Violation> errors)
    {
        Options = options;
        _schema = options.Schema;
        _functions = options.Functions;
        _codes = options.Codes;
        _errors = errors;
    }

    public ExprCompileOptions Options { get; }

    /// <summary>−1, or the collection read per item (outside aggregates) by any expression bound so far — Rules: "this is a line rule".</summary>
    public int ItemCollection { get; private set; } = -1;

    public bool HasErrors => _errors.Count > 0;

    public void Error(string path, string code, string detail)
        => _errors.Add(new Violation(FieldPath.Of(path), code, Severity.Error, detail: detail));

    /// <summary>Binds <paramref name="e"/>; <paramref name="path"/> is the JSON path of the node in the host document.</summary>
    public BoundExpression Bind(Expr e, string path)
    {
        var b = BindNode(e, path);
        if (b.ItemCollection >= 0 && ItemCollection < 0) ItemCollection = b.ItemCollection;
        return b;
    }

    /// <summary>Binds <paramref name="e"/> and requires <paramref name="expected"/> (literals convert; plain numbers fit numeric targets).</summary>
    public BoundExpression Bind(Expr e, string path, in ExprType expected)
    {
        var b = Bind(e, path);
        if (b.IsLiteral) return new BoundExpression { Kind = ExprKind.Literal, Value = ConvertLiteral(b.Value, expected, path), Type = expected, Source = e };
        if (!b.Type.IsNull && !b.Type.SameAs(expected) && !(expected.IsNumeric && b.Type.IsNumber))
            Error(path, _codes.Type, $"expected {expected}, got {b.Type}");
        return b;
    }

    /// <summary>Size limits of one expression (nodes, depth); false + a <see cref="ExprDiagnosticCodes.TooLarge"/> error when exceeded.</summary>
    public bool CheckSize(Expr e, string path)
    {
        var limits = Options.Limits;
        int nodes = e.CountNodes();
        if (nodes > limits.MaxNodes) { Error(path, _codes.TooLarge, $"{nodes} expression nodes (max {limits.MaxNodes})"); return false; }
        if (e.Depth() > limits.MaxDepth) { Error(path, _codes.TooLarge, $"nesting deeper than {limits.MaxDepth}"); return false; }
        return true;
    }

    // ── Expressions ─────────────────────────────────────────────────────────

    private BoundExpression BindNode(Expr e, string path)
    {
        switch (e.Kind)
        {
            case ExprKind.Literal: return Lit(e, e.Literal);
            case ExprKind.Var: return BindVar(e, path);
            case ExprKind.Line: return BindLine(e, path);
            case ExprKind.Add or ExprKind.Subtract or ExprKind.Multiply or ExprKind.Divide: return BindArithmetic(e, path);
            case ExprKind.Negate:
            {
                var x = BindNode(e.Args[0], path + ".neg");
                if (!x.Type.IsNumeric && !x.Type.IsNull) Error(path, _codes.Type, $"neg needs a number, got {x.Type}");
                return Node(e, x.Type, [x]);
            }
            case ExprKind.Equal or ExprKind.NotEqual or ExprKind.Less or ExprKind.LessOrEqual or ExprKind.Greater or ExprKind.GreaterOrEqual:
                return BindCompare(e, path);
            case ExprKind.And or ExprKind.Or:
            {
                if (e.Args.Length < 1) Error(path, _codes.Arity, "and/or need at least one operand");
                var args = new BoundExpression[e.Args.Length];
                for (int i = 0; i < args.Length; i++) args[i] = RequireBool(BindNode(e.Args[i], $"{path}.{Name(e.Kind)}[{i}]"), $"{path}.{Name(e.Kind)}[{i}]");
                return Node(e, ExprType.Bool, args);
            }
            case ExprKind.Not:
                return Node(e, ExprType.Bool, [RequireBool(BindNode(e.Args[0], path + ".not"), path + ".not")]);
            case ExprKind.IsSet:
                return Node(e, ExprType.Bool, [BindNode(e.Args[0], path + ".isSet")]);
            case ExprKind.In: return BindIn(e, path);
            case ExprKind.Between: return BindBetween(e, path);
            case ExprKind.If: return BindIf(e, path);
            case ExprKind.Coalesce: return BindCoalesce(e, path);
            case ExprKind.Sum or ExprKind.Count or ExprKind.Any or ExprKind.All or ExprKind.MinOf or ExprKind.MaxOf: return BindAggregate(e, path);
            case ExprKind.Call: return BindCall(e, path);
            default:
                Error(path, _codes.Malformed, $"unsupported node {e.Kind}");
                return Lit(e, RawValue.None);
        }
    }

    private static string Name(ExprKind k) => ExprJson.OperatorName(k);

    /// <summary>An operator node; <see cref="BoundExpression.ItemCollection"/> propagates from its operands.</summary>
    private static BoundExpression Node(Expr e, in ExprType type, BoundExpression[] args, RawValue[]? set = null, ExprFunction? function = null)
    {
        int item = -1;
        foreach (var a in args) if (a.ItemCollection >= 0) { item = a.ItemCollection; break; }
        return new BoundExpression { Kind = e.Kind, Type = type, Args = args, Source = e, ItemCollection = item, Set = set ?? [], Function = function };
    }

    private static BoundExpression Lit(Expr e, in RawValue v) => new()
    {
        Kind = ExprKind.Literal,
        Value = v,
        Type = v.Kind switch
        {
            ValueKind.None => ExprType.Null,
            ValueKind.Bool => ExprType.Bool,
            ValueKind.Int32 or ValueKind.Int64 => ExprType.Int with { IsNumberLiteral = true },
            ValueKind.Decimal or ValueKind.Double => ExprType.Decimal with { IsNumberLiteral = true },
            ValueKind.Text => ExprType.Text,
            _ => new ExprType(v.Type ?? RawType.Text),
        },
        Source = e,
    };

    private BoundExpression BindVar(Expr e, string path)
    {
        string name = e.Name!;
        if (name.StartsWith('$'))
        {
            var specials = Options.Specials;
            if (specials.TryGet(name, out var special))
            {
                int item = special.IsItemIndex && !_inAggregate && _schema.Default is { } d ? d.Id : -1;
                return new BoundExpression
                {
                    Kind = ExprKind.Var, IsSpecial = true, SpecialSlot = special.Slot, Type = special.Type, Source = e, ItemCollection = item,
                };
            }
            if (specials.TryGetUnavailable(name, out string code, out string detail)) { Error(path, code, detail); return Lit(e, default); }
            Error(path, _codes.UnknownSpecial, $"unknown special variable '{name}' ({SpecialNames()})");
            return Lit(e, default);
        }
        if (!_schema.TryGetField(name, out var field))
        {
            Error(path, _codes.UnknownField, $"'{name}' is not a field of {_schema.Noun} '{_schema.Name}'");
            return Lit(e, default);
        }
        if (!field.Exposed) Error(path, _codes.FieldNotExposed, $"'{name}' is not exposed to expressions");
        if (_schema.TryGetCollection(name, out _))
            Error(path, _codes.Type, $"'{name}' is the line list — read line fields with {{\"line\": …}} inside sum/count/any/all/minOf/maxOf");
        return new BoundExpression { Kind = ExprKind.Var, FieldId = field.Id, Type = field.ExprType, Source = e };
    }

    private string SpecialNames()
    {
        var names = new List<string>();
        foreach (var s in Options.Specials.All) names.Add(s.Name);
        names.Sort(StringComparer.Ordinal);
        return names.Count == 0 ? "none offered here" : string.Join(", ", names);
    }

    private BoundExpression BindLine(Expr e, string path)
    {
        if (_schema.Default is not { } items)
        {
            Error(path, _codes.NoItems, $"{_schema.Noun} '{_schema.Name}' has no lines");
            return Lit(e, default);
        }
        if (!items.Items.TryGetField(e.Name!, out var field))
        {
            Error(path, _codes.UnknownField, $"'{e.Name}' is not a line field of '{items.Items.Name}'");
            return Lit(e, default);
        }
        if (!field.Exposed) Error(path, _codes.FieldNotExposed, $"line field '{e.Name}' is not exposed to expressions");
        return new BoundExpression
        {
            Kind = ExprKind.Line, FieldId = field.Id, CollectionId = items.Id, Type = field.ExprType, Source = e,
            ItemCollection = _inAggregate ? -1 : items.Id,
        };
    }

    private BoundExpression BindArithmetic(Expr e, string path)
    {
        if (e.Args.Length < 2) { Error(path, _codes.Arity, $"{Name(e.Kind)} needs at least two operands"); return Lit(e, default); }
        string first = $"{path}.{Name(e.Kind)}[0]";
        var acc = BindNode(e.Args[0], first);
        for (int i = 1; i < e.Args.Length; i++)
        {
            string at = $"{path}.{Name(e.Kind)}[{i}]";
            var right = BindNode(e.Args[i], at);
            // × and ÷ scale an amount by a NUMBER: a number literal next to an amount stays a number (Total × 2). Coercing it to
            // the amount (Money 2) made Money × Money, which is undefined — Rules.Dynamic rejected `Total * 2` before Phase X.
            var (l, r) = e.Kind is ExprKind.Multiply or ExprKind.Divide && IsScale(acc, right)
                ? (acc, right)
                : CoercePair(acc, right, i == 1 ? first : $"{path}.{Name(e.Kind)}", at);
            var type = ArithmeticType(e.Kind, l.Type, r.Type, at);
            acc = Node(e, type, [l, r]);
        }
        return acc;
    }

    /// <summary>One side an amount, the other a plain number literal.</summary>
    private static bool IsScale(BoundExpression a, BoundExpression b)
        => (a.Type.IsAmount && b.IsLiteral && b.Type.IsNumberLiteral) || (b.Type.IsAmount && a.IsLiteral && a.Type.IsNumberLiteral);

    private ExprType ArithmeticType(ExprKind op, in ExprType a, in ExprType b, string path)
    {
        if (a.IsNull || b.IsNull) return a.IsNull ? b : a;
        if (!a.IsNumeric || !b.IsNumeric)
        {
            Error(path, _codes.Type, $"{Name(op)} needs numbers or amounts, got {a} and {b}");
            return ExprType.Decimal;
        }
        if (a.IsNumber && b.IsNumber)
            return op == ExprKind.Divide || !a.IsIntegral || !b.IsIntegral ? ExprType.Decimal : ExprType.Int;
        switch (op)
        {
            case ExprKind.Add or ExprKind.Subtract:
                if (a.SameAs(b)) return a with { IsNumberLiteral = false };
                break;
            case ExprKind.Multiply:
                if (a.IsAmount && b.IsNumber) return a;
                if (a.IsNumber && b.IsAmount) return b;
                break;
            case ExprKind.Divide:
                if (a.IsAmount && b.IsNumber) return a;
                if (a.IsAmount && a.SameAs(b)) return ExprType.Decimal;          // ratio
                break;
        }
        Error(path, _codes.Type, $"{Name(op)} of {a} and {b} is not defined (amounts: + and - need the same type; × and ÷ by a number)");
        return a;
    }

    private BoundExpression BindCompare(Expr e, string path)
    {
        if (e.Args.Length != 2) { Error(path, _codes.Arity, $"{Name(e.Kind)} needs two operands"); return Lit(e, RawValue.From(false)); }
        string at = $"{path}.{Name(e.Kind)}";
        var (l, r) = CoercePair(BindNode(e.Args[0], at + "[0]"), BindNode(e.Args[1], at + "[1]"), at + "[0]", at + "[1]");
        bool equality = e.Kind is ExprKind.Equal or ExprKind.NotEqual;
        if (!l.Type.IsNull && !r.Type.IsNull)
        {
            bool numeric = l.Type.IsNumeric && r.Type.IsNumeric && (l.Type.IsNumber && r.Type.IsNumber || l.Type.SameAs(r.Type));
            if (!numeric && !l.Type.SameAs(r.Type)) Error(at, _codes.Type, $"cannot compare {l.Type} with {r.Type}");
            else if (!equality && !l.Type.IsComparable) Error(at, _codes.Type, $"{l.Type} has no order (use == / !=)");
        }
        else if (!equality) Error(at, _codes.Type, "null has no order (use isSet)");
        return Node(e, ExprType.Bool, [l, r]);
    }

    private BoundExpression BindIn(Expr e, string path)
    {
        var value = BindNode(e.Args[0], path + ".in[0]");
        var set = new RawValue[e.List.Length];
        for (int i = 0; i < set.Length; i++)
            set[i] = ConvertLiteral(e.List[i], value.Type, $"{path}.in[1][{i}]");
        return Node(e, ExprType.Bool, [value], set);
    }

    private BoundExpression BindBetween(Expr e, string path)
    {
        if (e.Args.Length != 3) { Error(path, _codes.Arity, "between needs [value, low, high]"); return Lit(e, RawValue.From(false)); }
        var v = BindNode(e.Args[0], path + ".between[0]");
        var lo = Coerce(BindNode(e.Args[1], path + ".between[1]"), v.Type, path + ".between[1]");
        var hi = Coerce(BindNode(e.Args[2], path + ".between[2]"), v.Type, path + ".between[2]");
        if (!v.Type.IsComparable) Error(path, _codes.Type, $"{v.Type} has no order");
        return Node(e, ExprType.Bool, [v, lo, hi]);
    }

    private BoundExpression BindIf(Expr e, string path)
    {
        if (e.Args.Length != 3) { Error(path, _codes.Arity, "if needs [condition, then, else]"); return Lit(e, default); }
        var c = RequireBool(BindNode(e.Args[0], path + ".if[0]"), path + ".if[0]");
        var (a, b) = CoercePair(BindNode(e.Args[1], path + ".if[1]"), BindNode(e.Args[2], path + ".if[2]"), path + ".if[1]", path + ".if[2]");
        var type = Unify(a.Type, b.Type, path + ".if");
        return Node(e, type, [c, a, b]);
    }

    private BoundExpression BindCoalesce(Expr e, string path)
    {
        if (e.Args.Length < 2) { Error(path, _codes.Arity, "coalesce needs at least two operands"); return Lit(e, default); }
        var args = new BoundExpression[e.Args.Length];
        for (int i = 0; i < args.Length; i++) args[i] = BindNode(e.Args[i], $"{path}.coalesce[{i}]");
        // The non-literal operands decide the type (literals then convert to it); all literals → their unified type.
        var type = ExprType.Null;
        foreach (var a in args) if (!a.IsLiteral) type = Unify(type, a.Type, path + ".coalesce");
        if (type.IsNull) foreach (var a in args) type = Unify(type, a.Type, path + ".coalesce");
        for (int i = 0; i < args.Length; i++) args[i] = Coerce(args[i], type, $"{path}.coalesce[{i}]");
        return Node(e, type, args);
    }

    private BoundExpression BindAggregate(Expr e, string path)
    {
        string at = path + "." + Name(e.Kind);
        if (_schema.Default is not { } items) { Error(at, _codes.NoItems, $"{_schema.Noun} '{_schema.Name}' has no lines to aggregate"); return Lit(e, default); }
        if (_inAggregate) { Error(at, _codes.NestedAggregate, "aggregates cannot be nested"); return Lit(e, default); }
        _inAggregate = true;
        var x = BindNode(e.Args[0], at);
        _inAggregate = false;
        ExprType type;
        switch (e.Kind)
        {
            case ExprKind.Sum:
                if (!x.Type.IsNumeric) Error(at, _codes.Type, $"sum needs a number or amount per line, got {x.Type}");
                type = x.Type with { IsNumberLiteral = false };
                break;
            case ExprKind.MinOf or ExprKind.MaxOf:
                if (!x.Type.IsComparable) Error(at, _codes.Type, $"{Name(e.Kind)} needs comparable values, got {x.Type}");
                type = x.Type with { IsNumberLiteral = false };
                break;
            case ExprKind.Count:
                RequireBool(x, at);
                type = ExprType.Int;
                break;
            default:
                RequireBool(x, at);
                type = ExprType.Bool;
                break;
        }
        // The aggregate consumes the per-item reads of its operand: the node itself runs once.
        return new BoundExpression { Kind = e.Kind, Type = type, Args = [x], Source = e, CollectionId = items.Id };
    }

    private BoundExpression BindCall(Expr e, string path)
    {
        string at = path + "." + e.Name;
        if (!_functions.TryGet(e.Name!, out var f))
        {
            Error(at, _codes.UnknownFunction, $"unknown function '{e.Name}'");
            return Lit(e, default);
        }
        if (f.IsContract) Error(at, _codes.Unimplemented, $"function '{f.Name}' is declared but not implemented by this application");
        if (Options.RequireDeterministic && !f.IsDeterministic)
            Error(at, _codes.NonDeterministic, $"function '{f.Name}' depends on context (clock, user…) and can't be used here");
        if (e.Args.Length != f.Parameters.Length)
        {
            Error(at, _codes.Arity, $"{f.Signature} takes {f.Parameters.Length} argument(s), got {e.Args.Length}");
            return Lit(e, default);
        }
        var args = new BoundExpression[e.Args.Length];
        for (int i = 0; i < args.Length; i++) args[i] = BindNode(e.Args[i], $"{at}[{i}]");

        // Generics: bind T/U from non-literal arguments first, then literals; then convert literals and check constraints.
        var generic = new ExprType?[Math.Max(f.Generics.Length, 2)];
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < args.Length; i++)
            {
                var p = f.Parameters[i].Type;
                if (p.Fixed is not null || (args[i].IsLiteral != (pass == 1)) || args[i].Type.IsNull) continue;
                ref var g = ref generic[p.Generic];
                if (g is null) g = args[i].Type with { IsNumberLiteral = false };
            }
        for (int i = 0; i < args.Length; i++)
        {
            var p = f.Parameters[i].Type;
            string argPath = $"{at}[{i}]";
            if (p.Fixed is { } fixedType)
            {
                args[i] = Coerce(args[i], fixedType, argPath);
                var t = args[i].Type;
                if (!t.IsNull && !t.SameAs(fixedType) && !(t.IsNumber && fixedType.IsNumber))
                    Error(argPath, _codes.Type, $"{f.Signature}: '{f.Parameters[i].Name}' needs {fixedType}, got {t}");
                continue;
            }
            if (generic[p.Generic] is not { } g) continue;                // all-null arguments: nothing to check
            if (!Satisfies(g, f.Generics.IsEmpty ? TypeConstraint.Any : f.Generics[p.Generic]))
                Error(argPath, _codes.Type, $"{f.Signature}: {(p.Generic == 0 ? "T" : "U")} = {g} does not satisfy {f.Generics[p.Generic]}");
            args[i] = Coerce(args[i], g, argPath);
        }
        var returns = f.Returns.Fixed ?? generic[f.Returns.Generic] ?? ExprType.Null;
        return Node(e, returns, args, function: f);
    }

    private static bool Satisfies(in ExprType t, TypeConstraint c) => c switch
    {
        TypeConstraint.Comparable => t.IsComparable,
        TypeConstraint.Numeric => t.IsNumeric,
        TypeConstraint.Amount => t.IsAmount,
        _ => true,
    };

    // ── Literal conversion ─────────────────────────────────────────────────

    /// <summary>When one side is a literal and the other isn't, the literal takes the other side's type.</summary>
    private (BoundExpression, BoundExpression) CoercePair(BoundExpression a, BoundExpression b, string pathA, string pathB)
    {
        if (a.IsLiteral && !b.IsLiteral) return (Coerce(a, b.Type, pathA), b);
        if (b.IsLiteral && !a.IsLiteral) return (a, Coerce(b, a.Type, pathB));
        return (a, b);
    }

    private BoundExpression Coerce(BoundExpression b, in ExprType target, string path)
    {
        if (!b.IsLiteral || target.IsNull || b.Type.IsNull) return b;
        if (b.Type.SameAs(target) && !b.Type.IsNumberLiteral) return b;
        var v = ConvertLiteral(b.Value, target, path);
        return new BoundExpression { Kind = ExprKind.Literal, Value = v, Type = target with { IsNumberLiteral = false }, Source = b.Source };
    }

    /// <summary>A literal as a value of <paramref name="target"/> (numbers → amounts/ints, text → enum codes/dates/app types).</summary>
    public RawValue ConvertLiteral(in RawValue literal, in ExprType target, string path)
    {
        if (literal.Kind == ValueKind.None || target.IsNull) return literal;
        if (target.IsNumeric && literal.TryGet(out decimal d) && literal.Kind is not ValueKind.Text)
        {
            if (target.IsIntegral && d != decimal.Truncate(d)) { Error(path, _codes.BadLiteral, $"{d} is not a whole number ({target})"); return literal; }
            return RawValueConvert.FromDecimal(d, target.Raw);
        }
        if (literal.Kind == ValueKind.Bool && target.IsBool) return literal;
        if (literal.Kind == ValueKind.Text && literal.TryGet(out string? text) && text is not null)
        {
            if (target.IsEnum && target.EnumOf is { } domain)
            {
                foreach (var o in domain.Options)
                    if (string.Equals(o.Code, text, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Name, text, StringComparison.OrdinalIgnoreCase))
                        return RawValue.FromParts(ValueKind.EnumCode, unchecked((ulong)o.Value), 0, o.Code, 0, 0);
                Error(path, _codes.BadLiteral, $"'{text}' is not an option of {domain.Name} ({OptionCodes(domain)})");
                return literal;
            }
            switch (target.Raw.Storage)
            {
                case ValueKind.Text: return target.Raw.IsPrimitive ? literal : literal.WithType(target.Raw);
                case ValueKind.Date when DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                    return RawValue.From(date);
                case ValueKind.Time when TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var time): return RawValue.From(time);
                case ValueKind.DateTime when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt): return RawValue.From(dt);
                case ValueKind.Guid when Guid.TryParse(text, out var g): return RawValue.From(g);
            }
        }
        Error(path, _codes.BadLiteral, $"literal {literal} is not a {target}");
        return literal;
    }

    private static string OptionCodes(ExprEnumDomain d)
    {
        var codes = new List<string>();
        foreach (var o in d.Options) codes.Add(o.Code);
        return string.Join(", ", codes);
    }

    private ExprType Unify(in ExprType a, in ExprType b, string path)
    {
        if (a.IsNull) return b;
        if (b.IsNull) return a;
        if (a.IsNumber && b.IsNumber) return a.IsIntegral && b.IsIntegral ? ExprType.Int : ExprType.Decimal;
        if (a.SameAs(b)) return a with { IsNumberLiteral = false };
        Error(path, _codes.Type, $"branches have different types: {a} and {b}");
        return a;
    }

    private BoundExpression RequireBool(BoundExpression b, string path)
    {
        if (!b.Type.IsBool && !b.Type.IsNull) Error(path, _codes.Type, $"expected a condition (bool), got {b.Type}");
        return b;
    }
}
