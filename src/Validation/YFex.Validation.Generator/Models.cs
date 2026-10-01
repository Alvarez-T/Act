using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace YFex.Validation.Generator;

// All models are equatable (records of strings / primitives / EquatableArray) — never ISymbol or
// SyntaxNode — so the incremental pipeline can cache them.

internal enum MemberShape : byte
{
    Other,       // any other type (Required / ValidateWith only)
    String,
    Collection,  // arrays, ImmutableArray<T>, List<T>, IReadOnlyList<T>, ICollection<T>…
    Comparable,  // numbers, dates, Money, Percentual… (IComparable<T>)
}

internal enum CollectionKind : byte
{
    None,
    Array,
    ImmutableArray,
    List,
    IndexedList,  // IReadOnlyList<T> / IList<T> indexer
    CountOnly,    // has Count but no indexer (Each not supported)
}

internal enum RuleKind : byte
{
    Required,
    NotEmpty,
    Length,
    Range,
    Pattern,
    Email,
    TextRule,       // ITextRule / ITextRule<TArg>
    ValueRule,      // IValueRule<T> / IValueRule<T,TArg>
    AsyncValueRule, // IAsyncValueRule<T> — metadata only (not in sync Validate)
}

internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    /// <summary>
    /// In-source location when <paramref name="trees"/> knows the file — required for <c>#pragma warning disable</c> and
    /// .editorconfig severities to apply (an external-file location from <c>Location.Create(path, …)</c> ignores both).
    /// </summary>
    public Location ToLocation(SourceTrees? trees = null)
        => trees?.Find(FilePath) is { } tree && Span.End <= tree.Length
            ? Location.Create(tree, Span)
            : Location.Create(FilePath, Span, LineSpan);

    public static LocationInfo? From(Location? location)
        => location is null || location.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
}

/// <summary>Descriptor key (see <see cref="Diagnostics.ById"/>) + location + message args.</summary>
internal sealed record DiagnosticInfo(string Key, LocationInfo? Location, EquatableArray<string> Args)
{
    public Diagnostic ToDiagnostic(SourceTrees? trees = null)
        => Diagnostic.Create(Diagnostics.ById(Key), Location?.ToLocation(trees), Args.AsSpan().ToArray());
}

/// <summary>
/// Maps file paths back to the compilation's syntax trees at REPORT time. Models stay equatable (paths + spans only);
/// reporting nodes combine with the compilation so the (cached) emission nodes don't have to.
/// </summary>
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

/// <summary>One rule on a member (or on each element of a collection member).</summary>
internal sealed record RuleModel(
    RuleKind Kind,
    string CodeExpr,           // C# expression of the RuleCode: "\"required\"" / "global::X.Code"
    bool IsError,              // Severity.Error (stops the member's chain) vs Warning/Info
    string SeverityExpr,       // "global::YFex.Validation.Severity.Error"
    string? When,              // bool member name
    string? Profile,
    int MinLength,
    int MaxLength,
    string? MinExpr,           // Range: typed C# expression / field name
    string? MaxExpr,
    string? MinRawExpr,        // Range: RawValue expression (null when the type has no RawValue form)
    string? MaxRawExpr,
    string? MinConstLiteral,   // Range on integer primitives: literal for `public const`
    string? MaxConstLiteral,
    string? BoundsTypeFqn,     // Range: static readonly field type for non-literal bounds
    bool UsesOperators,        // Range: compare with < > (primitives) vs Checks.Within
    bool Coerce,
    string? Pattern,
    string? RuleTypeFqn,       // TextRule / ValueRule
    bool HasArgument,
    string? ArgumentExpr,
    string? ArgumentRawExpr,
    bool UnwrapNullable = false) // AsyncValueRule over T on a T? member: pass the unwrapped value
{
    public bool IsConditional => When is not null || Profile is not null;
}

internal sealed record EachModel(
    string ElementFqn,
    CollectionKind Kind,
    MemberShape ElementShape,
    bool ElementIsReferenceType,
    bool ElementIsNullableValue,
    string? ElementValidatableFqn,
    EquatableArray<RuleModel> ElementRules);

internal sealed record MemberModel(
    string Name,
    string TypeFqn,           // declared type, fully qualified (with '?' for nullable references)
    string CheckParamType,    // parameter type used by Rules.X.Check
    ushort Id,
    MemberShape Shape,
    CollectionKind Collection,
    bool IsReferenceType,
    bool IsNullableValue,
    bool CanInferRequired,    // non-nullable reference without explicit Required (P5 inference)
    string? NestedValidatableFqn,
    bool NestedIsNullable,    // member type allows null (reference or Nullable<T>)
    EachModel? Each,
    EquatableArray<RuleModel> Rules,
    bool HasAsyncRules,
    EquatableArray<RuleModel> AsyncRules,    // IAsyncValueRule<T> rules: metadata on DTOs, run by VM hooks
    JsonShapeModel? Json = null);            // Phase 6: JSON Schema export

