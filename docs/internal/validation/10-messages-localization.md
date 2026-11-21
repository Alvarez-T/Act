# 10 — Messages & localization

> Load for: **Phase 1** (contracts + built-in catalog), **Phase 2/5** (UI resolution), **Phase 4/6**
> (dynamic messages).
> Principle (D6): violations carry **code + args**; text is produced **at the edge** (UI, HTTP
> ProblemDetails for non-YFex clients, receipts). The server never sends pre-built strings to YFex clients.

---

## 1. Why a catalog, and who owns it (D13)

A violation `code = "len", args = [2, 80]` must become *"Nome deve ter entre 2 e 80 caracteres"*.
**Each module owns the texts of its own codes**; apps compose modules and can override; tenants and
runtime-authored rules add their own layers.

Lookup order (first hit wins):

```
1. Dynamic   messages stored WITH a runtime rule/table version (per culture) — e.g. "Black Friday Loja 12"
2. Tenant    tenant overrides ("Desconto acima de {0} precisa do gerente")
3. App       app overrides (POS wording)
4. Module    Sales, NFe, Customer… (generated static switch per module)
5. Built-in  YFex.Validation ("required", "len", "range", "email", "pattern", "rules.limit.max", "rules.cond.*")
6. Fallback  the code itself ("sale.discount.cap") — never throws
```

Codes are namespaced (`nfe.`, `sale.`, `customer.`) so modules never collide.

## 2. Contracts — **[YFex core]** + Validation overloads

Message catalogs are useful to **any** library that shows coded messages (Cqrs errors,
notifications, Composer, receipts), so the contracts live in **YFex core**
(`YFex/src/YFex/Localization/`, namespace `YFex.Localization`, README D20). They know nothing about
`Violation`; YFex.Validation adds the violation-aware overloads.

```csharp
namespace YFex.Localization;

/// One layer of messages. Implementations: generated module catalogs (static switch), tenant/dynamic
/// stores (dictionary loaded per version).
public interface IMessageSource
{
    /// Returns the template for code+culture, e.g. "{field} deve ter entre {0} e {1} caracteres".
    /// Culture fallback (pt-BR → pt → invariant) is handled by the chain, not by each source.
    bool TryGetTemplate(string code, string cultureName, out string template);
    /// Display name of a field ("CpfCnpj" → "CPF/CNPJ"); key = "<TypeKey>.<Path>" (see §5).
    bool TryGetFieldName(string fieldKey, string cultureName, out string name);
}

/// The chain used by UI/server edges. Formats with spans (args are YFex core RawValue → no boxing).
public interface IMessageCatalog
{
    string Format(string code, ReadOnlySpan<RawValue> args, CultureInfo culture, string? fieldKey = null);
    bool TryFormat(string code, ReadOnlySpan<RawValue> args, CultureInfo culture, Span<char> destination, out int written, string? fieldKey = null);
    string FieldName(string fieldKey, CultureInfo culture);
}

public sealed class MessageCatalog : IMessageCatalog
{
    /// Order matters: highest priority first (dynamic, tenant, app, modules…, built-in).
    public MessageCatalog(params ReadOnlySpan<IMessageSource> layers);
    public MessageCatalog WithLayer(IMessageSource layer, int position);   // e.g. tenant layer after login
}
```

```csharp
namespace YFex.Validation;

/// Violation-aware overloads (YFex.Validation). Detail (when set) bypasses templates.
/// Formatted strings are cached only in ErrorItem (07 §2), not globally.
public static class MessageCatalogValidationExtensions
{
    public static string Format(this IMessageCatalog catalog, in Violation v, CultureInfo culture, string? fieldKey = null);
    public static bool TryFormat(this IMessageCatalog catalog, in Violation v, CultureInfo culture, Span<char> destination, out int written, string? fieldKey = null);
}
```

Template syntax: `{field}` = field display name; `{0}`, `{1}` = `RuleArgs` formatted with
`RawValue.TryFormat` (culture-aware: money, percent, dates, EnumCode display names);
`{0:N0}` style format specifiers allowed. `Violation.Detail` (when set) bypasses templates.

## 3. Module catalogs — generated, AOT-safe

> **Moved 2026-11-20:** the generator and the engine (`MessageCatalog`) now live in the sibling repo `YFex.Localization`
> (design [localization](../localization/README.md)); YFV0040/YFV0041 became YFL0002/YFL0001. New projects use typed keys
> (`[Messages]`) and sectioned `texts.<culture>.json`; the flat `messages*.json` below still works.

Each module adds `Messages/*.json` as `AdditionalFiles`:

```json
// Sales/Messages/messages.pt-BR.json
{
  "sale.discount.cap": "Desconto máximo é {0}",
  "sale.installments.above-legal": "Máximo legal de {0} parcelas",
  "rules.loyalty.gold.name": "Cliente Gold {0}",
  "fields": { "SaleVm.ManualDiscount": "Desconto manual", "CloseSale.Installments": "Parcelas" }
}
```

A generator emits `internal sealed class SalesMessages : IMessageSource` with a `switch` on
(culture, code) → no `ResourceManager`, no reflection. **Where the generator lives:** first in
`YFex.Validation.Generator` (Phase 1). Because catalogs are a core concept, if a library that does
not use YFex.Validation needs module catalogs, move this emitter to a small core generator
(`YFex.Generator`, bundled with YFex core) — keep the emitter self-contained to make that move trivial. Registration:
`services.AddYFexMessages(b => b.AddModule<SalesMessages>().AddModule<NFeMessages>())`.
Built-in validation messages (`required`, `len`, …) ship in YFex.Validation for `en` and `pt-BR`.

Analyzer: a code used in a `w.Add(…, "sale.x")` / `[FieldCode]` / `[Rule("…")]` literal without an
entry in the module's default-culture file → warning **YFV0040** (missing message).

## 4. Dynamic and tenant layers

- **Dynamic:** `RuleSpec.Messages` (05 §8) and table display names are stored with the rule/table
  **version**; an old receipt shows the promotion name as it was when the sale happened.
- **Tenant:** stored like parameters (versioned, published with a permission such as
  `messages.override`), loaded into a dictionary-backed `IMessageSource` after login.

## 5. Field display names

Key = `<TypeName>.<Path>` (`"CreateCustomer.Document"`, `"SaleVm.ManualDiscount"`); lookup tries the
VM key, then the contract key (via `[ValidatesAs]` mapping), then the member name split by casing
("ManualDiscount" → "Manual discount"). Rule display names use `rules.<code>.name` (+ `.desc`),
condition templates use `rules.cond.eq|ne|lt|lte|gt|gte|in|true` (06 §4).

## 6. Where formatting happens

| Edge | Who formats |
|---|---|
| Avalonia/Uno/Blazor | `ErrorItem.Message` (once per stored violation), `Decision.Explain` for breakdowns |
| HTTP ProblemDetails (`errors` for non-YFex clients) | server, request culture |
| Receipts / reports | app, with the dynamic layer for historical names |
| Logs | code + args (no localization) |
