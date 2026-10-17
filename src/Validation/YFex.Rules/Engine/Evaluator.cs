using System.Buffers;
using System.Security.Cryptography;
using YFex.Cryptography;
using YFex.Helpers;
using YFex.Validation;
using static YFex.Rules.EvalState;
using YFex.Authorization.Public;

namespace YFex.Rules;

/// <summary>
/// The normative evaluation algorithm (04 §7). Pure, synchronous, deterministic: stable iteration orders (stage,
/// group declaration, priority desc, code ordinal), no hash-set iteration, decimal arithmetic on the rule set's amount
/// (whatever its type), one rounding policy.
/// Scratch state is pooled per thread; the only allocations are the Decision and its exact-size arrays (+ trace).
/// </summary>
internal static class Evaluator<TSituation> where TSituation : ISituation<TSituation>
{
    private const byte StatePending = 0, StateApplied = 1, StateSuperseded = 2, StateCapped = 3, StateBlocked = 4;

    public static Decision<TSituation> Evaluate(CompiledRuleSet<TSituation> set, in TSituation s, in EvaluateOptions options)
    {
        var scratch = EvaluationScratch.Rent();
        try
        {
            var run = new Run(set, scratch, options.Trace);
            return run.Execute(in s);
        }
        finally
        {
            scratch.Return();
        }
    }

    private sealed class Run(CompiledRuleSet<TSituation> set, EvaluationScratch scratch, TraceLevel trace)
    {
        private readonly RuleSetDefinition<TSituation> _def = set.Definition;
        private readonly List<Candidate> _candidates = scratch.List<Candidate>(0);
        private readonly List<LimitState> _limits = scratch.List<LimitState>(1);
        private readonly List<LimitState> _groupLimits = scratch.List<LimitState>(2);
        private readonly List<AppliedAdjustment> _adjustments = scratch.List<AppliedAdjustment>(3);
        private readonly List<Violation> _violations = scratch.List<Violation>(4);
        private readonly List<ApprovalRequest> _approvals = scratch.List<ApprovalRequest>(5);
        private readonly List<PendingTrace> _notes = scratch.List<PendingTrace>(6);
        private readonly List<int> _partition = scratch.List<int>(7);
        private readonly DirectiveBuffer _buffer = scratch.Directives;
        private readonly RawType? _amountType = set.Definition.AmountType;
        private readonly RoundingPolicy _rounding = set.Definition.Rounding;
        private decimal[] _lineBase = [];
        private decimal[] _lineInitial = [];
        private decimal[] _lineChange = [];
        private decimal[] _lineAllocated = [];
        private RawValue[] _derived = [];
        private int _lineCount;
        private decimal _current;
        private decimal _initial;
        private DateOnly _effectiveDate;
        private int _order;
        private ulong[]? _applicable;                                  // applicability index result (null = call every Applies)

        public Decision<TSituation> Execute(in TSituation s)
        {
            _effectiveDate = TSituation.EffectiveDate(in s);
            if (_def.Index is { } index && ApplicabilityIndex<TSituation>.Enabled)
            {
                _applicable = scratch.Mask(index.Words);
                index.Candidates(in s, _applicable.AsSpan(0, index.Words));
            }
            InitAmounts(in s);
            _derived = _def.DerivedFields.IsEmpty ? [] : new RawValue[_def.DerivedFields.Length];

            var groups = _def.Groups;
            int g = 0;
            foreach (var stage in _def.Stages)
            {
                for (; g < groups.Length && groups[g].Stage.Order == stage.Order; g++) ProcessGroup(groups[g], in s);
                ComputeDerived(stage, in s);
            }

            if (trace == TraceLevel.Full) NoteCompileNotes();
            CheckRequested(in s);
            return Build(in s);
        }

        // ── Amounts ─────────────────────────────────────────────────────────

        private void InitAmounts(in TSituation s)
        {
            _lineCount = TSituation.LineCount(in s);
            if (_lineCount > 0)
            {
                _lineBase = new decimal[_lineCount];
                _lineInitial = new decimal[_lineCount];
                _lineChange = new decimal[_lineCount];
                _lineAllocated = new decimal[_lineCount];
                decimal sum = 0m;
                for (int i = 0; i < _lineCount; i++)
                {
                    decimal b = _def.LineBase is { } lb ? _rounding.Round(lb(in s, i)) : 0m;
                    _lineBase[i] = _lineInitial[i] = b;
                    sum += b;
                }
                _current = _initial = sum;
            }
            else
            {
                _current = _initial = _def.Base is { } fb ? _rounding.Round(fb(in s)) : 0m;
            }
        }

        private void ComputeDerived(Stage stage, in TSituation s)
        {
            var derived = _def.DerivedFields;
            for (int i = 0; i < derived.Length; i++)
            {
                if (derived[i].Stage != stage) continue;
                var totals = new EngineTotals(_initial, _current, _lineBase.AsSpan(0, _lineCount), _amountType!);
                _derived[i] = derived[i].ComputeRaw(in s, in totals);
            }
        }

