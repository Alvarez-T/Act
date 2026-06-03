using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace YFex.Cqrs.SourceGenerator;

/// <summary>
/// Incremental Source Generator that auto-generates CQRS static helper methods
/// for partial classes containing nested Query, Command, or Events static classes,
/// and emits an AddYFexConfigurations DI registration extension.
///
/// Pipeline A — Static call helpers (always active):
///   For each aggregate's nested records, emits ValueTask-returning static helpers
///   that call YFexDispatcherProvider.Current.QueryAsync / CommandAsync / PublishAsync.
///
/// Pipeline B — AddYFexConfigurations registration (always active):
///   Scans for IAggregateConfiguration implementations and emits a DI registration extension.
/// </summary>
[Generator]
public sealed class CQRSGenerator : IIncrementalGenerator
{
    private const string IQueryInterface        = "YFex.Cqrs.IQuery";
    private const string ICommandInterface      = "YFex.Cqrs.ICommand";
    private const string ICommandTInterface     = "YFex.Cqrs.ICommand";   // generic variant, same base name
    private const string IEventInterface        = "YFex.Cqrs.IEvent";
    private const string IQueueableInterface    = "YFex.Cqrs.IQueueable";
    private const string IAggregateConfigBase   = "YFex.Cqrs.Configuration.IAggregateConfiguration";

