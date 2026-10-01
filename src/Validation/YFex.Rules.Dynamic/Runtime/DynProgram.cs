namespace YFex.Rules.Dynamic;

internal enum Op : byte
{
    Const, Field, LineField, Special,
    Add, Sub, Mul, Div, Neg,
    Eq, Ne, Lt, Le, Gt, Ge,
    Not,
    /// <summary>Top false/null → jump to A keeping it; else pop.</summary>
    AndJump,
    /// <summary>Top true → jump to A keeping it; else pop.</summary>
    OrJump,
    Jump,
    /// <summary>Pop; false/null → jump to A.</summary>
    JumpIfFalse,
    /// <summary>Top non-null → jump to A keeping it; else pop.</summary>
    JumpIfSet,
    In, Between, IsSet,
    /// <summary>A = function index, B = argument count.</summary>
    Call,
    /// <summary>A = sub-program (per line), B = aggregate kind, C = result type index.</summary>
    Agg,
}

internal readonly record struct Instr(Op Op, int A = 0, int B = 0, int C = 0);

/// <summary>
/// A compiled expression: flat postfix code over a value stack (05 §8). No delegates per node, no boxing: values are
/// <see cref="RawValue"/>s, fields are read by id through <see cref="ISituation{TSelf}.Read"/>/<c>ReadLine</c>. Immutable and
/// thread-safe (stacks are per thread).
/// </summary>
internal sealed class DynProgram<TS> where TS : ISituation<TS>
{
    [ThreadStatic] private static Stack<RawValue[]>? t_pool;

    public required Instr[] Code { get; init; }
    public required RawValue[] Consts { get; init; }
    public required RawValue[][] Sets { get; init; }
    public required RawType[] Types { get; init; }
    public required RuleFunction[] Functions { get; init; }
    public required DynProgram<TS>[] Subs { get; init; }
    public required int MaxStack { get; init; }
    public required DynType ResultType { get; init; }

    public bool RunBool(in TS s, int line, in RawValue @base, DateOnly date) => IsTrue(Run(in s, line, in @base, date));

    public RawValue Run(in TS s, int line, in RawValue @base, DateOnly date)
    {
        var pool = t_pool ??= new Stack<RawValue[]>();
        var stack = pool.Count > 0 && pool.Peek().Length >= MaxStack ? pool.Pop() : new RawValue[Math.Max(MaxStack, 8)];
        try { return Execute(in s, line, in @base, date, stack); }
        finally
        {
            Array.Clear(stack, 0, Math.Min(stack.Length, MaxStack));          // don't keep strings alive
            pool.Push(stack);
        }
    }

    private RawValue Execute(in TS s, int line, in RawValue @base, DateOnly date, RawValue[] st)
    {
        int sp = 0;
        var code = Code;
        for (int pc = 0; pc < code.Length; pc++)
        {
            var ins = code[pc];
            switch (ins.Op)
            {
                case Op.Const: st[sp++] = Consts[ins.A]; break;
                case Op.Field: st[sp++] = TS.Read(in s, (ushort)ins.A); break;
                case Op.LineField: st[sp++] = line >= 0 ? TS.ReadLine(in s, line, (ushort)ins.A) : RawValue.None; break;
                case Op.Special:
                    st[sp++] = (SpecialVar)ins.A switch
                    {
                        SpecialVar.Base => @base,
                        SpecialVar.EffectiveDate => RawValue.From(date),
                        _ => line >= 0 ? RawValue.From((long)line) : RawValue.None,
                    };
                    break;
                case Op.Add or Op.Sub or Op.Mul or Op.Div:
                    sp--;
                    st[sp - 1] = Arithmetic(ins.Op, in st[sp - 1], in st[sp], Types[ins.A]);
                    break;
                case Op.Neg:
                    st[sp - 1] = st[sp - 1].TryGet(out decimal neg) ? Amounts.ToRaw(-neg, Types[ins.A]) : RawValue.None;
                    break;
                case Op.Eq or Op.Ne or Op.Lt or Op.Le or Op.Gt or Op.Ge:
                    sp--;
                    st[sp - 1] = RawValue.From(Compare(ins.Op, in st[sp - 1], in st[sp]));
                    break;
                case Op.Not: st[sp - 1] = RawValue.From(!IsTrue(st[sp - 1])); break;
                case Op.AndJump:
                    if (!IsTrue(st[sp - 1])) { st[sp - 1] = RawValue.From(false); pc = ins.A - 1; }
                    else sp--;
                    break;
                case Op.OrJump:
                    if (IsTrue(st[sp - 1])) pc = ins.A - 1;
                    else sp--;
                    break;
                case Op.Jump: pc = ins.A - 1; break;
                case Op.JumpIfFalse:
                    if (!IsTrue(st[--sp])) pc = ins.A - 1;
                    break;
                case Op.JumpIfSet:
                    if (st[sp - 1].Kind != ValueKind.None) pc = ins.A - 1;
                    else sp--;
                    break;
                case Op.In:
                {
                    bool found = false;
                    foreach (ref readonly var v in Sets[ins.A].AsSpan())
                        if (ValueEquals(in st[sp - 1], in v)) { found = true; break; }
                    st[sp - 1] = RawValue.From(found);
                    break;
                }
                case Op.Between:
                {
                    sp -= 2;
                    ref var x = ref st[sp - 1];
                    bool inside = x.Kind != ValueKind.None && x.TryCompareTo(in st[sp], out int lo) && lo >= 0
                        && x.TryCompareTo(in st[sp + 1], out int hi) && hi <= 0;
                    x = RawValue.From(inside);
                    break;
                }
                case Op.IsSet: st[sp - 1] = RawValue.From(st[sp - 1].Kind != ValueKind.None); break;
                case Op.Call:
                {
                    int n = ins.B;
                    sp -= n;
                    var args = st.AsSpan(sp, n);
                    bool hasNull = false;
                    foreach (ref readonly var a in args) hasNull |= a.Kind == ValueKind.None;
                    st[sp++] = hasNull ? RawValue.None : Functions[ins.A].Implementation!(args);
                    break;
                }
                case Op.Agg: st[sp++] = Aggregate(in s, Subs[ins.A], (ExprKind)ins.B, Types[ins.C], in @base, date); break;
            }
        }
        return sp > 0 ? st[sp - 1] : RawValue.None;
    }

