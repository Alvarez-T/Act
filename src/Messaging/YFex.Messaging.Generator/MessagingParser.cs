using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Messaging.Generator;

internal static class MessagingParser
{
    private const string SubscribeAttributeName    = "SubscribeAttribute";
    private const string SubscribeAttributeNs      = "YFex.Messaging";
    private const string SubscribeShortName        = "Subscribe";

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
        var classDecl    = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        ct.ThrowIfCancellationRequested();

        if (semanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol)
            return null;

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

                // Only process the first SubscribeAttribute per method for now;
                // AllowMultiple on the same method for different T is handled by
                // having multiple [Subscribe<T>] attributes — each gets its own iteration.
            }
        }

        if (methods.Count == 0) return null;

        return new SubscribeClassModel(
            namespaceName,
            classSymbol.Name,
            new EquatableArray<SubscribeMethodModel>(methods.ToArray()));
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
}
