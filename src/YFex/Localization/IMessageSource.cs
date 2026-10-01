using System.Globalization;

namespace YFex.Localization;

/// <summary>
/// One layer of coded messages. Implementations: generated module catalogs (static switch, no
/// ResourceManager), tenant/dynamic stores (dictionary loaded per version).
/// Culture fallback (pt-BR → pt → invariant) is handled by the <see cref="MessageCatalog"/> chain,
/// not by each source — a source answers only for the exact culture name asked.
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
/// The message chain used at UI/server edges. Formats coded messages with typed arguments
/// (<see cref="RawValue"/> → no boxing).
/// <para>Template syntax: <c>{field}</c> = field display name; <c>{0}</c>, <c>{1}</c> = arguments
/// formatted culture-aware by <see cref="RawValue.TryFormat(Span{char}, out int, ReadOnlySpan{char}, IFormatProvider?)"/>;
/// <c>{0:N0}</c> passes a format; <c>{{</c> / <c>}}</c> escape braces.</para>
/// </summary>
public interface IMessageCatalog
{
    /// <summary>Formats <paramref name="code"/>; unknown codes render as the code itself.</summary>
    string Format(string code, ReadOnlySpan<RawValue> args, CultureInfo culture, string? fieldKey = null);

    /// <summary>Span version of <see cref="Format"/>; false when <paramref name="destination"/> is too small.</summary>
    bool TryFormat(string code, ReadOnlySpan<RawValue> args, CultureInfo culture, Span<char> destination,
        out int written, string? fieldKey = null);

    /// <summary>Display name for <paramref name="fieldKey"/>; falls back to the humanized member name
    /// (<c>"SaleVm.ManualDiscount"</c> → <c>"Manual discount"</c>).</summary>
    string FieldName(string fieldKey, CultureInfo culture);
}