    private static RawValue Aggregate(in TS s, DynProgram<TS> sub, ExprKind kind, RawType type, in RawValue @base, DateOnly date)
    {
        int lines = TS.LineCount(in s);
        switch (kind)
        {
            case ExprKind.Sum:
            {
                decimal acc = 0m;
                for (int i = 0; i < lines; i++)
                    if (sub.Run(in s, i, in @base, date).TryGet(out decimal v)) acc += v;
                return Amounts.ToRaw(acc, type);
            }
            case ExprKind.Count:
            {
                long n = 0;
                for (int i = 0; i < lines; i++) if (sub.RunBool(in s, i, in @base, date)) n++;
                return RawValue.From(n);
            }
            case ExprKind.Any:
                for (int i = 0; i < lines; i++) if (sub.RunBool(in s, i, in @base, date)) return RawValue.From(true);
                return RawValue.From(false);
            case ExprKind.All:
                for (int i = 0; i < lines; i++) if (!sub.RunBool(in s, i, in @base, date)) return RawValue.From(false);
                return RawValue.From(true);
            default:
            {
                RawValue best = RawValue.None;
                for (int i = 0; i < lines; i++)
                {
                    var v = sub.Run(in s, i, in @base, date);
                    if (v.Kind == ValueKind.None) continue;
                    if (best.Kind == ValueKind.None || (v.TryCompareTo(in best, out int c) && (kind == ExprKind.MinOf ? c < 0 : c > 0))) best = v;
                }
                return best;
            }
        }
    }

    private static RawValue Arithmetic(Op op, in RawValue a, in RawValue b, RawType type)
    {
        if (!a.TryGet(out decimal x) || !b.TryGet(out decimal y)) return RawValue.None;
        try
        {
            decimal r = op switch
            {
                Op.Add => x + y,
                Op.Sub => x - y,
                Op.Mul => x * y,
                _ => y == 0m ? throw new DivideByZeroException() : x / y,
            };
            return Amounts.ToRaw(r, type);
        }
        catch (Exception e) when (e is DivideByZeroException or OverflowException)
        {
            return RawValue.None;                                             // total: no exception escapes a rule
        }
    }

    private static bool Compare(Op op, in RawValue a, in RawValue b)
    {
        if (op is Op.Eq or Op.Ne)
        {
            bool eq = ValueEquals(in a, in b);
            return op == Op.Eq ? eq : !eq;
        }
        if (a.Kind == ValueKind.None || b.Kind == ValueKind.None || !a.TryCompareTo(in b, out int c)) return false;
        return op switch { Op.Lt => c < 0, Op.Le => c <= 0, Op.Gt => c > 0, _ => c >= 0 };
    }

    /// <summary>Equality across numeric storages (Int32 field vs Int64 arithmetic) by value; otherwise exact.</summary>
    internal static bool ValueEquals(in RawValue a, in RawValue b)
    {
        if (a.Kind == ValueKind.None || b.Kind == ValueKind.None) return a.Kind == b.Kind;
        if (a.Kind != b.Kind && a.Type is { IsNumeric: true } && b.Type is { IsNumeric: true })
            return a.TryCompareTo(in b, out int c) && c == 0;
        return a.Equals(b);
    }

    internal static bool IsTrue(in RawValue v) => v.Kind == ValueKind.Bool && v.TryGet(out bool b) && b;
}
