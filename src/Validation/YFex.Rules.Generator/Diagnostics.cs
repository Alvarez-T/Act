using Microsoft.CodeAnalysis;

namespace YFex.Rules.Generator;

internal static class Diagnostics
{
    private const string Category = "YFex.Rules";

    private static DiagnosticDescriptor D(string id, string title, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error)
        => new(id, title, message, Category, severity, isEnabledByDefault: true);

    // Situations
    public static readonly DiagnosticDescriptor SituationNotPartial = D("YFR0001", "Situation must be partial",
        "Situation '{0}' must be partial (also its containing types)");
    public static readonly DiagnosticDescriptor UnsupportedFieldType = D("YFR0002", "Unsupported situation field type",
        "Field '{0}' of situation '{1}' has unsupported type '{2}' (scalars, enums, IRawValue types, single-scalar ids, ImmutableArray of a [Situation])");
    public static readonly DiagnosticDescriptor InvalidEffectiveDate = D("YFR0003", "Invalid [RuleEffectiveDate] field",
        "Situation '{0}': [RuleEffectiveDate] must be on exactly one DateOnly/DateTime field ({1})");
    public static readonly DiagnosticDescriptor TooManyTiers = D("YFR0007", "Too many rule tiers",
        "Situation '{0}': at most 4 [RuleTier] fields are supported");
    public static readonly DiagnosticDescriptor TooManyLines = D("YFR0004", "Only one lines field",
        "Situation '{0}' has more than one ImmutableArray of situations; only one level of lines is supported");
    public static readonly DiagnosticDescriptor TooManyFields = D("YFR0005", "Situation has more than 64 fields",
        "Situation '{0}' has {1} fields; DiffMask folds fields ≥ 63 into bit 63", DiagnosticSeverity.Warning);
    public static readonly DiagnosticDescriptor NoConstructor = D("YFR0006", "No constructor for deserialization",
        "Situation '{0}' needs a constructor taking its non-derived fields in declaration order (records get it from the primary constructor)");

    // Rules
    public static readonly DiagnosticDescriptor RuleShape = D("YFR0010", "Rule method missing or mistyped",
        "Rule '{0}': {1}");
    public static readonly DiagnosticDescriptor UnknownGroup = D("YFR0011", "Unknown rule group",
        "'{0}' joins group '{1}', which no [RuleSet<{2}>] of this compilation declares");
    public static readonly DiagnosticDescriptor AmbiguousGroup = D("YFR0012", "Ambiguous rule group",
        "'{0}' joins group '{1}', declared by several rule sets of '{2}' ({3}); set RuleSet = \"…\"");
    public static readonly DiagnosticDescriptor RuleNotAccessible = D("YFR0013", "Rule type not accessible",
        "Rule '{0}' must be at least internal (and so must its containing types) to be called by the generated rule set");
    public static readonly DiagnosticDescriptor DuplicateCode = D("YFR0014", "Duplicate rule code",
        "Code '{0}' is used by several rules/tables of rule set '{1}'");
    public static readonly DiagnosticDescriptor NoGroup = D("YFR0015", "Rule without group",
        "Rule '{0}' must set Group = nameof(<RuleSet>.<Group>)");
    public static readonly DiagnosticDescriptor NotSplittable = D("YFR0016", "Applicability not split",
        "Rule '{0}': Applies is not an &&-chain of simple conditions; the trace reports a single condition with message key '{1}'",
        DiagnosticSeverity.Info);
    public static readonly DiagnosticDescriptor InvalidDate = D("YFR0017", "Invalid effective date",
        "Rule '{0}': '{1}' is not an ISO date (yyyy-MM-dd)");

