using System.Globalization;
using YFex.Localization;

namespace YFex.Validation;

/// <summary>
/// Violation-aware overloads over the YFex core message catalog. <see cref="Violation.Detail"/>
/// (when set) bypasses templates. Formatted strings are cached by the UI error item, not globally.
/// </summary>
public static class MessageCatalogValidationExtensions
{
    public static string Format(this IMessageCatalog catalog, in Violation v, CultureInfo culture, string? fieldKey = null)
    {
        if (v.Detail is not null) return v.Detail;
        RuleArgsBuffer buffer = v.Args.Buffer;
        ReadOnlySpan<RawValue> args = ((ReadOnlySpan<RawValue>)buffer)[..v.Args.Count];
        return catalog.Format(v.Code.Value ?? string.Empty, args, culture, fieldKey);
    }

    public static bool TryFormat(this IMessageCatalog catalog, in Violation v, CultureInfo culture,
        Span<char> destination, out int written, string? fieldKey = null)
    {
        if (v.Detail is not null)
        {
            written = 0;
            if (!v.Detail.AsSpan().TryCopyTo(destination)) return false;
            written = v.Detail.Length;
            return true;
        }
        RuleArgsBuffer buffer = v.Args.Buffer;
        ReadOnlySpan<RawValue> args = ((ReadOnlySpan<RawValue>)buffer)[..v.Args.Count];
        return catalog.TryFormat(v.Code.Value ?? string.Empty, args, culture, destination, out written, fieldKey);
    }
}
