using System.Buffers;
using System.Globalization;

namespace YFex.Localization;

/// <summary>
/// Layered <see cref="IMessageCatalog"/>. Layers are ordered highest priority first:
/// dynamic → tenant → app → modules… → built-in.
/// <para>
/// Lookup is culture-major: the exact culture is searched across <b>all</b> layers before falling
/// back to the parent culture (pt-BR → pt → invariant), so a built-in pt-BR text wins over an app
/// text that only exists in invariant.
/// </para>
/// Immutable: <see cref="WithLayer"/> returns a new catalog (e.g. add the tenant layer after login).
/// </summary>
public sealed class MessageCatalog : IMessageCatalog
{
    private readonly IMessageSource[] _layers;

    public MessageCatalog(params ReadOnlySpan<IMessageSource> layers) => _layers = layers.ToArray();

    private MessageCatalog(IMessageSource[] layers) => _layers = layers;

    /// <summary>The layers, highest priority first.</summary>
    public ReadOnlySpan<IMessageSource> Layers => _layers;

    /// <summary>Returns a new catalog with <paramref name="layer"/> inserted at <paramref name="position"/>
    /// (0 = highest priority; clamped to the valid range).</summary>
    public MessageCatalog WithLayer(IMessageSource layer, int position)
    {
        ArgumentNullException.ThrowIfNull(layer);
        position = Math.Clamp(position, 0, _layers.Length);
        var layers = new IMessageSource[_layers.Length + 1];
        _layers.AsSpan(0, position).CopyTo(layers);
        layers[position] = layer;
        _layers.AsSpan(position).CopyTo(layers.AsSpan(position + 1));
        return new MessageCatalog(layers);
    }

    /// <summary>Finds the template for <paramref name="code"/> with culture fallback.</summary>
    public bool TryGetTemplate(string code, CultureInfo culture, out string template)
    {
        for (CultureInfo c = culture; ; c = c.Parent)
        {
            string name = c.Name;
            foreach (IMessageSource layer in _layers)
                if (layer.TryGetTemplate(code, name, out template)) return true;
            if (name.Length == 0) break;
        }
        template = string.Empty;
        return false;
    }

    public string Format(string code, ReadOnlySpan<RawValue> args, CultureInfo culture, string? fieldKey = null)
    {
        if (!TryGetTemplate(code, culture, out string template)) return code;
        return Render(template, args, culture, fieldKey);
    }

    public bool TryFormat(string code, ReadOnlySpan<RawValue> args, CultureInfo culture, Span<char> destination,
        out int written, string? fieldKey = null)
    {
        if (!TryGetTemplate(code, culture, out string template))
        {
            written = 0;
            if (!code.AsSpan().TryCopyTo(destination)) return false;
            written = code.Length;
            return true;
        }
        return TryRender(template, args, culture, fieldKey, destination, out written);
    }

    public string FieldName(string fieldKey, CultureInfo culture)
    {
        for (CultureInfo c = culture; ; c = c.Parent)
        {
            string name = c.Name;
            foreach (IMessageSource layer in _layers)
                if (layer.TryGetFieldName(fieldKey, name, out string display)) return display;
            if (name.Length == 0) break;
        }
        return Humanize(fieldKey);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    /// <summary>Renders <paramref name="template"/> to a string (grows a pooled buffer as needed).</summary>
    public string Render(string template, ReadOnlySpan<RawValue> args, CultureInfo culture, string? fieldKey = null)
    {
        Span<char> stack = stackalloc char[256];
        if (TryRender(template, args, culture, fieldKey, stack, out int written))
            return new string(stack[..written]);

        int size = 512;
        while (true)
        {
            char[] rented = ArrayPool<char>.Shared.Rent(size);
            try
            {
                if (TryRender(template, args, culture, fieldKey, rented, out written))
                    return new string(rented, 0, written);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }
            size *= 2;
        }
    }

    /// <summary>Renders <paramref name="template"/> into <paramref name="destination"/>.</summary>
    public bool TryRender(ReadOnlySpan<char> template, ReadOnlySpan<RawValue> args, CultureInfo culture,
        string? fieldKey, Span<char> destination, out int written)
    {
        int pos = 0;
        written = 0;

        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];

            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                if (pos >= destination.Length) return false;
                destination[pos++] = c;
                i++;
                continue;
            }

            if (c == '{')
            {
                int close = template[(i + 1)..].IndexOf('}');
                if (close >= 0)
                {
                    if (!TryWritePlaceholder(template.Slice(i + 1, close), args, culture, fieldKey,
                            destination[pos..], out int w, out bool recognized))
                        return false; // recognized placeholder that did not fit
                    if (recognized)
                    {
                        pos += w;
                        i += close + 1;
                        continue;
                    }
                }
            }

            if (pos >= destination.Length) return false;
            destination[pos++] = c;
        }

        written = pos;
        return true;
    }

    // Returns false only when a recognized placeholder does not fit; unknown placeholders are
    // written literally by the caller (recognized = false).
    private bool TryWritePlaceholder(ReadOnlySpan<char> body, ReadOnlySpan<RawValue> args, CultureInfo culture,
        string? fieldKey, Span<char> destination, out int written, out bool recognized)
    {
        written = 0;
        recognized = true;

        if (body.SequenceEqual("field"))
        {
            string name = fieldKey is null ? string.Empty : FieldName(fieldKey, culture);
            if (!name.AsSpan().TryCopyTo(destination)) return false;
            written = name.Length;
            return true;
        }

        int colon = body.IndexOf(':');
        ReadOnlySpan<char> indexText = colon < 0 ? body : body[..colon];
        ReadOnlySpan<char> format = colon < 0 ? default : body[(colon + 1)..];

        if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            || index >= args.Length)
        {
            recognized = false;
            return true;
        }

        return args[index].TryFormat(destination, out written, format, culture);
    }

    /// <summary>Last segment of a field key split by casing: <c>"SaleVm.ManualDiscount"</c> → <c>"Manual discount"</c>.</summary>
    public static string Humanize(string fieldKey)
    {
        ReadOnlySpan<char> name = fieldKey;
        int dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        int bracket = name.IndexOf('[');
        if (bracket >= 0) name = name[..bracket];
        if (name.IsEmpty) return fieldKey;

        Span<char> buffer = name.Length <= 128 ? stackalloc char[256] : new char[name.Length * 2];
        int pos = 0;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool boundary = i > 0 && char.IsUpper(c)
                && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (boundary) buffer[pos++] = ' ';
            buffer[pos++] = i == 0 ? char.ToUpperInvariant(c)
                : boundary && i + 1 < name.Length && char.IsLower(name[i + 1]) ? char.ToLowerInvariant(c)
                : c;
        }
        return new string(buffer[..pos]);
    }
}
