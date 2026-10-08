using Microsoft.CodeAnalysis;

namespace YFex.Expressions.Generator;

internal static class Diagnostics
{
    private const string Category = "YFex.Expressions";

    private static DiagnosticDescriptor D(string id, string title, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error)
        => new(id, title, message, Category, severity, isEnabledByDefault: true);

    // Formerly YFR0040–YFR0044 (YFex.Rules.Generator, Phase 6); moved in Phase X (XD2).
    public static readonly DiagnosticDescriptor FunctionContainer = D("YFE0001", "[ExprFunctions] container shape",
        "'{0}' must be a static partial class or a partial interface (also its containing types) to receive its expression function descriptors");
    public static readonly DiagnosticDescriptor FunctionType = D("YFE0002", "Unsupported expression function type",
        "Expression function '{0}': '{1}' has unsupported type '{2}' (bool, numbers, string, dates, Guid, enums, IRawValue types)");
    public static readonly DiagnosticDescriptor FunctionShape = D("YFE0003", "Expression function method shape",
        "'{0}.{1}' must be {2} (no ref/out parameters, not generic)");
    public static readonly DiagnosticDescriptor FunctionDuplicate = D("YFE0004", "Duplicate expression function name",
        "Expression function '{0}' is declared twice in '{1}'");
    public static readonly DiagnosticDescriptor FunctionDescription = D("YFE0005", "Expression function without description",
        "Expression function '{0}' has no Description — authors and AI only see what the description says", DiagnosticSeverity.Warning);

    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "YFE0001" => FunctionContainer,
        "YFE0002" => FunctionType,
        "YFE0003" => FunctionShape,
        "YFE0004" => FunctionDuplicate,
        "YFE0005" => FunctionDescription,
        _ => throw new System.ArgumentOutOfRangeException(nameof(id), id, null),
    };
}
