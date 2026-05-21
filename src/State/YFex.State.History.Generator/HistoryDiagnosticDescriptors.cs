using Microsoft.CodeAnalysis;

namespace YFex.State.History.Generator;

internal static class HistoryDiagnosticDescriptors
{
    private const string Category = "YFex.State.History";

    public static readonly DiagnosticDescriptor ScopeAndContextBothSet = new(
        id: "YFEX0701",
        title: "[Undoable] has both Scope and Context set",
        messageFormat: "'{0}': [Undoable] cannot specify both Scope and Context.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UndoableWithoutObservable = new(
        id: "YFEX0702",
        title: "[Undoable] without [Observable]",
        messageFormat: "'{0}': [Undoable] must be combined with [Observable]. The OnXxxChanging hook is only emitted by the Observable generator.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ContextNotFound = new(
        id: "YFEX0703",
        title: "[Undoable] Context references unknown property",
        messageFormat: "'{0}': Context = \"{1}\" does not refer to a [UndoContext]-marked property on '{2}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UndoContextWrongType = new(
        id: "YFEX0704",
        title: "[UndoContext] property must be of type UndoContext",
        messageFormat: "'{0}': [UndoContext] requires the property type to be 'YFex.State.History.UndoContext', but found '{1}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ClassNotPartial = new(
        id: "YFEX0705",
        title: "Class must be partial",
        messageFormat: "'{0}' must be declared 'partial' to use [Undoable] or [UndoContext].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ClassNotStateObject = new(
        id: "YFEX0706",
        title: "Class must inherit StateObject",
        messageFormat: "'{0}' must inherit from 'YFex.State.StateObject' to use [Undoable].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UndoableOnComputed = new(
        id: "YFEX0707",
        title: "[Undoable] on computed or read-only property",
        messageFormat: "'{0}': [Undoable] cannot be applied to a read-only or [Computed] property.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UndoableCollectionWrongType = new(
        id: "YFEX0708",
        title: "[UndoableCollection] on non-StateList property",
        messageFormat: "'{0}': [UndoableCollection] requires a 'YFex.State.Collections.StateList<T>' property, but found '{1}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ClassLevelUndoableNoObservables = new(
        id: "YFEX0709",
        title: "Class-level [Undoable] with no [Observable] properties",
        messageFormat: "'{0}': [Undoable] applied at class level but no [Observable] properties were found.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
