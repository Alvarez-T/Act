using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Messaging.Rpc.Generator;

/// <summary>
/// Incremental source generator that emits the reflection-free glue for YFex.Messaging.Rpc:
/// <list type="bullet">
///   <item><c>YFexOutboxCommandRegistrations</c> — outbox replay map (deserialize + typed dispatch),
///         replacing <c>Type.GetType</c> + <c>MakeGenericMethod</c>.</item>
///   <item><c>YFexLocalHandlerRegistrations</c> — <c>LocalHandlerInvoker</c> typed resolve+invoke map,
///         replacing <c>MakeGenericType</c> + <c>MethodInfo.Invoke</c>.</item>
///   <item><c>YFexRpcEventRegistrations</c> — <c>RpcEventBus.RegisterEventType&lt;T&gt;()</c> pre-registration
///         for every event type, replacing the old reflective server-push dispatch fallback.</item>
/// </list>
/// Discovery mirrors <c>YFex.Cqrs.SourceGenerator</c>: records nested in the <c>Commands</c> / <c>Queries</c>
/// / <c>Events</c> static classes of a partial aggregate, implementing <c>YFex.Cqrs.ICommand</c> /
/// <c>ICommand&lt;T&gt;</c> / <c>IQuery&lt;T&gt;</c> / <c>IEvent</c>.
/// </summary>
[Generator]
public sealed class OutboxCommandGenerator : IIncrementalGenerator
{
    private const string ICommandInterface = "YFex.Cqrs.ICommand";
    private const string IQueryInterface   = "YFex.Cqrs.IQuery";
    private const string IEventInterface   = "YFex.Cqrs.IEvent";
    private const string CommandClassName  = "Commands";
    private const string QueryClassName    = "Queries";
    private const string EventClassName    = "Events";

    // Tag prefixes encode member kind into the cached EquatableArray<string> entries.
    private const string TagVoidCommand   = "C:";   // C:<cmdFqn>
    private const string TagResultCommand = "CR:";  // CR:<cmdFqn>|<resultFqn>
    private const string TagQuery         = "Q:";   // Q:<queryFqn>|<resultFqn>
    private const string TagEvent         = "E:";   // E:<eventFqn>

