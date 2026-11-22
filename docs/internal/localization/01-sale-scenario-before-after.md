# 01 — Sale scenario, before and after

> **2026-11-21:** LZ-P4 built; `_ignore/SaleScenario` now uses the "after" forms (README §7, "LZ-P4 closed"). The
> remaining differences from this page: field titles come from sections (⚑LZ9) and outputs from `[Output]` methods (⚑LZ28).

> **Superseded in part (2026-11-20):** the approved design keeps **no text in C#**: `Title = …` / `Description = …` on
> `[Rule]`, `[RuleSet]`, `[Stage]`, `[Group]` and `[Permission]` below are replaced by the sections of
> `texts.<culture>.json` (README §3.3, [language files](../../../../YFex.Localization/docs/language-files.md)), and
> `[Message("key")]` takes the key only. The call-site examples (§2–§5) are still the LZ-P4 proposal.

Source: `_ignore/SaleScenario` (2026-11-19). Every "after" is a proposal (⚑LZ, pending). Rows marked **today** already
work with the current generators and only need the sample rewritten.

## 1. Messages: the hand-written switch disappears (⚑LZ2–LZ4, LZ10)

Before — keys repeated in the rule, in the switch and at every call site:

```csharp
[Rule("promo.l3p2", Group = nameof(SaleRules.Promotions), Version = 7)]
public readonly struct Leve3Pague2 : IRule<SaleSituation, SaleLineSituation> { … }

public sealed class SaleMessages : IMessageSource
{
    public bool TryGetTemplate(string code, string cultureName, out string template)
    {
        template = code switch
        {
            "promo.l3p2" => "Leve 3 pague 2",
            "stock.out" => "{field}: só há {0} em estoque",
            "sale.boleto-needs-customer" => "Boleto exige cliente identificado",
            …
        };
        return template.Length > 0;
    }
    public bool TryGetFieldName(string fieldKey, string cultureName, out string name)
    {
        name = fieldKey switch { "sale.Installments" or "Installments" => "Parcelas", … };
        return name.Length > 0;
    }
}
```

After — the text sits on the declaration; the generator builds the catalog:

```csharp
[assembly: NeutralResourcesLanguage("pt-BR")]

[Rule("promo.l3p2", Group = nameof(SaleRules.Promotions), Version = 7,
      Title = "Leve 3 pague 2", Description = "Na compra de 3, a terceira sai grátis.")]
public readonly struct Leve3Pague2 : IRule<SaleSituation, SaleLineSituation> { … }

[Messages]
public static partial class SaleText
{
    [Message("sale.boleto-needs-customer", "Boleto exige cliente identificado")]
    public static partial MessageKey BoletoNeedsCustomer { get; }

    [Message("stock.out", "{field}: só há {0} em estoque")]
    public static partial MessageKey<decimal> StockOut { get; }
}
```

```json
// Messages/texts.en.json (only for other cultures; the generator lists missing keys per culture in YFexCultures)
{ "promo.l3p2": "Buy 3 pay 2", "stock.out": "{field}: only {0} in stock", "sale.boleto-needs-customer": "Boleto needs an identified customer" }
```

The explanation keys from the RX plan (pasted by the user) also move onto declarations:

| Before (catalog key typed by hand) | After |
|---|---|
| `"sale" => "Venda"` | `[RuleSet<SaleSituation>("sale", Title = "Venda")]` |
| `"sale.stage.limits" => "Limites"` | `[Stage(After = nameof(Payment), Title = "Limites")] public static partial Stage Limits { get; }` |
| `"sale.group.Installments" => "Parcelamento"` | `[Group(Title = "Parcelamento")] public static readonly RuleGroup Installments = new(Limits, Resolve.MostRestrictive);` |
| `"sale.Tenant" => "Empresa"` | `[property: RuleTier, Title("Empresa")] string Tenant` on the situation |
| `"payment.pix.description" => "…"` | `[Rule("payment.pix", …, Description = "Pagamentos no Pix têm 2% de desconto.")]` |

Cons: neutral-culture texts live in C# source; a long description makes the attribute line long (it can move to the
culture file alone). Generator work in Rules and Authorization (LZ-P3).

## 2. Field paths and codes at call sites

Shape invariant — before:

```csharp
v.Invariant(static (in SaleAggregate.Commands.CloseSale c, ref ViolationWriter w) =>
{
    if (c.Payment == PaymentMethod.Boleto && c.CustomerId is null)
        w.Add(FieldPath.Of("CustomerId"), "sale.boleto-needs-customer");
});
```

After — the rule is "CustomerId is required when paying by boleto", so it is a field rule (⚑LZ14):

```csharp
v.Field(c => c.CustomerId).Required()
 .When(c => c.Payment == PaymentMethod.Boleto)
 .Message(SaleText.BoletoNeedsCustomer);
```

General cross-field check (one violation, predicate = valid):

```csharp
v.Rule(c => c.Payment != PaymentMethod.Boleto || c.CustomerId is not null, on: c => c.CustomerId, SaleText.BoletoNeedsCustomer);
```