        /// <summary>An engine decimal as a value of the rule set's amount type (None without an amount).</summary>
        private RawValue Raw(decimal amount) => _amountType is null ? RawValue.None : Amounts.ToRaw(amount, _amountType);

        /// <summary>A directive/cap amount as engine decimal; it must be of the rule set's amount type (or a plain number).</summary>
        private decimal AmountOf(in RawValue value, string owner)
        {
            if (_amountType is null)
                throw new InvalidOperationException($"Rule set '{_def.Name}' has no amount (declare Base or LineBase) — '{owner}' can't Increase/Decrease.");
            if (!value.Fits(_amountType) || !value.TryGet(out decimal d))
                throw new InvalidOperationException($"'{owner}': a {value.Type?.Key ?? value.Kind.ToString()} amount doesn't match rule set '{_def.Name}' amount type {_amountType.Key}.");
            return d;
        }

        /// <summary>Size (≥ 0) a directive asks for from <paramref name="base"/>, before caps/Max.</summary>
        private decimal Requested(in Directive d, decimal @base, int ci)
        {
            decimal m = d.IsPercent
                ? (_amountType is null ? AmountOf(RawValue.None, RefOf(ci).Code) : _rounding.Round(d.Percent.ApplyTo(@base)))
                : _rounding.Round(AmountOf(d.Amount, RefOf(ci).Code));
            return m >= 0m ? m : throw new InvalidOperationException($"'{RefOf(ci).Code}': adjustment sizes are ≥ 0 — use Increase or Decrease for the direction.");
        }

        /// <summary><see cref="Requested"/> limited by the directive's own Max.</summary>
        private decimal Limited(in Directive d, decimal requested, int ci)
            => d.IsPercent && !d.MaxAmount.IsNone && requested > AmountOf(d.MaxAmount, RefOf(ci).Code) ? AmountOf(d.MaxAmount, RefOf(ci).Code) : requested;

        // ── Groups ──────────────────────────────────────────────────────────

        private void ProcessGroup(RuleGroup group, in TSituation s)
        {
            int start = _candidates.Count;
            var parameters = set.ParameterValues;
            var rules = set.Rules;
            int codeRules = _def.Rules.Length;                         // the applicability index covers code rules only

            foreach (int r in set.RulesOf(group.Index))
            {
                var rule = rules[r];
                if (set.RuleDisabledBy(r) is { } by) { Note(RuleRefOf(r), group, -1, new Overridden(by)); continue; }
                if (!rule.IsEffective(_effectiveDate)) { Note(RuleRefOf(r), group, -1, new NotEffective(_effectiveDate)); continue; }

                if (rule.IsLineRule)
                {
                    for (int i = 0; i < _lineCount; i++)
                    {
                        var ctx = new RuleContext(_effectiveDate, Raw(_lineBase[i]), i, parameters);
                        if (!rule.LineApplies!(in s, i, in ctx)) { NoteNotApplicable(r, group, i, in s); continue; }
                        int ci = Begin(r, -1, -1, i, rule.Priority);
                        var d = new DirectiveWriter(_buffer, ci, (ushort)r);
                        rule.LineEvaluate!(in s, i, in ctx, ref d);
                        End(ci);
                    }
                }
                else
                {
                    if (_applicable is { } mask && r < codeRules && (mask[r >> 6] & (1UL << (r & 63))) == 0) { NoteNotApplicable(r, group, -1, in s); continue; }
                    var ctx = new RuleContext(_effectiveDate, Raw(_current), -1, parameters);
                    if (!rule.Applies!(in s, in ctx)) { NoteNotApplicable(r, group, -1, in s); continue; }
                    int ci = Begin(r, -1, -1, -1, rule.Priority);
                    var d = new DirectiveWriter(_buffer, ci, (ushort)r);
                    rule.Evaluate!(in s, in ctx, ref d);
                    End(ci);
                }
            }

            foreach (int t in _def.TablesOf(group.Index))
            {
                var inst = set.Tables[t];
                var binding = inst.Binding;
                if (set.TableDisabledBy(t) is { } by) { Note(TableRef(inst, -1), group, -1, new Overridden(by)); continue; }
                int rows = Math.Max(inst.RowCount, 1);
                int[] selected = scratch.Rows(rows);
                int lines = binding.IsLineTable ? _lineCount : 1;
                ushort owner = (ushort)(rules.Length + t);
                for (int li = 0; li < lines; li++)
                {
                    int line = binding.IsLineTable ? li : -1;
                    int n = inst.Select(in s, line, _effectiveDate, selected, out int closestRow, out int closestColumn);
                    if (n == 0)
                    {
                        Note(TableRef(inst, closestRow), group, line,
                            new NotApplicable(closestColumn < 0 ? ConditionId.None : new(owner, (byte)closestColumn)));
                        continue;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        int row = selected[k];
                        int priority = binding.Schema.HitPolicy == HitPolicy.Priority ? inst.Meta(row).Priority : binding.Priority;
                        int ci = Begin(-1, t, row, line, priority);
                        var d = new DirectiveWriter(_buffer, ci, owner);
                        inst.Emit(row, in s, line, ref d);
                        End(ci);
                    }
                }
            }

            if (_candidates.Count == start) return;
            Resolve(group, start);
            Apply(group, start);
        }

