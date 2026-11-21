using System.Globalization;

namespace YFex.Localization;

/// <summary>
/// Formatting of typed message keys: the argument count and types come from the key's declaration
/// (<c>MessageKey&lt;decimal&gt;</c> takes one <see cref="decimal"/>), so a wrong argument is a compile error.
/// Arguments become <see cref="RawValue"/> through <see cref="RawValueConvert.From{T}(T)"/> without boxing.
/// </summary>
/// <example>
/// <code>
/// catalog.Format(SaleText.BoletoNeedsCustomer, culture);
/// catalog.Format(SaleText.StockOut, available, culture, fieldKey: "sale.line.Quantity");
/// </code>
/// </example>
public static class MessageCatalogFormatting
{
    extension(IMessageCatalog catalog)
    {
        /// <summary>Formats a key without arguments.</summary>
        public string Format(MessageKey key, CultureInfo culture, string? fieldKey = null)
            => catalog.Format(in key, default, culture, fieldKey);

        /// <summary>Formats a key with one typed argument.</summary>
        public string Format<T0>(MessageKey<T0> key, T0 a0, CultureInfo culture, string? fieldKey = null)
        {
            MessageKey k = key.Key;
            return catalog.Format(in k, [RawValueConvert.From(a0)], culture, fieldKey);
        }

        /// <summary>Formats a key with two typed arguments.</summary>
        public string Format<T0, T1>(MessageKey<T0, T1> key, T0 a0, T1 a1, CultureInfo culture, string? fieldKey = null)
        {
            MessageKey k = key.Key;
            return catalog.Format(in k, [RawValueConvert.From(a0), RawValueConvert.From(a1)], culture, fieldKey);
        }

        /// <summary>Formats a key with three typed arguments.</summary>
        public string Format<T0, T1, T2>(MessageKey<T0, T1, T2> key, T0 a0, T1 a1, T2 a2, CultureInfo culture, string? fieldKey = null)
        {
            MessageKey k = key.Key;
            return catalog.Format(in k, [RawValueConvert.From(a0), RawValueConvert.From(a1), RawValueConvert.From(a2)], culture, fieldKey);
        }
    }
}
