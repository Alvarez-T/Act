using System.Collections.Immutable;
using YFex.Helpers;
using YFex.Validation;
using YFex.Authorization.Public;

namespace YFex.Rules;

/// <summary>How a decision table picks rows (DMN vocabulary, 05 §1.3).</summary>
public enum HitPolicy : byte
{
    /// <summary>First matching row in table order (unreachable rows = publish warning).</summary>
    First = 0,
    /// <summary>At most one row may match any input (overlap = publish error).</summary>
    Unique = 1,
    /// <summary>Highest row <see cref="RowMeta.Priority"/> among matches (equal-priority overlap = publish error).</summary>
    Priority = 2,
    /// <summary>All matches become candidates; the group's <see cref="Resolve"/> combines them.</summary>
    Collect = 3,
}

/// <summary>
/// A decision table bound to a situation (05 §1). The nested <c>Row</c> record struct IS the table's schema:
/// <see cref="WhenAttribute"/> columns are conditions over situation fields, <see cref="ThenAttribute"/> columns
/// are directive outputs. The generator binds column names at compile time (typos = YFR0101).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DecisionTableAttribute<TSituation>(string code, HitPolicy hitPolicy = HitPolicy.First) : Attribute
    where TSituation : ISituation<TSituation>
{
    public string Code { get; } = code;
    public HitPolicy HitPolicy { get; } = hitPolicy;
    /// <summary>Group of the rule set it competes in (<c>nameof(SaleRules.Installments)</c>).</summary>
    public string? Group { get; init; }
    /// <summary>Competes like a rule with this priority (First strategy).</summary>
    public int Priority { get; init; }
    /// <summary>
    /// Rule tiers that may publish versions (D12): names of the situation's <see cref="RuleTierAttribute"/> fields
    /// (<c>EditableAt = [nameof(Shipment.Carrier)]</c>). Empty = code rows only (sealed).
    /// </summary>
    public string[]? EditableAt { get; init; }
    public string? PublishPermission { get; init; }
    /// <summary>Type implementing <see cref="IRowGuard{TRow}"/> — sealed limits every row must respect.</summary>
    public Type? Guardrail { get; init; }
    /// <summary>Every possible input must match a row (gap = publish error).</summary>
    public bool Complete { get; init; }
    /// <summary>Name of the rule set when several rule sets of the situation declare the group.</summary>
    public string? RuleSet { get; init; }
}

/// <summary>Condition column bound to a situation field (<c>"Payment"</c>) or a line field (<c>"Lines[].Category"</c> → line table).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class WhenAttribute(string field) : Attribute
{
    public string Field { get; } = field;
}

/// <summary>Output column: <c>[Then(DirectiveKind.LimitMax, "Installments")]</c>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ThenAttribute(DirectiveKind kind, string? field = null) : Attribute
{
    public DirectiveKind Kind { get; } = kind;
    public string? Field { get; } = field;
    /// <summary>Limits: what happens when the requested value exceeds it.</summary>
    public Exceed OnExceed { get; init; }
    public string? ApprovalPermission { get; init; }
}

/// <summary>Sealed checks every row must satisfy — editor (live), publish (reject) and evaluation (row excluded + traced).</summary>
public interface IRowGuard<TRow>
{
    static abstract void Check(in TRow row, ref ViolationWriter w);
}

/// <summary>
/// Implemented by the generator on a <see cref="DecisionTableAttribute{TSituation}"/> class (05 §2). The runtime owns
/// freezing, hit policies and publish analysis; the generated members only know the row's cells.
/// </summary>
public interface IDecisionTable<TSituation, TRow> where TSituation : ISituation<TSituation>
{
    static abstract string Code { get; }
    static abstract TableSchema Schema { get; }
    /// <summary>Rows shipped with the code (tier 0). Declared by the user; empty when omitted.</summary>
    static abstract ReadOnlySpan<TRow> DefaultRows { get; }

    /// <summary>−1 when every <c>[When]</c> cell of <paramref name="row"/> matches; else the index of the first failing column.</summary>
    static abstract int Match(in TRow row, in TSituation s, int lineIndex);

