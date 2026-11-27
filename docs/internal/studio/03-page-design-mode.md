# 03 — Page design mode: changing existing developer pages

> Part of the [Studio design family](README.md). Status: **proposal** (2026-11-25), level 1 of README §2. Decisions
> ⚑C15–⚑C21, C26, C28 (README §8), all pending. Answers 02 open question 4 (⚑C15).
> Origin: the user, 2026-11-25: an admin presses a button only admins see, the page enters design mode, and the admin
> shows or hides fields, moves them, adds new ones, and uses a shield icon to set who may change them.

## 1. What exists and what is missing

| Piece | Today | Gap |
|---|---|---|
| Forms from shapes | `YForm` builds editors from the generated `FormShape`; `FieldOverride`, `FieldSlot`, `YFormRest` change a generated form **in XAML** (Forms FM-P4) | the same overrides from **data** at run time (⚑C20) |
| Data grid from shapes | `YDataGrid` columns from the same generator (⚑DG9), column menu, row menu, custom rows | column overrides and extra computed columns from data (⚑C20) |
| Drag and drop | `YDrag`/`YDrop` built (UI DA4) | — |
| Property inspector, palette, suggestion box | `YPropertyInspector` exists; `YPalette`, `YSuggestBox` designed (⚑LB37, LB8) | a design surface that hosts them over a live page (⚑C21) |
| Per-tenant page changes in code | CustomLogic page swap (built), slot injection engine (built), field contributions (design) | nothing for data-driven changes |
| What a developer allows to change | nothing | extension points (⚑C16, C17) |
| Who may see a value | nothing (UI hide only) | Authorization field access (⚑C19) |

## 2. The developer declares what may change (⚑C16, ⚑C17)

