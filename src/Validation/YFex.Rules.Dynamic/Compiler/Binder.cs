using System.Collections.Immutable;
using System.Globalization;
using YFex.Validation;

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
    public const string Arity = "rules.dynamic.arity";
    public const string Type = "rules.dynamic.type";
    public const string BadLiteral = "rules.dynamic.literal";
    public const string NoAmount = "rules.dynamic.no-amount";
    public const string TooLarge = "rules.dynamic.too-large";
    public const string Missing = "rules.dynamic.missing";
}

/// <summary>Special variables (<c>{"var":"$base"}</c>).</summary>
internal enum SpecialVar : byte { None, Base, EffectiveDate, LineIndex }

/// <summary>Type-checked node (literals already converted to the type they are compared/combined with).</summary>
internal sealed class Bound
{
    public required ExprKind Kind { get; init; }
    public required DynType Type { get; init; }
    public RawValue Value { get; init; }
    public ushort FieldId { get; init; }
    public SpecialVar Special { get; init; }
    public RuleFunction? Function { get; init; }
    public Bound[] Args { get; init; } = [];
    public RawValue[] Set { get; init; } = [];
    /// <summary>The spec node (condition texts, trace).</summary>
    public required Expr Source { get; init; }
    public bool IsLiteral => Kind == ExprKind.Literal;
}

/// <summary>
/// Binds a spec against the situation schema, the rule set (groups, policies, amount type) and the function catalog:
/// resolves names, infers and checks every type, converts literals (<c>"Pix"</c> → the enum value, <c>500</c> → Money 500 when
/// compared with a Money field) and reports problems with the JSON path of the offending node.
/// </summary>
internal sealed class Binder<TS> where TS : ISituation<TS>
{
    public const int MaxNodes = 500, MaxDepth = 32, MaxDirectives = 20;

    private readonly RuleSetDefinition<TS> _set;
    private readonly FunctionCatalog _functions;
    private readonly List<Violation> _errors;
    private readonly SituationSchema _schema = TS.Schema;
    private bool _inAggregate;

    public Binder(RuleSetDefinition<TS> set, FunctionCatalog functions, List<Violation> errors)
    {
        _set = set;
        _functions = functions;
        _errors = errors;
    }

    /// <summary>True when an expression read a line field outside an aggregate → the rule runs once per line.</summary>
    public bool UsesLine { get; private set; }

    public bool HasErrors => _errors.Count > 0;

    public void Error(string path, string code, string detail)
        => _errors.Add(new Violation(FieldPath.Of(path), code, Severity.Error, detail: detail));

    // ── Expressions ─────────────────────────────────────────────────────────

    public Bound Bind(Expr e, string path)
    {
        switch (e.Kind)
        {
            case ExprKind.Literal: return Lit(e, e.Literal);
            case ExprKind.Var: return BindVar(e, path);
            case ExprKind.Line: return BindLine(e, path);
            case ExprKind.Add or ExprKind.Subtract or ExprKind.Multiply or ExprKind.Divide: return BindArithmetic(e, path);
            case ExprKind.Negate:
            {
                var x = Bind(e.Args[0], path + ".neg");
                if (!x.Type.IsNumeric && !x.Type.IsNull) Error(path, DynamicRuleCodes.Type, $"neg needs a number, got {x.Type}");
                return new Bound { Kind = e.Kind, Type = x.Type, Args = [x], Source = e };
            }
            case ExprKind.Equal or ExprKind.NotEqual or ExprKind.Less or ExprKind.LessOrEqual or ExprKind.Greater or ExprKind.GreaterOrEqual:
                return BindCompare(e, path);
            case ExprKind.And or ExprKind.Or:
            {
                if (e.Args.Length < 1) Error(path, DynamicRuleCodes.Arity, "and/or need at least one operand");
                var args = new Bound[e.Args.Length];
                for (int i = 0; i < args.Length; i++) args[i] = RequireBool(Bind(e.Args[i], $"{path}.{Name(e.Kind)}[{i}]"), $"{path}.{Name(e.Kind)}[{i}]");
                return new Bound { Kind = e.Kind, Type = DynType.Bool, Args = args, Source = e };
            }
            case ExprKind.Not:
                return new Bound { Kind = e.Kind, Type = DynType.Bool, Args = [RequireBool(Bind(e.Args[0], path + ".not"), path + ".not")], Source = e };
            case ExprKind.IsSet:
                return new Bound { Kind = e.Kind, Type = DynType.Bool, Args = [Bind(e.Args[0], path + ".isSet")], Source = e };
            case ExprKind.In: return BindIn(e, path);
            case ExprKind.Between: return BindBetween(e, path);
            case ExprKind.If: return BindIf(e, path);
            case ExprKind.Coalesce: return BindCoalesce(e, path);
            case ExprKind.Sum or ExprKind.Count or ExprKind.Any or ExprKind.All or ExprKind.MinOf or ExprKind.MaxOf: return BindAggregate(e, path);
            case ExprKind.Call: return BindCall(e, path);
            default:
                Error(path, DynamicRuleCodes.Malformed, $"unsupported node {e.Kind}");
                return Lit(e, RawValue.None);
        }
    }

