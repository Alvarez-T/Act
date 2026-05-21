using Microsoft.CodeAnalysis;

namespace YFex.State.Generator;

/// <summary>
/// YFex.State incremental source generator.
/// Attributes are defined in YFex.State.dll (the runtime) — not injected here.
/// Uses ForAttributeWithMetadataName for every attribute family — never CreateSyntaxProvider
/// with broad predicates (kills IDE responsiveness).
/// </summary>
[Generator]
public sealed class StateGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ── [Observable] pipeline ────────────────────────────────────────────

        // ── [StateCommand] pipeline ──────────────────────────────────────────

        var rawCommands = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "YFex.State.StateCommandAttribute",
                predicate: CommandParser.IsCandidateSyntax,
                transform: CommandParser.Transform)
            .Where(static m => m is not null)
            .Select(static (m, _) => m!.Value);

        // Diagnostics for IncludeCancelCommand without CancellationToken
        var cancelDiagnostics = rawCommands
            .Where(static m => m.IsMissingCancellationToken)
            .Select(static (m, _) => (m.ClassName, m.MethodName));

        context.RegisterSourceOutput(cancelDiagnostics,
            static (spc, info) => spc.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.CancelCommandMissingCancellationToken,
                    Location.None,
                    $"{info.ClassName}.{info.MethodName}")));

        // Diagnostics for TargetProperty on a void/non-returning command
        var targetPropertyDiagnostics = rawCommands
            .Where(static m => m.IsTargetPropertyOnVoid)
            .Select(static (m, _) => (m.ClassName, m.MethodName));

        context.RegisterSourceOutput(targetPropertyDiagnostics,
            static (spc, info) => spc.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.TargetPropertyOnVoidCommand,
                    Location.None,
                    $"{info.ClassName}.{info.MethodName}")));

        var commandPipeline = rawCommands
            .Collect()
            .SelectMany(CommandParser.GroupByClass);

        context.RegisterSourceOutput(commandPipeline,
            static (spc, model) => CommandEmitter.Emit(spc, model));

        // ── [Observable] pipeline ────────────────────────────────────────────

        var rawModels = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "YFex.State.ObservableAttribute",
                predicate: ObservableParser.IsCandidateSyntax,
                transform: ObservableParser.Transform)
            .Where(static m => m is not null)
            .Select(static (m, _) => m!.Value);

        // Report [NotifyOnTaskCompletion] diagnostics separately so they survive the grouping step.
        var taskTypeDiagnostics = rawModels
            .Where(static m => m.HasTaskTypeError)
            .Select(static (m, _) => (m.PropertyName, m.ClassName));

        context.RegisterSourceOutput(taskTypeDiagnostics,
            static (spc, info) => spc.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.NotifyOnTaskCompletionInvalidType,
                    Location.None,
                    $"{info.ClassName}.{info.PropertyName}")));

        var observablePipeline = rawModels
            .Collect()
            .SelectMany(ObservableParser.GroupByClass);

        context.RegisterSourceOutput(observablePipeline,
            static (spc, model) => ObservableEmitter.Emit(spc, model));
    }
}