internal sealed record InvariantModel(
    string MethodName,
    ushort Id,
    bool IsStatic,
    EquatableArray<string> Reads,   // member names; empty + ReadsAll = run always
    bool ReadsAll);

internal sealed record ContainerModel(string Keyword, string NameWithTypeParameters);

internal sealed record TypeModel(
    string Namespace,
    EquatableArray<ContainerModel> Containers,
    string Keyword,                  // class | struct | record | record struct
    string NameWithTypeParameters,
    string FullyQualifiedName,
    string HintName,
    bool IsGeneric,
    bool IsRegistrable,              // non-generic and accessible from a module initializer
    string? BaseValidatableFqn,
    int BaseMemberCount,
    string FieldIdsClass,
    string FieldsClass,
    string RulesClass,
    string InvariantIdsClass,
    EquatableArray<MemberModel> Members,
    EquatableArray<InvariantModel> Invariants,
    EquatableArray<DiagnosticInfo> Diagnostics,
    bool CanEmit,
    LocationInfo? Location,
    VmModel? Vm = null,              // non-null for YFex.State view models (hooks, 07 / D22)
    string? MetadataName = null,     // Compilation.GetTypeByMetadataName key (fluent merge / VM rebuild)
    EquatableArray<string> CompiledFluentKeys = default); // D27: fluent rules compiled here → CompiledFluentRules<T>

// ── Fluent configuration (.Validation(v => …), 03 §4, D23/D27) ───────────────────────

/// <summary>Rules one <c>.Validation</c> lambda declares for one member (only the ones the generator compiles).</summary>
internal sealed record FluentMemberModel(
    string Name,
    EquatableArray<RuleModel> Rules,  // sync + async (split by Kind at merge)
    bool Each);                       // v.Each(c => c.Items): element type validated with its own rules

/// <summary>One <c>.Validation(v => …)</c> call.</summary>
internal sealed record FluentConfigModel(
    string TargetFqn,
    string TargetMetadataName,
    bool TargetEmittable,             // partial, in source, non-generic → rules are compiled into the target
    EquatableArray<FluentMemberModel> Members,
    EquatableArray<string> CompiledKeys,
    EquatableArray<DiagnosticInfo> Diagnostics);

// ── View models (StateObject subclasses) ─────────────────────────────────────────────

/// <summary>A contract member rule chain borrowed by a VM property (<c>global::C.Rules.Name</c>).</summary>
internal sealed record VmBorrowModel(
    int ContractIndex,
    string RulesClassFqn,            // "global::Ns.CreateCustomer.Rules.Document" (null-free: only validatable owners)
    string? Profile);                // contract profile (null = active under every profile)

/// <summary>Per VM member data beyond <see cref="MemberModel"/> (same order as TypeModel.Members).</summary>
internal sealed record VmMemberModel(
    string Name,
    string TypeFqn,                  // exact hook parameter type (matches the State generator's spelling)
    bool HasHook,                    // writable [Observable] of this class → State declares __OnValidate_X
    byte Trigger,                    // YFex.State.ValidateOn: 0 Change, 1 Commit, 2 Submit
    int DebounceMs,                  // [Debounce] on the property → delays async rules
    EquatableArray<string> LegacySync,   // obsolete [ValidateWith(typeof(V))] → V.Validate(value)
    EquatableArray<string> LegacyAsync,  // obsolete [ValidateAsync(typeof(V))] → V.ValidateAsync(value, ct)
    EquatableArray<VmBorrowModel> Borrowed,
    EquatableArray<string> WhenDependents, // members whose When-guarded rules read this member
    ulong InvariantMask,             // bit k = k-th invariant reads this member
    bool ConstraintsFrom = false);   // [ConstraintsFrom] Decision<TS> (07 §9.3): violations → bag (Rule), constraints merged

/// <summary>Contract path → VM member (one per mapped member).</summary>
internal sealed record PathMapEntry(string ContractTemplate, string VmMember);

internal sealed record VmContractModel(
    int Index,
    string Fqn,
    string ShortName,
    string? Profile,
    bool IsValidatable,
    EquatableArray<PathMapEntry> PathMap,
    string ToMethodName,
    string? ToExpr,                  // generated construction; null → partial declaration / user method
    bool EmitToDeclaration);         // emit `public partial C ToC();` (user implements it)

internal sealed record VmModel(
    EquatableArray<VmMemberModel> Members,
    EquatableArray<VmContractModel> Contracts,
    string? ShowExpr,                // bag.Show default ([ValidationOptions] or legacy compatibility)
    string? DefaultProfile,          // first declared [ValidatesAs(Profile)]
    bool BaseHasValidation,          // a base VM has generated validation → override / new
    bool BaseHasContracts,           // a base VM has ApplyServer<T> → new + fallback
    bool HasCqrs = false);           // compilation references YFex.Cqrs → ApplyServer overloads for its results (07 §7)