        private int Begin(int rule, int table, int row, int line, int priority)
        {
            int ci = _candidates.Count;
            _candidates.Add(new Candidate
            {
                Rule = rule, Table = table, Row = row, Line = line, Priority = priority, Seq = ci,
                DirStart = _buffer.Count, By = -1,
            });
            return ci;
        }

        private void End(int ci)
        {
            var c = _candidates[ci];
            c.DirCount = _buffer.Count - c.DirStart;
            _candidates[ci] = c;
        }

        // ── Resolution (04 §4 strategies) ───────────────────────────────────

        private void Resolve(RuleGroup group, int start)
        {
            var kind = group.Resolve.Kind;
            for (int line = -1; line < _lineCount; line++)
            {
                FillPartition(start, line);
                if (_partition.Count == 0) continue;

                switch (kind)
                {
                    case ResolveKind.First:
                        MarkApplied(_partition[0]);
                        for (int k = 1; k < _partition.Count; k++) Supersede(_partition[k], _partition[0]);
                        break;
                    case ResolveKind.Exclusive:
                    {
                        int best = _partition[0];
                        decimal bestEffect = Effect(best);
                        for (int k = 1; k < _partition.Count; k++)
                        {
                            decimal b = Effect(_partition[k]);
                            bool better = group.Resolve.Prefer == Prefer.Largest ? b > bestEffect : b < bestEffect;
                            if (better) { best = _partition[k]; bestEffect = b; }
                        }
                        MarkApplied(best);
                        foreach (int ci in _partition) if (ci != best) Supersede(ci, best);
                        break;
                    }
                    default:
                        foreach (int ci in _partition) MarkApplied(ci);
                        break;
                }
            }
        }

        private void FillPartition(int start, int line)
        {
            _partition.Clear();
            for (int ci = start; ci < _candidates.Count; ci++)
                if (_candidates[ci].Line == line) _partition.Add(ci);
            // priority desc, then insertion order (rules before tables at equal priority: rules were added first).
            // Insertion sort: partitions are tiny and a comparison lambda capturing `this` would allocate per call.
            for (int i = 1; i < _partition.Count; i++)
            {
                int x = _partition[i];
                var cx = _candidates[x];
                int j = i - 1;
                while (j >= 0)
                {
                    var cj = _candidates[_partition[j]];
                    bool after = cj.Priority < cx.Priority || (cj.Priority == cx.Priority && cj.Seq > cx.Seq);
                    if (!after) break;
                    _partition[j + 1] = _partition[j];
                    j--;
                }
                _partition[j + 1] = x;
            }
        }

        private void MarkApplied(int ci)
        {
            var c = _candidates[ci];
            c.State = StateApplied;
            _candidates[ci] = c;
        }

        private void Supersede(int ci, int by)
        {
            var c = _candidates[ci];
            c.State = StateSuperseded;
            c.By = by;
            _candidates[ci] = c;
        }

        /// <summary>Tentative size of a candidate's adjustments from the current bases (Exclusive / Most/LeastRestrictive).</summary>
        private decimal Effect(int ci)
        {
            var c = _candidates[ci];
            decimal sum = 0m;
            for (int k = c.DirStart; k < c.DirStart + c.DirCount; k++)
            {
                ref readonly var d = ref _buffer[k].Directive;
                if (d.Case != DirectiveCase.Adjust) continue;
                decimal @base = d.Target.IsLine && c.Line >= 0 ? _lineBase[c.Line] : _current;
                sum += Limited(in d, Requested(in d, @base, ci), ci);
            }
            return sum;
        }

        // ── Application ─────────────────────────────────────────────────────

