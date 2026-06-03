using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace YFex.Cqrs.Analyzer;

/// <summary>
/// Diagnostics:
/// YFQUE001 — IQueueable on a record that doesn't implement ICommand or ICommand&lt;T&gt;.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class QueueAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor YFQUE001 = new(
        id:                 "YFQUE001",
        title:              "IQueueable requires ICommand",
        messageFormat:      "'{0}' implements IQueueable but does not implement ICommand or ICommand<T>. IQueueable is only valid on command records.",
        category:           "YFex.Cqrs",
        defaultSeverity:    DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(YFQUE001);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext ctx)
    {
        if (ctx.Symbol is not INamedTypeSymbol type) return;
        if (!type.IsRecord) return;

        bool implementsIQueueable = type.AllInterfaces.Any(
            i => !i.IsGenericType && i.Name == "IQueueable" &&
                 i.ContainingNamespace.ToDisplayString() == "YFex.Cqrs");

        if (!implementsIQueueable) return;

        bool implementsICommand = type.AllInterfaces.Any(i =>
            i.Name == "ICommand" &&
            i.ContainingNamespace.ToDisplayString() == "YFex.Cqrs");

        if (!implementsICommand)
        {
            foreach (var loc in type.Locations)
                ctx.ReportDiagnostic(Diagnostic.Create(YFQUE001, loc, type.Name));
        }
    }
}
