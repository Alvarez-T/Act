namespace YFex.Expressions;

/// <summary>
/// What a program reads. Implement it on a <b>struct or ref struct</b>: <see cref="ExprProgram.Run{TReader}"/> is generic over the
/// reader, so the JIT/AOT compiler emits one specialized copy per reader type — direct calls, no boxing, no virtual dispatch.
/// Ids come from the <see cref="ExprSchema"/> the program was bound against.
/// </summary>
public interface IFieldReader
{
    /// <summary>Top-level field <paramref name="fieldId"/>; <see cref="RawValue.None"/> when absent/null.</summary>
    RawValue Read(ushort fieldId);

    /// <summary>Number of items in collection <paramref name="collectionId"/>.</summary>
    int Count(ushort collectionId);

    /// <summary>Field <paramref name="fieldId"/> (an id of the collection's item schema) of item <paramref name="index"/>.</summary>
    RawValue ReadItem(ushort collectionId, int index, ushort fieldId);
}

internal enum Op : byte
{
    /// <summary>Push Consts[A].</summary>
    Const,
    /// <summary>Push reader.Read(A).</summary>
    Field,
    /// <summary>Push reader.ReadItem(B, item, A) — null outside per-item evaluation.</summary>
    ItemField,
    /// <summary>Push env[A]; A = <see cref="ExprSpecial.ItemIndexSlot"/> → the current item index.</summary>
    Special,
    /// <summary>Arithmetic in decimal; result typed as Types[A].</summary>
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
    /// <summary>A = sub-program (per item), B = aggregate kind, C = result type index, D = collection id.</summary>
    Agg,
}

internal readonly record struct Instr(Op Op, int A = 0, int B = 0, int C = 0, int D = 0);

/// <summary>
/// A compiled expression: flat postfix code over a value stack. No delegates per node, no boxing: values are
/// <see cref="RawValue"/>s, fields are read by id through <see cref="IFieldReader"/>. Immutable and thread-safe (value stacks
/// are pooled per thread). Total: division by zero and overflow give null; a call with a null argument gives null.
/// </summary>
public sealed class ExprProgram
{
    [ThreadStatic] private static Stack<RawValue[]>? t_pool;

    internal Instr[] Code { get; init; } = [];
    internal RawValue[] Consts { get; init; } = [];
    internal RawValue[][] Sets { get; init; } = [];
    internal RawType[] Types { get; init; } = [];
    internal ExprFunction[] Functions { get; init; } = [];
    internal ExprProgram[] Subs { get; init; } = [];
    internal int MaxStack { get; init; }

    /// <summary>Static type of the result.</summary>
    public required ExprType ResultType { get; init; }

    /// <summary>
    /// −1 = evaluates once; otherwise the id of the collection it reads per item outside any aggregate — run it once per item
    /// with <c>item</c> = the index (Rules: a per-line rule; Composer: a computed grid column).
    /// </summary>
    public required int ItemCollection { get; init; }

    /// <summary>Evaluates the expression. <paramref name="item"/>: current item index for per-item programs, else −1.</summary>
    public RawValue Run<TReader>(ref TReader reader, in ExprEnv env, int item = -1) where TReader : IFieldReader, allows ref struct
    {
        var pool = t_pool ??= new Stack<RawValue[]>();
        var stack = pool.Count > 0 && pool.Peek().Length >= MaxStack ? pool.Pop() : new RawValue[Math.Max(MaxStack, 8)];
        try { return Execute(ref reader, in env, item, stack); }
        finally
        {
            Array.Clear(stack, 0, Math.Min(stack.Length, MaxStack));          // don't keep strings alive
            pool.Push(stack);
        }
    }

    /// <summary>True only when the result is the boolean <c>true</c> (null and non-bool → false).</summary>
    public bool RunBool<TReader>(ref TReader reader, in ExprEnv env, int item = -1) where TReader : IFieldReader, allows ref struct
        => IsTrue(Run(ref reader, in env, item));

    private RawValue Execute<TReader>(ref TReader r, in ExprEnv env, int item, RawValue[] st) where TReader : IFieldReader, allows ref struct
    {
        int sp = 0;
        var code = Code;
        for (int pc = 0; pc < code.Length; pc++)
        {
            var ins = code[pc];
            switch (ins.Op)
            {
                case Op.Const: st[sp++] = Consts[ins.A]; break;
                case Op.Field: st[sp++] = r.Read((ushort)ins.A); break;
                case Op.ItemField: st[sp++] = item >= 0 ? r.ReadItem((ushort)ins.B, item, (ushort)ins.A) : RawValue.None; break;
                case Op.Special:
                    st[sp++] = ins.A == ExprSpecial.ItemIndexSlot
                        ? item >= 0 ? RawValue.From((long)item) : RawValue.None
                        : env[ins.A];
                    break;
                case Op.Add or Op.Sub or Op.Mul or Op.Div:
                    sp--;
                    st[sp - 1] = Arithmetic(ins.Op, in st[sp - 1], in st[sp], Types[ins.A]);
                    break;
                case Op.Neg:
                    st[sp - 1] = st[sp - 1].TryGet(out decimal neg) ? RawValueConvert.FromDecimal(-neg, Types[ins.A]) : RawValue.None;
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
                case Op.Agg: st[sp++] = Aggregate(ref r, in env, Subs[ins.A], (ExprKind)ins.B, Types[ins.C], (ushort)ins.D); break;
            }
        }
        return sp > 0 ? st[sp - 1] : RawValue.None;
    }

    private static RawValue Aggregate<TReader>(ref TReader r, in ExprEnv env, ExprProgram sub, ExprKind kind, RawType type, ushort collection)
        where TReader : IFieldReader, allows ref struct
    {
        int items = r.Count(collection);
        switch (kind)
        {
            case ExprKind.Sum:
            {
                decimal acc = 0m;
                for (int i = 0; i < items; i++)
                    if (sub.Run(ref r, in env, i).TryGet(out decimal v)) acc += v;
                return RawValueConvert.FromDecimal(acc, type);
            }
            case ExprKind.Count:
            {
                long n = 0;
                for (int i = 0; i < items; i++) if (sub.RunBool(ref r, in env, i)) n++;
                return RawValue.From(n);
            }
            case ExprKind.Any:
                for (int i = 0; i < items; i++) if (sub.RunBool(ref r, in env, i)) return RawValue.From(true);
                return RawValue.From(false);
            case ExprKind.All:
                for (int i = 0; i < items; i++) if (!sub.RunBool(ref r, in env, i)) return RawValue.From(false);
                return RawValue.From(true);
            default:
            {
                RawValue best = RawValue.None;
                for (int i = 0; i < items; i++)
                {
                    var v = sub.Run(ref r, in env, i);
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
            decimal result = op switch
            {
                Op.Add => x + y,
                Op.Sub => x - y,
                Op.Mul => x * y,
                _ => y == 0m ? throw new DivideByZeroException() : x / y,
            };
            return RawValueConvert.FromDecimal(result, type);
        }
        catch (Exception e) when (e is DivideByZeroException or OverflowException)
        {
            return RawValue.None;                                             // total: no exception escapes an expression
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