        private void Apply(RuleGroup group, int start)
        {
            var resolve = group.Resolve;
            bool stack = resolve.Kind == ResolveKind.Stack;
            bool least = resolve.Kind == ResolveKind.LeastRestrictive;
            bool pickOneAdjust = resolve.Kind is ResolveKind.MostRestrictive or ResolveKind.LeastRestrictive;
            _groupLimits.Clear();

            for (int line = -1; line < _lineCount; line++)
            {
                FillPartition(start, line);
                if (_partition.Count == 0) continue;

                // Most/LeastRestrictive: only the smallest/largest adjusting candidate keeps its adjustments.
                int adjustWinner = -1;
                if (pickOneAdjust)
                {
                    decimal best = 0m;
                    foreach (int ci in _partition)
                    {
                        if (!HasAdjust(ci)) continue;
                        decimal b = Effect(ci);
                        if (adjustWinner < 0 || (least ? b > best : b < best)) { adjustWinner = ci; best = b; }
                    }
                }

                // Stack: every adjustment is computed from the group's starting base; the total change is capped.
                decimal stackBase = line >= 0 ? _lineBase[line] : _current;
                decimal capLeft = decimal.MaxValue;
                if (stack)
                {
                    if (resolve.Cap is { } cap) capLeft = _rounding.Round(cap.ApplyTo(stackBase));
                    if (!resolve.CapAmount.IsNone)
                    {
                        decimal capAmount = AmountOf(resolve.CapAmount, group.Name);
                        if (capAmount < capLeft) capLeft = capAmount;
                    }
                }

                foreach (int ci in _partition)
                {
                    var c = _candidates[ci];
                    if (c.State != StateApplied) continue;
                    bool blocked = false;
                    for (int k = c.DirStart; k < c.DirStart + c.DirCount; k++)
                    {
                        var d = _buffer[k].Directive;
                        switch (d.Case)
                        {
                            case DirectiveCase.Adjust:
                                if (pickOneAdjust && ci != adjustWinner) break;
                                ApplyAdjust(ci, in d, group, stack ? stackBase : null, ref capLeft);
                                break;
                            case DirectiveCase.Limit:
                                MergeLimit(least ? _groupLimits : _limits, in d, ci, least);
                                break;
                            case DirectiveCase.Grant:
                                MergeGrant(least ? _groupLimits : _limits, in d, ci, least);
                                break;
                            case DirectiveCase.Deny:
                                _violations.Add(d.Violation);
                                blocked = true;
                                break;
                            case DirectiveCase.Warn:
                                _violations.Add(d.Violation);
                                break;
                            case DirectiveCase.Approval:
                                _approvals.Add(new ApprovalRequest(d.Permission.GetValueOrDefault(), d.Violation, RefOf(ci)));
                                break;
                        }
                    }
                    if (pickOneAdjust && adjustWinner >= 0 && ci != adjustWinner && OnlyAdjusts(ci)) Supersede(ci, adjustWinner);
                    if (blocked)
                    {
                        c = _candidates[ci];
                        c.State = StateBlocked;
                        _candidates[ci] = c;
                    }
                }
            }

            // LeastRestrictive: the group's combined limits constrain the decision like any other limit.
            if (least)
                foreach (var l in _groupLimits) MergeState(_limits, in l);
        }

        private bool HasAdjust(int ci)
        {
            var c = _candidates[ci];
            for (int k = c.DirStart; k < c.DirStart + c.DirCount; k++)
                if (_buffer[k].Directive.Case == DirectiveCase.Adjust) return true;
            return false;
        }

        private bool OnlyAdjusts(int ci)
        {
            var c = _candidates[ci];
            for (int k = c.DirStart; k < c.DirStart + c.DirCount; k++)
                if (_buffer[k].Directive.Case != DirectiveCase.Adjust) return false;
            return c.DirCount > 0;
        }

        private void ApplyAdjust(int ci, in Directive d, RuleGroup group, decimal? stackBase, ref decimal capLeft)
        {
            var c = _candidates[ci];
            bool line = d.Target.IsLine;
            if (line && c.Line < 0)
                throw new InvalidOperationException($"'{RefOf(ci).Code}' adjusted AdjustTarget.Line outside a line rule/table.");

            decimal current = line ? _lineBase[c.Line] : _current;
            decimal @base = stackBase ?? current;
            decimal requested = Requested(in d, @base, ci);
            decimal amount = Limited(in d, requested, ci);
            ResolutionNote note = default;

            if (amount != requested) note = new CappedBy(RefOf(ci), Raw(requested), Raw(amount));
            if (stackBase is not null)
            {
                if (amount > capLeft)
                {
                    amount = capLeft < 0m ? 0m : capLeft;
                    note = new CappedBy(new RuleRef(group.Name, 0, 0, 0), Raw(requested), Raw(amount));
                }
                if (capLeft != decimal.MaxValue) capLeft -= amount;
            }
            bool decrease = d.Direction == AdjustDirection.Decrease;
            if (decrease && amount > current) amount = current < 0m ? 0m : current;   // never below zero

            if (amount != requested)
            {
                c.State = StateCapped;
                c.CappedFrom = requested;
                c.CappedTo = amount;
                _candidates[ci] = c;
                // A capped candidate still applied: keep it counted as applied for later directives.
                c.State = StateApplied;
            }

            decimal change = decrease ? -amount : amount;
            LineAllocation[]? allocations = null;
            if (line)
            {
                _lineBase[c.Line] += change;
                _lineChange[c.Line] += change;
                _current += change;
            }
            else
            {
                _current += change;
                if (_def.Allocation.Kind == AllocationKind.Proportional && _lineCount > 0 && amount != 0m)
                    allocations = Allocate(amount, decrease);
            }

            _adjustments.Add(new AppliedAdjustment(RefOf(ci), group.Stage.Ref, d.Target, line ? c.Line : -1, ++_order,
                d.Direction, d.IsPercent ? d.Percent : null, Raw(@base), Raw(amount), note, allocations));
        }