    private static readonly SymbolDisplayFormat FqnFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Included);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<EquatableArray<string>> perAggregate =
            context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCandidateClass(node),
                    transform: static (ctx, ct) => GetAggregateMembers(ctx, ct))
                .Where(static m => m is not null && m.Value.Length > 0)
                .Select(static (m, _) => m!.Value);

        IncrementalValueProvider<ImmutableArray<EquatableArray<string>>> collected = perAggregate.Collect();

        context.RegisterSourceOutput(collected, static (spc, aggregates) =>
        {
            if (aggregates.IsDefaultOrEmpty) return;

            var entries = aggregates
                .SelectMany(static a => a)
                .Distinct()
                .OrderBy(static s => s, System.StringComparer.Ordinal)
                .ToList();

            if (entries.Count == 0) return;

            // Outbox replay covers commands only (both void and result-bearing).
            var replayCommands = entries
                .Where(static e => e.StartsWith(TagVoidCommand) || e.StartsWith(TagResultCommand))
                .Select(StripTag)
                .ToList();
            if (replayCommands.Count > 0)
                spc.AddSource("YFexOutboxCommandRegistrations.g.cs", EmitOutbox(replayCommands));

            spc.AddSource("YFexLocalHandlerRegistrations.g.cs", EmitLocalHandlers(entries));

            var events = entries.Where(static e => e.StartsWith(TagEvent))
                                .Select(static e => e.Substring(TagEvent.Length))
                                .ToList();
            spc.AddSource("YFexRpcEventRegistrations.g.cs", EmitEvents(events));
        });
    }

    // Strips the C:/CR: tag for the outbox builder.Add<...>() generic argument list.
    private static string StripTag(string entry)
    {
        if (entry.StartsWith(TagResultCommand))
            return entry.Substring(TagResultCommand.Length).Replace("|", ", ");
        return entry.Substring(TagVoidCommand.Length);
    }

    private static bool IsCandidateClass(SyntaxNode node)
    {
        if (node is not ClassDeclarationSyntax cls) return false;
        if (!cls.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))) return false;

        return cls.Members.OfType<ClassDeclarationSyntax>().Any(nested =>
            (nested.Identifier.ValueText == CommandClassName ||
             nested.Identifier.ValueText == QueryClassName ||
             nested.Identifier.ValueText == EventClassName) &&
            nested.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
    }

    private static EquatableArray<string>? GetAggregateMembers(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol classSymbol)
            return null;

        var entries = new List<string>();

        foreach (var nested in classSymbol.GetTypeMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (!nested.IsStatic) continue;

            foreach (var member in nested.GetTypeMembers())
            {
                ct.ThrowIfCancellationRequested();
                if (!member.IsRecord || member.IsAbstract) continue;

                string fqn = member.ToDisplayString(FqnFormat);

                if (nested.Name == CommandClassName)
                {
                    bool isCommand = member.AllInterfaces.Any(i =>
                        !i.IsGenericType && GetBaseInterfaceName(i) == ICommandInterface);
                    if (!isCommand) continue;

                    string? resultType = GenericResultOf(member, ICommandInterface);
                    entries.Add(resultType is null
                        ? TagVoidCommand + fqn
                        : TagResultCommand + fqn + "|" + resultType);
                }
                else if (nested.Name == QueryClassName)
                {
                    string? resultType = GenericResultOf(member, IQueryInterface);
                    if (resultType is null) continue;
                    entries.Add(TagQuery + fqn + "|" + resultType);
                }
                else if (nested.Name == EventClassName)
                {
                    bool isEvent = member.AllInterfaces.Any(i =>
                        !i.IsGenericType && GetBaseInterfaceName(i) == IEventInterface);
                    if (!isEvent) continue;
                    entries.Add(TagEvent + fqn);
                }
            }
        }

        return entries.Count == 0 ? (EquatableArray<string>?)null : new EquatableArray<string>(entries);
    }

    // Returns the TResult FQN of the first generic interface whose base name matches, or null.
    private static string? GenericResultOf(INamedTypeSymbol member, string interfaceName)
    {
        foreach (var iface in member.AllInterfaces)
            if (iface.IsGenericType && GetBaseInterfaceName(iface) == interfaceName)
                return iface.TypeArguments[0].ToDisplayString(FqnFormat);
        return null;
    }

    private static string GetBaseInterfaceName(INamedTypeSymbol iface)
    {
        var ns = iface.ContainingNamespace;
        string nsPart = (ns is null || ns.IsGlobalNamespace) ? string.Empty : ns.ToDisplayString() + ".";
        return nsPart + iface.Name;
    }

    // ── Emit: outbox replay (commands) ──────────────────────────────────────────

    private static string EmitOutbox(List<string> commandFqns)
    {
        var sb = new StringBuilder(1024);
        AppendHeader(sb);
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine("using YFex.Messaging.Rpc;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Generated reflection-free outbox command replay registrations.</summary>");
        sb.AppendLine("public static class YFexOutboxCommandRegistrations");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Builds the command replay map for every command type in this assembly.</summary>");
        sb.AppendLine("    [global::System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(\"Uses reflection-based JSON deserialization for outbox payloads.\")]");
        sb.AppendLine("    [global::System.Diagnostics.CodeAnalysis.RequiresDynamicCode(\"Uses reflection-based JSON deserialization for outbox payloads.\")]");
        sb.AppendLine("    public static OutboxCommandRegistry BuildOutboxCommandRegistry()");
        sb.AppendLine("    {");
        sb.AppendLine("        var builder = new OutboxCommandRegistryBuilder();");
        foreach (var fqn in commandFqns)
            sb.Append("        builder.Add<").Append(fqn).AppendLine(">();");
        sb.AppendLine("        return builder.Build();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Registers the generated <see cref=\"OutboxCommandRegistry\"/> as the authoritative singleton.</summary>");
        sb.AppendLine("    [global::System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(\"Uses reflection-based JSON deserialization for outbox payloads.\")]");
        sb.AppendLine("    [global::System.Diagnostics.CodeAnalysis.RequiresDynamicCode(\"Uses reflection-based JSON deserialization for outbox payloads.\")]");
        sb.AppendLine("    public static IServiceCollection AddYFexOutboxCommands(this IServiceCollection services)");
        sb.AppendLine("    {");
        sb.AppendLine("        services.RemoveAll<OutboxCommandRegistry>();");
        sb.AppendLine("        services.AddSingleton(BuildOutboxCommandRegistry());");
        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ── Emit: LocalHandlerInvoker typed map (commands + queries) ─────────────────

    private static string EmitLocalHandlers(List<string> entries)
    {
        var sb = new StringBuilder(1024);
        AppendHeader(sb);
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine("using YFex.Messaging.Rpc;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Generated reflection-free LocalHandlerInvoker resolve+invoke registrations.</summary>");
        sb.AppendLine("public static class YFexLocalHandlerRegistrations");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Builds the typed handler-invoke map for every command/query type in this assembly.</summary>");
        sb.AppendLine("    public static LocalHandlerInvokerRegistry BuildLocalHandlerInvokerRegistry()");
        sb.AppendLine("    {");
        sb.AppendLine("        var builder = new LocalHandlerInvokerRegistryBuilder();");
        foreach (var entry in entries)
        {
            if (entry.StartsWith(TagResultCommand))
            {
                var args = entry.Substring(TagResultCommand.Length).Replace("|", ", ");
                sb.Append("        builder.AddCommand<").Append(args).AppendLine(">();");
            }
            else if (entry.StartsWith(TagVoidCommand))
            {
                var t = entry.Substring(TagVoidCommand.Length);
                sb.Append("        builder.AddCommand<").Append(t).AppendLine(">();");
            }
            else if (entry.StartsWith(TagQuery))
            {
                var args = entry.Substring(TagQuery.Length).Replace("|", ", ");
                sb.Append("        builder.AddQuery<").Append(args).AppendLine(">();");
            }
        }
        sb.AppendLine("        return builder.Build();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Registers the generated <see cref=\"LocalHandlerInvokerRegistry\"/> as the authoritative singleton.</summary>");
        sb.AppendLine("    public static IServiceCollection AddYFexLocalHandlers(this IServiceCollection services)");
        sb.AppendLine("    {");
        sb.AppendLine("        services.RemoveAll<LocalHandlerInvokerRegistry>();");
        sb.AppendLine("        services.AddSingleton(BuildLocalHandlerInvokerRegistry());");
        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ── Emit: RpcEventBus pre-registrations (events) ─────────────────────────────

    private static string EmitEvents(List<string> eventFqns)
    {
        var sb = new StringBuilder(1024);
        AppendHeader(sb);
        sb.AppendLine("using YFex.Messaging.Rpc;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Generated AOT-safe RpcEventBus event-type pre-registrations.</summary>");
        sb.AppendLine("public static class YFexRpcEventRegistrations");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Pre-registers every event type in this assembly for AOT-safe server-push dispatch.</summary>");
        sb.AppendLine("    public static RpcEventBus RegisterYFexRpcEventTypes(this RpcEventBus bus)");
        sb.AppendLine("    {");
        foreach (var fqn in eventFqns)
            sb.Append("        bus.RegisterEventType<").Append(fqn).AppendLine(">();");
        sb.AppendLine("        return bus;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb)
    {
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
    }
}
