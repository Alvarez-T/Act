using YFex.Tenancy;

namespace YFex.Localization;

/// <summary>
/// Texts that change at run time, per scope (localization ⚑LZ22): a tenant's or a store's own wording of a message, a field
/// title or a rule title, edited in an admin screen. Each owner implements it where the data lives — the server table in
/// YFex.Data, the device cache in YFex.Persistence, an in-memory store in YFex.Tenancy — and the YFex.Tenancy layer
/// (<c>ScopedMessageSource</c>) puts it in the catalog for the current scope, deepest scope first.
/// </summary>
/// <remarks>
/// Keys and culture names are those of the language files (<c>stock.out</c>, <c>perm.sales.close</c>, <c>sale.Installments</c>;
/// <c>pt-BR</c>, <c>""</c> = invariant). A store answers only its own scope: falling back to the parent scope and then to the
/// code's texts is the layer's job.
/// </remarks>
public interface IMessageTextStore
{
    /// <summary>Template for <paramref name="code"/> written for exactly <paramref name="scope"/> and <paramref name="cultureName"/>.</summary>
    bool TryGetTemplate(in TenancyPath scope, string code, string cultureName, out string template);

    /// <summary>Field display name for exactly <paramref name="scope"/> and <paramref name="cultureName"/>.</summary>
    bool TryGetFieldName(in TenancyPath scope, string fieldKey, string cultureName, out string name);
}