        /// <summary>
        /// Splits <paramref name="amount"/> proportionally to the current line bases; shares truncated toward zero at the
        /// rounding precision, the remainder goes to the largest line (lowest index on ties) → Σ shares == amount exactly (D32).
        /// </summary>
        private LineAllocation[] Allocate(decimal amount, bool decrease)
        {
            decimal total = 0m;
            int largest = 0;
            for (int i = 0; i < _lineCount; i++)
            {
                total += _lineBase[i];
                if (_lineBase[i] > _lineBase[largest]) largest = i;
            }
            Span<decimal> shares = _lineCount <= 128 ? stackalloc decimal[_lineCount] : new decimal[_lineCount];
            if (total == 0m)
            {
                shares[largest] = amount;
            }
            else
            {
                decimal assigned = 0m;
                for (int i = 0; i < _lineCount; i++)
                {
                    shares[i] = Math.Round(amount * _lineBase[i] / total, _rounding.Decimals, MidpointRounding.ToZero);
                    assigned += shares[i];
                }
                shares[largest] += amount - assigned;
            }
            var result = new LineAllocation[_lineCount];
            for (int i = 0; i < _lineCount; i++)
            {
                decimal change = decrease ? -shares[i] : shares[i];
                _lineAllocated[i] += change;
                _lineBase[i] += change;
                result[i] = new(i, Raw(shares[i]));
            }
            return result;
        }

        // ── Limits & grants ─────────────────────────────────────────────────

        private static int FindLimit(List<LimitState> limits, ushort fieldId)
        {
            for (int i = 0; i < limits.Count; i++) if (limits[i].Field.Id == fieldId) return i;
            return -1;
        }

        private static void MergeLimit(List<LimitState> limits, in Directive d, int ci, bool least)
        {
            var incoming = new LimitState
            {
                Field = d.Field,
                Min = d.Min, MinExceed = d.OnExceed, MinPermission = d.Permission, MinCandidate = d.Min.IsNone ? -1 : ci,
                Max = d.Max, MaxExceed = d.OnExceed, MaxPermission = d.Permission, MaxCandidate = d.Max.IsNone ? -1 : ci,
                OptionsCandidate = -1,
            };
            if (least) MergeStateLeast(limits, in incoming);
            else MergeState(limits, in incoming);
        }

        private static void MergeGrant(List<LimitState> limits, in Directive d, int ci, bool least)
        {
            int i = FindLimit(limits, d.Field.Id);
            if (i < 0)
            {
                limits.Add(new LimitState { Field = d.Field, Options = d.Options, OptionsCandidate = ci, MinCandidate = -1, MaxCandidate = -1 });
                return;
            }
            var l = limits[i];
            if (l.Options.IsEmpty) { l.Options = d.Options; l.OptionsCandidate = ci; }
            else l.Options = least ? Union(l.Options, d.Options) : l.Options.Intersect(d.Options);
            limits[i] = l;
        }

        /// <summary>Most restrictive merge (max of mins, min of maxes, options intersection) — the cross-group rule.</summary>
        private static void MergeState(List<LimitState> limits, in LimitState x)
        {
            int i = FindLimit(limits, x.Field.Id);
            if (i < 0) { limits.Add(x); return; }
            var l = limits[i];
            if (!x.Min.IsNone && (l.Min.IsNone || (x.Min.TryCompareTo(in l.Min, out int c) && c > 0)))
            { l.Min = x.Min; l.MinExceed = x.MinExceed; l.MinPermission = x.MinPermission; l.MinCandidate = x.MinCandidate; }
            if (!x.Max.IsNone && (l.Max.IsNone || (x.Max.TryCompareTo(in l.Max, out int c2) && c2 < 0)))
            { l.Max = x.Max; l.MaxExceed = x.MaxExceed; l.MaxPermission = x.MaxPermission; l.MaxCandidate = x.MaxCandidate; }
            if (!x.Options.IsEmpty)
            {
                if (l.Options.IsEmpty) { l.Options = x.Options; l.OptionsCandidate = x.OptionsCandidate; }
                else l.Options = l.Options.Intersect(x.Options);
            }
            limits[i] = l;
        }

        /// <summary>Least restrictive merge inside a LeastRestrictive group (min of mins, max of maxes).</summary>
        private static void MergeStateLeast(List<LimitState> limits, in LimitState x)
        {
            int i = FindLimit(limits, x.Field.Id);
            if (i < 0) { limits.Add(x); return; }
            var l = limits[i];
            if (!x.Min.IsNone && (l.Min.IsNone || (x.Min.TryCompareTo(in l.Min, out int c) && c < 0)))
            { l.Min = x.Min; l.MinExceed = x.MinExceed; l.MinPermission = x.MinPermission; l.MinCandidate = x.MinCandidate; }
            if (!x.Max.IsNone && (l.Max.IsNone || (x.Max.TryCompareTo(in l.Max, out int c2) && c2 > 0)))
            { l.Max = x.Max; l.MaxExceed = x.MaxExceed; l.MaxPermission = x.MaxPermission; l.MaxCandidate = x.MaxCandidate; }
            limits[i] = l;
        }

