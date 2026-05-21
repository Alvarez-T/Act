using Microsoft.CodeAnalysis;

namespace YFex.State.Generator;

internal static class DiagnosticDescriptors
{
    private const string HelpBase = "https://yfex.dev/diagnostics/";

    // ── YFEX0100–0199  Observable errors ────────────────────────────────────

    public static readonly DiagnosticDescriptor ObservableOnNonPartial = new(
        id: "YFEX0101",
        title: "[Observable] requires partial",
        messageFormat: "Property or field '{0}' marked [Observable] must be declared in a partial class",
        category: "YFex.Observable",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "YFEX0101");

    public static readonly DiagnosticDescriptor ObservableNotInStateObject = new(
        id: "YFEX0102",
        title: "[Observable] outside StateObject",
        messageFormat: "'{0}' is marked [Observable] but '{1}' does not inherit from StateObject<T>",
        category: "YFex.Observable",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "YFEX0102");

    public static readonly DiagnosticDescriptor NotifyOnTaskCompletionInvalidType = new(
        id: "YFEX0110",
        title: "[NotifyOnTaskCompletion] requires Task or Task<T>",
        messageFormat: "'{0}' is marked [NotifyOnTaskCompletion] but its type is not System.Threading.Tasks.Task or Task<T>. The attribute will be ignored.",
        category: "YFex.Observable",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "YFEX0110");

    // ── YFEX0200–0299  Computed errors ──────────────────────────────────────

    public static readonly DiagnosticDescriptor ComputedCycle = new(
        id: "YFEX0210",
        title: "[Computed] dependency cycle detected",
        messageFormat: "Computed property '{0}' is part of a dependency cycle: {1}",
        category: "YFex.Computed",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "YFEX0210");

    public static readonly DiagnosticDescriptor ComputedDeepChain = new(
        id: "YFEX0211",
        title: "[Computed] deep property chain — only root tracked",
        messageFormat: "Expression '{0}' contains a member-access chain; only the root '{1}' is tracked. Use [Computed(DependsOn = ...)] to be explicit.",
        category: "YFex.Computed",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "YFEX0211");

    public static readonly DiagnosticDescriptor ComputedMethodCall = new(
        id: "YFEX0212",
        title: "[Computed] expression contains a method call",
        messageFormat: "Computed property '{0}' calls method '{1}'; auto-inference may be incomplete. Specify [Computed(DependsOn = ...)] to silence this warning.",
        category: "YFex.Computed",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "YFEX0212");

    // ── YFEX0300–0399  Validation errors ────────────────────────────────────

    public static readonly DiagnosticDescriptor ValidatorMustBeStaticAbstract = new(
        id: "YFEX0301",
        title: "Validator must implement static abstract interface",
        messageFormat: "'{0}' does not implement IValidator<{1}> or IAsyncValidator<{1}>",
        category: "YFex.Validation",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "YFEX0301");

    // ── YFEX0600–0699  Command errors ────────────────────────────────────────

    public static readonly DiagnosticDescriptor CommandNotPartial = new(
        id: "YFEX0601",
        title: "[StateCommand] method must be in a partial class",
        messageFormat: "Method '{0}' is marked [StateCommand] but its containing type '{1}' is not partial",
        category: "YFex.Commands",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "YFEX0601");

    public static readonly DiagnosticDescriptor CancelCommandMissingCancellationToken = new(
        id: "YFEX0610",
        title: "IncludeCancelCommand requires a CancellationToken parameter",
        messageFormat: "'{0}' has IncludeCancelCommand=true but is not an async method with a CancellationToken parameter. The cancel command will not be generated.",
        category: "YFex.Commands",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "YFEX0610");

    public static readonly DiagnosticDescriptor TargetPropertyOnVoidCommand = new(
        id: "YFEX0611",
        title: "TargetProperty requires a result-returning return type",
        messageFormat: "'{0}' sets TargetProperty but does not return Task<T> or ValueTask<T>. The TargetProperty assignment will be skipped.",
        category: "YFex.Commands",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "YFEX0611");
}
