using Microsoft.CodeAnalysis;

namespace YFex.Expressions.Generator;

/// <summary>Incremental generator bundled with YFex.Expressions: emits descriptors for <c>[ExprFunctions]</c> containers.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ExpressionsGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var functions = context.SyntaxProvider
            .ForAttributeWithMetadataName(FunctionGen.ContainerAttribute, static (n, _) => true, FunctionGen.Transform)
            .Where(static m => m is not null).Select(static (m, _) => m!)
            .WithTrackingName("YFexExpressions.Functions");
        context.RegisterSourceOutput(functions, FunctionGen.Emit);
        context.RegisterSourceOutput(functions.Select(static (m, _) => m.Diagnostics).Collect().Combine(context.CompilationProvider),
            static (spc, pair) =>
            {
                SourceTrees? trees = null;
                foreach (var group in pair.Left)
                    foreach (var d in group) spc.ReportDiagnostic(d.ToDiagnostic(trees ??= new SourceTrees(pair.Right)));
            });
    }
}
