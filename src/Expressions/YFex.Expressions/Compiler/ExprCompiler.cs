using YFex.Validation;

namespace YFex.Expressions;

/// <summary>
/// Entry points: <see cref="Compile"/> binds + checks + emits one expression; <see cref="Emit"/> turns a tree already bound with an
/// <see cref="ExprBinder"/> (hosts binding several expressions of one document) into an <see cref="ExprProgram"/>.
/// </summary>
public static class ExprCompiler
{
    /// <summary>
    /// Binds, type-checks and compiles <paramref name="expr"/>. Problems go to <paramref name="errors"/> (paths under
    /// <paramref name="path"/>) and the result is null. <paramref name="expected"/>: required result type (literals convert).
    /// </summary>
    public static ExprProgram? Compile(Expr expr, string path, ExprCompileOptions options, List<Violation> errors, ExprType? expected = null)
    {
        int before = errors.Count;
        var binder = new ExprBinder(options, errors);
        if (!binder.CheckSize(expr, path)) return null;
        var bound = expected is { } t ? binder.Bind(expr, path, t) : binder.Bind(expr, path);
        return errors.Count > before ? null : Emit(bound);
    }

    /// <summary>Flattens a bound tree (postfix, short-circuit jumps, per-item sub-programs for aggregates).</summary>
    public static ExprProgram Emit(BoundExpression root) => Emitter.Compile(root);

    private sealed class Emitter
    {
        private readonly List<Instr> _code = [];
        private readonly List<RawValue> _consts = [];
        private readonly List<RawValue[]> _sets = [];
        private readonly List<RawType> _types = [];
        private readonly List<ExprFunction> _functions = [];
        private readonly List<ExprProgram> _subs = [];
        private int _depth, _maxDepth;

        public static ExprProgram Compile(BoundExpression root, int itemCollection = int.MinValue)
        {
            var e = new Emitter();
            e.Emit(root);
            return new ExprProgram
            {
                Code = [.. e._code],
                Consts = [.. e._consts],
                Sets = [.. e._sets],
                Types = [.. e._types],
                Functions = [.. e._functions],
                Subs = [.. e._subs],
                MaxStack = Math.Max(e._maxDepth, 1),
                ResultType = root.Type,
                ItemCollection = itemCollection == int.MinValue ? root.ItemCollection : itemCollection,
            };
        }

        private void Push(int n = 1) { _depth += n; _maxDepth = Math.Max(_maxDepth, _depth); }
        private void Pop(int n = 1) => _depth -= n;
        private int Add(Instr i) { _code.Add(i); return _code.Count - 1; }
        private void Patch(int at, int target) => _code[at] = _code[at] with { A = target };

        private int TypeIndex(in ExprType t)
        {
            var raw = t.IsNull ? RawType.Decimal : t.Raw;
            int i = _types.IndexOf(raw);
            if (i < 0) { _types.Add(raw); i = _types.Count - 1; }
            return i;
        }

        private void Emit(BoundExpression b)
        {
            switch (b.Kind)
            {
                case ExprKind.Literal:
                    _consts.Add(b.Value);
                    Add(new Instr(Op.Const, _consts.Count - 1));
                    Push();
                    return;
                case ExprKind.Var:
                    Add(b.IsSpecial ? new Instr(Op.Special, b.SpecialSlot) : new Instr(Op.Field, b.FieldId));
                    Push();
                    return;
                case ExprKind.Line:
                    Add(new Instr(Op.ItemField, b.FieldId, b.CollectionId));
                    Push();
                    return;
                case ExprKind.Add or ExprKind.Subtract or ExprKind.Multiply or ExprKind.Divide:
                    Emit(b.Args[0]);
                    Emit(b.Args[1]);
                    Add(new Instr(b.Kind switch { ExprKind.Add => Op.Add, ExprKind.Subtract => Op.Sub, ExprKind.Multiply => Op.Mul, _ => Op.Div }, TypeIndex(b.Type)));
                    Pop();
                    return;
                case ExprKind.Negate:
                    Emit(b.Args[0]);
                    Add(new Instr(Op.Neg, TypeIndex(b.Type)));
                    return;
                case ExprKind.Equal or ExprKind.NotEqual or ExprKind.Less or ExprKind.LessOrEqual or ExprKind.Greater or ExprKind.GreaterOrEqual:
                    Emit(b.Args[0]);
                    Emit(b.Args[1]);
                    Add(new Instr(b.Kind switch
                    {
                        ExprKind.Equal => Op.Eq, ExprKind.NotEqual => Op.Ne, ExprKind.Less => Op.Lt,
                        ExprKind.LessOrEqual => Op.Le, ExprKind.Greater => Op.Gt, _ => Op.Ge,
                    }));
                    Pop();
                    return;
                case ExprKind.And or ExprKind.Or:
                {
                    var jumps = new List<int>();
                    for (int i = 0; i < b.Args.Length; i++)
                    {
                        Emit(b.Args[i]);
                        if (i < b.Args.Length - 1)
                        {
                            jumps.Add(Add(new Instr(b.Kind == ExprKind.And ? Op.AndJump : Op.OrJump)));
                            Pop();                                               // continuing path popped it
                        }
                    }
                    foreach (int j in jumps) Patch(j, _code.Count);
                    return;
                }
                case ExprKind.Not:
                    Emit(b.Args[0]);
                    Add(new Instr(Op.Not));
                    return;
                case ExprKind.IsSet:
                    Emit(b.Args[0]);
                    Add(new Instr(Op.IsSet));
                    return;
                case ExprKind.In:
                    Emit(b.Args[0]);
                    _sets.Add(b.Set);
                    Add(new Instr(Op.In, _sets.Count - 1));
                    return;
                case ExprKind.Between:
                    Emit(b.Args[0]);
                    Emit(b.Args[1]);
                    Emit(b.Args[2]);
                    Add(new Instr(Op.Between));
                    Pop(2);
                    return;
                case ExprKind.If:
                {
                    Emit(b.Args[0]);
                    int toElse = Add(new Instr(Op.JumpIfFalse));
                    Pop();
                    Emit(b.Args[1]);
                    int toEnd = Add(new Instr(Op.Jump));
                    Pop();
                    Patch(toElse, _code.Count);
                    Emit(b.Args[2]);
                    Patch(toEnd, _code.Count);
                    return;
                }
                case ExprKind.Coalesce:
                {
                    var jumps = new List<int>();
                    for (int i = 0; i < b.Args.Length; i++)
                    {
                        Emit(b.Args[i]);
                        if (i < b.Args.Length - 1) { jumps.Add(Add(new Instr(Op.JumpIfSet))); Pop(); }
                    }
                    foreach (int j in jumps) Patch(j, _code.Count);
                    return;
                }
                case ExprKind.Sum or ExprKind.Count or ExprKind.Any or ExprKind.All or ExprKind.MinOf or ExprKind.MaxOf:
                    _subs.Add(Compile(b.Args[0], b.CollectionId));
                    Add(new Instr(Op.Agg, _subs.Count - 1, (int)b.Kind, TypeIndex(b.Type), b.CollectionId));
                    Push();
                    return;
                case ExprKind.Call:
                    foreach (var a in b.Args) Emit(a);
                    _functions.Add(b.Function!);
                    Add(new Instr(Op.Call, _functions.Count - 1, b.Args.Length));
                    Pop(b.Args.Length);
                    Push();
                    return;
                default:
                    throw new InvalidOperationException($"Cannot compile {b.Kind}.");
            }
        }
    }
}