        private static OptionSet Union(in OptionSet a, in OptionSet b)
        {
            if (a.IsEmpty || b.IsEmpty) return OptionSet.Unrestricted;
            var values = new List<RawValue>(a.Count + b.Count);
            foreach (var v in a) values.Add(v);
            foreach (var v in b) if (!a.Contains(in v)) values.Add(v);
            return OptionSet.Of(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values));
        }

        // ── [Requested] checks (04 §7 step 6) ───────────────────────────────

        private void CheckRequested(in TSituation s)
        {
            foreach (var f in TSituation.Schema.Fields)
                if (f.Role == FieldRole.Requested) Check(f.Ref, TSituation.Read(in s, f.Id));
            var derived = _def.DerivedFields;
            for (int i = 0; i < derived.Length; i++)
                if (derived[i].IsRequested) Check(derived[i].Ref, _derived[i]);
        }

        private void Check(SituationFieldRef field, RawValue value)
        {
            int i = FindLimit(_limits, field.Id);
            if (i < 0 || value.IsNone) return;
            var l = _limits[i];
            var path = FieldPath.Of(field.Name);
            if (!l.Max.IsNone && value.TryCompareTo(in l.Max, out int c) && c > 0)
                Exceeded(path, "rules.limit.max", l.Max, value, l.MaxExceed, l.MaxPermission, l.MaxCandidate);
            if (!l.Min.IsNone && value.TryCompareTo(in l.Min, out int c2) && c2 < 0)
                Exceeded(path, "rules.limit.min", l.Min, value, l.MinExceed, l.MinPermission, l.MinCandidate);
            if (!l.Options.IsEmpty && !l.Options.Contains(in value))
                Exceeded(path, "rules.limit.options", RawValue.From(l.Options.Count), value, Exceed.Deny, null, l.OptionsCandidate);
        }

        private void Exceeded(in FieldPath path, string code, RawValue limit, RawValue value, Exceed exceed, Permission? permission, int ci)
        {
            var origin = new ViolationOrigin(ViolationSource.Rule, ci >= 0 ? OwnerOf(ci) : (ushort)0);
            var args = new RuleArgs(limit, value);
            if (exceed == Exceed.RequireApproval && permission is not null)
            {
                var v = new Violation(in path, code, Severity.Warning, in args, origin);
                _violations.Add(v);
                _approvals.Add(new ApprovalRequest(permission.GetValueOrDefault(), in v, ci >= 0 ? RefOf(ci) : default));
                return;
            }
            _violations.Add(new Violation(in path, code, exceed == Exceed.Warn ? Severity.Warning : Severity.Error, in args, origin));
        }

        // ── Trace helpers ───────────────────────────────────────────────────

        private void Note(RuleRef rule, RuleGroup group, int line, TraceOutcome outcome)
        {
            if (trace != TraceLevel.Full) return;
            _notes.Add(new PendingTrace { Rule = rule, Stage = group.Stage.Ref, Group = group.Name, Line = line, Outcome = outcome });
        }

        private void NoteNotApplicable(int r, RuleGroup group, int line, in TSituation s)
        {
            if (trace != TraceLevel.Full) return;
            var rule = set.Rules[r];
            byte failed = rule.Explain is { } explain ? explain(in s, line) : byte.MaxValue;
            var id = failed == byte.MaxValue
                ? (rule.Conditions.Length == 0 ? ConditionId.None : new ConditionId((ushort)r, 0))
                : new ConditionId((ushort)r, failed);
            Note(RuleRefOf(r), group, line, new NotApplicable(id));
        }

        private void NoteCompileNotes()
        {
            foreach (var n in set.Notes)
            {
                if (n.Kind is not (CompileNoteKind.ParameterClamped or CompileNoteKind.RowRejected or CompileNoteKind.SealedOverrideIgnored)) continue;
                _notes.Add(new PendingTrace
                {
                    Rule = new RuleRef(n.Code, 0, n.Tier, 0), Stage = default, Group = "", Line = -1,
                    Outcome = new Overridden(0),
                });
            }
        }

        // ── Refs ────────────────────────────────────────────────────────────

        private RuleRef RuleRefOf(int r)
        {
            var rule = set.Rules[r];
            return new RuleRef(rule.Code, rule.Version, set.RuleTier(r), set.RuleDataVersion(r));
        }

        private RuleRef TableRef(TableInstance<TSituation> inst, int row)
            => new(inst.Binding.Code, inst.Binding.Version, inst.Tier, inst.Info.Version, row >= 0 ? inst.Meta(row).RowId : default);

