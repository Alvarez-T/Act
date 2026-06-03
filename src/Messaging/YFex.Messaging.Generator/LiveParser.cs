using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Messaging.Generator;

internal static class LiveParser
{
    private const string LiveAttributeName  = "LiveAttribute";
    private const string LiveAttributeNs    = "YFex.Messaging";
    private const string LiveShortName      = "Live";

    // ─────────────────────────────────────────────────────────────────────────
    // Step 1: Cheap syntax predicate
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
                    string name = attr.Name switch
                    {
                        SimpleNameSyntax sn  => sn.Identifier.ValueText,
                        QualifiedNameSyntax { Right: SimpleNameSyntax s } => s.Identifier.ValueText,
                        _ => string.Empty
                    };
                    if (name is LiveShortName or LiveAttributeName) return true;
                }
            }
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 2: Semantic transform
    // ─────────────────────────────────────────────────────────────────────────

    public static LiveClassModel? Transform(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var classDecl     = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        ct.ThrowIfCancellationRequested();

        if (semanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol)
            return null;

        string namespaceName = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : classSymbol.ContainingNamespace.ToDisplayString();

        bool inheritsMvvm        = InheritsMvvmStateObject(classSymbol);
        bool inheritsPageViewModel = InheritsPageViewModel(classSymbol);
        var methods              = new List<LiveMethodModel>();
        uint nextBaseId          = 100u;

        foreach (var member in classDecl.Members)
        {
            ct.ThrowIfCancellationRequested();
            if (member is not MethodDeclarationSyntax methodDecl) continue;

            if (semanticModel.GetDeclaredSymbol(methodDecl, ct) is not IMethodSymbol methodSymbol)
                continue;

            foreach (var attr in methodSymbol.GetAttributes())
            {
                if (!IsLiveAttribute(attr.AttributeClass)) continue;

                // ── Resolve T in Task<T> / ValueTask<T> ───────────────────
                string? valueTypeFqn = ResolveValueType(methodSymbol);
                if (valueTypeFqn is null) break; // not a Task<T> / ValueTask<T> method

                // ── Attribute named args ───────────────────────────────────
                int       pollMs         = 0;
                int       cache          = 0; // LiveCache.Local
                int       suspendBehavior = 0; // LiveSuspendBehavior.PauseAndRefreshOnResume
                int       staleTimeMs    = 0;
                string[]? dependsOn      = null;

                foreach (var arg in attr.NamedArguments)
                {
                    switch (arg.Key)
                    {
                        case "PollMs":
                            pollMs = (int)(arg.Value.Value ?? 0);
                            break;
                        case "Cache":
                            cache = (int)(arg.Value.Value ?? 0);
                            break;
                        case "SuspendBehavior":
                            suspendBehavior = (int)(arg.Value.Value ?? 0);
                            break;
                        case "StaleTimeMs":
                            staleTimeMs = (int)(arg.Value.Value ?? 0);
                            break;
                        case "DependsOn":
                            // TypedConstant array of strings
                            if (!arg.Value.IsNull && arg.Value.Kind == Microsoft.CodeAnalysis.TypedConstantKind.Array)
                            {
                                var depList = new System.Collections.Generic.List<string>();
                                foreach (var item in arg.Value.Values)
                                {
                                    if (item.Value is string s && !string.IsNullOrWhiteSpace(s))
                                        depList.Add(s.Trim());
                                }
                                if (depList.Count > 0) dependsOn = depList.ToArray();
                            }
                            break;
                    }
                }

                // ── MemoryPack check for cross-boundary cache tiers ────────
                bool needsMpWarn = cache != 0 // != Local
                    && !HasMemoryPackableAttribute(methodSymbol.ReturnType);

                // ── YFLIV0002: no auto-refresh trigger configured ──────────
                var deps = new EquatableArray<string>(dependsOn ?? System.Array.Empty<string>());
                bool needsRefreshWarn = pollMs == 0 && deps.Count == 0;

                // ── Derive property name ───────────────────────────────────
                string methodName  = methodSymbol.Name;
                string propName    = StripAsyncSuffix(methodName);

                methods.Add(new LiveMethodModel(
                    methodName,
                    propName,
                    valueTypeFqn,
                    nextBaseId,
                    pollMs,
                    cache,
                    needsMpWarn,
                    deps,
                    needsRefreshWarn,
                    suspendBehavior,
                    staleTimeMs));

                nextBaseId += 6; // reserve 6 IDs per method: Value, IsLoading, Error, LastFetchedAt, IsStale, IsFromOfflineCache
                break; // only one [Live] per method
            }
        }

        if (methods.Count == 0) return null;

        return new LiveClassModel(
            namespaceName,
            classSymbol.Name,
            inheritsMvvm,
            inheritsPageViewModel,
            new EquatableArray<LiveMethodModel>(methods.ToArray()));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsLiveAttribute(INamedTypeSymbol? symbol)
    {
        if (symbol is null) return false;
        if (symbol.IsGenericType) return false;
        if (symbol.Name != LiveAttributeName) return false;
        return symbol.ContainingNamespace?.ToDisplayString() == LiveAttributeNs;
    }

    /// <summary>
    /// Returns the fully-qualified T when the return type is Task&lt;T&gt; or ValueTask&lt;T&gt;.
    /// Returns null for void, Task (non-generic), or other return types.
    /// </summary>
    private static string? ResolveValueType(IMethodSymbol method)
    {
        if (method.ReturnType is not INamedTypeSymbol returnType) return null;
        if (!returnType.IsGenericType) return null;
        if (returnType.TypeArguments.Length != 1) return null;

        string baseName = returnType.ContainingNamespace?.ToDisplayString() + "." + returnType.Name;
        if (baseName is not ("System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask"))
            return null;

        return returnType.TypeArguments[0].ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static string StripAsyncSuffix(string name)
    {
        const string suffix = "Async";
        return name.Length > suffix.Length && name.EndsWith(suffix)
            ? name.Substring(0, name.Length - suffix.Length)
            : name;
    }

    private static bool HasMemoryPackableAttribute(ITypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            string name = attr.AttributeClass?.Name ?? string.Empty;
            if (name is "MemoryPackableAttribute" or "MemoryPackable") return true;
        }
        return false;
    }

    private static bool InheritsMvvmStateObject(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name == "MvvmStateObject" &&
                current.ContainingNamespace?.ToDisplayString() == "YFex.State.Mvvm")
                return true;
            current = current.BaseType;
        }
        return false;
    }

    private static bool InheritsPageViewModel(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.Name == "PageViewModel" &&
                current.ContainingNamespace?.ToDisplayString() == "YFex.Mvvm")
                return true;
            current = current.BaseType;
        }
        return false;
    }
}
