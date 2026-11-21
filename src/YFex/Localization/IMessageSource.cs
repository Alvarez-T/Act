using System.Globalization;

namespace YFex.Localization;

/// <summary>
/// One layer of coded messages. Implementations: generated module catalogs (from the module's language files), tenant and
/// dynamic stores (dictionary loaded per version). Culture fallback (pt-BR → pt → invariant) is handled by the catalog chain
/// (<c>YFex.Localization.MessageCatalog</c>), not by each source: a source answers only for the exact culture name asked.
/// </summary>
public interface IMessageSource
{
    /// <summary>Template for <paramref name="code"/> in exactly <paramref name="cultureName"/>
    /// (<c>""</c> = invariant), e.g. <c>"{field} deve ter entre {0} e {1} caracteres"</c>.</summary>
    bool TryGetTemplate(string code, string cultureName, out string template);

    /// <summary>Display name of a field (<c>"CustomerVm.CpfCnpj"</c> → <c>"CPF/CNPJ"</c>).
    /// Key = <c>"&lt;TypeName&gt;.&lt;Path&gt;"</c>.</summary>
    bool TryGetFieldName(string fieldKey, string cultureName, out string name);
}

/// <summary>
/// A generated module catalog: besides the key lookup, it answers the keys it declares by their index
/// (<see cref="MessageKey.Index"/>), an array read with no hashing.
/// </summary>
public interface IMessageModule : IMessageSource
{
    /// <summary>Template of the declared key number <paramref name="index"/> in exactly <paramref name="cultureName"/>.</summary>
    bool TryGetTemplate(int index, string cultureName, out string template);
}

/// <summary>
/// The message chain used at UI/server edges. Formats coded messages with typed arguments
/// (<see cref="RawValue"/> → no boxing).
/// <para>Template syntax: <c>{field}</c> = field display name; <c>{0}</c>, <c>{1}</c> = arguments
/// formatted culture-aware by <see cref="RawValue.TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/>;
/// <c>{0:N0}</c> passes a format; <c>{{</c> / <c>}}</c> escape braces.</para>
/// <para>Typed keys format through the extension methods of <see cref="MessageCatalogFormatting"/>
/// (<c>catalog.Format(SaleText.StockOut, 3m, culture)</c>); the argument types are checked by the compiler.</para>
/// </summary>
public interface IMessageCatalog
{
    /// <summary>Formats <paramref name="code"/>; unknown codes render as the code itself.</summary>
    string Format(string code, ReadOnlySpan<RawValue> args, CultureInfo culture, string? fieldKey = null);

    /// <summary>Span version of <see cref="Format(string, ReadOnlySpan{RawValue}, CultureInfo, string?)"/>; false when
    /// <paramref name="destination"/> is too small.</summary>
    bool TryFormat(string code, ReadOnlySpan<RawValue> args, CultureInfo culture, Span<char> destination,
        out int written, string? fieldKey = null);

    /// <summary>Display name for <paramref name="fieldKey"/>; falls back to the humanized member name
    /// (<c>"SaleVm.ManualDiscount"</c> → <c>"Manual discount"</c>).</summary>
    string FieldName(string fieldKey, CultureInfo culture);

    /// <summary>Formats <paramref name="key"/>; a catalog that knows the key's module reads it by index.
    /// Unknown keys render as the key itself.</summary>
    string Format(in MessageKey key, ReadOnlySpan<RawValue> args, CultureInfo culture, string? fieldKey = null)
        => Format(key.Value, args, culture, fieldKey);

    /// <summary>Span version of <see cref="Format(in MessageKey, ReadOnlySpan{RawValue}, CultureInfo, string?)"/>.</summary>
    bool TryFormat(in MessageKey key, ReadOnlySpan<RawValue> args, CultureInfo culture, Span<char> destination,
        out int written, string? fieldKey = null)
        => TryFormat(key.Value, args, culture, destination, out written, fieldKey);
}
