using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace YFex.Rules.Generator;

internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    /// <summary>In-source location when the file is in the compilation (so <c>#pragma warning disable</c> applies).</summary>
    public Location ToLocation(SourceTrees? trees = null)
        => trees?.Find(FilePath) is { } tree && Span.End <= tree.Length
            ? Location.Create(tree, Span)
            : Location.Create(FilePath, Span, LineSpan);

    public static LocationInfo? From(Location? location)
        => location is null || location.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
}

internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Args)
{
    public Diagnostic ToDiagnostic(SourceTrees? trees = null)
        => Diagnostic.Create(Diagnostics.ById(Id), Location?.ToLocation(trees), Args.AsSpan().ToArray());

    public static DiagnosticInfo Create(string id, Location? location, params string[] args)
        => new(id, LocationInfo.From(location), new EquatableArray<string>(args));
}

internal sealed class SourceTrees(Compilation compilation)
{
    private System.Collections.Generic.Dictionary<string, SyntaxTree>? _byPath;

    public SyntaxTree? Find(string path)
    {
        if (_byPath is null)
        {
            _byPath = new System.Collections.Generic.Dictionary<string, SyntaxTree>(System.StringComparer.Ordinal);
            foreach (var tree in compilation.SyntaxTrees)
                if (!string.IsNullOrEmpty(tree.FilePath)) _byPath[tree.FilePath] = tree;
        }
        return _byPath.TryGetValue(path, out var t) ? t : null;
    }
}

/// <summary>How a situation field value is read/written (the emitters switch on it).</summary>
internal enum FieldShape
{
    Scalar,      // RawValue.From(x) / r.Read<T>()
    Enum,        // RawValue.FromEnum(x) / r.ReadEnum<T>()
    Wrapper,     // id wrapping a scalar: RawValue.From(x.Inner) / new T(r.Read<TInner>())
    WrapperEnum, // wrapper around an enum
    Lines,       // ImmutableArray<TLine>
}

/// <summary>One field of a situation (ids = index in <see cref="SituationModel.Fields"/>).</summary>
internal sealed record SituationFieldModel(
    string Name,
    string TypeFqn,          // global::… (nullable annotation stripped for Nullable<T> → see IsNullable)
    string ValueTypeFqn,     // underlying scalar/enum type (for wrappers: inner; for nullable: the non-null type)
    string? InnerMember,     // wrapper's inner property
    FieldShape Shape,
    string Kind,             // ValueKind member name, or the RawType expression of an IRawValue type (Shapes.RawTypeOf)
    string Role,             // FieldRole member name
    int Tier,                // [RuleTier] level (1-based), 0 = not a tier
    bool Exposed,
    bool IsNullable,         // Nullable<T> value type
    bool IsReferenceNullable,// string? etc.
    EquatableArray<EnumMemberModel> EnumMembers,
    string? LineTypeFqn);    // Lines: TLine

internal sealed record EnumMemberModel(string Name, string Code, long Value);

internal sealed record SituationModel(
    string Namespace,
    string TypeName,               // simple name
    string FullyQualified,         // global::Ns.Outer.Name
    EquatableArray<ContainingType> Containing,
    string Keyword,                // "record struct" / "struct" / "record" / "class"
    bool IsReadOnly,
    string SchemaName,
    EquatableArray<SituationFieldModel> Fields,
    bool HasPrimaryConstructor,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public int LinesIndex
    {
        get
        {
            for (int i = 0; i < Fields.Length; i++) if (Fields[i].Shape == FieldShape.Lines) return i;
            return -1;
        }
    }
}

internal sealed record ContainingType(string Keyword, string Name);

// ── Rule sets ────────────────────────────────────────────────────────────────

internal sealed record DerivedFieldModel(string Member, string Name, bool Requested);

internal sealed record RuleSetModel(
    string Namespace,
    string TypeName,
    string FullyQualified,
    EquatableArray<ContainingType> Containing,
    string SetName,
    string SituationFqn,
    string? LineTypeFqn,
    string? LinesMember,
    EquatableArray<string> Groups,             // RuleGroup field names, declaration order
    EquatableArray<DerivedFieldModel> Derived,
    bool HasRounding,
    bool HasAllocation,
    string? LineBaseKind,                       // "line" (in TS, in TLine) | null
    bool HasBase,
    string? AmountFqn,                          // return type of Base/LineBase (IAmount or number) | null = no amount
    EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>One split applicability condition of a rule.</summary>
internal sealed record ConditionModel(string Text, string MessageKey, string? Field, string ValueExpr, string CheckExpr);

internal sealed record RuleModel(
    string Code,
    string TypeFqn,
    string TypeName,
    string SituationFqn,
    string? LineTypeFqn,
    string? LinesMember,
    string Group,
    string? RuleSet,
    int Priority,
    int Version,
    bool IsSealed,
    string? EffectiveFrom,
    string? EffectiveTo,
    bool AppliesHasContext,
    bool EvaluateHasContext,
    EquatableArray<string> Parameters,        // static RuleParameter<> field names
    EquatableArray<ConditionModel> Conditions,
    bool Splittable,                          // conditions come with a runtime explain body
    string SituationParam,                    // user parameter names inside Applies
    string? LineParam,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics);

// ── Tables ───────────────────────────────────────────────────────────────────

internal enum CellKind { Value, Match, Band }

internal sealed record ColumnModel(
    int Index,
    string Member,
    bool IsWhen,
    CellKind Cell,
    string CellTypeFqn,       // T of Match<T>/Band<T>, or the member type for Value
    string MemberTypeFqn,     // full member type
    bool CellIsEnum,
    string Field,             // bound name as written ("Payment", "Lines[].Category", "InstallmentValue")
    string Directive,         // DirectiveKind member ("None" for When)
    string? OnExceed,
    string? Permission,       // C# expression of the typed Permission (PermissionArguments), null = none
    // resolution (filled in the transform when the field is a situation/line field)
    string? Access,           // C# expression reading the situation value: "s.Payment" / "__line.Category"
    string? FieldRefExpr,     // SituationFieldRef expression for Then columns
    string ValueKind,
    bool IsLineField,
    EquatableArray<EnumMemberModel> EnumMembers);

/// <summary>A member of a table Row (column or data-only) for the row JSON codec.</summary>
internal sealed record RowMemberModel(string Name, string TypeFqn, CellKind Cell, string ValueTypeFqn, bool IsEnum, bool Supported);

internal sealed record TableModel(
    string Namespace,
    string TypeName,
    string FullyQualified,
    EquatableArray<ContainingType> Containing,
    string Code,
    string HitPolicy,
    string? Group,
    string? RuleSet,
    int Priority,
    string Editable,
    string? PublishPermission,     // C# expression of the typed Permission (PermissionArguments), null = none
    string? GuardrailFqn,
    bool Complete,
    string SituationFqn,
    string SchemaName,
    string RowName,
    string RowFqn,
    bool RowValidatable,
    bool HasDefaultRows,           // user declared DefaultRows
    bool DefaultRowsIsSpan,        // ReadOnlySpan<Row> property (implicit impl) vs Row[] field
    string? LinesMember,
    string? LineTypeFqn,
    EquatableArray<ColumnModel> Columns,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics,
    EquatableArray<RowMemberModel> RowMembers = default,
    bool RowPositional = true)
{
    public bool IsLineTable
    {
        get
        {
            foreach (var c in Columns) if (c.IsLineField) return true;
            return false;
        }
    }
}
