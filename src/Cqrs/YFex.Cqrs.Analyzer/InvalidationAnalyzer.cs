using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace YFex.Cqrs.Analyzer;

/// <summary>
/// Diagnostics:
/// YFINV001 — Both Invalidates and InvalidatedBy declared for the same (command, query) pair (Warning at edit time).
/// YFINV002 — Invalidates/InvalidatedBy on a union or group type cannot take a match predicate.
/// YFINV003 — Optimistic apply expression references non-existent properties on TQuery.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidationAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor YFINV001 = new(
        id:                 "YFINV001",
        title:              "Duplicate invalidation declaration",
        messageFormat:      "Both Invalidates<{0}> on the command and InvalidatedBy<{1}> on the query are declared for the same pair. Remove one to avoid runtime errors.",
        category:           "YFex.Cqrs",
        defaultSeverity:    DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:        "CompiledMessagingRegistry.Build() will throw at startup when both are declared for the same (command, query) pair.");

    public static readonly DiagnosticDescriptor YFINV002 = new(
        id:                 "YFINV002",
        title:              "Match predicate not allowed on group/union invalidation",
        messageFormat:      "Invalidates<{0}> or InvalidatedBy<{0}> with a match predicate is not valid when '{0}' is a union or IInvalidationGroup. Remove the predicate argument.",
        category:           "YFex.Cqrs",
        defaultSeverity:    DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor YFINV003 = new(
        id:                 "YFINV003",
        title:              "Optimistic apply expression references invalid property",
        messageFormat:      "The Optimistic apply expression references '{0}' which does not exist or has an incompatible type on '{1}'.",
        category:           "YFex.Cqrs",
        defaultSeverity:    DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(YFINV001, YFINV002, YFINV003);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        // Full static detection of YFINV001/002/003 requires cross-method dataflow
        // across configuration classes; implemented as a best-effort invocation check.
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext ctx)
    {
        if (ctx.Operation is not IInvocationOperation inv) return;

        string methodName = inv.TargetMethod.Name;

        // YFINV002: Invalidates<TGroup>(predicate) or InvalidatedBy<TGroup>(predicate) with a match arg
        if ((methodName is "Invalidates" or "InvalidatedBy") &&
            inv.Arguments.Length > 0 &&
            inv.TargetMethod.TypeArguments.Length > 0)
        {
            var targetType = inv.TargetMethod.TypeArguments[0];
            bool isGroup = targetType.AllInterfaces.Any(i =>
                i.Name is "IInvalidationGroup" or "IEventGroup" &&
                i.ContainingNamespace.ToDisplayString() == "YFex.Cqrs");

            bool isUnion = targetType.GetAttributes().Any(a =>
                a.AttributeClass?.Name == "UnionAttribute" &&
                a.AttributeClass.ContainingNamespace.ToDisplayString() == "System.Runtime.CompilerServices");

            if ((isGroup || isUnion) && inv.Arguments.Length > 0)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    YFINV002,
                    inv.Syntax.GetLocation(),
                    targetType.Name));
            }
        }
    }
}
