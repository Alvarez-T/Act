using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace YFex.Cqrs.Analyzer;

/// <summary>
/// Diagnostics:
/// YFCACHE001 — [Live(Cache=ClientPersistent)] on a query that doesn't implement ICacheable.
/// YFCACHE002 — ICacheable on a record that doesn't implement IQuery&lt;T&gt;.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CacheAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor YFCACHE001 = new(
        id:                 "YFCACHE001",
        title:              "LiveCache.ClientPersistent requires ICacheable",
        messageFormat:      "[Live(Cache = ClientPersistent)] on '{0}' targets a query that does not implement ICacheable.",
        category:           "YFex.Cqrs",
        defaultSeverity:    DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:        "ClientPersistent caching requires the backing query record to implement ICacheable.");

    public static readonly DiagnosticDescriptor YFCACHE002 = new(
        id:                 "YFCACHE002",
        title:              "ICacheable requires IQuery<T>",
        messageFormat:      "'{0}' implements ICacheable but does not implement IQuery<T>. ICacheable is only valid on query records.",
        category:           "YFex.Cqrs",
        defaultSeverity:    DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(YFCACHE001, YFCACHE002);

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

        bool implementsICacheable = type.AllInterfaces.Any(
            i => !i.IsGenericType && i.Name == "ICacheable" &&
                 i.ContainingNamespace.ToDisplayString() == "YFex.Cqrs");

        bool implementsIQuery = type.AllInterfaces.Any(
            i => i.IsGenericType && i.Name == "IQuery" &&
                 i.ContainingNamespace.ToDisplayString() == "YFex.Cqrs");

        if (implementsICacheable && !implementsIQuery)
        {
            foreach (var location in type.Locations)
                ctx.ReportDiagnostic(Diagnostic.Create(YFCACHE002, location, type.Name));
        }
    }
}
