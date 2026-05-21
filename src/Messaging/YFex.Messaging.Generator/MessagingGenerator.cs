using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace YFex.Messaging.Generator;

/// <summary>
/// Incremental source generator that processes classes containing
/// [Subscribe&lt;T&gt;] decorated methods.
///
/// Detection strategy: CreateSyntaxProvider — detects by method attribute names,
/// not by class-level attributes, so it mirrors the CQRSGenerator approach.
///
/// Pipeline:
///   1. CreateSyntaxProvider — fast syntax filter (class has methods with Subscribe attrs)
///   2. Semantic transform   — resolve event types, extract attribute properties
///   3. RegisterSourceOutput — emit {ClassName}.Messaging.g.cs
/// </summary>
[Generator]
public sealed class MessagingGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ── [Subscribe<T>] pipeline ───────────────────────────────────────────
        IncrementalValuesProvider<SubscribeClassModel> subscribeModels =
            context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => MessagingParser.IsCandidateClass(node),
                    transform: static (ctx, ct) => MessagingParser.Transform(ctx, ct))
                .Where(static m => m is not null && m.Value.HasAnySubscriptions)
                .Select(static (m, _) => m!.Value);

        context.RegisterSourceOutput(subscribeModels,
            static (spc, model) => MessagingEmitter.Emit(spc, model));

        // ── [Live] pipeline ───────────────────────────────────────────────────
        IncrementalValuesProvider<LiveClassModel> liveModels =
            context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => LiveParser.IsCandidateClass(node),
                    transform: static (ctx, ct) => LiveParser.Transform(ctx, ct))
                .Where(static m => m is not null && m.Value.HasAnyLiveProperties)
                .Select(static (m, _) => m!.Value);

        context.RegisterSourceOutput(liveModels,
            static (spc, model) => LiveEmitter.Emit(spc, model));
    }
}
