using Microsoft.CodeAnalysis;

namespace YFex.Persistence.Generator;

internal static class PersistenceDiagnostics
{
    public static readonly DiagnosticDescriptor YFPER0001 = new(
        id:                 "YFPER0001",
        title:              "[Persist] property type may not be MemoryPack-serializable",
        messageFormat:      "Property '{0}' of type '{1}' is marked [Persist] but the type does not have " +
                            "[MemoryPackable]. Add [MemoryPackable] or use a primitive/built-in type.",
        category:           "YFex.Persistence",
        defaultSeverity:    DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor YFPER0002 = new(
        id:                 "YFPER0002",
        title:              "[Persist] requires partial class",
        messageFormat:      "Class '{0}' has [Persist] properties but is not declared as partial. " +
                            "Add the 'partial' keyword to enable snapshot generation.",
        category:           "YFex.Persistence",
        defaultSeverity:    DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
