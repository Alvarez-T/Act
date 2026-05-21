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
/// for partial classes containing nested Query, Command, or Events static classes.
///
/// Detection strategy: CreateSyntaxProvider (not ForAttributeWithMetadataName)
/// because we detect by nested class structure, not by attribute decoration.
///
/// Pipeline:
///   1. SyntaxProvider.CreateSyntaxProvider — cheap syntax filter
///   2. Semantic transform — extract IQuery&lt;T&gt;/ICommand/IEvent records
///   3. RegisterSourceOutput — emit {ClassName}.g.cs
/// </summary>
[Generator]
public sealed class CQRSGenerator : IIncrementalGenerator
{
    // Interface full names used for semantic matching
    private const string IQueryInterface   = "YFex.Cqrs.IQuery";
    private const string ICommandInterface = "YFex.Cqrs.ICommand";
    private const string IEventInterface   = "YFex.Cqrs.IEvent";

    // Nested class names we look for
    private const string QueryClassName   = "Queries";
    private const string CommandClassName = "Commands";
    private const string EventsClassName  = "Events";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ── Step 1: Cheap syntax-only filter ──────────────────────────────────
        // Runs on every keystroke; must be fast (no SemanticModel access).
        // We look for ClassDeclarationSyntax that:
        //   • has the 'partial' modifier
        //   • contains at least one nested ClassDeclarationSyntax
        IncrementalValuesProvider<ClassDeclarationSyntax> candidateSyntax =
            context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _)  => IsCandidateClass(node),
                    transform: static (ctx, ct)  => (ClassDeclarationSyntax)ctx.Node)
                .Where(static x => x is not null)!;

        // ── Step 2: Semantic transform ─────────────────────────────────────────
        // Runs only on classes that passed the syntax filter.
        // Uses SemanticModel to resolve interface implementations.
        IncrementalValuesProvider<ClassToGenerate?> classModels =
            candidateSyntax.Select(static (syntax, ct) =>
            {
                // We need the SemanticModel; it is obtained via the containing
                // compilation that Roslyn passes through the closure.
                // However Select() does not give us GeneratorSyntaxContext directly,
                // so we use a secondary CreateSyntaxProvider just for the semantic pass.
                return (ClassToGenerate?)null; // placeholder — see combined pipeline below
            });

        // Combined pipeline: syntax filter + semantic extraction in one provider
        IncrementalValuesProvider<ClassToGenerate> models =
            context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidateClass(node),
                    transform: static (ctx, ct) => GetClassToGenerate(ctx, ct))
                .Where(static m => m is not null && m.Value.HasAnyMembers)
                .Select(static (m, _) => m!.Value);

        // ── Step 3: Emit source output ─────────────────────────────────────────
        context.RegisterSourceOutput(models,
            static (spc, model) => EmitSource(spc, model));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 1: Syntax predicate (fast, syntax-only)
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsCandidateClass(SyntaxNode node)
    {
        // Must be a class declaration
        if (node is not ClassDeclarationSyntax classDecl)
            return false;

        // Must have 'partial' keyword
        bool isPartial = false;
        foreach (var mod in classDecl.Modifiers)
        {
            if (mod.IsKind(SyntaxKind.PartialKeyword))
            {
                isPartial = true;
                break;
            }
        }
        if (!isPartial) return false;

        foreach (var member in classDecl.Members)
        {
            if (member is ClassDeclarationSyntax nestedClass)
            {
                string name = nestedClass.Identifier.ValueText;
                if (name != QueryClassName && name != CommandClassName && name != EventsClassName)
                    continue;

                foreach (var mod in nestedClass.Modifiers)
                {
                    if (mod.IsKind(SyntaxKind.PartialKeyword))
                        return true;
                }
            }
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 2: Semantic transform
    // ─────────────────────────────────────────────────────────────────────────

    private static ClassToGenerate? GetClassToGenerate(
        GeneratorSyntaxContext context,
        CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var semanticModel = context.SemanticModel;

        ct.ThrowIfCancellationRequested();

        // Resolve the class symbol
        if (semanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol)
            return null;

        string namespaceName = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : classSymbol.ContainingNamespace.ToDisplayString();

        string className = classSymbol.Name;

        var queries   = new List<QueryToGenerate>();
        var commands  = new List<CommandToGenerate>();
        var events    = new List<EventToGenerate>();

        // Iterate nested types of the partial class
        foreach (var nestedType in classSymbol.GetTypeMembers())
        {
            ct.ThrowIfCancellationRequested();

            if (!nestedType.IsStatic) continue;

            switch (nestedType.Name)
            {
                case QueryClassName:
                    ExtractQueries(nestedType, queries, ct);
                    break;
                case CommandClassName:
                    ExtractCommands(nestedType, commands, ct);
                    break;
                case EventsClassName:
                    ExtractEvents(nestedType, events, ct);
                    break;
            }
        }

        return new ClassToGenerate(
            namespaceName,
            className,
            new EquatableArray<QueryToGenerate>(queries),
            new EquatableArray<CommandToGenerate>(commands),
            new EquatableArray<EventToGenerate>(events));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Extraction helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void ExtractQueries(
        INamedTypeSymbol queryClass,
        List<QueryToGenerate> output,
        CancellationToken ct)
    {
        foreach (var member in queryClass.GetTypeMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (!member.IsRecord) continue;

            // Find IQuery<T> in implemented interfaces (including inherited)
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

            string recordName = member.Name;
            string methodName = CodeBuilder.GetQueryMethodName(recordName);
            var parameters    = ExtractParameters(member);

            output.Add(new QueryToGenerate(
                recordName,
                methodName,
                returnType,
                new EquatableArray<ParameterInfo>(parameters)));
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

            bool implementsICommand = false;
            foreach (var iface in member.AllInterfaces)
            {
                if (!iface.IsGenericType && GetBaseInterfaceName(iface) == ICommandInterface)
                {
                    implementsICommand = true;
                    break;
                }
            }

            if (!implementsICommand) continue;

            string recordName = member.Name;
            string methodName = CodeBuilder.GetCommandMethodName(recordName);
            var parameters    = ExtractParameters(member);

            output.Add(new CommandToGenerate(
                recordName,
                methodName,
                new EquatableArray<ParameterInfo>(parameters)));
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

            bool implementsIEvent = false;
            foreach (var iface in member.AllInterfaces)
            {
                if (!iface.IsGenericType && GetBaseInterfaceName(iface) == IEventInterface)
                {
                    implementsIEvent = true;
                    break;
                }
            }

            if (!implementsIEvent) continue;

            string recordName = member.Name;
            string methodName = CodeBuilder.GetEventMethodName(recordName);

            output.Add(new EventToGenerate(
                recordName,
                methodName));
        }
    }

    /// <summary>
    /// Extracts positional parameters from a record's primary constructor.
    /// Falls back to the first declared constructor if no primary constructor is found.
    /// </summary>
    private static List<ParameterInfo> ExtractParameters(INamedTypeSymbol record)
    {
        var result = new List<ParameterInfo>();

        // Records expose their primary constructor parameters as the first constructor
        IMethodSymbol? primaryCtor = null;
        foreach (var ctor in record.Constructors)
        {
            if (ctor.IsImplicitlyDeclared) continue;
            primaryCtor = ctor;
            break;
        }

        if (primaryCtor is null) return result;

        foreach (var param in primaryCtor.Parameters)
        {
            string typeName = param.Type.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
                    .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));

            result.Add(new ParameterInfo(
                typeName,
                param.Name,
                CodeBuilder.ToCamelCase(param.Name)));
        }

        return result;
    }

    /// <summary>
    /// Returns the fully-qualified base name of an interface WITHOUT generic arity.
    /// e.g. "YFex.Cqrs.IQuery&lt;UserDto&gt;" → "YFex.Cqrs.IQuery"
    /// </summary>
    private static string GetBaseInterfaceName(INamedTypeSymbol iface)
    {
        var ns = iface.ContainingNamespace;
        string namespacePart = (ns is null || ns.IsGlobalNamespace)
            ? string.Empty
            : ns.ToDisplayString() + ".";
        return namespacePart + iface.Name;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 3: Source emission
    // ─────────────────────────────────────────────────────────────────────────

    private static void EmitSource(SourceProductionContext spc, ClassToGenerate model)
    {
        string source   = CodeBuilder.GenerateSource(model);
        string fileName = $"{model.ClassName}.g.cs";
        spc.AddSource(fileName, source);
    }
}