    // Rule sets
    public static readonly DiagnosticDescriptor RuleSetNotStaticPartial = D("YFR0020", "Rule set must be static partial",
        "Rule set '{0}' must be a static partial class (also its containing types)");
    public static readonly DiagnosticDescriptor RuleSetWithoutGroups = D("YFR0021", "Rule set without groups",
        "Rule set '{0}' declares no static readonly RuleGroup fields", DiagnosticSeverity.Warning);
    public static readonly DiagnosticDescriptor NotAnAmount = D("YFR0022", "Base/LineBase must return an amount",
        "Rule set '{0}': {1} returns '{2}', which is not an amount (an IAmount<T> type such as Money or Mass, or decimal/int/long/double)");
    public static readonly DiagnosticDescriptor AmountMismatch = D("YFR0023", "Base and LineBase amounts differ",
        "Rule set '{0}': Base and LineBase must return the same amount type");

    // View models (D31)
    public static readonly DiagnosticDescriptor SourceNotPartial = D("YFR0030", "[SituationSource] view model must be partial",
        "View model '{0}' must be partial (also its containing types) to receive BuildSituation()");
    public static readonly DiagnosticDescriptor SourceMemberMismatch = D("YFR0031", "Situation source member does not fit",
        "'{0}.{1}': {2}");

    // Tables
    public static readonly DiagnosticDescriptor UnknownColumnField = D("YFR0101", "Unknown table column field",
        "Column '{0}' of table '{1}' binds '{2}', which is neither a field of '{3}', a line field, nor a derived field of its rule set");
    public static readonly DiagnosticDescriptor ColumnTypeMismatch = D("YFR0102", "Table column type mismatch",
        "Column '{0}' of table '{1}': {2}");
    public static readonly DiagnosticDescriptor TableNotPartial = D("YFR0103", "Decision table must be partial",
        "Decision table '{0}' must be a partial class (also its containing types)");
    public static readonly DiagnosticDescriptor NoRow = D("YFR0104", "Decision table without Row",
        "Decision table '{0}' must declare a nested 'record struct Row' whose members carry [When]/[Then]");
    public static readonly DiagnosticDescriptor InvalidThen = D("YFR0105", "Invalid [Then] column",
        "Column '{0}' of table '{1}': {2}");
    public static readonly DiagnosticDescriptor ColumnWithoutRole = D("YFR0106", "Row member without [When]/[Then]",
        "Member '{0}' of '{1}' has neither [When] nor [Then]; it is data only (ignored by evaluation)", DiagnosticSeverity.Info);
    public static readonly DiagnosticDescriptor UnknownTier = D("YFR0107", "Unknown rule tier",
        "Decision table '{0}': EditableAt names '{1}', which is not a [RuleTier] field of '{2}'");
    public static readonly DiagnosticDescriptor UnresolvedPermission = D("YFR0108", "Permission argument not resolvable",
        "Decision table '{0}': {1} must be a string id or a permission catalog constant (Catalog.Ids.X) whose property carries [Permission]");

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "YFR0001" => SituationNotPartial,
        "YFR0002" => UnsupportedFieldType,
        "YFR0003" => InvalidEffectiveDate,
        "YFR0007" => TooManyTiers,
        "YFR0004" => TooManyLines,
        "YFR0005" => TooManyFields,
        "YFR0006" => NoConstructor,
        "YFR0010" => RuleShape,
        "YFR0011" => UnknownGroup,
        "YFR0012" => AmbiguousGroup,
        "YFR0013" => RuleNotAccessible,
        "YFR0014" => DuplicateCode,
        "YFR0015" => NoGroup,
        "YFR0016" => NotSplittable,
        "YFR0017" => InvalidDate,
        "YFR0020" => RuleSetNotStaticPartial,
        "YFR0021" => RuleSetWithoutGroups,
        "YFR0022" => NotAnAmount,
        "YFR0023" => AmountMismatch,
        "YFR0030" => SourceNotPartial,
        "YFR0031" => SourceMemberMismatch,
        "YFR0101" => UnknownColumnField,
        "YFR0102" => ColumnTypeMismatch,
        "YFR0103" => TableNotPartial,
        "YFR0104" => NoRow,
        "YFR0105" => InvalidThen,
        "YFR0106" => ColumnWithoutRole,
        "YFR0107" => UnknownTier,
        "YFR0108" => UnresolvedPermission,
        _ => throw new System.ArgumentOutOfRangeException(nameof(id), id, null),
    };
}
