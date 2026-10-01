namespace YFex.Rules;

/// <summary>
/// Applicability index (04 §12.1): situation-level rules whose split conditions include <c>field == constant</c> (or a
/// bool field test) on an enum / bool / integer situation field are pre-filtered — one field read + one lookup per indexed
/// field instead of an <c>Applies</c> call per rule. Built once per definition; conservative by construction: a rule is only
/// skipped when one of its own <c>&amp;&amp;</c> operands is certainly false (same value kind on both sides), so decisions and
/// traces are identical with or without it (the skipped rule is noted exactly like a false <c>Applies</c>).
/// </summary>
internal sealed class ApplicabilityIndex<TSituation> where TSituation : ISituation<TSituation>
{
    /// <summary>Test switch (equivalence tests evaluate with and without the index).</summary>
    internal static bool Enabled = true;

    private readonly Entry[] _entries;

    private sealed class Entry(ushort fieldId, ulong[] constrained, Dictionary<RawValue, ulong[]> allowed)
    {
        public ushort FieldId { get; } = fieldId;
        /// <summary>Rules with an equality condition on this field.</summary>
        public ulong[] Constrained { get; } = constrained;
        /// <summary>Per value: the constrained rules whose conditions on this field all equal it.</summary>
        public Dictionary<RawValue, ulong[]> Allowed { get; } = allowed;
    }

    private ApplicabilityIndex(Entry[] entries, int words)
    {
        _entries = entries;
        Words = words;
    }

    /// <summary>Length of the candidate mask (one bit per rule).</summary>
    public int Words { get; }

    /// <summary>Null when no rule has an indexable condition.</summary>
    public static ApplicabilityIndex<TSituation>? Build(ReadOnlySpan<RuleDescriptor<TSituation>> rules)
    {
        int words = (rules.Length + 63) >> 6;
        var byField = new Dictionary<ushort, (ulong[] Constrained, Dictionary<RawValue, List<int>> Values, Dictionary<int, RawValue?> Required)>();
        var schema = TSituation.Schema;
        for (int r = 0; r < rules.Length; r++)
        {
            var rule = rules[r];
            if (rule.IsLineRule) continue;
            foreach (var c in rule.Conditions)
            {
                if (c.Field is not { } name || !TSituation.TryGetFieldId(name, out ushort id) || id >= schema.Count) continue;
                var kind = schema[id].Kind;
                if (kind is not (ValueKind.EnumCode or ValueKind.Bool or ValueKind.Int32 or ValueKind.Int64)) continue;
                RawValue expected = c.MessageKey switch
                {
                    "rules.cond.eq" => c.Value,
                    "rules.cond.true" => RawValue.From(true),
                    "rules.cond.false" => RawValue.From(false),
                    _ => default,
                };
                if (expected.Kind != kind) continue;                      // different storage → equality not provable here

                if (!byField.TryGetValue(id, out var e)) byField[id] = e = (new ulong[words], new(), new());
                e.Constrained[r >> 6] |= 1UL << (r & 63);
                // Two different required values on one field → the rule can never apply (null marks the conflict).
                if (!e.Required.TryGetValue(r, out var prior)) e.Required[r] = expected;
                else if (prior is { } p && !p.Equals(expected)) e.Required[r] = null;
            }
        }
        if (byField.Count == 0) return null;

        var entries = new List<Entry>(byField.Count);
        foreach (var (id, e) in byField)
        {
            var allowed = new Dictionary<RawValue, ulong[]>();
            foreach (var (rule, value) in e.Required)
            {
                if (value is not { } v) continue;
                if (!allowed.TryGetValue(v, out var mask)) allowed[v] = mask = new ulong[words];
                mask[rule >> 6] |= 1UL << (rule & 63);
            }
            entries.Add(new Entry(id, e.Constrained, allowed));
        }
        entries.Sort(static (a, b) => a.FieldId.CompareTo(b.FieldId));
        return new ApplicabilityIndex<TSituation>([.. entries], words);
    }

    /// <summary>Fills <paramref name="candidates"/>: bit set = the rule may apply (call <c>Applies</c>); clear = certainly not.</summary>
    public void Candidates(in TSituation s, Span<ulong> candidates)
    {
        candidates.Fill(ulong.MaxValue);
        foreach (var e in _entries)
        {
            var value = TSituation.Read(in s, e.FieldId);
            e.Allowed.TryGetValue(value, out var allowed);
            for (int i = 0; i < candidates.Length; i++)
                candidates[i] &= ~e.Constrained[i] | (allowed is null ? 0UL : allowed[i]);
        }
    }
}