A customization profile sits next to the view model, the same style as a capability profile ("next to it, never inside
it"). Field facts (type, constraints, label key) come from the Forms shape, so the profile only says what is allowed.

```csharp
// illustrative; names are not decided
public sealed class OrderEditorCustomization : ICustomizationProfile<OrderEditorViewModel>
{
    public void Configure(ICustomizationBuilder<OrderEditorViewModel> page)
    {
        page.Form(x => x.Order)                              // layout of generated fields is open by default (⚑C17)
            .Lock(o => o.Total, o => o.Customer)             // never hidden, moved or relabelled
            .MayRequire(o => o.Notes, o => o.DeliveryDate)   // behavior is closed unless listed
            .AllowExtensionFields();                         // Order is IExtensible

        page.Grid(x => x.Lines)
            .MayAddComputedColumns()                         // Expressions over the row's fields
            .MayStyleRows();

        page.Slot("toolbar").Slot("row-menu");               // where buttons may be added (with flows, 01 §3)
        page.Event("Saved", x => x.Saved);                   // page events a flow may react to (Mvvm request)
    }
}
```

**Default openness (⚑C17):** hiding, moving and relabelling generated fields is harmless, so it is open unless locked;
anything that changes behavior (required, buttons, events, new fields) is closed unless declared. A page without a
profile has no design mode at all.

Cons: one more class per customizable page. Rejected: attributes inside the view model (mixes the contract with the
page's code); putting it in the capability profile (Capabilities describes actions; customization rights are a different
contract and Authorization decides them).

## 3. Design mode (⚑C21, ⚑C28)

```
┌ Order 10452 ───────────────────────────────────────────── [✎ Customize page] ┐   ← only with studio.page.customize
│                                                                              │     (and the license module, ⚑C28)
│  Design mode · Layer: [ACME ▾]  Preview as: [Cashier ▾]   [Discard] [Save draft] [Enact…] │
│ ┌ Customer ─────────────────────────────────────────┐  ┌ Inspector ──────────┐│
│ │ 🔒 Customer [ACME Ltda           ]                │  │ Field  Notes         ││
│ │ ⋮⋮ Notes    [                    ]  👁 ✎ 🔒 🛡     │  │ Visible   ☑          ││
│ │ ⋮⋮ Delivery [   ] (new field)       👁 ✎ 🔒       │  │ Label     Observações││
│ └───────────────────────────────────────────────────┘  │ Required  ☐ (stricter)││
│ ┌ Hidden and available fields ─┐                        │ 🔒 Locked for  Stores ││
│ │ Discount · Carrier · + New field │                    │ 🛡 Who sees  Managers ││
│ └──────────────────────────────┘                        └─────────────────────┘│
│ Toolbar: [Save] [Print] [+ Button]       Row menu: [Open] [+ Button]           │
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Entering:** a page-header button visible only with the `studio.page.customize` permission for some layer in the
  user's scope chain. The page stays live and read-only; commands are disabled.
- **Adorners:** drag handle (`YDrag`), eye (show or hide), pencil (label, required), lock (⚑C18), shield (⚑C19).
  Locked items show the lock and refuse edits.
- **Panels:** the inspector (`YPropertyInspector` in a `YDetailPresenter`), a palette of hidden fields, extension
  fields and buttons (`YPalette`), and the flow builder for buttons and page events (`YFlowBuilder`).
- **Preview as** a role or a store before enacting; **Enact** asks for the layer and an optional `EffectiveFrom`.
- The surface is generic (`YDesignSurface` in YFex.UI): it knows selection, adorners and edit commands, not Studio.
  Studio supplies the model, the allowed operations and the catalog.

## 4. What an overlay holds

An overlay is a revision per (page key, scope). Example as stored (canonical JSON, keys illustrative):

```json
{ "page": "sales.order.editor", "scope": "acme/store-12", "basePage": "1.14",
  "edits": [
    { "hide":    "Order.Discount" },
    { "move":    "Order.Notes", "after": "Order.Customer" },
    { "label":   "Order.Notes", "text": { "pt-BR": "Observações da entrega" } },
    { "require": "Order.DeliveryDate" },
    { "addField": "Ext.DeliveryWindow", "type": "TimeRange", "section": "Customer" },
    { "lock":    "Order.Notes", "below": "store" },
    { "column":  "Lines", "add": "Margin", "expr": "(Price - Cost) / Price", "format": "percent" },
    { "button":  "row-menu", "label": { "pt-BR": "Enviar por WhatsApp" }, "flow": "flow:send-order-whatsapp" },
    { "on":      "Saved", "when": "Order.Total > 10000", "flow": "flow:notify-manager" } ] }
```

Every edit is checked when it is saved: the target exists in the shape or catalog, the profile allows it, a `require`
is stricter, an expression binds, a flow only uses catalog items the author may use. Errors are `Violation`s with paths
into the overlay, like Rules.Dynamic's enact checks.

## 5. Layers and locks (⚑C18)

```
developer page (code, profile)            ← what is allowed at all
  └ tenant overlay (ACME)                 ← head office
      └ store overlay (store 12)          ← store manager
          └ personal overlay (user)       ← "personalize my form" (Forms ⚑FM16): hide, move, column widths only
```

Applied in order, most specific last (Tenancy scope chain). A lock set at one layer stops every layer below from
changing that item. A personal overlay may only hide, move and resize, never require or add buttons, and needs no
enact: it is saved for the user directly.

## 6. The shield: two different things (⚑C19)

| Icon | Means | Owner | Enforced |
|---|---|---|---|
| 🔒 lock | who may **customize** this item (design rights) | Studio overlay | when an overlay is saved |
| 🛡 shield | who may **see or change the value** (field access) | **YFex.Authorization** (new feature) | on the server: queries omit or mask the value (masking from Privacy), command stage 2 rejects writes, the API and AI tools get the same result |

Hiding a field in a layout is **not** security: the value still comes back from the query and through the API. So the
shield edits an Authorization field-access policy; design mode is only a convenient editor for it, and a page shows the
field as hidden or masked because the value did not arrive. Without field access built, the shield is not offered.

## 7. How the page applies an overlay (⚑C20)

The controls never read Studio types. Studio turns the resolved overlay into neutral override lists that the
controls already understand from XAML:

| Control | XAML today (FM-P4) | From data (request) |
|---|---|---|
| `YForm` | `FieldOverride` (size, new row, visible), `FieldSlot`, `YFormRest` | `IFormOverrideSource`: hidden, order, label key, required (stricter), extra fields with their editor from the value type |
| `YDataGrid` | column definitions from the shape | column overrides (hidden, order, width) and extra columns whose values the owner computes |
| slots (`YPage` command bars, row menu) | developer items | extra items with a command that runs a flow |

Extension fields render with the editor registered for their value type (`YFormEditors`), read and write the record's
`ExtensionBag`, and are validated on the server with the same violation model (02 §11.2).

Applying happens once when the page builds, keyed by (page, scope chain, overlay revisions); a new revision rebuilds
the page on next open, or live when the admin enacts in design mode.

## 8. Delivery and offline (⚑C26)

Overlays are small revisions. Devices hold the resolved overlays for their scope in the revision cache
(`YFex.Revisions.Sync` over `IKeyValueStore`) and apply them offline. Sites receive them through the Installations road
(signed bundles) like rule data. A device that has an older revision shows the older layout; nothing an overlay does
can make a command valid that the server rejects.

## 9. Cons and risks

- **Developer pages change under overlays.** A renamed field breaks an overlay edit; the edit is then skipped and
  reported to the admin (catalog drift, 02 §17 risk 3), and CI compiles every enacted overlay against the new shapes.
- **Hand-written areas** are not editable: design mode only offers what came from a shape, a declared slot or a field.
- **Support cost:** "why does my screen look different" — the page header shows "customized by ACME, store 12" and the
  inspector lists which layer set each change.