    private const string QueryClassName   = "Queries";
    private const string CommandClassName = "Commands";
    private const string EventsClassName  = "Events";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ── Pipeline A: Static helpers ─────────────────────────────────────────
        IncrementalValuesProvider<ClassToGenerate> helperModels =
            context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidateClass(node),
                    transform: static (ctx, ct)  => GetClassToGenerate(ctx, ct))
                .Where(static m => m is not null && m.Value.HasAnyMembers)
                .Select(static (m, _) => m!.Value);

        context.RegisterSourceOutput(helperModels,
            static (spc, model) => spc.AddSource(
                $"{model.ClassName}.g.cs",
                CodeBuilder.GenerateSource(model)));

        // ── Pipeline B: AddYFexConfigurations ─────────────────────────────────
        IncrementalValuesProvider<ConfigRegistration> configModels =
            context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsConfigurationClass(node),
                    transform: static (ctx, ct)  => GetConfigRegistration(ctx, ct))
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);

        IncrementalValueProvider<ImmutableArray<ConfigRegistration>> allConfigs =
            configModels.Collect();

        context.RegisterSourceOutput(allConfigs,
            static (spc, registrations) =>
            {
                if (registrations.IsDefaultOrEmpty) return;
                spc.AddSource(
                    "YFexConfigurationRegistrations.g.cs",
                    CodeBuilder.GenerateConfigRegistration(registrations));
            });

        // ── Pipeline C: Fusion RPC contracts (gated on YFEX_RPC symbol) ───────
        // Detect whether the consumer project has the YFEX_RPC preprocessor symbol.
        // This symbol is injected automatically by YFex.Messaging.Rpc.targets when
        // the YFex.Messaging.Rpc NuGet package is referenced.
        IncrementalValueProvider<bool> isRpcEnabled =
            context.ParseOptionsProvider.Select(static (opts, _) =>
                opts is Microsoft.CodeAnalysis.CSharp.CSharpParseOptions cpo &&
                cpo.PreprocessorSymbolNames.Contains("YFEX_RPC"));

        // Combine each aggregate model with the YFEX_RPC flag and filter.
        IncrementalValuesProvider<ClassToGenerate> rpcModels =
            helperModels
                .Combine(isRpcEnabled)
                .Where(static pair => pair.Right)
                .Select(static (pair, _) => pair.Left);

        // Emit three files per aggregate: interface, server impl, and registrations.
        context.RegisterSourceOutput(rpcModels, static (spc, model) =>
        {
            var (ifaceHint, ifaceSrc) = RpcContractCodeBuilder.GenerateInterface(model);
            spc.AddSource(ifaceHint, ifaceSrc);

            var (implHint, implSrc) = RpcContractCodeBuilder.GenerateServerImpl(model);
            spc.AddSource(implHint, implSrc);

            var (regHint, regSrc) = RpcContractCodeBuilder.GenerateRegistrations(model);
            spc.AddSource(regHint, regSrc);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pipeline A helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsCandidateClass(SyntaxNode node)
    {
        if (node is not ClassDeclarationSyntax cls) return false;

        bool isPartial = cls.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        if (!isPartial) return false;

        return cls.Members.OfType<ClassDeclarationSyntax>().Any(nested =>
        {
            bool isNamed = nested.Identifier.ValueText is QueryClassName or CommandClassName or EventsClassName;
            bool isNestedPartial = nested.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
            return isNamed && isNestedPartial;
        });
    }

    private static ClassToGenerate? GetClassToGenerate(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        var model     = ctx.SemanticModel;
        ct.ThrowIfCancellationRequested();

        if (model.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol) return null;

        string ns        = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : classSymbol.ContainingNamespace.ToDisplayString();
        string className = classSymbol.Name;

        var queries  = new List<QueryToGenerate>();
        var commands = new List<CommandToGenerate>();
        var events   = new List<EventToGenerate>();

        foreach (var nested in classSymbol.GetTypeMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (!nested.IsStatic) continue;

            switch (nested.Name)
            {
                case QueryClassName:   ExtractQueries(nested, queries, ct);   break;
                case CommandClassName: ExtractCommands(nested, commands, ct);  break;
                case EventsClassName:  ExtractEvents(nested, events, ct);     break;
            }
        }

        return new ClassToGenerate(ns, className,
            new EquatableArray<QueryToGenerate>(queries),
            new EquatableArray<CommandToGenerate>(commands),
            new EquatableArray<EventToGenerate>(events));
    }

    private static void ExtractQueries(
        INamedTypeSymbol queryClass,
        List<QueryToGenerate> output,
        CancellationToken ct)
    {
        foreach (var member in queryClass.GetTypeMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (!member.IsRecord) continue;

            string? returnType = null;
            foreach (var iface in member.AllInterfaces)
            {
                if (iface.IsGenericType && GetBaseInterfaceName(iface) == IQueryInterface)
                {
                    returnType = iface.TypeArguments[0].ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat
                            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
                    break;
                }
            }

            if (returnType is null) continue;

            // Skip if user already declared a method with the same name (override guard)
            string methodName = CodeBuilder.GetQueryMethodName(member.Name);
            if (queryClass.GetMembers(methodName).Any(m => m.Kind == SymbolKind.Method)) continue;

            output.Add(new QueryToGenerate(
                member.Name, methodName, returnType,
                new EquatableArray<ParameterInfo>(ExtractParameters(member))));
        }
    }

    private static void ExtractCommands(
        INamedTypeSymbol commandClass,
        List<CommandToGenerate> output,
        CancellationToken ct)
    {
        foreach (var member in commandClass.GetTypeMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (!member.IsRecord) continue;

            bool isQueueable = member.AllInterfaces
                .Any(i => !i.IsGenericType && GetBaseInterfaceName(i) == IQueueableInterface);

            // ICommand<TResult>
            string? resultType = null;
            foreach (var iface in member.AllInterfaces)
            {
                if (iface.IsGenericType && GetBaseInterfaceName(iface) == ICommandInterface)
                {
                    resultType = iface.TypeArguments[0].ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat
                            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
                    break;
                }
            }

            // Confirm it implements ICommand or ICommand<T>
            bool implementsICommand = resultType is not null ||
                member.AllInterfaces.Any(i => !i.IsGenericType && GetBaseInterfaceName(i) == ICommandInterface);

            if (!implementsICommand) continue;

            string methodName = CodeBuilder.GetCommandMethodName(member.Name);
            if (commandClass.GetMembers(methodName).Any(m => m.Kind == SymbolKind.Method)) continue;

            output.Add(new CommandToGenerate(
                member.Name, methodName,
                new EquatableArray<ParameterInfo>(ExtractParameters(member)),
                resultType,
                isQueueable));
        }
    }

    private static void ExtractEvents(
        INamedTypeSymbol eventsClass,
        List<EventToGenerate> output,
        CancellationToken ct)
    {
        foreach (var member in eventsClass.GetTypeMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (!member.IsRecord) continue;

            bool implementsIEvent = member.AllInterfaces
                .Any(i => !i.IsGenericType && GetBaseInterfaceName(i) == IEventInterface);
            if (!implementsIEvent) continue;

            string methodName = "Raise";
            if (eventsClass.GetMembers(methodName)
                .Any(m => m.Kind == SymbolKind.Method &&
                          m is IMethodSymbol ms &&
                          ms.Parameters.Length > 0 &&
                          ms.Parameters[0].Type.Name == member.Name)) continue;

            output.Add(new EventToGenerate(member.Name, methodName));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pipeline B helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsConfigurationClass(SyntaxNode node)
    {
        if (node is not ClassDeclarationSyntax cls) return false;
        return !cls.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword)) &&
               cls.BaseList?.Types.Count > 0;
    }

    private static ConfigRegistration? GetConfigRegistration(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not ClassDeclarationSyntax cls) return null;
        if (ctx.SemanticModel.GetDeclaredSymbol(cls, ct) is not INamedTypeSymbol symbol) return null;

        // Skip file-local types (C# 11 `file` modifier) — they are not reachable from
        // the generated file; attempting to reference them causes compilation errors.
        if (symbol.IsFileLocal) return null;

        // Skip private/protected types — not accessible outside their declaring scope.
        if (symbol.DeclaredAccessibility == Accessibility.Private ||
            symbol.DeclaredAccessibility == Accessibility.Protected ||
            symbol.DeclaredAccessibility == Accessibility.ProtectedOrInternal ||
            symbol.DeclaredAccessibility == Accessibility.ProtectedAndInternal)
            return null;

        var configs = new List<(string InterfaceName, string AggregateType)>();

        foreach (var iface in symbol.AllInterfaces)
        {
            if (!iface.IsGenericType) continue;
            string baseName = GetBaseInterfaceName(iface);
            if (baseName != IAggregateConfigBase &&
                baseName != IAggregateConfigBase.Replace(".Configuration.", ".Configuration.IServer") &&
                baseName != IAggregateConfigBase.Replace(".Configuration.", ".Configuration.IClient") &&
                !baseName.EndsWith("AggregateConfiguration", System.StringComparison.Ordinal)) continue;

            string aggTypeName = iface.TypeArguments[0].ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
                    .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));

            configs.Add((iface.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
                    .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)),
                aggTypeName));
        }

        if (configs.Count == 0) return null;

        string ns = symbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : symbol.ContainingNamespace.ToDisplayString();
        string fqn = string.IsNullOrEmpty(ns) ? symbol.Name : $"{ns}.{symbol.Name}";

        return new ConfigRegistration(fqn, configs);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static List<ParameterInfo> ExtractParameters(INamedTypeSymbol record)
    {
        var result = new List<ParameterInfo>();
        IMethodSymbol? primaryCtor = record.Constructors.FirstOrDefault(c => !c.IsImplicitlyDeclared);
        if (primaryCtor is null) return result;

        foreach (var param in primaryCtor.Parameters)
        {
            string typeName = param.Type.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
                    .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
            result.Add(new ParameterInfo(typeName, param.Name, CodeBuilder.ToCamelCase(param.Name)));
        }
        return result;
    }

    private static string GetBaseInterfaceName(INamedTypeSymbol iface)
    {
        var ns = iface.ContainingNamespace;
        string nsPart = (ns is null || ns.IsGlobalNamespace) ? string.Empty : ns.ToDisplayString() + ".";
        return nsPart + iface.Name;
    }
}
