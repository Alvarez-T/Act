using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace YFex.Xml.Facets;

/// <summary>
/// The constraining facets of an XSD <c>simpleType</c>. Domain-neutral: this
/// type knows nothing about NFe or any consumer. A generated faceted struct
/// holds a single static readonly instance and validates through it.
/// </summary>
public sealed class XsdFacets
{
    public XsdWhitespace Whitespace { get; init; } = XsdWhitespace.Collapse;
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public int? Length { get; init; }
    public int? TotalDigits { get; init; }
    public int? FractionDigits { get; init; }

    /// <summary>Raw XSD pattern (not yet anchored). Compiled lazily and cached.</summary>
    public string? Pattern { get; init; }

    /// <summary>Enumeration restriction set, or <see langword="null"/> when unrestricted.</summary>
    public FrozenSet<string>? Enumeration { get; init; }

    private Regex? _compiled;

    /// <summary>
    /// XSD patterns are whole-string anchored. We wrap in <c>^(?:…)$</c> and use
    /// <see cref="RegexOptions.CultureInvariant"/>; XSD regex is Unicode-aware so
    /// the default .NET Unicode handling is appropriate.
    /// </summary>
    private Regex CompiledPattern =>
        _compiled ??= new Regex($"^(?:{Pattern})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public bool HasPattern => !string.IsNullOrEmpty(Pattern);

    /// <summary>Whole-string anchored pattern match against the (already normalized) value.</summary>
    public bool IsPatternMatch(string value) => CompiledPattern.IsMatch(value);
}
