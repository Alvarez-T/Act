# 02 — Part 2 preview: the CRUD screen pattern

> Part of the [forms design](README.md). Status: **outline for later** (2026-11-18). Recorded now so part 1 does not block it.
> Decisions ⚑FM40–FM46 and ⚑FM49 in the README.

## 1. What YFex.Farma shows today

Read on 2026-11-18: `app/YFexFarma.Desktop/Views/Customer/CrudCustomerView.axaml` (550 lines) and
`app/YFexFarma.Desktop.Presentation/Customers/CrudCustomerViewModel.cs`.

| Observation | Where | What the pattern must remove |
|---|---|---|
| The same three-tab customer form is written **twice**: once for "detail panel on the right", once for "detail panel at the bottom", switched by `IsPanelRight` / `IsPanelBottom` | `CrudCustomerView.axaml:172` and `:380` | one form, one property for where the detail shows |
| Tabs are hand-made buttons with `Classes="tab-nav active"` driving `SwitchPanel.SwitchTo`; every group's content is built at once | `:214-232` | a tab view that builds content and view model on first open |
| Each field is `StackPanel` + `TextBlock` label + `TextBox`, watermark typed by hand (`(00) 00000-0000`) | `:243-270` | generated fields, masks from the type |
| Grid columns typed by hand, again listing the same properties | `:181-187` | columns from the row model's shape |
| Model fields are all `string` (`CpfCnpj`, `Phone`, `Email`), no validation | `CustomerModel.cs` | value objects + `[ValidatesAs<TCommand>]` |
| Save / Cancel buttons repeated per tab with no command bound | `:272-274` and others | one command bar, commands from the view model |
| Hard-coded colours, sizes and a custom `GridSplitter` template | `:12-120` | tokens and `Y` controls |
| CommunityToolkit `[RelayCommand]` mixed with YFex.State `[Observable]`; `OnNavigation` throws | view model | YFex.State commands; the base class implements navigation |

## 2. The pattern

Every CRUD screen is the same four parts. The base classes in `YFex.Mvvm` (stubs today: `EditorViewModel<TModel>`, `ListViewModel`, `MasterDetailViewModel`, `SelectorViewModel`) own the flow; a module writes only what is specific.

```
 ┌ command bar ─ New · Save · Delete · Duplicate · Export · Refresh   (capabilities, permission-gated) ┐
 │ search / filters  (from the row shape + YFex.Search)                                               │
 ├─────────────────────────────────────────┬──────────────────────────────────────────────────────────┤
 │ list (YDataGrid, columns from the row   │ detail: YForm of the edit model, tabs on demand,          │
 │ shape, paging, selection)               │ Create / Edit; details screen read-only; dirty guard      │
 └─────────────────────────────────────────┴──────────────────────────────────────────────────────────┘
   detail position: Right | Bottom | Dialog | Page   (one property, saved per user)
```

> **Moved (2026-11-23, user):** where and how the detail opens (left/top/right/bottom/center; docked, flyout, sheet, modal,
> page, pop out), detail-only fields, tabs and the action commands now belong to the data grid plan:
> [YFex.UI data-and-logic/01-detail-presenter](../../../../YFex.UI/docs/plans/data-and-logic/01-detail-presenter.md)
> (⚑DG1–DG13; ⚑DG10 records the move). This section keeps only the CRUD view-model flow.

### 2.1 View model (illustrative, *proposed* API)

```csharp
public sealed partial class CustomersViewModel
    : CrudViewModel<CustomerRow, CustomerEditModel, CustomerId>
{
    protected override IQuery<PagedList<CustomerRow>> List(ListRequest request)
        => Customer.Queries.List(request.Page, request.PageSize);

    protected override async Task<CustomerEditModel> Load(CustomerId id, CancellationToken ct)
        => CustomerEditModel.From(await Customer.Queries.GetById(id).Send(ct));

    protected override ICommand Save(CustomerEditModel model)
        => model.Id.IsNew ? model.ToCreateCommand() : model.ToUpdateCommand();
}
```