    private static string Name(ExprKind k) => RuleSpecJson.OperatorName(k);

    private static Bound Lit(Expr e, in RawValue v) => new()
    {
        Kind = ExprKind.Literal,
        Value = v,
        Type = v.Kind switch
        {
            ValueKind.None => DynType.Null,
            ValueKind.Bool => DynType.Bool,
            ValueKind.Int32 or ValueKind.Int64 => DynType.Int with { IsNumberLiteral = true },
            ValueKind.Decimal or ValueKind.Double => DynType.Decimal with { IsNumberLiteral = true },
            ValueKind.Text => DynType.Text,
            _ => new DynType(v.Type ?? RawType.Text),
        },
        Source = e,
    };

    private Bound BindVar(Expr e, string path)
    {
        string name = e.Name!;
        if (name.StartsWith('$'))
        {
            switch (name)
            {
                case "$base":
                    if (_set.AmountType is not { } amount) { Error(path, DynamicRuleCodes.NoAmount, $"rule set '{_set.Name}' has no amount ($base)"); return Lit(e, default); }
                    return new Bound { Kind = ExprKind.Var, Special = SpecialVar.Base, Type = new DynType(amount), Source = e };
                case "$effectiveDate":
                    return new Bound { Kind = ExprKind.Var, Special = SpecialVar.EffectiveDate, Type = DynType.Date, Source = e };
                case "$lineIndex":
                    if (!_inAggregate) UsesLine = true;
                    return new Bound { Kind = ExprKind.Var, Special = SpecialVar.LineIndex, Type = DynType.Int, Source = e };
                default:
                    Error(path, DynamicRuleCodes.UnknownField, $"unknown special variable '{name}' ($base, $effectiveDate, $lineIndex)");
                    return Lit(e, default);
            }
        }
        if (!_schema.TryGetField(name, out var field))
        {
            Error(path, DynamicRuleCodes.UnknownField, $"'{name}' is not a field of situation '{_schema.Name}'");
            return Lit(e, default);
        }
        if (!field.Exposed) Error(path, DynamicRuleCodes.FieldNotExposed, $"'{name}' is not exposed to runtime rules");
        if (field.Role == FieldRole.Lines) Error(path, DynamicRuleCodes.Type, $"'{name}' is the line list — read line fields with {{\"line\": …}} inside sum/count/any/all/minOf/maxOf");
        return new Bound { Kind = ExprKind.Var, FieldId = field.Id, Type = TypeOf(field), Source = e };
    }

    private Bound BindLine(Expr e, string path)
    {
        if (_schema.LineSchema is not { } lines)
        {
            Error(path, DynamicRuleCodes.NoLines, $"situation '{_schema.Name}' has no lines");
            return Lit(e, default);
        }
        if (!lines.TryGetField(e.Name!, out var field))
        {
            Error(path, DynamicRuleCodes.UnknownField, $"'{e.Name}' is not a line field of '{lines.Name}'");
            return Lit(e, default);
        }
        if (!field.Exposed) Error(path, DynamicRuleCodes.FieldNotExposed, $"line field '{e.Name}' is not exposed to runtime rules");
        if (!_inAggregate) UsesLine = true;
        return new Bound { Kind = ExprKind.Line, FieldId = field.Id, Type = TypeOf(field), Source = e };
    }

    private static DynType TypeOf(SituationFieldInfo f) => new(f.Type, f.Kind == ValueKind.EnumCode ? f : null);

