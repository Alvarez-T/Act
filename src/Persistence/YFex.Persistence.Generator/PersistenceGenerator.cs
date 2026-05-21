using Microsoft.CodeAnalysis;

namespace YFex.Persistence.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class PersistenceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (node, _) => PersistenceParser.IsCandidateClass(node),
                transform: (ctx,  ct) => PersistenceParser.Transform(ctx, ct))
            .Where(m => m is not null && m.Value.HasAnyPersistProperties)
            .Select((m, _) => m!.Value);

        context.RegisterSourceOutput(models, (spc, model) => PersistenceEmitter.Emit(spc, model));
    }
}