        private RuleRef RefOf(int ci)
        {
            var c = _candidates[ci];
            return c.Rule >= 0 ? RuleRefOf(c.Rule) : TableRef(set.Tables[c.Table], c.Row);
        }

        private ushort OwnerOf(int ci)
        {
            var c = _candidates[ci];
            return (ushort)(c.Rule >= 0 ? c.Rule : set.Rules.Length + c.Table);
        }

        // ── Decision ────────────────────────────────────────────────────────

        private Decision<TSituation> Build(in TSituation s)
        {
            var violations = ViolationList.From(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_violations));
            var approvals = new ApprovalRequestList(_approvals.Count == 0 ? null : [.. _approvals]);

            Verdict verdict;
            if (violations.ErrorCount > 0) verdict = new Denied(violations);
            else if (!approvals.IsEmpty) verdict = new NeedsApproval(approvals);
            else if (!violations.IsEmpty) verdict = new Warned(violations);
            else verdict = new Allowed();

            _limits.Sort((a, b) => a.Field.Id.CompareTo(b.Field.Id));
            var limits = new FieldLimit[_limits.Count];
            for (int i = 0; i < limits.Length; i++)
            {
                var l = _limits[i];
                limits[i] = new FieldLimit(l.Field, l.Min, l.Max, l.Options);
            }

            LineTotal[]? lines = null;
            if (_lineCount > 0)
            {
                lines = new LineTotal[_lineCount];
                for (int i = 0; i < _lineCount; i++)
                    lines[i] = new LineTotal(i, Raw(_lineInitial[i]), Raw(_lineChange[i]), Raw(_lineAllocated[i]), Raw(_lineBase[i]));
            }
            var totals = new DecisionTotals(Raw(_initial), Raw(_current), Raw(_current - _initial), lines);
            var adjustments = new AdjustmentList(_adjustments.Count == 0 ? null : [.. _adjustments]);

            ulong fingerprint;
            Hash256 digest = default;
            if (approvals.IsEmpty)
            {
                var h = StableHash64.Create(FingerprintSeed);
                AppendOutcome(ref h, verdict.Kind, limits, adjustments, approvals, violations, _derived);
                fingerprint = h.Finish();
            }
            else (fingerprint, digest) = FingerprintAndDigest(_def.Name, verdict.Kind, limits, adjustments, approvals, violations, _derived);
            var traceData = trace == TraceLevel.Off ? null : BuildTrace();

