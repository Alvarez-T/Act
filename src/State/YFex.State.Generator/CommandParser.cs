using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.State.Generator
{
    internal enum ReturnTypeKind : byte
    {
        /// <summary>void or any unrecognised type — generates a sync command.</summary>
        Void = 0,
        /// <summary>System.Threading.Tasks.Task — async, no result.</summary>
        Task = 1,
        /// <summary>System.Threading.Tasks.Task&lt;T&gt; — async, result of type T.</summary>
        TaskOfT = 2,
        /// <summary>System.Threading.Tasks.ValueTask — async, no result. Enables fast-path check.</summary>
        ValueTask = 3,
        /// <summary>System.Threading.Tasks.ValueTask&lt;T&gt; — async, result of type T. Enables fast-path + auto-flatten.</summary>
        ValueTaskOfT = 4,
    }

    internal static class CommandParser
    {
        public static bool IsCandidateSyntax(SyntaxNode node, CancellationToken _)
            => node is MethodDeclarationSyntax;

        public static CommandMethodRawModel? Transform(
            GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (ctx.TargetSymbol is not IMethodSymbol method) return null;

            INamedTypeSymbol? containingType = method.ContainingType;
            if (containingType is null) return null;

            if (!IsPartialClass(containingType)) return null;
            if (!InheritsStateObject(containingType)) return null;

            // ── Return type classification ───────────────────────────────────
            ReturnTypeKind returnKind;
            string? resultTypeName = null;

            if (method.ReturnType is INamedTypeSymbol returnType)
            {
                // Use OriginalDefinition for robust matching regardless of generic args
                string origDef = returnType.OriginalDefinition.ToDisplayString();
                switch (origDef)
                {
                    case "System.Threading.Tasks.Task":
                        returnKind = ReturnTypeKind.Task;
                        break;
                    case "System.Threading.Tasks.Task<TResult>":
                        returnKind = ReturnTypeKind.TaskOfT;
                        resultTypeName = returnType.TypeArguments[0]
                            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        break;
                    case "System.Threading.Tasks.ValueTask":
                        returnKind = ReturnTypeKind.ValueTask;
                        break;
                    case "System.Threading.Tasks.ValueTask<TResult>":
                        returnKind = ReturnTypeKind.ValueTaskOfT;
                        resultTypeName = returnType.TypeArguments[0]
                            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        break;
                    default:
                        // void, or async method whose return type Roslyn doesn't model as named
                        returnKind = method.IsAsync && method.ReturnType.SpecialType == SpecialType.None
                            ? ReturnTypeKind.Task   // rare: async without explicit return type marker
                            : ReturnTypeKind.Void;
                        break;
                }
            }
            else
            {
                returnKind = ReturnTypeKind.Void;
            }

            bool isAsync = returnKind != ReturnTypeKind.Void;

            // ── CancellationToken parameter ──────────────────────────────────
            bool hasCancellationToken = false;
            foreach (var param in method.Parameters)
            {
                if (param.Type.Name == "CancellationToken")
                {
                    hasCancellationToken = true;
                    break;
                }
            }

            // ── [StateCommand] attribute properties ──────────────────────────
            bool includeCancelCommand = false;
            string? cancelCommandName = null;
            string? targetPropertyName = null;

            foreach (var attr in ctx.Attributes)
            {
                foreach (var named in attr.NamedArguments)
                {
                    switch (named.Key)
                    {
                        case "IncludeCancelCommand" when named.Value.Value is bool b:
                            includeCancelCommand = b;
                            break;
                        case "CancelCommandName" when named.Value.Value is string s:
                            cancelCommandName = s;
                            break;
                        case "TargetProperty" when named.Value.Value is string tp:
                            targetPropertyName = tp;
                            break;
                    }
                }
            }

            // ── Command name derivation ──────────────────────────────────────
            string methodBaseName = method.Name;
            if (methodBaseName.EndsWith("Async", StringComparison.Ordinal))
                methodBaseName = methodBaseName.Substring(0, methodBaseName.Length - 5);

            string commandPropertyName = methodBaseName + "Command";
            cancelCommandName ??= "Cancel" + commandPropertyName;

            string ns = containingType.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : containingType.ContainingNamespace.ToDisplayString();

            // TargetProperty only makes sense when the method returns a value
            bool hasResult = returnKind == ReturnTypeKind.TaskOfT || returnKind == ReturnTypeKind.ValueTaskOfT;
            bool isTargetPropertyOnVoid = targetPropertyName is not null && !hasResult;

            return new CommandMethodRawModel(
                Namespace: ns,
                ClassName: containingType.Name,
                TypeParameters: GetTypeParameters(containingType),
                MethodName: method.Name,
                CommandPropertyName: commandPropertyName,
                IsAsync: isAsync,
                ReturnKind: returnKind,
                ResultTypeName: resultTypeName,
                TargetPropertyName: isTargetPropertyOnVoid ? null : targetPropertyName,
                HasCancellationToken: hasCancellationToken,
                IncludeCancelCommand: includeCancelCommand && isAsync && hasCancellationToken,
                CancelCommandName: cancelCommandName,
                IsSealed: containingType.IsSealed,
                IsMissingCancellationToken: includeCancelCommand && (!isAsync || !hasCancellationToken),
                IsTargetPropertyOnVoid: isTargetPropertyOnVoid);
        }

        public static IEnumerable<CommandClassModel> GroupByClass(
            ImmutableArray<CommandMethodRawModel> all, CancellationToken _)
        {
            var groups = new Dictionary<string, List<CommandMethodRawModel>>();
            foreach (var m in all)
            {
                string key = string.IsNullOrEmpty(m.Namespace) ? m.ClassName : m.Namespace + "." + m.ClassName;
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = new List<CommandMethodRawModel>();
                list.Add(m);
            }

            foreach (var kvp in groups)
            {
                var first = kvp.Value[0];
                yield return new CommandClassModel(
                    Namespace: first.Namespace,
                    ClassName: first.ClassName,
                    TypeParameters: first.TypeParameters,
                    Methods: new EquatableArray<CommandMethodRawModel>(kvp.Value.ToArray()),
                    IsSealed: first.IsSealed);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static bool IsPartialClass(INamedTypeSymbol type)
        {
            foreach (var decl in type.DeclaringSyntaxReferences)
            {
                if (decl.GetSyntax() is ClassDeclarationSyntax cls)
                    foreach (var mod in cls.Modifiers)
                        if (mod.IsKind(SyntaxKind.PartialKeyword)) return true;
            }
            return false;
        }

        private static bool InheritsStateObject(INamedTypeSymbol type)
        {
            var current = type.BaseType;
            while (current is not null)
            {
                if (current.Name == "StateObject" &&
                    current.ContainingNamespace?.ToDisplayString() == "YFex.State")
                    return true;
                current = current.BaseType;
            }
            return false;
        }

        private static EquatableArray<string> GetTypeParameters(INamedTypeSymbol type)
        {
            if (type.TypeParameters.IsEmpty) return EquatableArray<string>.Empty;
            var names = new string[type.TypeParameters.Length];
            for (int i = 0; i < names.Length; i++)
                names[i] = type.TypeParameters[i].Name;
            return new EquatableArray<string>(names);
        }
    }

    internal readonly record struct CommandMethodRawModel(
        string Namespace,
        string ClassName,
        EquatableArray<string> TypeParameters,
        string MethodName,
        string CommandPropertyName,
        bool IsAsync,
        ReturnTypeKind ReturnKind,
        string? ResultTypeName,
        string? TargetPropertyName,
        bool HasCancellationToken,
        bool IncludeCancelCommand,
        string CancelCommandName,
        bool IsSealed,
        bool IsMissingCancellationToken,
        bool IsTargetPropertyOnVoid
    ) : IEquatable<CommandMethodRawModel>;

    internal readonly record struct CommandClassModel(
        string Namespace,
        string ClassName,
        EquatableArray<string> TypeParameters,
        EquatableArray<CommandMethodRawModel> Methods,
        bool IsSealed
    ) : IEquatable<CommandClassModel>;
}