    /// <summary>Writes the row's <c>[Then]</c> outputs.</summary>
    static abstract void Emit(in TRow row, in TSituation s, int lineIndex, ref DirectiveWriter d);

    /// <summary>Row shape (<c>[Validatable]</c> rules) + guardrail (<see cref="IRowGuard{TRow}"/>) checks.</summary>
    static abstract void CheckRow(in TRow row, ref ViolationWriter w);

    /// <summary>Number of <c>[When]</c> columns.</summary>
    static abstract int ConditionCount { get; }

    /// <summary>Some input matches both rows' cells in condition column <paramref name="column"/>.</summary>
    static abstract bool Intersects(in TRow a, in TRow b, int column);

    /// <summary>Every input matching <paramref name="b"/>'s cell also matches <paramref name="a"/>'s in <paramref name="column"/>.</summary>
    static abstract bool Covers(in TRow a, in TRow b, int column);

    /// <summary>Writes the row's members as properties of the open <c>"cells"</c> object (05 §2 RowsJson; store/files/editor).</summary>
    static abstract void WriteRow(System.Text.Json.Utf8JsonWriter writer, in TRow row);

    /// <summary>Reads a row from its <c>"cells"</c> object (missing members = defaults).</summary>
    static abstract TRow ReadRow(ref System.Text.Json.Utf8JsonReader reader);

    /// <summary>
    /// Condition column <paramref name="column"/> of <paramref name="row"/> as untyped values (publish analysis: the
    /// <c>Complete</c> gap check; non-generic editors). Generated; <see cref="ConditionCell.Unknown"/> = not available.
    /// </summary>
    static virtual ConditionCell GetCondition(in TRow row, int column) => ConditionCell.Unknown;

    /// <summary>Every possible value of a Match column with a closed type (enum members, bool); null = open domain.</summary>
    static virtual RawValue[]? ConditionDomain(int column) => null;
}

/// <summary>Per-row metadata managed by the store/editor (parallel to rows).</summary>
public readonly record struct RowMeta(Guid RowId, int Priority = 0, bool Sealed = false,
    DateOnly? EffectiveFrom = null, DateOnly? EffectiveTo = null, string? Note = null)
{
    public bool IsEffective(DateOnly effectiveDate)
        => (EffectiveFrom is not { } f || effectiveDate >= f) && (EffectiveTo is not { } t || effectiveDate <= t);

    /// <summary>Stable id of code row <paramref name="index"/> of <paramref name="tableCode"/> (deterministic across builds).</summary>
    public static Guid CodeRowId(string tableCode, int index)
    {
        var h = StableHash64.Create(0x7AB1E);
        h.Append(tableCode);
        h.Append(index);
        ulong a = h.Finish();
        var h2 = StableHash64.Create(a);
        h2.Append(index);
        ulong b = h2.Finish();
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, a);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], b);
        return new Guid(bytes);
    }
}

/// <summary>Version header of a published table (no rows).</summary>
public readonly record struct TableVersionInfo(string TableCode, long Version, RuleTierPath TierPath, DateOnly EffectiveFrom,
    string? PublishedBy = null, DateTimeOffset PublishedAt = default, string? Note = null)
{
    /// <summary>Depth of <see cref="TierPath"/> (0 = code).</summary>
    public int Tier => TierPath.Depth;
}

/// <summary>Non-generic part of <see cref="TableVersion{TRow}"/> (host storage).</summary>
public abstract class TableVersion(in TableVersionInfo info)
{
    public TableVersionInfo Info { get; } = info;
    public string TableCode => Info.TableCode;
    public long Version => Info.Version;
    public RuleTierPath TierPath => Info.TierPath;
    public DateOnly EffectiveFrom => Info.EffectiveFrom;
    public abstract int RowCount { get; }
}

