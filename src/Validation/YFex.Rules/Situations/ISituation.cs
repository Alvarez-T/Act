using YFex.Helpers;

namespace YFex.Rules;

/// <summary>
/// Implemented by the generator for every <see cref="SituationAttribute"/> type (04 §1.3). All members are static →
/// the engine calls them through the type parameter, never through an object (no boxing, inlinable).
/// </summary>
public interface ISituation<TSelf> where TSelf : ISituation<TSelf>
{
    /// <summary>Data description of the fields (tables, editors, dynamic rules, publish checks, catalog).</summary>
    static abstract SituationSchema Schema { get; }

    /// <summary>Reads a field by id without reflection/boxing. Unknown id / lines field → <see cref="RawValue.None"/>.</summary>
    static abstract RawValue Read(in TSelf s, ushort fieldId);

    /// <summary><c>"Payment"</c> → its id (ordinal, case-sensitive).</summary>
    static abstract bool TryGetFieldId(ReadOnlySpan<char> name, out ushort id);

    /// <summary>Number of lines (0 when the situation has no lines field).</summary>
    static abstract int LineCount(in TSelf s);

    /// <summary>Reads field <paramref name="lineFieldId"/> of line <paramref name="lineIndex"/> (ids of the line schema).</summary>
    static abstract RawValue ReadLine(in TSelf s, int lineIndex, ushort lineFieldId);

    /// <summary>The <see cref="RuleEffectiveDateAttribute"/> field; <see cref="DateOnly.MaxValue"/> when the situation has none.</summary>
    static abstract DateOnly EffectiveDate(in TSelf s);

    /// <summary>Path built from the <see cref="RuleTierAttribute"/> fields (declaration order); code when there are none.</summary>
    static abstract RuleTierPath TierPath(in TSelf s);

    /// <summary>Feeds the canonical form (every non-derived field, lines included) into <paramref name="hash"/>.</summary>
    static abstract void AppendTo(in TSelf s, ref StableHash64 hash);

    /// <summary>Canonical binary write (audit/replay: "reproduce this decision years later").</summary>
    static abstract void Serialize(in TSelf s, ref SituationWriter writer);

    /// <summary>Inverse of <see cref="Serialize"/>.</summary>
    static abstract TSelf Deserialize(ref SituationReader reader);

    /// <summary>Bit i set = field i differs (fields ≥ 64 share bit 63). Incremental UI evaluation (04 §12.3).</summary>
    static abstract ulong DiffMask(in TSelf a, in TSelf b);
}

/// <summary>Helpers over <see cref="ISituation{TSelf}"/> (the generator also emits type-level forwarders).</summary>
public static class Situation
{
    /// <summary>Deterministic 64-bit hash over all fields (StableHash64 over the canonical form).</summary>
    public static ulong Hash<TS>(in TS s) where TS : ISituation<TS>
    {
        var h = StableHash64.Create();
        TS.AppendTo(in s, ref h);
        return h.Finish();
    }

    /// <summary>Writes the canonical bytes (<see cref="SituationWriter.FormatVersion"/> header + fields).</summary>
    public static void WriteTo<TS>(in TS s, System.Buffers.IBufferWriter<byte> buffer) where TS : ISituation<TS>
    {
        var w = new SituationWriter(buffer);
        w.WriteHeader();
        TS.Serialize(in s, ref w);
    }

    /// <summary>Reads bytes written by <see cref="WriteTo{TS}"/>.</summary>
    public static TS ReadFrom<TS>(ReadOnlySpan<byte> data) where TS : ISituation<TS>
    {
        var r = new SituationReader(data);
        r.ReadHeader();
        return TS.Deserialize(ref r);
    }
}

/// <summary>
/// Typed handle to one field of a situation — code rules, tables and Limit/Grant directives refer to these, never to
/// strings. Example: <c>d.Limit(SaleSituation.Fields.Installments, max: 6)</c>.
/// </summary>
public readonly record struct SituationField<TSituation, T>(ushort Id, string Name, RawType Type)
    where TSituation : ISituation<TSituation>
{
    public static implicit operator SituationFieldRef(SituationField<TSituation, T> f) => new(f.Id, f.Name);
}

/// <summary>Untyped field reference <c>(Id, Name)</c> — situation fields and rule-set derived fields (ids ≥ 0x8000).</summary>
public readonly record struct SituationFieldRef(ushort Id, string Name)
{
    /// <summary>First id given to rule-set <see cref="DerivedField{TSituation,T}"/>s (never collides with situation ids).</summary>
    public const ushort DerivedBase = 0x8000;

    public bool IsDerived => Id >= DerivedBase;
    public override string ToString() => Name;
}