            return new Decision<TSituation>(_def, set.RuleArray, in s, verdict, violations, approvals, totals, adjustments, set.Version,
                fingerprint, digest, traceData, limits, _derived);
        }

        /// <summary>
        /// 04 §8: hash of the OUTCOME — verdict; limits by field id; options; adjustments (code, target, direction, amount) in
        /// order; approval permissions; violation (path, code); derived field values (engine-computed outcome: two
        /// decisions differing only in InstallmentValue must not compare equal). Excludes the trace and the ruleset version.
        /// </summary>
        private const ulong FingerprintSeed = 0xDEC1510;

        /// <summary>
        /// Decisions that need approvals also get <see cref="Decision{TSituation}.Digest"/> (cryptography ⚑K12): SHA-256 of
        /// <c>"yfex.rules.decision.v1" ‖ 0 ‖ rule set name (length-prefixed) ‖ the exact canonical outcome stream the fingerprint hashes</c>.
        /// Approval tokens sign the digest; the 64-bit fingerprint stays for equality and UI.
        /// </summary>
        private static (ulong Fingerprint, Hash256 Digest) FingerprintAndDigest(string ruleSet, VerdictKind verdict, FieldLimit[] limits,
            in AdjustmentList adjustments, in ApprovalRequestList approvals, in ViolationList violations, RawValue[] derived)
        {
            var outcome = new ArrayBufferWriter<byte>(256);
            outcome.Write("yfex.rules.decision.v1\0"u8);
            var prefix = StableHash64.Create(0, outcome);
            prefix.Append(ruleSet);
            var h = StableHash64.Create(FingerprintSeed, outcome);
            AppendOutcome(ref h, verdict, limits, adjustments, approvals, violations, derived);
            Span<byte> digest = stackalloc byte[Hash256.Size];
            SHA256.HashData(outcome.WrittenSpan, digest);
            return (h.Finish(), new Hash256(digest));
        }

        private static void AppendOutcome(ref StableHash64 h, VerdictKind verdict, FieldLimit[] limits, in AdjustmentList adjustments,
            in ApprovalRequestList approvals, in ViolationList violations, RawValue[] derived)
        {
            h.Append((byte)verdict);
            h.Append(limits.Length);
            foreach (ref readonly var l in limits.AsSpan())
            {
                h.Append((int)l.Field.Id);
                l.Min.AppendTo(ref h);
                l.Max.AppendTo(ref h);
                l.Options.AppendTo(ref h);
            }
            h.Append(adjustments.Count);
            Span<byte> rowId = stackalloc byte[16];
            foreach (ref readonly var a in adjustments.AsSpan())
            {
                h.Append(a.Rule.Code);
                a.Rule.RowId.TryWriteBytes(rowId);
                h.Append(rowId);
                h.Append(a.Target.IsLine);
                h.Append(a.LineIndex);
                h.Append((byte)a.Direction);
                a.Amount.AppendTo(ref h);
            }
            h.Append(approvals.Count);
            foreach (ref readonly var a in approvals.AsSpan()) h.Append(a.Permission.Value);   // same bytes as the old string id
            h.Append(violations.Count);
            foreach (ref readonly var v in violations.AsSpan())
            {
                v.Path.AppendTo(ref h);
                h.Append(v.Code.Value);
            }
            h.Append(derived.Length);
            foreach (ref readonly var v in derived.AsSpan()) v.AppendTo(ref h);
        }

        private DecisionTrace BuildTrace()
        {
            var entries = new List<TraceEntry>(_candidates.Count + _notes.Count);
            for (int ci = 0; ci < _candidates.Count; ci++)
            {
                var c = _candidates[ci];
                TraceOutcome outcome = c.State switch
                {
                    StateSuperseded => new Superseded(RefOf(c.By)),
                    StateBlocked => new Blocked(FirstDeny(in c)),
                    _ => c.CappedFrom != c.CappedTo ? new Capped(Raw(c.CappedFrom), Raw(c.CappedTo)) : new Applied(),
                };
                if (trace == TraceLevel.Applied && outcome.Kind is not (TraceOutcomeKind.Applied or TraceOutcomeKind.Capped or TraceOutcomeKind.Blocked)) continue;
                var group = c.Rule >= 0 ? set.Rules[c.Rule].Group : set.Tables[c.Table].Binding.Group;
                var dirs = new Directive[c.DirCount];
                for (int k = 0; k < c.DirCount; k++) dirs[k] = _buffer[c.DirStart + k].Directive;
                entries.Add(new TraceEntry(RefOf(ci), group.Stage.Ref, group.Name, c.Line, outcome, dirs));
            }
            foreach (var n in _notes)
                entries.Add(new TraceEntry(n.Rule, n.Stage, n.Group, n.Line, n.Outcome, null));
            return new DecisionTrace([.. entries], trace);
        }

        private Violation FirstDeny(in Candidate c)
        {
            for (int k = c.DirStart; k < c.DirStart + c.DirCount; k++)
                if (_buffer[k].Directive.Case == DirectiveCase.Deny) return _buffer[k].Directive.Violation;
            return default;
        }
    }
}

/// <summary>Evaluation scratch records (non-generic so pooled lists are shared by every situation type).</summary>
internal static class EvalState
{
    internal struct Candidate
    {
        public int Rule;          // rule index, or −1 for a table row
        public int Table;         // table index, or −1
        public int Row;           // table row, −1 for code rules
        public int Line;          // −1 = situation level
        public int Priority;
        public int Seq;           // insertion order (stable tie-break)
        public int DirStart;
        public int DirCount;
        public byte State;
        public int By;            // superseding candidate
        public decimal CappedFrom;
        public decimal CappedTo;
    }

    internal struct LimitState
    {
        public SituationFieldRef Field;
        public RawValue Min;
        public Exceed MinExceed;
        public Permission? MinPermission;
        public int MinCandidate;
        public RawValue Max;
        public Exceed MaxExceed;
        public Permission? MaxPermission;
        public int MaxCandidate;
        public OptionSet Options;
        public int OptionsCandidate;
    }

    internal struct PendingTrace
    {
        public RuleRef Rule;
        public StageRef Stage;
        public string Group;
        public int Line;
        public TraceOutcome Outcome;
    }
}

/// <summary>Pooled evaluation buffers (one per thread; re-entrant evaluations get a fresh one).</summary>
internal sealed class EvaluationScratch
{
    [ThreadStatic] private static EvaluationScratch? t_cached;

    private readonly object?[] _lists = new object?[8];
    private int[] _rows = new int[16];

    public DirectiveBuffer Directives { get; private set; } = new();

    public static EvaluationScratch Rent()
    {
        var s = t_cached;
        if (s is null) return new EvaluationScratch();
        t_cached = null;
        return s;
    }

    public void Return()
    {
        foreach (var l in _lists) (l as System.Collections.IList)?.Clear();
        Directives.Clear();
        t_cached = this;
    }

    public List<T> List<T>(int slot) => (List<T>)(_lists[slot] ??= new List<T>());

    private ulong[] _mask = new ulong[2];

    /// <summary>Applicability candidate mask (contents are overwritten by the index).</summary>
    public ulong[] Mask(int words)
    {
        if (_mask.Length < words) _mask = new ulong[words];
        return _mask;
    }

    public int[] Rows(int count)
    {
        if (_rows.Length < count) _rows = new int[Math.Max(count, _rows.Length * 2)];
        return _rows;
    }
}
