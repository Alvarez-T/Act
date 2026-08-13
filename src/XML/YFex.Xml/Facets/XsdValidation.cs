using System.Globalization;

namespace YFex.Xml.Facets;

/// <summary>Neutral pass/fail result of a facet check — no domain coupling.</summary>
public readonly struct XsdValidation
{
    public bool IsValid { get; }
    public string? Message { get; }

    private XsdValidation(bool valid, string? message)
    {
        IsValid = valid;
        Message = message;
    }

    public static readonly XsdValidation Success = new(true, null);
    public static XsdValidation Fail(string message) => new(false, message);
}

public static class XsdValidator
{
    /// <summary>
    /// Validates a string value against the facets. Applies the whitespace facet
    /// first, then length/pattern/enumeration. Returns the <b>normalized</b> value
    /// via <paramref name="normalized"/> so callers can store the canonical form.
    /// </summary>
    public static XsdValidation ValidateString(this XsdFacets facets, string? value, out string normalized)
    {
        normalized = XsdWhitespaceNormalizer.Normalize(value ?? string.Empty, facets.Whitespace);

        int len = normalized.Length;
        if (facets.Length is { } exact && len != exact)
            return XsdValidation.Fail($"length {len} ≠ required {exact}");
        if (facets.MinLength is { } min && len < min)
            return XsdValidation.Fail($"length {len} < minLength {min}");
        if (facets.MaxLength is { } max && len > max)
            return XsdValidation.Fail($"length {len} > maxLength {max}");

        if (facets.Enumeration is { } set && !set.Contains(normalized))
            return XsdValidation.Fail($"\"{normalized}\" is not an allowed enumeration value");

        if (facets.HasPattern && !facets.IsPatternMatch(normalized))
            return XsdValidation.Fail($"\"{normalized}\" does not match pattern {facets.Pattern}");

        return XsdValidation.Success;
    }

    /// <summary>
    /// Validates a decimal against <c>totalDigits</c>/<c>fractionDigits</c>.
    /// </summary>
    public static XsdValidation ValidateDecimal(this XsdFacets facets, decimal value)
    {
        if (facets.FractionDigits is { } frac)
        {
            int scale = (decimal.GetBits(value)[3] >> 16) & 0x7F;
            if (scale > frac)
                return XsdValidation.Fail($"fractionDigits {scale} > {frac}");
        }

        if (facets.TotalDigits is { } total)
        {
            string digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0');
            if (digits.Length == 0) digits = "0";
            if (digits.Length > total)
                return XsdValidation.Fail($"totalDigits {digits.Length} > {total}");
        }

        return XsdValidation.Success;
    }
}
