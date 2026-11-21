using YFex.Tenancy;

namespace YFex.Localization;

/// <summary>What a run-time text is (localization ⚑LZ22): a message template or a field display name.</summary>
public enum MessageTextKind : byte
{
    /// <summary>A message template (<see cref="IMessageTextStore.TryGetTemplate"/>), key = message code.</summary>
    Template = 1,

    /// <summary>A field display name (<see cref="IMessageTextStore.TryGetFieldName"/>), key = field key.</summary>
    FieldName = 2,
}

/// <summary>
/// One edit of a run-time text (localization ⚑LZ22): the server store (YFex.Data) records it, devices replay it into their
/// cache (YFex.Persistence). <see cref="Revision"/> numbers the edits of one store from 1 with no gaps, so a reader asks
/// for "every change after the revision I have". <see cref="Text"/> <see langword="null"/> removes the text: the code's
/// text (or a parent scope's) shows again.
/// </summary>
/// <param name="Revision">Position in the store's edit log, from 1.</param>
/// <param name="Kind">Template or field name.</param>
/// <param name="Scope">The scope the text belongs to (<see cref="TenancyPath.None"/> = every scope).</param>
/// <param name="Key">Message code or field key, as in the language files (<c>stock.out</c>, <c>sale.Installments</c>).</param>
/// <param name="CultureName">Exact culture name (<c>pt-BR</c>, <c>""</c> = invariant).</param>
/// <param name="Text">The text, or <see langword="null"/> when the edit removed it.</param>
/// <param name="EditedBy">Who made the edit (an actor id), when known.</param>
/// <param name="EditedAt">When the store recorded the edit (UTC).</param>
public readonly record struct MessageTextChange(
    long Revision,
    MessageTextKind Kind,
    TenancyPath Scope,
    string Key,
    string CultureName,
    string? Text,
    string? EditedBy,
    DateTimeOffset EditedAt);
