using YFex.Xml.Facets;

namespace YFex.Xml;

/// <summary>
/// Contract implemented by every generated faceted-string struct (e.g. <c>TUf</c>,
/// <c>TChNFe</c>). The generator emits the <see cref="Facets"/> instance and the
/// <see cref="Value"/> storage; this interface lets generic infrastructure
/// validate and read any of them without reflection.
/// </summary>
public interface IXsdString
{
    /// <summary>The constraining facets for this named simple type (static per type).</summary>
    static abstract XsdFacets Facets { get; }

    /// <summary>The validated, normalized string value.</summary>
    string Value { get; }
}

/// <summary>
/// Contract for a generated value type that validates on construction and
/// supports a non-throwing factory. <typeparamref name="TSelf"/> is the struct.
/// </summary>
public interface IXsdValue<TSelf> : IXsdString where TSelf : IXsdValue<TSelf>
{
    static abstract bool TryParse(string? value, out TSelf result);
    static abstract TSelf Parse(string value);
}