The base class provides: paging and refresh, selection → load, `New` / `Save` / `Delete` / `Duplicate` commands with `CanExecute` from state (`IsModified`, `HasSelection`, busy), confirm on delete, "discard changes?" through NavigatR `OnLeaving`, server violations back onto fields (Validation's `ApplyServer`), toasts on success, offline-queued status from Messaging.

### 2.2 View

```xml
<!-- the whole screen when nothing is custom -->
<y:YCrudPage DetailPlacement="{Binding DetailPlacement}" />

<!-- custom detail, everything else default -->
<y:YCrudPage>
    <y:YCrudPage.Detail>
        <y:YTabView>
            <y:YTab Header="{y:Text Customer.Contact}">
                <y:YForm Source="{Binding Editor.Model}" Groups="Contact" />
            </y:YTab>
            <y:YTab Header="{y:Text Customer.Fiscal}" Route="{x:Type vm:CustomerFiscalViewModel}" />  <!-- view model created on first open -->
        </y:YTabView>
    </y:YCrudPage.Detail>
</y:YCrudPage>
```

## 3. Tabs on demand (the `SwitchPanel` question)

`SwitchPanel` keeps every group as a child, so all content is built up front. `YTabStrip` is a strip without content. Proposal: **one `YTabView`** (strip + content host) instead of improving `SwitchPanel`:

- content is a template, built on first selection; optionally unloaded after N minutes hidden;
- a tab may name a route: its view model is created through NavigatR on first selection, suspended when hidden (`YPageHost` already suspends hidden pages), and activated through State's `IActivatable` / `[LoadOnInit]`;
- transitions from motion tokens (`Slide`, `Fade`, none for reduced motion), the same as page transitions;
- keyboard (Ctrl+Tab, Ctrl+1…9), overflow and drag reorder already exist in `YTabStrip`.

`SwitchPanel` stays for small in-place switches (it has no strip) or is retired once `YTabView` covers its uses (⚑FM43).

## 4. A model can be a whole page (⚑FM49)

Not every screen is a list + detail. Settings, a company profile, a pharmacy's fiscal configuration, a "new sale header" are **one model = one page**. The same shape that renders a form can render the page around it, so these screens need no view and almost no view model.

### 4.1 What the developer writes

```csharp
[FormPage(Title = "Settings.Fiscal", Icon = "Receipt")]       // proposed: the model is a navigable page
[ValidatesAs<FiscalSettings.Commands.UpdateCommand>]
public partial class FiscalSettingsModel : StateObject
{
    [FieldGroup("Company")]  …                                  // each group becomes a page section
    [FieldGroup("Invoices")] …
    [FieldGroup("Certificate", StartsCollapsed = true)] …

    [FormCommand(Primary = true)]  public Task Save() => …;     // proposed: commands on the page's command bar
    [FormCommand]                  public Task TestCertificate() => …;
}
```

```csharp
Navigator.NavigateTo<FormPage<FiscalSettingsModel>>();          // or a route registered once for every [FormPage] model
```

### 4.2 What the page gives for free

| Part | From |
|---|---|
| page header: title, description, icon (`YPageHeader`, pinning) | `[FormPage]` + catalog keys |
| sections as a long scroll with a section index (jump list on the side, current section highlighted) **or** as tabs (`Layout = Sections \| Tabs \| Wizard`) | groups and child models of the shape |
| command bar: Save (Ctrl+S), Discard, the model's `[FormCommand]` methods, enabled by state (busy, `IsModified`, valid) | model + State |
| load on open, save through the command, "discard changes?" on leave, draft restore | `FormPageViewModel<TModel>` (generic, in Mvvm) + NavigatR + Persistence |
| validation summary per section and at the bottom (01 §12), error dots on the section index / tabs | Validation + 01 §12 |
| `Wizard` layout: one group per step, Next validates only that step's fields (Validation profiles), Finish runs the command | shape groups + `ProfileId` |

### 4.3 Options

| Option | How | Pros | Cons |
|---|---|---|---|
| **A. Generic page** (recommended): one `FormPageViewModel<TModel>` in YFex.Mvvm and one `YFormPage` view in YFex.Avalonia, mapped once in the view map | no generated view model; `FormPage<TModel>` works for every `[FormPage]` model | needs `Load`/`Save` hooks: the model's `[FormCommand]` methods or a small `IFormPageHandler<TModel>` |
| B. Generated page per model | the generator emits `FiscalSettingsPage : PageViewModel` | easy to extend by `partial` | more generated code; Forms generator would reference Mvvm types (layer check) |
| C. Hand-written page that just hosts `YForm` | today's approach | full freedom | the boilerplate this design removes |

A page model can still be opened as a dialog (`Presentation = Dialog`, through `IDialog`) or docked as a tab, because the view model is an ordinary page view model.

## 5. Open questions for part 2

| ⚑ | Question |
|---|---|
| FM40 ✅ | Base classes in `YFex.Mvvm` (`CrudViewModel<TRow, TModel, TId>` composed of `ListViewModel<TRow>` + `EditorViewModel<TModel>`) vs a separate `YFex.Mvvm.Crud` package. **Decided 2026-11-25 (user): in `YFex.Mvvm`.** |
| FM41 ✅ | **Decided (user 2026-11-18): separate models**, each with its own boundary: `CustomerRow` (list), `CustomerEditModel` (form), `CustomerDetailsModel` (details screen with purchases, appointments, convênio); see 01 §9.1 |
| FM42 ✅ | `YCrudPage` as a templated control in YFex.Avalonia (domain-free, binds to an interface in `YFex.UI.Public`) vs a page template in the app. **Decided 2026-11-25 (user): the control, provided every part can be extended or replaced.** It always has: the page toolbar, the grid with its own toolbar, an add button, master-detail (`YDetailPresenter`), and the detail can extend to another view model's tile (pop out / dock, ⚑DG4–DG5). Parts are slots (`PageCommands`, `GridToolbar`, `Grid`, `Detail`, `Empty`) that add to or replace the default |
| FM43 ✅ | `YTabView` new control vs extend `SwitchPanel`. **Decided 2026-11-25 (user): `YTabView : TabControl`** (Avalonia's selection, keyboard and automation), tabs drawn with the `YTabStrip` look and tokens, content from templates or a `Route` built on first selection. **Confirmed 2026-11-26 (user):** only the selected page in the visual tree by default; an option to keep pages is planned in [10](10-part3-follow-ups.md) §1 (⚑FM68) |
| FM44 | Commands as YFex.Capabilities entries (search "novo cliente", AI) and gates from YFex.Authorization |
| FM45 ✅ | Contract → edit model mapping (`CustomerEditModel.From(Customer)`): Validation `[ValidatesAs]` generator, Forms generator, or hand-written. **Decided 2026-11-25 (user): hand-written.** An edit model often joins several DTOs or reshapes properties, so no automapping; the CRUD base only calls the app's `Load` |
| FM46 ✅ | Detail placement and column widths per user: State `[Persist]` vs Persistence store directly. **Decided 2026-11-25 (user):** the form configuration says what may be remembered (`f.Form().Remember(FormMemory.CollapsedSections \| DetailPlacement \| GridColumns)`, per-section defaults); `YForm`/`YCrudPage` save and load it per user and model through YFex.Persistence with no view-model code; "Restaurar layout padrão" clears it; it is the user layer of ⚑FM62 |