    private Bound BindArithmetic(Expr e, string path)
    {
        if (e.Args.Length < 2) { Error(path, DynamicRuleCodes.Arity, $"{Name(e.Kind)} needs at least two operands"); return Lit(e, default); }
        string first = $"{path}.{Name(e.Kind)}[0]";
        var acc = Bind(e.Args[0], first);
        for (int i = 1; i < e.Args.Length; i++)
        {
            string at = $"{path}.{Name(e.Kind)}[{i}]";
            var right = Bind(e.Args[i], at);
            var (l, r) = CoercePair(acc, right, i == 1 ? first : $"{path}.{Name(e.Kind)}", at);
            var type = ArithmeticType(e.Kind, l.Type, r.Type, at);
            acc = new Bound { Kind = e.Kind, Type = type, Args = [l, r], Source = e };
        }
        return acc;
    }

    private DynType ArithmeticType(ExprKind op, in DynType a, in DynType b, string path)
    {
        if (a.IsNull || b.IsNull) return a.IsNull ? b : a;
        if (!a.IsNumeric || !b.IsNumeric)
        {
            Error(path, DynamicRuleCodes.Type, $"{Name(op)} needs numbers or amounts, got {a} and {b}");
            return DynType.Decimal;
        }
        if (a.IsNumber && b.IsNumber)
            return op == ExprKind.Divide || !a.IsIntegral || !b.IsIntegral ? DynType.Decimal : DynType.Int;
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
                if (a.IsAmount && a.SameAs(b)) return DynType.Decimal;          // ratio
                break;
        }
        Error(path, DynamicRuleCodes.Type, $"{Name(op)} of {a} and {b} is not defined (amounts: + and - need the same type; × and ÷ by a number)");
        return a;
    }

    private Bound BindCompare(Expr e, string path)
    {
        if (e.Args.Length != 2) { Error(path, DynamicRuleCodes.Arity, $"{Name(e.Kind)} needs two operands"); return Lit(e, RawValue.From(false)); }
        string at = $"{path}.{Name(e.Kind)}";
        var (l, r) = CoercePair(Bind(e.Args[0], at + "[0]"), Bind(e.Args[1], at + "[1]"), at + "[0]", at + "[1]");
        bool equality = e.Kind is ExprKind.Equal or ExprKind.NotEqual;
        if (!l.Type.IsNull && !r.Type.IsNull)
        {
            bool numeric = l.Type.IsNumeric && r.Type.IsNumeric && (l.Type.IsNumber && r.Type.IsNumber || l.Type.SameAs(r.Type));
            if (!numeric && !l.Type.SameAs(r.Type)) Error(at, DynamicRuleCodes.Type, $"cannot compare {l.Type} with {r.Type}");
            else if (!equality && !l.Type.IsComparable) Error(at, DynamicRuleCodes.Type, $"{l.Type} has no order (use == / !=)");
        }
        else if (!equality) Error(at, DynamicRuleCodes.Type, "null has no order (use isSet)");
        return new Bound { Kind = e.Kind, Type = DynType.Bool, Args = [l, r], Source = e };
    }

    private Bound BindIn(Expr e, string path)
    {
        var value = Bind(e.Args[0], path + ".in[0]");
        var set = new RawValue[e.List.Length];
        for (int i = 0; i < set.Length; i++)
            set[i] = ConvertLiteral(e.List[i], value.Type, $"{path}.in[1][{i}]");
        return new Bound { Kind = ExprKind.In, Type = DynType.Bool, Args = [value], Set = set, Source = e };
    }

    private Bound BindBetween(Expr e, string path)
    {
        if (e.Args.Length != 3) { Error(path, DynamicRuleCodes.Arity, "between needs [value, low, high]"); return Lit(e, RawValue.From(false)); }
        var v = Bind(e.Args[0], path + ".between[0]");
        var lo = Coerce(Bind(e.Args[1], path + ".between[1]"), v.Type, path + ".between[1]");
        var hi = Coerce(Bind(e.Args[2], path + ".between[2]"), v.Type, path + ".between[2]");
        if (!v.Type.IsComparable) Error(path, DynamicRuleCodes.Type, $"{v.Type} has no order");
        return new Bound { Kind = ExprKind.Between, Type = DynType.Bool, Args = [v, lo, hi], Source = e };
    }

    private Bound BindIf(Expr e, string path)
    {
        if (e.Args.Length != 3) { Error(path, DynamicRuleCodes.Arity, "if needs [condition, then, else]"); return Lit(e, default); }
        var c = RequireBool(Bind(e.Args[0], path + ".if[0]"), path + ".if[0]");
        var (a, b) = CoercePair(Bind(e.Args[1], path + ".if[1]"), Bind(e.Args[2], path + ".if[2]"), path + ".if[1]", path + ".if[2]");
        var type = Unify(a.Type, b.Type, path + ".if");
        return new Bound { Kind = ExprKind.If, Type = type, Args = [c, a, b], Source = e };
    }

    private Bound BindCoalesce(Expr e, string path)
    {
        if (e.Args.Length < 2) { Error(path, DynamicRuleCodes.Arity, "coalesce needs at least two operands"); return Lit(e, default); }
        var args = new Bound[e.Args.Length];
        for (int i = 0; i < args.Length; i++) args[i] = Bind(e.Args[i], $"{path}.coalesce[{i}]");
        // The non-literal operands decide the type (literals then convert to it); all literals → their unified type.
        var type = DynType.Null;
        foreach (var a in args) if (!a.IsLiteral) type = Unify(type, a.Type, path + ".coalesce");
        if (type.IsNull) foreach (var a in args) type = Unify(type, a.Type, path + ".coalesce");
        for (int i = 0; i < args.Length; i++) args[i] = Coerce(args[i], type, $"{path}.coalesce[{i}]");
        return new Bound { Kind = ExprKind.Coalesce, Type = type, Args = args, Source = e };
    }

    private Bound BindAggregate(Expr e, string path)
    {
        string at = path + "." + Name(e.Kind);
        if (_schema.LineSchema is null) { Error(at, DynamicRuleCodes.NoLines, $"situation '{_schema.Name}' has no lines to aggregate"); return Lit(e, default); }
        if (_inAggregate) { Error(at, DynamicRuleCodes.NestedAggregate, "aggregates cannot be nested"); return Lit(e, default); }
        _inAggregate = true;
        var x = Bind(e.Args[0], at);
        _inAggregate = false;
        DynType type;
        switch (e.Kind)
        {
            case ExprKind.Sum:
                if (!x.Type.IsNumeric) Error(at, DynamicRuleCodes.Type, $"sum needs a number or amount per line, got {x.Type}");
                type = x.Type with { IsNumberLiteral = false };
                break;
            case ExprKind.MinOf or ExprKind.MaxOf:
                if (!x.Type.IsComparable) Error(at, DynamicRuleCodes.Type, $"{Name(e.Kind)} needs comparable values, got {x.Type}");
                type = x.Type with { IsNumberLiteral = false };
                break;
            case ExprKind.Count:
                RequireBool(x, at);
                type = DynType.Int;
                break;
            default:
                RequireBool(x, at);
                type = DynType.Bool;
                break;
        }
        return new Bound { Kind = e.Kind, Type = type, Args = [x], Source = e };
    }

    private Bound BindCall(Expr e, string path)
    {
        string at = path + "." + e.Name;
        if (!_functions.TryGet(e.Name!, out var f))
        {
            Error(at, DynamicRuleCodes.UnknownFunction, $"unknown function '{e.Name}'");
            return Lit(e, default);
        }
        if (f.IsContract) Error(at, DynamicRuleCodes.Unimplemented, $"function '{f.Name}' is declared but not implemented by this application");
        if (e.Args.Length != f.Parameters.Length)
        {
            Error(at, DynamicRuleCodes.Arity, $"{f.Signature} takes {f.Parameters.Length} argument(s), got {e.Args.Length}");
            return Lit(e, default);
        }
        var args = new Bound[e.Args.Length];
        for (int i = 0; i < args.Length; i++) args[i] = Bind(e.Args[i], $"{at}[{i}]");

        // Generics: bind T/U from non-literal arguments first, then literals; then convert literals and check constraints.
        var generic = new DynType?[Math.Max(f.Generics.Length, 2)];
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
                    Error(argPath, DynamicRuleCodes.Type, $"{f.Signature}: '{f.Parameters[i].Name}' needs {fixedType}, got {t}");
                continue;
            }
            if (generic[p.Generic] is not { } g) continue;                // all-null arguments: nothing to check
            if (!Satisfies(g, f.Generics.IsEmpty ? TypeConstraint.Any : f.Generics[p.Generic]))
                Error(argPath, DynamicRuleCodes.Type, $"{f.Signature}: {(p.Generic == 0 ? "T" : "U")} = {g} does not satisfy {f.Generics[p.Generic]}");
            args[i] = Coerce(args[i], g, argPath);
        }
        var returns = f.Returns.Fixed ?? generic[f.Returns.Generic] ?? DynType.Null;
        return new Bound { Kind = ExprKind.Call, Type = returns, Function = f, Args = args, Source = e };
    }

    private static bool Satisfies(in DynType t, TypeConstraint c) => c switch
    {
        TypeConstraint.Comparable => t.IsComparable,
        TypeConstraint.Numeric => t.IsNumeric,
        TypeConstraint.Amount => t.IsAmount,
        _ => true,
    };

    // ── Literal conversion ─────────────────────────────────────────────────

    /// <summary>When one side is a literal and the other isn't, the literal takes the other side's type.</summary>
    private (Bound, Bound) CoercePair(Bound a, Bound b, string pathA, string pathB)
    {
        if (a.IsLiteral && !b.IsLiteral) return (Coerce(a, b.Type, pathA), b);
        if (b.IsLiteral && !a.IsLiteral) return (a, Coerce(b, a.Type, pathB));
        return (a, b);
    }

    private Bound Coerce(Bound b, in DynType target, string path)
    {
        if (!b.IsLiteral || target.IsNull || b.Type.IsNull) return b;
        if (b.Type.SameAs(target) && !b.Type.IsNumberLiteral) return b;
        var v = ConvertLiteral(b.Value, target, path);
        return new Bound { Kind = ExprKind.Literal, Value = v, Type = target with { IsNumberLiteral = false }, Source = b.Source };
    }

    /// <summary>A literal as a value of <paramref name="target"/> (numbers → amounts/ints, text → enum codes/dates/app types).</summary>
    public RawValue ConvertLiteral(in RawValue literal, in DynType target, string path)
    {
        if (literal.Kind == ValueKind.None || target.IsNull) return literal;
        if (target.IsNumeric && literal.TryGet(out decimal d) && literal.Kind is not ValueKind.Text)
        {
            if (target.IsIntegral && d != decimal.Truncate(d)) { Error(path, DynamicRuleCodes.BadLiteral, $"{d} is not a whole number ({target})"); return literal; }
            return Amounts.ToRaw(d, target.Raw);
        }
        if (literal.Kind == ValueKind.Bool && target.IsBool) return literal;
        if (literal.Kind == ValueKind.Text && literal.TryGet(out string? text) && text is not null)
        {
            if (target.IsEnum && target.EnumOf is { } field)
            {
                foreach (var o in field.EnumOptions)
                    if (string.Equals(o.Code, text, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Name, text, StringComparison.OrdinalIgnoreCase))
                        return RawValue.FromParts(ValueKind.EnumCode, unchecked((ulong)o.Value), 0, o.Code, 0, 0);
                Error(path, DynamicRuleCodes.BadLiteral, $"'{text}' is not an option of {field.Name} ({Options(field)})");
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
        Error(path, DynamicRuleCodes.BadLiteral, $"literal {literal} is not a {target}");
        return literal;
    }

    private static string Options(SituationFieldInfo f)
    {
        var codes = new List<string>();
        foreach (var o in f.EnumOptions) codes.Add(o.Code);
        return string.Join(", ", codes);
    }

    private DynType Unify(in DynType a, in DynType b, string path)
    {
        if (a.IsNull) return b;
        if (b.IsNull) return a;
        if (a.IsNumber && b.IsNumber) return a.IsIntegral && b.IsIntegral ? DynType.Int : DynType.Decimal;
        if (a.SameAs(b)) return a with { IsNumberLiteral = false };
        Error(path, DynamicRuleCodes.Type, $"branches have different types: {a} and {b}");
        return a;
    }

    private Bound RequireBool(Bound b, string path)
    {
        if (!b.Type.IsBool && !b.Type.IsNull) Error(path, DynamicRuleCodes.Type, $"expected a condition (bool), got {b.Type}");
        return b;
    }
}
