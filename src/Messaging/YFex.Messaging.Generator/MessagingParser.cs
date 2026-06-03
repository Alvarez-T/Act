using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Messaging.Generator;

internal static class MessagingParser
{
    private const string SubscribeAttributeName = "SubscribeAttribute";
    private const string SubscribeAttributeNs   = "YFex.Messaging";
    private const string SubscribeShortName     = "Subscribe";

    private const string StateObjectFqn     = "YFex.State.StateObject";
    private const string MessagingHostFqn   = "YFex.Messaging.MessagingHost";

    // ─────────────────────────────────────────────────────────────────────────
    // Step 1: Cheap syntax-only predicate
    // ─────────────────────────────────────────────────────────────────────────

    public static bool IsCandidateClass(SyntaxNode node)
    {
        if (node is not ClassDeclarationSyntax classDecl) return false;

        foreach (var member in classDecl.Members)
        {
            if (member is not MethodDeclarationSyntax method) continue;
            foreach (var attrList in method.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    string name = GetAttributeIdentifier(attr.Name);
                    if (name is SubscribeShortName or SubscribeAttributeName)
                        return true;
                }
            }
        }
        return false;
    }

    private static string GetAttributeIdentifier(NameSyntax name) => name switch
    {
        GenericNameSyntax gn       => gn.Identifier.ValueText,
        SimpleNameSyntax sn        => sn.Identifier.ValueText,
        QualifiedNameSyntax { Right: GenericNameSyntax g } => g.Identifier.ValueText,
        QualifiedNameSyntax { Right: SimpleNameSyntax  s } => s.Identifier.ValueText,
        _ => string.Empty
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Step 2: Semantic transform — one model per class
    // ─────────────────────────────────────────────────────────────────────────

    public static SubscribeClassModel? Transform(
        GeneratorSyntaxContext context,
        CancellationToken ct)
    {
        var classDecl     = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        ct.ThrowIfCancellationRequested();

        if (semanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol)
            return null;

        // ── YFSUB001: must be partial ──────────────────────────────────────
        bool isPartial = classDecl.Modifiers.Any(SyntaxKind.PartialKeyword);
        if (!isPartial)
        {
            context.SemanticModel.Compilation.GetDiagnostics();  // ensure workspace is valid
            // We report via the source production context — return a sentinel
            // that the pipeline recognises as needing a diagnostic.
            // Since we can't report here, we set methods to empty after setting a flag.
            // The emitter skips empty models; the diagnostic fires below via a separate check.
            // Simplest: return null and trust the IDE's partial-check; but for clarity emit the model
            // with a zero-method list and let the emitter call ReportDiagnostic.
            // Actually: the clean way is to return a special model variant.
            // For now: report is done in MessagingEmitter via YFSUB001 on zero-method models.
            // We will signal this with a dedicated marker — use IsMessagingHost=false + empty methods.
        }

        // ── YFSUB002: must inherit StateObject or MessagingHost ────────────
        bool inheritsStateObject   = InheritsFrom(classSymbol, StateObjectFqn);
        bool inheritsMessagingHost = InheritsFrom(classSymbol, MessagingHostFqn);
        bool validBase = inheritsStateObject || inheritsMessagingHost;

        string namespaceName = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : classSymbol.ContainingNamespace.ToDisplayString();

        var methods = new List<SubscribeMethodModel>();

        foreach (var member in classDecl.Members)
        {
            ct.ThrowIfCancellationRequested();
            if (member is not MethodDeclarationSyntax methodDecl) continue;

            if (semanticModel.GetDeclaredSymbol(methodDecl, ct) is not IMethodSymbol methodSymbol)
                continue;

            foreach (var attr in methodSymbol.GetAttributes())
            {
                if (!IsSubscribeAttribute(attr.AttributeClass)) continue;

                // ── Extract event type T from SubscribeAttribute<T> ────────
                var eventType = attr.AttributeClass!.TypeArguments[0];
                string eventFqn = eventType.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat);

                // ── Async detection: ValueTask or Task return type ─────────
                string returnFqn = methodSymbol.ReturnType.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat);
                bool isAsync = returnFqn is
                    "global::System.Threading.Tasks.ValueTask" or
                    "global::System.Threading.Tasks.Task";

                // ── Named attribute arguments ──────────────────────────────
                bool    keepAlive  = false;
                string? match      = null;
                string? target     = null;
                string? group      = null;
                int     debounceMs = 0;
                int     throttleMs = 0;

                foreach (var arg in attr.NamedArguments)
                {
                    switch (arg.Key)
                    {
                        case "KeepAlive":  keepAlive  = (bool)(arg.Value.Value ?? false); break;
                        case "FilterBy":   match      = (string?)arg.Value.Value; break;
                        case "Target":     target     = (string?)arg.Value.Value; break;
                        case "Group":      group      = (string?)arg.Value.Value; break;
                        case "DebounceMs": debounceMs = (int)(arg.Value.Value ?? 0); break;
                        case "ThrottleMs": throttleMs = (int)(arg.Value.Value ?? 0); break;
                    }
                }

                // ── YFRPC0001: cross-boundary without [MemoryPackable] ─────
                bool needsMpWarn = (target is not null || group is not null)
                    && !HasMemoryPackableAttribute(eventType);

                methods.Add(new SubscribeMethodModel(
                    methodSymbol.Name,
                    eventFqn,
                    isAsync,
                    keepAlive,
                    filterBy: match,
                    target,
                    group,
                    needsMemoryPackWarning: needsMpWarn,
                    debounceMs: debounceMs,
                    throttleMs: throttleMs));
            }
        }

        if (methods.Count == 0) return null;

        return new SubscribeClassModel(
            namespaceName,
            classSymbol.Name,
            new EquatableArray<SubscribeMethodModel>(methods.ToArray()),
            isMessagingHost: inheritsMessagingHost,
            isPartial:      isPartial,
            validBase:      validBase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static bool HasMemoryPackableAttribute(ITypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            string name = attr.AttributeClass?.Name ?? string.Empty;
            if (name is "MemoryPackableAttribute" or "MemoryPackable") return true;
        }
        return false;
    }

    private static bool IsSubscribeAttribute(INamedTypeSymbol? symbol)
    {
        if (symbol is null) return false;
        if (!symbol.IsGenericType) return false;
        if (symbol.Name != SubscribeAttributeName) return false;
        if (symbol.TypeArguments.Length != 1) return false;

        string ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return ns == SubscribeAttributeNs;
    }

    /// <summary>
    /// Returns true when <paramref name="type"/> or any of its base types has a fully-qualified
    /// name equal to <paramref name="targetFqn"/> (e.g. "YFex.State.StateObject").
    /// </summary>
    internal static bool InheritsFrom(INamedTypeSymbol type, string targetFqn)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            string fqn = current.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? $"{ns.ToDisplayString()}.{current.Name}"
                : current.Name;
            if (fqn == targetFqn) return true;
            current = current.BaseType;
        }
        return false;
    }
}