/// <summary>Metadata of one situation field.</summary>
public sealed class SituationFieldInfo(ushort id, string name, RawType type, FieldRole role = FieldRole.Normal,
    int tier = 0, bool exposed = true, EnumOption[]? enumOptions = null, SituationSchema? lineSchema = null,
    string? clrType = null)
{
    public ushort Id { get; } = id;
    public string Name { get; } = name;
    /// <summary>Value type of the field (<c>Money.RawType</c>, <see cref="RawType.Int32"/>, an app type…).</summary>
    public RawType Type { get; } = type;
    /// <summary>Storage kind of <see cref="Type"/>.</summary>
    public ValueKind Kind => Type.Storage;
    public FieldRole Role { get; } = role;
    /// <summary>1-based tier level when <see cref="Role"/> is <see cref="FieldRole.Tier"/>; 0 otherwise.</summary>
    public int Tier { get; } = tier;
    public bool Exposed { get; } = exposed;
    /// <summary>Enum members (<c>EnumCode</c>-aware) for dropdowns and publish-time checks; empty otherwise.</summary>
    public ReadOnlySpan<EnumOption> EnumOptions => enumOptions;
    /// <summary>Schema of the line type when <see cref="Role"/> is <see cref="FieldRole.Lines"/>.</summary>
    public SituationSchema? LineSchema { get; } = lineSchema;
    /// <summary>Display name of the CLR type (editors/diagnostics only).</summary>
    public string? ClrType { get; } = clrType;

    public SituationFieldRef Ref => new(Id, Name);
}

/// <summary>Data description of a situation (04 §1.3). Field ids are dense: <c>Fields[i].Id == i</c>.</summary>
public sealed class SituationSchema
{
    private readonly SituationFieldInfo[] _fields;

    private readonly SituationFieldInfo[] _tiers;

    public SituationSchema(string name, SituationFieldInfo[] fields)
    {
        Name = name;
        _fields = fields;
        var tiers = new List<SituationFieldInfo>();
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Id != i) throw new ArgumentException($"Field ids must be dense: '{fields[i].Name}' has id {fields[i].Id}, expected {i}.", nameof(fields));
            if (fields[i].Role == FieldRole.Lines) LinesFieldId = i;
            if (fields[i].Role == FieldRole.Tier) tiers.Add(fields[i]);
        }
        tiers.Sort((a, b) => a.Tier.CompareTo(b.Tier));
        for (int i = 0; i < tiers.Count; i++)
            if (tiers[i].Tier != i + 1) throw new ArgumentException($"Rule tier '{tiers[i].Name}' has level {tiers[i].Tier}, expected {i + 1}.", nameof(fields));
        if (tiers.Count > RuleTierPath.MaxDepth) throw new ArgumentException($"At most {RuleTierPath.MaxDepth} rule tiers are supported.", nameof(fields));
        _tiers = [.. tiers];
    }

    /// <summary>The <see cref="RuleTierAttribute"/> fields, tier 1 first.</summary>
    public ReadOnlySpan<SituationFieldInfo> Tiers => _tiers;

    /// <summary>Name of tier <paramref name="depth"/> (<c>"Code"</c> for 0).</summary>
    public string TierName(int depth) => depth == 0 ? "Code" : (uint)(depth - 1) < (uint)_tiers.Length ? _tiers[depth - 1].Name : $"Tier{depth}";

    /// <summary>
    /// True when data published at tier <paramref name="depth"/> may be used by something editable at the tiers named in
    /// <paramref name="editableAt"/> (field names of <see cref="Tiers"/>). The code tier (0) is always allowed.
    /// </summary>
    public bool AllowsTier(ReadOnlySpan<string> editableAt, int depth)
    {
        if (depth == 0) return true;
        if ((uint)(depth - 1) >= (uint)_tiers.Length) return false;
        string name = _tiers[depth - 1].Name;
        foreach (var e in editableAt)
            if (string.Equals(e, name, StringComparison.Ordinal)) return true;
        return false;
    }

    public string Name { get; }
    public ReadOnlySpan<SituationFieldInfo> Fields => _fields;
    public int Count => _fields.Length;
    /// <summary>Id of the lines field, −1 when none.</summary>
    public int LinesFieldId { get; } = -1;
    public SituationSchema? LineSchema => LinesFieldId < 0 ? null : _fields[LinesFieldId].LineSchema;

    public SituationFieldInfo this[int id] => _fields[id];

    public bool TryGetField(ReadOnlySpan<char> name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SituationFieldInfo? field)
    {
        foreach (var f in _fields)
            if (name.SequenceEqual(f.Name)) { field = f; return true; }
        field = null;
        return false;
    }
}
