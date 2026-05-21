using Microsoft.CodeAnalysis;

namespace YFex.State.History.Generator;

/// <summary>
/// YFex.State.History incremental source generator.
/// Instruments [Undoable] properties with undo delta capture, context fields, static setter
/// delegates, and command wrappers. Also validates [UndoContext] property types.
/// Attributes are defined in YFex.State.History.dll (the runtime).
/// Uses ForAttributeWithMetadataName — never CreateSyntaxProvider with broad predicates.
/// </summary>
[Generator]
public sealed class HistoryGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ── Pipeline 1: [Undoable] on properties / classes ────────────────────

        var rawUndoable = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "YFex.State.History.UndoableAttribute",
                predicate: UndoableParser.IsPropertyOrClassCandidate,
                transform: UndoableParser.TransformUndoable)
            .Where(static m => m is not null)
            .Select(static (m, _) => m!.Value);

        // Diagnostics for YFEX0701–0707
        RegisterUndoableDiagnostics(context, rawUndoable);

        // ── Pipeline 2: [UndoContext] on properties ───────────────────────────

        var rawUndoContext = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "YFex.State.History.UndoContextAttribute",
                predicate: UndoableParser.IsPropertyCandidate,
                transform: UndoableParser.TransformUndoContext)
            .Where(static m => m is not null)
            .Select(static (m, _) => m!.Value);

        // Diagnostics for YFEX0704–0705
        RegisterUndoContextDiagnostics(context, rawUndoContext);

        // ── Merge both streams and group by class ─────────────────────────────

        var merged = rawUndoable
            .Where(static m => !m.HasError)
            .Collect()
            .Combine(rawUndoContext.Where(static m => !m.HasError).Collect())
            .SelectMany(static (pair, ct) =>
            {
                var combined = new System.Collections.Generic.List<UndoableRawModel>(
                    pair.Left.Length + pair.Right.Length);
                foreach (var item in pair.Left)  combined.Add(item);
                foreach (var item in pair.Right) combined.Add(item);
                return UndoableParser.GroupByClass(
                    System.Collections.Immutable.ImmutableArray.CreateRange(combined), ct);
            });

        context.RegisterSourceOutput(merged,
            static (spc, model) => UndoableEmitter.Emit(spc, model));
    }

    // ── Diagnostic registration helpers ──────────────────────────────────────

    private static void RegisterUndoableDiagnostics(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<UndoableRawModel> raw)
    {
        // Only report diagnostics for entries that have a property (skip context-only)
        var errored = raw.Where(static m => m.HasError && m.Property.HasValue);

        context.RegisterSourceOutput(errored, static (spc, m) =>
        {
            if (m.Property is null) return;
            var prop = m.Property.Value;

            if (prop.IsReadOnly)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    HistoryDiagnosticDescriptors.UndoableOnComputed,
                    Location.None,
                    $"{m.ClassName}.{prop.PropertyName}"));
                return;
            }

            // YFEX0702 — missing [Observable]
            spc.ReportDiagnostic(Diagnostic.Create(
                HistoryDiagnosticDescriptors.UndoableWithoutObservable,
                Location.None,
                $"{m.ClassName}.{prop.PropertyName}"));
        });
    }

    private static void RegisterUndoContextDiagnostics(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<UndoableRawModel> raw)
    {
        var errored = raw.Where(static m => m.HasError && m.ContextProp.HasValue);

        context.RegisterSourceOutput(errored, static (spc, m) =>
        {
            if (m.ContextProp is null) return;
            spc.ReportDiagnostic(Diagnostic.Create(
                HistoryDiagnosticDescriptors.UndoContextWrongType,
                Location.None,
                $"{m.ClassName}.{m.ContextProp.Value.PropertyName}",
                "unknown"));
        });
    }
}