/// <summary>One published (immutable) version of a table for one tier path (05 §3.1). Every source produces this.</summary>
public sealed class TableVersion<TRow> : TableVersion
{
    public TableVersion(in TableVersionInfo info, ImmutableArray<TRow> rows, ImmutableArray<RowMeta> meta = default) : base(in info)
    {
        Rows = rows;
        if (meta.IsDefaultOrEmpty && !rows.IsDefaultOrEmpty)
        {
            var b = ImmutableArray.CreateBuilder<RowMeta>(rows.Length);
            for (int i = 0; i < rows.Length; i++) b.Add(new RowMeta(RowMeta.CodeRowId(info.TableCode + "@" + info.Version, i)));
            meta = b.MoveToImmutable();
        }
        if (!rows.IsDefaultOrEmpty && meta.Length != rows.Length) throw new ArgumentException("Meta must be parallel to rows.", nameof(meta));
        Meta = meta.IsDefault ? [] : meta;
    }

    public ImmutableArray<TRow> Rows { get; }
    /// <summary>RowId, Priority, Sealed, EffectiveFrom/To, Note (parallel to <see cref="Rows"/>).</summary>
    public ImmutableArray<RowMeta> Meta { get; }
    public override int RowCount => Rows.IsDefault ? 0 : Rows.Length;
}

/// <summary>Anything that can provide versions: the code defaults, the database store, a JSON file (05 §3).</summary>
public interface ITableSource<TRow>
{
    ValueTask<ImmutableArray<TableVersion<TRow>>> LoadAsync(string tableCode, RuleTierPath tierPath, CancellationToken ct);
}

/// <summary>Where a table column's cell lives.</summary>
public enum ColumnRole : byte { When = 0, Then = 1 }

/// <summary>Shape of a column's cell.</summary>
public enum CellShape : byte { Value = 0, Match = 1, Band = 2 }

/// <summary>Column metadata for editors and publish checks (05 §2).</summary>
public sealed class TableColumn(int index, string name, ColumnRole role, CellShape shape, string field, RawType type,
    DirectiveKind directive = DirectiveKind.None, EnumOption[]? enumOptions = null, FieldConstraints constraints = default,
    bool lineField = false)
{
    public int Index { get; } = index;
    /// <summary>Row member name (and header message key suffix).</summary>
    public string Name { get; } = name;
    public ColumnRole Role { get; } = role;
    public CellShape Shape { get; } = shape;
    /// <summary>Bound situation/line/derived field name.</summary>
    public string Field { get; } = field;
    public bool IsLineField { get; } = lineField;
    /// <summary>Value type of the cells (<c>Money.RawType</c>, <see cref="RawType.Int32"/>, an app type…).</summary>
    public RawType Type { get; } = type;
    public ValueKind Kind => Type.Storage;
    public DirectiveKind Directive { get; } = directive;
    public ReadOnlySpan<EnumOption> EnumOptions => enumOptions;
    /// <summary>Cell constraints from the row's validation attributes (editor limits).</summary>
    public FieldConstraints Constraints { get; } = constraints;
}

/// <summary>Table metadata: code, hit policy, editability and columns.</summary>
public sealed class TableSchema(string code, string situation, HitPolicy hitPolicy, string[]? editableAt, Permission? publishPermission,
    bool isLineTable, bool complete, TableColumn[] columns)
{
    private readonly string[] _editableAt = editableAt ?? [];

    public string Code { get; } = code;
    public string Situation { get; } = situation;
    public HitPolicy HitPolicy { get; } = hitPolicy;
    /// <summary>Names of the rule tiers that may publish versions; empty = code rows only.</summary>
    public ReadOnlySpan<string> EditableAt => _editableAt;
    /// <summary>False when only the code rows exist (no tier may publish, disable or replace it).</summary>
    public bool IsEditable => _editableAt.Length > 0;
    /// <summary>Permission that publishing a version requires (null = the host's fallback, e.g. Rules.Admin's option).</summary>
    public Permission? PublishPermission { get; } = publishPermission;
    public bool IsLineTable { get; } = isLineTable;
    public bool Complete { get; } = complete;
    public ReadOnlySpan<TableColumn> Columns => columns;
}
