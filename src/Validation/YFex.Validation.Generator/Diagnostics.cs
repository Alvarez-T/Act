using Microsoft.CodeAnalysis;

namespace YFex.Validation.Generator;

internal static class Diagnostics
{
    private const string Category = "YFex.Validation";

    public static readonly DiagnosticDescriptor NotPartial = new(
        "YFV0001", "Validatable type must be partial",
        "Type '{0}' declares validation but is not partial (also required for its containing types)",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RuleNotApplicable = new(
        "YFV0002", "Rule not applicable to member type",
        "Rule '{0}' is not applicable to member '{1}' of type '{2}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    // YFV0003 / YFV0004 / YFV0011 belong to fluent configuration parsing (Phase 3).
    public static readonly DiagnosticDescriptor FluentNotPartial = new(
        "YFV0003", "Message with fluent validation is not partial",
        "Message '{0}' has fluent validation but is not partial; metadata is runtime-only",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor FluentNonConstant = new(
        "YFV0004", "Fluent rule has non-constant arguments",
        "Fluent rule on '{0}' has non-constant arguments; enforced at runtime only (no compile-time constraints)",
        Category, DiagnosticSeverity.Info, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedRuleType = new(
        "YFV0005", "Unsupported rule type",
        "'{0}' does not implement a supported rule interface for '{1}' (IValueRule<T>, IValueRule<T,TArg>, ITextRule, ITextRule<TArg>, IAsyncValueRule<T>)",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidRange = new(
        "YFV0006", "Invalid range bounds",
        "Range bounds on '{0}' are invalid for type '{1}': {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidPattern = new(
        "YFV0007", "Invalid regular expression",
        "Pattern on '{0}' is not a valid regular expression: {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidInvariantSignature = new(
        "YFV0008", "Unsupported invariant signature",
        "Invariant '{0}' has an unsupported signature; use 'static void X(in T v, ref ViolationWriter w)' or 'void X(ref ViolationWriter w)'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvariantReadsUnknown = new(
        "YFV0009", "Invariant reads could not be inferred",
        "Invariant '{0}': the members it reads could not be inferred; it runs on every validation",
        Category, DiagnosticSeverity.Info, isEnabledByDefault: true);

    // Same id, Warning severity (design 03 §6: YFV0009 is Info when reads are unknown, Warning on mismatch).
#pragma warning disable RS2001
    public static readonly DiagnosticDescriptor InvariantReadsMismatch = new(
        "YFV0009", "Invariant Reads disagrees with its body",
        "Invariant '{0}': explicit Reads does not include '{1}', which the body reads",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);
#pragma warning restore RS2001

    public static readonly DiagnosticDescriptor DuplicateDataAnnotation = new(
        "YFV0010", "DataAnnotations and YFex.Validation rule stacked",
        "Member '{0}' has both a DataAnnotations and a YFex.Validation attribute for rule '{1}'; the DataAnnotations one is ignored",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ConflictingRule = new(
        "YFV0011", "Rule declared twice with different arguments",
        "Member '{0}' declares rule '{1}' twice with different arguments",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NestedCycle = new(
        "YFV0012", "Cycle in nested validatable graph",
        "Cycle in nested validatable graph through non-nullable members: {0}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GeneratedNameClash = new(
        "YFV0013", "Member clashes with a generated nested class",
        "Member named '{0}' clashes with generated nested class; generated class renamed to 'Validation{0}'",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TooDeep = new(
        "YFV0014", "Too many nested collection levels",
        "More than 4 nested collection levels in path '{0}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidWhen = new(
        "YFV0015", "When member missing or not bool",
        "'When' member '{0}' is missing or not of type bool",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    // ── View models / [ValidatesAs] (07 §4.6, D22) ──────────────────────────────────────

    public static readonly DiagnosticDescriptor ContractMembersUnmapped = new(
        "YFV0020", "Contract members not mapped by the view model",
        "'{0}' does not map contract '{1}' member(s): {2}; {3}",
        Category, DiagnosticSeverity.Info, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MapsToUnknown = new(
        "YFV0021", "[MapsTo] target does not exist on the contract",
        "[MapsTo(\"{0}\")] on '{1}': no [ValidatesAs] contract has a member at that path",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MappedTypeMismatch = new(
        "YFV0022", "View-model property type incompatible with the contract member",
        "'{0}' of type '{1}' cannot be converted implicitly to contract member '{2}.{3}' of type '{4}'",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor LooserThanContract = new(
        "YFV0023", "View-model rule looser than the contract",
        "'{0}': {1} is looser than contract '{2}' ({3}); the server would reject values the form accepts",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AmbiguousContractMatch = new(
        "YFV0024", "Property matched by several contracts",
        "'{0}' matches members of several [ValidatesAs] contracts ({1}); use Prefix, Profile or [MapsTo]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoSetterHook = new(
        "YFV0025", "Validation cannot run when the property changes",
        "'{0}': {1}; its rules run only in ValidateAll",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RequiredInferred = new(
        "YFV0030", "Required inferred from nullability",
        "Required inferred from nullability for: {0}",
        Category, DiagnosticSeverity.Info, isEnabledByDefault: true);

    public static DiagnosticDescriptor ById(string key) => key switch
    {
        "YFV0001" => NotPartial,
        "YFV0002" => RuleNotApplicable,
        "YFV0003" => FluentNotPartial,
        "YFV0004" => FluentNonConstant,
        "YFV0005" => UnsupportedRuleType,
        "YFV0006" => InvalidRange,
        "YFV0007" => InvalidPattern,
        "YFV0008" => InvalidInvariantSignature,
        "YFV0009" => InvariantReadsUnknown,
        "YFV0009w" => InvariantReadsMismatch,
        "YFV0010" => DuplicateDataAnnotation,
        "YFV0011" => ConflictingRule,
        "YFV0012" => NestedCycle,
        "YFV0013" => GeneratedNameClash,
        "YFV0014" => TooDeep,
        "YFV0015" => InvalidWhen,
        "YFV0020" => ContractMembersUnmapped,
        "YFV0021" => MapsToUnknown,
        "YFV0022" => MappedTypeMismatch,
        "YFV0023" => LooserThanContract,
        "YFV0024" => AmbiguousContractMatch,
        "YFV0025" => NoSetterHook,
        "YFV0030" => RequiredInferred,
        _ => throw new System.ArgumentOutOfRangeException(nameof(key), key, null),
    };
}
