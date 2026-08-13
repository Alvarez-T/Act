namespace YFex.Xml.Facets;

/// <summary>
/// XSD <c>whiteSpace</c> facet. Determines how whitespace is processed
/// <b>before</b> any pattern/length check is applied.
/// </summary>
public enum XsdWhitespace : byte
{
    /// <summary>No normalization — the value is used exactly as supplied.</summary>
    Preserve = 0,
    /// <summary>Each tab, line-feed and carriage-return is replaced by a single space.</summary>
    Replace = 1,
    /// <summary>Replace, then collapse runs of spaces to one and trim leading/trailing.</summary>
    Collapse = 2,
}

public static class XsdWhitespaceNormalizer
{
    /// <summary>Applies the whitespace facet to <paramref name="value"/>.</summary>
    public static string Normalize(string value, XsdWhitespace mode)
    {
        if (string.IsNullOrEmpty(value) || mode == XsdWhitespace.Preserve)
            return value;

        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int n = 0;
        foreach (char c in value)
            buffer[n++] = c is '\t' or '\n' or '\r' ? ' ' : c;

        if (mode == XsdWhitespace.Replace)
            return new string(buffer[..n]);

        // Collapse: trim + single-space runs.
        int w = 0;
        bool prevSpace = true; // start true → trims leading
        for (int i = 0; i < n; i++)
        {
            char c = buffer[i];
            if (c == ' ')
            {
                if (prevSpace) continue;
                prevSpace = true;
                buffer[w++] = ' ';
            }
            else
            {
                prevSpace = false;
                buffer[w++] = c;
            }
        }
        if (w > 0 && buffer[w - 1] == ' ') w--; // trim trailing
        return new string(buffer[..w]);
    }
}
