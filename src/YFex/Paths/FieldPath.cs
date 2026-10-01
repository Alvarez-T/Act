using YFex.Helpers;

namespace YFex;

/// <summary>
/// Location of a member inside an object graph: a constant <see cref="Template"/> emitted by a
/// generator (<c>"Name"</c>, <c>"Address.City"</c>, <c>"Lines[].Quantity"</c>) plus the
/// <see cref="Indices"/> that fill its <c>[]</c> placeholders in order. No string is concatenated
/// until something renders it (<see cref="TryFormat"/> / <see cref="ToString"/>).
/// <code>
/// FieldPath.Of("Name")                       → Name
/// FieldPath.Of("Lines[].Quantity", 3)        → Lines[3].Quantity
/// FieldPath.Of("Orders[].Lines[].Qty", 1, 4) → Orders[1].Lines[4].Qty
/// </code>
/// <c>default</c> = object level (form-level summary). Used by violations, Composer bindings,
/// Xml/fiscal element paths and UI binding paths.
/// </summary>
public readonly struct FieldPath : IEquatable<FieldPath>
{
    private FieldPath(string? template, in IndexPath indices)
    {
        Template = template;
        Indices = indices;
    }

    /// <summary>Constant member path with <c>[]</c> placeholders; <see langword="null"/> = object level.</summary>
    public string? Template { get; }

    /// <summary>Values for the template's <c>[]</c> placeholders, in order.</summary>
    public IndexPath Indices { get; }

    /// <summary>True for the object-level path (no member).</summary>
    public bool IsEmpty => Template is null;

    /// <summary>The object-level path.</summary>
    public static FieldPath Empty => default;

    public static FieldPath Of(string template) => new(template, default);
    public static FieldPath Of(string template, int i0) => new(template, IndexPath.Of(i0));
    public static FieldPath Of(string template, int i0, int i1) => new(template, IndexPath.Of(i0, i1));
    public static FieldPath Of(string template, in IndexPath indices) => new(template, indices);

    /// <summary>Fills the next <c>[]</c> (used while validating collection elements).</summary>
    public FieldPath WithIndex(int index) => new(Template, Indices.Append(index));

    /// <summary>
    /// Places this path under a parent member: <c>"Address"</c> + <c>"City"</c> → <c>"Address.City"</c>;
    /// with <paramref name="index"/>, <c>"Lines[]"</c> + <c>"Quantity"</c> → <c>"Lines[2].Quantity"</c>.
    /// Allocates the combined template (generators emit combined constants on hot paths instead).
    /// </summary>
    public FieldPath Prefixed(string parentTemplate, int? index = null)
    {
        string template = Template is null ? parentTemplate : string.Concat(parentTemplate, ".", Template);
        return new(template, index is int i ? Indices.Prepend(i) : Indices);
    }

    /// <summary>Places this path under <paramref name="parent"/> (templates joined, indices concatenated).</summary>
    public FieldPath Prefixed(in FieldPath parent)
    {
        if (parent.Template is null) return this;
        string template = Template is null ? parent.Template : string.Concat(parent.Template, ".", Template);
        return new(template, parent.Indices.Concat(Indices));
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    /// <summary>Writes the concrete path (<c>Lines[3].Quantity</c>). Missing indices render as <c>[]</c>.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        if (Template is null) return true;

        ReadOnlySpan<char> t = Template;
        Span<int> idx = stackalloc int[IndexPath.MaxDepth];
        idx = idx[..Indices.CopyTo(idx)];
        int pos = 0, next = 0;

        while (!t.IsEmpty)
        {
            int ph = t.IndexOf("[]");
            ReadOnlySpan<char> literal = ph < 0 ? t : t[..(ph + 1)];   // include '['
            if (!literal.TryCopyTo(destination[pos..])) return false;
            pos += literal.Length;
            if (ph < 0) break;

            if (next < idx.Length)
            {
                if (!idx[next++].TryFormat(destination[pos..], out int w, default, System.Globalization.CultureInfo.InvariantCulture))
                    return false;
                pos += w;
            }
            if (pos >= destination.Length) return false;
            destination[pos++] = ']';
            t = t[(ph + 2)..];
        }

        charsWritten = pos;
        return true;
    }

    /// <summary>Allocates; for logs, wire and tests.</summary>
    public override string ToString()
    {
        if (Template is null) return string.Empty;
        if (Indices.IsEmpty) return Template;
        int size = Template.Length + IndexPath.MaxDepth * 11;
        Span<char> buffer = size <= 512 ? stackalloc char[512] : new char[size];
        return TryFormat(buffer, out int written) ? new string(buffer[..written]) : Template;
    }

    /// <summary>
    /// Compares with an already formatted path (<c>"Lines[3].Quantity"</c>) without allocating.
    /// Used by UI lookups that receive the bound path as text.
    /// </summary>
    public bool FormattedEquals(ReadOnlySpan<char> formatted)
    {
        if (Template is null) return formatted.IsEmpty;
        if (Indices.IsEmpty) return formatted.SequenceEqual(Template);
        int size = Template.Length + IndexPath.MaxDepth * 11;
        Span<char> buffer = size <= 512 ? stackalloc char[512] : new char[size];
        return TryFormat(buffer, out int written) && formatted.SequenceEqual(buffer[..written]);
    }

    /// <summary>
    /// Parses a concrete path (<c>"Orders[1].Lines[4].Qty"</c>) back into template + indices — the
    /// wire → <see cref="FieldPath"/> direction (server responses). Empty text = object level.
    /// Allocates the template string.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out FieldPath path)
    {
        path = default;
        if (text.IsEmpty) return true;

        Span<char> template = text.Length <= 512 ? stackalloc char[512] : new char[text.Length];
        Span<int> indices = stackalloc int[IndexPath.MaxDepth];
        int tLen = 0, count = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == ']') return false;
            if (c != '[')
            {
                template[tLen++] = c;
                continue;
            }

            int close = text[(i + 1)..].IndexOf(']');
            if (close <= 0 || count == IndexPath.MaxDepth) return false;
            if (!int.TryParse(text.Slice(i + 1, close), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int index))
                return false;

            indices[count++] = index;
            template[tLen++] = '[';
            template[tLen++] = ']';
            i += close + 1;
        }

        path = new FieldPath(new string(template[..tLen]), IndexPath.Of(indices[..count]));
        return true;
    }

    /// <summary>Appends a canonical encoding (template + indices) to <paramref name="hash"/>.</summary>
    public void AppendTo(ref StableHash64 hash)
    {
        hash.Append(Template);
        IndexPath indices = Indices;
        hash.Append(indices.Count);
        for (int i = 0; i < indices.Count; i++) hash.Append(indices[i]);
    }

    // ── Equality ─────────────────────────────────────────────────────────────

    public bool Equals(FieldPath other)
        => string.Equals(Template, other.Template, StringComparison.Ordinal) && Indices.Equals(other.Indices);

    public override bool Equals(object? obj) => obj is FieldPath other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Template is null ? 0 : string.GetHashCode(Template, StringComparison.Ordinal), Indices);

    public static bool operator ==(FieldPath left, FieldPath right) => left.Equals(right);
    public static bool operator !=(FieldPath left, FieldPath right) => !left.Equals(right);
}