Several violations from one pass — `Invariant` stays, shorter with C# 14 parameter modifiers and the generated path
(**today**: `CloseSale.Fields.CustomerId` already exists):

```csharp
v.Invariant((in c, ref w) =>
{
    if (c.Payment == PaymentMethod.Boleto && c.CustomerId is null) w.Add(CloseSale.Fields.CustomerId, SaleText.BoletoNeedsCustomer);
});
```

Context rule — before:

```csharp
v.Add(FieldPath.Of("Lines[].Quantity", i), "stock.out", RawValue.From(available));
```

After (⚑LZ4, LZ13) — wrong argument type or a renamed member is a compile error:

```csharp
v.Add(CloseSale.Fields.Lines.At(i).Quantity, SaleText.StockOut, available);
```

Cons: nested path structs add generated code per collection member; `.Message` and `Rule` are new Validation API.

## 3. Amount fields (⚑LZ15 → folded into ⚑LZ28, supersedes ⚑RX14)

> **Corrected 2026-11-21:** the `[Amount]` attribute and `ForAmount()` below were dropped when ⚑LZ15 was folded into
> ⚑LZ28. As approved: no view-model attribute; the receipt is `Explanation.ForObject()`, outputs explain with `ForOutput`, the
> view asks for the receipt layout (validation/14 §3.4, ⚑RX14). The snippets show the approved form.

Before — a string list whose meaning is unclear ("which UI fields show the rule set's amount?"):

```csharp
public static string[] AmountFields => ["Total"];
```

After — no list and no attribute: the receipt has its own query and the view asks for it:

```csharp
[Observable, ConstraintsFrom] public partial Decision<SaleSituation>? Policy { get; set; }

// no attribute: the view asks for the receipt — YMoneyText().Bind(vm.Total).Why(layout: WhyLayout.Receipt) → ForObject()
public Money Total => Policy?.Totals.Final.As<Money>() ?? Money.Zero;
```

Server and thin clients ask by symbol, not by name:

```csharp
explanation.ForObject();                                         // the receipt
explanation.ForOutput(SaleRules.Outputs.InstallmentValue);       // an [Output] value
explanation.ForField(SaleSituation.Fields.Installments);         // limits, rules, checklist for one field
explanation.ForField(SaleLineSituation.Fields.Quantity, line: 2);
```

Cons: `ForField(ReadOnlySpan<char>)` (RX plan) becomes typed, so a UI that only has a binding path needs the generated
VM → situation mapping (it exists for `[SituationSource]`). When a VM has several decisions: `[Amount(nameof(Policy))]`.

## 4. Tables and stages (⚑LZ11, LZ12)

```csharp
// before
public static readonly Stage Items = Stage.First("items");
public readonly partial record struct Row(
    [property: When("Payment")] Match<PaymentMethod> Payment,
    [property: Then(DirectiveKind.LimitMax, "Installments"), Range(1, 48)] int MaxInstallments);

// after
[Stage(Title = "Itens")] public static partial Stage Items { get; }
public readonly partial record struct Row(
    [property: When] Match<PaymentMethod> Payment,                                                // field = parameter name
    [property: LimitMax(nameof(SaleSituation.Installments)), Range(1, 48)] int MaxInstallments);
```

## 5. Program.cs (⚑LZ16, and **today** items)

| Before | After |
|---|---|
| `user.Permissions.Add("sales.close")` | `user.Permissions.Add(SalePermissions.Close)` (**today**: typed property exists) |
| `SaleRules.Set.TryGetTable("sale.installments", out var table)` | `var table = SaleRules.Tables.Installments;` |
| `after.Trace!.For("sale.installments")` | `after.Trace!.For(InstallmentTable.Code)` |
| `new PublishTable("sale", "sale.installments", tenantPath, …)` | `PublishTable.For(SaleRules.Tables.Installments, tenantPath, …)` |
| `source.TryGetConstraints("Installments", out var c)` | `source.TryGetConstraints(SaleVm.Fields.Installments, out var c)` |
| `vm.Validation.Get("Installments")` | `vm.Validation.Get(SaleVm.Fields.Installments)` |
| hand-written `Text(in Violation v)` with `Span<RawValue>` | `catalog.Format(v, pt)` (**today**: `MessageCatalogValidationExtensions`) |

## 6. What stays a literal

- The **one** declaration of a stored identity: `[Rule("promo.l3p2")]`, `[Message("stock.out", …)]`,
  `[Permission("sales.close")]`, `[Situation("sale")]` (⚑LZ3: renaming the C# symbol must not change what is stored in
  audit, rule stores and signed rule data). C# attributes accept only constants, so a value object cannot appear there;
  the generated symbol is what every other line uses.
- Neutral-culture texts on declarations, and translations in culture files.
- Tests that check parsing of identities arriving as text.

With ⚑LZ27 (shipped-identities file), `[Situation]` and `[Message]` could later derive their id from the C# name and
still fail the build on an accidental rename.
