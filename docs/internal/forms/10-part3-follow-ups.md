# Forms part 3: follow-ups after the part 2 review (2026-11-26)

> Status: **plan** (2026-11-26; ⚑FM70 and ⚑FM71 accepted 2026-11-27). Builds on `Agent/forms-part2` ([09](09-part2-build-log.md)), or on main once that branch is
> merged. Comes from the user's answers to the part 2 "decisions to confirm" (09 §8): ⚑FM43 confirmed with an option to add,
> ⚑FM63 changed to opt-in. Two questions the user asked became ⚑FM70 (`[FormCommand]`) and ⚑FM71 (generated
> registration of form configurations). Decisions ⚑FM68–⚑FM71 are new.

| § | Step | Repos | ⚑ |
|---|---|---|---|
| 1 | `YTabView` hidden pages: an option to keep them in the visual tree | YFex.UI | FM43 ✅, FM68 |
| 2 | Sensitive display is opt-in | YFex core, YFex.UI | FM63 ✅ (changed), FM69 |
| 3 | Page commands (what `[FormCommand]` was for) | YFex.Mvvm, YFex.State or Mvvm generator, YFex.UI | FM70 ✅ |
| 4 | Generated registration of form configurations | YFex.UI (Forms generator) | FM71 ✅ |
| 6 | Required from configuration, enforced on the server | core, YFex.Validation, YFex.Domain, YFex.Rules, YFex.Tenancy, YFex.UI | FM62-1 ✅, FM72–FM74 ✅ |

All four steps can be built (⚑FM68–FM71 accepted 2026-11-27).

## 1. `YTabView` hidden pages option (⚑FM43 ✅, ⚑FM68)

**Confirmed (user, 2026-11-26):** the default stays as built: only the selected page is in the visual tree, every page's
content stays a logical child of its page (editors, values and validation kept). **New:** an option to keep built pages in
the visual tree.

### 1.1 Why the option

| Case | Why keep the page in the tree |
|---|---|
| a page with a heavy visual tree (a chart, a big grid, a map) switched back and forth | no re-attach, re-measure and re-template on every switch |
| a page whose state lives in the visual tree, not the view model (scroll offset of a non-virtual list, an expanded tree, a running animation, a video, a native host such as a WebView) | detaching loses it or restarts it |
| automation and the probe | every page's controls can be found without selecting the tab |

The cost: every kept page takes part in style and resource changes (theme switch) and holds its memory. Layout skips it
while hidden (`IsVisible = false`).

### 1.2 Shape (proposal, ⚑FM68)

```csharp
public enum TabPageRetention : byte
{
    Detached = 0, // default: only the selected page in the visual tree, content kept as a logical child (today)
    Kept = 1,     // a page stays in the visual tree once built; hidden pages are IsVisible = false
}
```

| Member | On | What |
|---|---|---|
| `YTabView.PageRetention` | the view | default `Detached` |
| `YTabPage.Retention` (`TabPageRetention?`) | one page | null = the view's value; lets one heavy page stay while the rest detach |
| `YForm.TabRetention` | forms with `GroupsAs.Tabs`, detail tabs (`Detail.Tabs`) | passed to the `YTabView` the form builds |

Pages are still **built on first selection** in both modes (`PageTemplate`, `Route`); `Kept` only changes what happens after.
A `Route` page keeps NavigatR's suspend-while-hidden in both modes (the view model is suspended; only the view stays).

### 1.3 How

- The template's single `ContentPresenter` becomes a host panel: `Detached` shows only the selected content (as today);
  `Kept` adds each built page's content once and toggles `IsVisible`. The earlier `YTabView : TemplatedControl` of the data
  grid branch did this; reuse its approach, not its code.
- `Reveal(control)`, focus-first-invalid and `ExpandInvalidSections` work the same (the page is selected first).
- FM43-1 (`GetLogicalDescendants` lists the selected content twice) must not get worse: check `YPageHost.NestedHosts` and the
  tests that count descendants in both modes.

### 1.4 Done when

- Tests: switching in `Kept` keeps a scroll offset and an expanded state; `Detached` still has only the selected page in the
  visual tree; per-page override; a route page suspended while hidden in both modes; a form with `GroupsAs.Tabs` validates and
  focuses a field on a hidden page in both modes.
- Gallery: Tabs › "Tab view" gets a retention picker; probe light, dark, 1280×720; StressKit tab case in both modes.

## 2. Sensitive display is opt-in (⚑FM63 changed, ⚑FM69)

**Decided (user, 2026-11-26): masking `[Sensitive]` values on screen is not on by default; an app, a value type, a model or a
field turns it on.** Built in part 2 the other way (lists, exports and one record masked by default), so this step changes
the defaults, not the mechanism.

### 2.1 New defaults

| Context | Part 2 default | New default |
|---|---|---|
| `Record` (form, details) | masked, reveal with permission | **clear** |
| `List` (grid, search results) | masked | **clear** |
| `Export` | masked | **clear** |
| `Log` (logs, traces, AI messages) | masked always | **masked always** (Privacy's guarantee, not a display choice) |
| `DataSensitivity.Secret` (passwords, keys) | hidden always | **hidden always** |

⚑FM69 (proposal): `Log` and `Secret` stay forced, because they are YFex.Privacy's protection of logs and secrets, not a screen
choice; "optional" applies to how a person sees personal data on screen. Rejected: everything off, logs included.

### 2.2 How an app turns it on

| Level | Code |
|---|---|
| whole app, the part 2 behavior | `defaults.MaskSensitive()` in `IFormDefaults` (sets List/Export masked, Record masked with reveal) |
| one value type | `defaults.Sensitive<Cpf>(DisplayContext.List, SensitiveDisplay.Masked)` |
| one model or field | `form.Field(m => m.Document).Sensitive(DisplayContext.Record, SensitiveDisplay.Masked).RevealWith(...)` (exists) |
| tenant | a tenant setting may turn masking on (a later layer may only make it stricter, unchanged) |

### 2.3 Changes

- core `SensitiveDisplays.Default`: personal data in `Record`, `List`, `Export` → `Clear`; `Resolve` keeps the `Log` and
  `Secret` rules. The XML docs and the FM63 remarks change with it.
- YFex.UI: `IFormDefaults` builder gets `MaskSensitive()`; `FormRules.Display` resolves from the new defaults; the reveal button
  shows only when a layer asked for `Masked`.
- Since opting in is now explicit, close the two gaps of part 2 for apps that opt in:
  - FM63-1a: the Forms generator records a property's `[Sensitive]` (raw strings, not only `IClassifiedData` value types) in
    `IFieldShape` (`Sensitivity`), so a field can be masked without a value type.
  - FM63-1b: `YDataGrid` applies `FormRules.Display` with `DisplayContext.List` to its columns (the row model's configuration).
  - FM63-2 stays: an editable field shows its value in clear (masking while typing is not planned).
- Gallery "Configuration layers": the masked e-mail example opts in with a visible line of code; tests updated
  (`FormConfigurationTests` sensitive defaults: clear by default, masked when opted in, log still masked, secret hidden).

### 2.4 Showing a CPF in clear (user, 2026-11-27)

`Cpf` (`YFex.Entity.Public/Cpf.cs:27`) is `[Sensitive(DataSensitivity.PersonalIdentifier, Mask = "***.###.###-**")]`.

| Where | Shown |
|---|---|
| any screen, by default (new §2.1) | **in clear** (`111.444.777-35`) |
| an app that turned masking on (`MaskSensitive()`) but wants CPF in clear | `defaults.Sensitive<Cpf>(DisplayContext.Record, SensitiveDisplay.Clear)` (and `List`, `Export`): the app-defaults layer is the first one, so a later layer can still mask it (only stricter) but nothing below it forces a mask |
| logs, traces, AI messages | masked (`***.444.777-**`), forced by ⚑FM69 |

Finding CPF-1: `default(Cpf).ToString()` is `"00000000000"` and `Cpf` has no `IsEmpty` (`Cnpj`/`CpfCnpj` have `IsDefault`,
`EmailAddress`/`PhoneNumber` `IsEmpty`): an empty CPF field may show zeros. Settled by §6.3.

## 3. Page commands (⚑FM70 ✅)

`[FormCommand]` was designed (02 §4.1) as methods **on the model** that appear on the page's command bar. It was not built.

### 3.1 Use cases

Mostly **settings pages** (one model = one page, ⚑FM49), where the page does more than Save and Discard:

| Page | Commands |
|---|---|
| Fiscal settings (NF-e/NFC-e) | Test certificate · Import certificate (.pfx) · Check SEFAZ status · Send test NFC-e (homologação) |
| Printers and devices | Print test page · Find printers · Read the scale (test) |
| E-mail / SMTP | Send test e-mail |
| Payment / TEF / API integrations | Test connection · Generate a new key · Copy webhook URL |
| Backup settings | Back up now · Restore… |
| Any settings page | Restore defaults |
| Store onboarding (Wizard layout) | a step's own action ("Validate CNPJ" before Next) |

And on record pages and CRUD details: Duplicate, Print, Send by e-mail, Deactivate.

Not page commands (they belong next to a field): "Look up CNPJ" fills fields from the document; "Find printer" next to the
printer field. These come with the value type's input (⚑FM47) or a `FieldSlot`.

### 3.2 Why not on the model

- The model is data (a `StateObject` that validates as a command). Testing a certificate needs services (the SEFAZ client,
  the key store) that a model does not have and should not get.
- A record has three models (⚑FM27); a command belongs to the screen, not to the edit model.
- `FormPageViewModel<TModel>` can already be inherited (⚑FM49), and `[StateCommand]` already generates commands: a page view
  model can have `TestCertificate` today. Only the way to the command bar is missing.

### 3.3 Options

| Option | How | For | Against |
|---|---|---|---|
| **A. Page commands on the page view model** (recommended) | `[StateCommand, PageCommand("Fiscal.TestCertificate", Icon = "Certificate", Placement = Bar)]` on a method of the page view model; the generator lists them in `PageViewModel.Commands` (label key, icon key, `ICommand`, placement, primary: data, no visual classes); `YFormPage` and `YCrudPage` put them in `YPageHeader.Actions` / the page menu; the command palette (⚑UI215, FM44) lists them | one way for every page (form page, CRUD page, hand-written page), no XAML; also searchable | a small attribute + generator work in the layer that generates `[StateCommand]` |
| B. No attribute: the view model fills `Commands` by hand | `Commands = [new PageCommand(TestCertificateCommand, "Fiscal.TestCertificate", "Certificate")]` | nothing generated | boilerplate; easy to forget the palette |
| C. `[FormCommand]` on the model (02 §4.1 as designed) | methods on the model | no view model needed | the model gets services; mixes data and screen |
| D. A `PageCommands` slot on `YFormPage` in XAML | as `YCrudPage` has | full freedom | needs a hand-written view, which the generic page removes |

### 3.4 Commands in the form configuration (user, 2026-11-27: "configurable too")

The view model **defines** a command (its body needs services); the form configuration (⚑FM62) **configures** it, like a
field, through the same layers:

```csharp
public sealed class FiscalSettingsFormConfiguration : IFormConfiguration<FiscalSettingsModel>
{
    public void Configure(FormBuilder<FiscalSettingsModel> form)
    {
        form.Command(FiscalSettingsPage.Commands.ImportCertificate)
            .EnabledWith(Permissions.Fiscal.ManageCertificate)   // or VisibleWith: hidden without the permission
            .Placement(CommandPlacement.Menu)                    // bar → the page's "…" menu
            .Order(1);
        form.Command(FiscalSettingsPage.Commands.SendTestInvoice).Hidden();
    }
}
```

| Rule | What |
|---|---|
| Configurable | `Hidden`, `VisibleWith`/`EnabledWith(permission)`, `Label`, `Icon`, `Order`, `Placement` (Bar, Menu), `Primary`, `ConfirmFirst(message key)` |
| Keys | generated per page view model (`FiscalSettingsPage.Commands.ImportCertificate`, a `PageCommandKey`), never a string, so a typo is a build error (no magic strings) |
| Layers | the same order as fields (app defaults → model → module → client/server → tenant → user → Composer); hide, lock and permissions only add up; label, order, placement take the later value; no permission checker = fails closed |
| Tenant | tenant settings (`forms.{Model}`, `JsonFormChangeSource`) may hide, relabel or reorder a command |
| Not configurable | the body: a configuration cannot add a command that has no method. Commands defined as data (a Capability, a CustomLogic action) come with ⚑FM44 later |
| Where it shows | the configured result is what `YFormPage`, `YCrudPage`, `YPageHeader` and the command palette read: a hidden or unpermitted command is not listed in the palette either |

Placement (part of ⚑FM70): page commands are
not a Forms concept, so `PageCommand` belongs in YFex.Mvvm (`PageViewModel`) and `YFex.UI.Public` (the contract the views read),
not in YFex.UI.Forms.

## 4. Generated registration of form configurations (⚑FM71 ✅)

Today (part 2) every configuration class is registered by hand at startup:

```csharp
FormConfigurations.Default
    .Add(new AppFormDefaults())
    .Add(new CustomerFormConfiguration())
    .Add(new FiscalSettingsFormConfiguration());
```

### 4.1 Proposal: find the classes by interface, no attributes

The Forms generator already runs in every assembly with `[Form]` models. It also finds every non-abstract class that
implements `IFormConfiguration<T>` (and its Module/Client/Server variants) or `IFormDefaults`, and emits one method per
assembly, as YFex.Cqrs does for `IAggregateConfiguration<T>` (`AddYFexConfigurations`):

```csharp
// generated in each assembly
public static class FormConfigurationRegistrations
{
    public static FormConfigurations AddGeneratedFormConfigurations(this FormConfigurations forms) => forms
        .Add(new AppFormDefaults())
        .Add(new CustomerFormConfiguration())
        .Add(new FiscalSettingsFormConfiguration());
}

// the app (or each module's setup), once per assembly
FormConfigurations.Default.AddGeneratedFormConfigurations();
```

- **No attribute on anything.** The interface a class implements says what it is; its layer comes from the interface
  (`IClientFormConfiguration` → Client) and the assembly it is compiled into (a server-only assembly has server classes only).
  The model's attributes stay layout facts (⚑FM62).
- Order: sorted by full type name, so a build is repeatable. Within one layer, hidden/locked/required/masking only add up, so
  order matters only for label, order and preset: two classes of the same layer setting a different label for one field get
  an analyzer warning.
- Constructors: configuration classes stay parameterless (they hold rules, not services; services are what the page view
  model or the handler is for). An analyzer error for a configuration class without a parameterless constructor.
- Hand registration keeps working (tests, a configuration chosen at run time).

Rejected: an assembly-level attribute listing the classes; a `[FormConfiguration]` attribute on each class (the interface
already says it); reflection scanning (not AOT-safe); registering through DI (`FormConfigurations` is read by controls without
a service provider).

## 5. Decisions

| ⚑ | Topic | Proposal | Rejected | See |
|---|---|---|---|---|
| FM43 ✅ | Tab pages in the visual tree | **Confirmed 2026-11-26 (user):** only the selected page in the visual tree by default; keeping pages is an option (§1) | — | §1 |
| FM68 ✅ | Name and shape of the option | **Accepted 2026-11-27 (user).** `PageRetention = Detached \| Kept` on `YTabView`, nullable per `YTabPage`, `YForm.TabRetention` passes it on | a bool `KeepPagesLoaded`; a third mode that releases a page's content when hidden (later if a case needs it) | §1.2 |
| FM63 ✅ (changed) | Sensitive display | **Decided 2026-11-26 (user): not by default, optional.** Screens show `[Sensitive]` values in clear unless the app, a value type, a model or a field asks for masking | masked by default (part 2) | §2 |
| FM69 ✅ | What stays forced | **Accepted 2026-11-27 (user)**; a type such as `Cpf` can still be shown in clear on screen (§2.4). logs always masked and secrets always hidden (Privacy's rules) | everything optional, logs included | §2.1 |
| FM70 ✅ | `[FormCommand]` | **Accepted 2026-11-27 (user), option A, and configurable in the form configuration like fields (§3.4).** Replace with page commands on the page view model (`[PageCommand]` beside `[StateCommand]`, in YFex.Mvvm + `YFex.UI.Public`), shown in the page header and the command palette; field actions stay with the value type's input | commands on the model; by hand only; a XAML slot only | §3 |
| FM71 ✅ | Registering configurations | **Accepted 2026-11-27 (user).** The Forms generator emits `AddGeneratedFormConfigurations()` per assembly from the classes' interfaces; no attributes; parameterless classes; repeatable order | attributes; reflection; DI | §4 |
| FM62-1 ✅ | Required from configuration | **Accepted 2026-11-27 (user).** §6: the contract declares which fields a configuration may make required (`[Requirable]`); the generated validator checks them on client **and** server from one lookup; value objects say what "empty" is (`IsEmpty`, core) | a hook for extra required paths (UI only); the form checks it (as built in part 2) | §6 |
| FM72 ✅ | Which fields a tenant may require | **Accepted 2026-11-27 (user).** Only fields marked `[Requirable]` on the command (tenant JSON naming another field is ignored and logged) | every optional field of a `[ValidatesAs]` command | §6.2 |
| FM73 ✅ | "Empty" on value objects | **Accepted 2026-11-27 (user)** (contract name still to pick when built). A core contract with `bool IsEmpty` that every value object implements (`Cpf`, `Cnpj`, `CpfCnpj`, `EmailAddress`, `PhoneNumber`…); Validation's presence checks, the Forms inputs and display use it | keep `Checks.IsDefault` (`default` struct = empty) and the mixed `IsDefault`/`IsEmpty` names | §6.3 |
| FM74 ✅ | Conditional requirements ("CPF required above R$ 10.000") | **Accepted 2026-11-27 (user).** YFex.Rules: a `Require` directive, and `Decision.ConstraintsFor` sets `Required`, so a rule both marks and enforces | Forms conditions only (UI); `[Required(When = …)]` (the client controls the condition) | §6.4 |

## 6. Required from configuration (⚑FM62-1 reopened and decided 2026-11-27, with ⚑FM72–FM74)

**Problem (part 2, 09 FM62-1):** a tenant setting says "Billing code is required here". Only the form checks it: a summary
line and focus, no message under the field, and **the server does not enforce it**, so another client (an API call, an old
app, an import) can skip it. The user asked to look at attributes and core value objects instead of a hook.

### 6.1 What exists (checked 2026-11-27)

| Mechanism | Where | Per tenant at run time? | Server? | Marks the field? |
|---|---|---|---|---|
| `[Required]`, `[NotEmpty]`, `[Length]`, `[Range]`… (+ `Profile`, `When`) | YFex.Validation `Attributes.cs:21–113` | no: fixed per type | yes (stage 1) | yes (`FieldConstraints.Required`) |
| `[ValidatesAs<TCommand>]` | the UI model validates with the **command's** rules (`Emitter.Vm.cs:262–271`) | — | same rules | yes |
| `RuntimeRules<T>` | `RuntimeRules.cs:17` | static per type, not per tenant; its constraints are shadowed for generated members (`Emitter.cs:624–656`, finding RQ-1) | yes | no |
| Profiles | `ProfileId` | the server always runs `All` | — | no |
| `[Required(When = nameof(X))]` | `Parser.cs:465` | reads the message itself: the client controls it | yes | no (conditional rules are left out of constraints) |
| YFex.Rules Deny at the tenant tier (code or Dynamic JSON) | `RulesStage` (YFex.Cqrs `Runtime/RulesStage.cs:95`), `RuleTierPath` | **yes** | **yes** (tier values loaded on the server) | **no**: directives are Deny/Warn/Limit/Grant/Adjust/Approval; `Decision.ConstraintsFor` builds only min/max/options (`Decision.cs:281`) |
| `IContextRule<TCommand>` | stage 5, DI | yes | yes | no (the UI sees it only after the server answers) |
| Value objects | `Cpf`, `Cnpj`… parse themselves (`TryParse`, `IsValid`); an invalid one cannot be built | — | — | — |

So nothing today is both **per tenant** and **checked on the client and the server with a field mark**. Value objects
already make the *format* check unnecessary (an invalid `Cpf` cannot exist); what is left for a value-object field is only
**presence**, which is exactly "required".

### 6.2 Proposal: the contract says what may be required, the generated validator checks it

```csharp
public sealed record CreateCustomerCommand(
    string Name,
    [property: Requirable] Cpf? Document,          // a tenant (or a module) may make it required
    [property: Requirable] string? BillingCode,
    PhoneNumber? Phone) : ICommand;
```

- `[Requirable]` (YFex.Validation, beside `[Required]`) marks a field a configuration may make required. The field is optional
  by type (`Cpf?`, `string?`); on a non-nullable field it is an analyzer error (it is already required).
- The Validation generator emits the presence check for it, guarded by one lookup:
  `if (required.IsRequired(Paths.BillingCode) && IsEmpty(v.BillingCode))` → the normal `required` violation. The same generated
  code runs in the UI model (through `[ValidatesAs]`) and in stage 1 on the server.
- The lookup is a small core contract (`IRequiredFields`: `bool IsRequired(Type message, FieldPath path)`), given to validation
  the way `ValidationServices` already are:
  - server: from the tenant's settings (`validation.{Command}.required`), loaded on the server for the caller's tenant, so a
    client cannot turn it off;
  - client: the same settings (synced), plus the configuration's code layers: `form.Field(m => m.X).Required()` writes this
    requirement for the module/app layer instead of being a UI-only check.
- `TryGetConstraints` returns `Required` for a requirable field the lookup turns on, so the field gets its "*" and the message
  under it, and `YValidationSummary` lists it like any other error. Forms' own required check (`ValidateRequired()`) goes away.
- Messages: the normal `required` message (⚑FM61 template), nothing new.
- ⚑FM72: only `[Requirable]` fields can be required by a tenant. The developer then knows which fields the server may need
  (imports and other clients must fill them); tenant JSON naming another field is ignored and logged.

Why better than the hook: the rule lives where the server already checks (the command contract); one generated check for both
sides; the field mark comes for free; no per-instance API on `StateObject`.

### 6.3 Value objects say what "empty" is (⚑FM73)

Today presence on a value struct is `Checks.IsDefault(v)` (`Checks.cs:26`), and the types disagree: `Cpf` has nothing
(`default(Cpf)` prints `"00000000000"`, CPF-1), `Cnpj`/`CpfCnpj` have `IsDefault`, `EmailAddress`/`PhoneNumber` have `IsEmpty`.

- A core contract (name ⚑, e.g. `IValueObject` with `bool IsEmpty { get; }`) that every core and domain value object
  implements; `default` of each is its empty value.
- Validation's `[Required]`/`[Requirable]` checks use `IsEmpty` when the type implements it, else as today.
- Forms: an input shows an empty value object as an empty box (never `00000000000`); `ToString()` of an empty value returns
  `""`; drafts store it as empty.
- Domain value objects (YFex.Domain) implement it too; the `IsDefault` names stay one release as obsolete aliases.

### 6.4 Conditional requirements stay with YFex.Rules (⚑FM74)

"CPF required when the sale is above R$ 10.000" or "required for convênio customers" is a business rule, not a setting, and
YFex.Rules already runs such rules per tenant and on the server. Missing piece: a `Require` directive (and its Dynamic
`DirectiveSpecKind`), so a decision both **marks** the field (`Decision.ConstraintsFor` sets `Required`, read through
`[ConstraintsFrom]`) and **denies** when it is empty.

### 6.5 Requests to other libraries

| Library | Change | Breaking | ⚑ |
|---|---|---|---|
| core `src/YFex` | `IRequiredFields`; value-object `IsEmpty` contract | no (new) | FM62-1, FM73 |
| YFex.Validation | `[Requirable]` + generator check and constraint; `IsEmpty` in presence checks; fix RQ-1 (run-time constraints shadowed for generated members) | no | FM62-1, FM73 |
| YFex.Domain | `Cpf`, `Cnpj`, `CpfCnpj` implement `IsEmpty`; `default(Cpf).ToString()` = `""` | `IsDefault` → obsolete alias | FM73 |
| YFex.Rules (+ Dynamic) | `Require` directive; `ConstraintsFor` sets `Required` | no | FM74 |
| YFex.Tenancy | settings key `validation.{Command}.required`, read on the server | no | FM62-1 |
| YFex.UI | Forms' `ValidateRequired()` removed; `FieldRuleBuilder.Required()` writes the requirement; tenant JSON `required` moves from `forms.{Model}` to `validation.{Command}` | yes (part 2 API, not merged) | FM62-1 |

## 7. Build log (2026-11-27)

Built on `Agent/forms-part2` (worktrees `C:\Dev\YFex\.claude\worktrees\forms\`), one commit per step. YFex.UI main (data
views, YKanban, YScheduler) was merged into the branch first (**b7bf93b**; conflicts only in `IconKey.cs` and `Icons.axaml`,
both sides appending icons). The §6 lower layers were built in parallel by another session on `Agent/required-fields`
([11](11-required-fields-build-log.md)).

| Step | Repo | Commit | What |
|---|---|---|---|
| §1 ⚑FM68 | YFex.UI | 2ba651e | `TabPageRetention` (`Detached` default, `Kept`); `YTabView.PageRetention` (inherited; `YForm` and `YDetailPresenter` add it as owners), `YTabPage.Retention` per page. `YTabView` hosts page contents itself (one presenter for the selected detached page, one per kept page) instead of TabControl's single presenter; content stays a logical child of its page and is listed once |
| §2 ⚑FM63/FM69 | YFex core | 67afbc9 | `SensitiveDisplays.Default`: clear on Record, List, Export; logs masked and secrets hidden stay forced. `SensitiveDisplays.Masking` = the set an app opts into. `Masks.TryWriteSpec` applies a `[Sensitive(Mask)]` spec at run time |
| §2 | YFex.UI | cf441ff | `FormDefaultsBuilder.MaskSensitive()`; FM63-1a: the Forms generator records a property's own `[Sensitive]` (`IFieldShape.Sensitivity`, `SensitiveMask`), so a raw `string` is masked with its spec; FM63-1b: `YDataGrid` generated columns apply `DisplayContext.List` from the row model's rules |
| ⚑FM64 | YFex.UI | 0f44b96 | `IFormConfiguration` → `IViewConfiguration<T>` (+ Module/Client/Server), `FormConfigurations` → `ViewConfigurations`, `FormBuilder` → `ViewBuilder`, `FormRuleSet` → `ViewRuleSet`, `FormLayer` → `ViewLayer` (`Composer` → `Studio`). `FormRules`, `IFormDefaults`, `FormOptionsBuilder` keep their names |
| ⚑FM64 | YFex.Mvvm | 416ddbf | doc names |
| §3 ⚑FM70 | YFex.State | 553a0e3 | the State generator reads YFex.Mvvm's `[PageCommand]` by metadata name (the FM49-1 pattern): a key per command in the page's nested `Commands` class and an override of `PageViewModel.PageCommands` (base page's first), each command bridged with `MvvmCommandAdapter` |
| §3 | YFex.Mvvm | 625f0cf | `[PageCommand(label, Icon, Placement, Primary)]`; `PageViewModel.PageCommands` (virtual, empty) |
| §3 | YFex.UI | 561309d | `YFex.UI.Public`: `PageCommand`, `PageCommandKey`, `CommandPlacement`, `IHasPageCommands` (`IFormPage`, `ICrudPage` extend it). Forms: `ViewBuilder.Command(key)` → `CommandRuleBuilder` (Hidden, Disabled, VisibleWith, EnabledWith, Label, Icon, Order, Placement, Primary, ConfirmFirst), kept as rules under `command:{Name}` so the layers and tenant JSON apply; `FormRules.Commands`; `ViewConfigurations.For(Type)` for page view models; `PageCommandSuggestions` (command palette). `YFormPage` and `YCrudPage` show bar commands and a "more" menu, ask first when configured, rebuild when the configurations change |
| §4 ⚑FM71 | YFex.UI | e5cb2f0 | `ViewConfigurationGenerator`: `AddGeneratedViewConfigurations()` per assembly (namespace = assembly name), classes found by interface, ordered by full name; YFFORM009 (no parameterless constructor: left for hand registration), YFFORM010 (one model and layer, two labels for one field or command); private nested classes skipped |

Names that differ from §1–§4 of this plan: `PageRetention` is also the name on `YForm` (an inherited property cannot be
renamed per owner), not `TabRetention`; the list of a page's commands is `PageViewModel.PageCommands` (the nested key class is
`Commands`, so the list cannot share the name); app-wide masking is `defaults.MaskSensitive()` and a type's clear display
`defaults.For<Cpf>().Sensitive(DisplayContext.Record, SensitiveDisplay.Clear)`; the generated method is
`AddGeneratedViewConfigurations()` (after the FM64 rename).

Checks: tab, form, detail, wizard, grid, page-command and configuration filters pass (126 + 113 + 15 + 10); YFex.Tests Privacy
29/29; YFex.State 435/435; generator test for FM71 passes. Probes (light, dark, 1280×720): `tab-retention.probe`,
`sensitive-opt-in.probe`, `page-commands.probe` (header button, "more" menu ordered by the configuration, the confirmation
asked and the command run after "Sim").

Findings:

- FM68-1: Avalonia's `ScrollViewer` keeps its offset when it leaves the visual tree, so a scroll position is not what `Kept`
  adds (§1.1 said it was). What differs: a detached page is attached again on every switch back (templates, `OnAttached`,
  animations, native hosts); a kept one joins once. The gallery shows an attach counter.
- FM63-3: a masked list column still sorts by the raw value (the order reveals a little about the hidden values).
- FM70-1: `[PageCommand]` takes its label as a plain string (like `[FormPage(Title)]`), not a catalog key: localizing page
  command labels needs a key form (a later decision).
- FM70-2: the State generator names YFex.Mvvm and YFex.UI.Public types by metadata name (same pattern as FM49-1).
- FM70-3: `PageCommandSuggestions` is ready but no gallery shell wires a command palette to the active page yet.
- Pre-existing, not from this work: the Tabs gallery page's older `YTabStrip` examples report `corner` lints; the Forms page
  reports a `covered-text` lint on an example header; `ViewMapGeneratorTests` (YFex.UI.SourceGenerator.Tests) fail when built
  with `--artifacts-path` (they load the generator from a fixed `C:\Temp\YFex.UI.SourceGenerator` path).

### 7.1 §6 YFex.UI part (2026-11-27)

The four `Agent/required-fields` branches ([11](11-required-fields-build-log.md)) were brought in: core merged into the forms
core branch (**fd9e30e**); Validation, Domain and Rules through the forms folder's junctions, now pointing at the
`required-fields` worktrees (no merge into main yet). Four Validation additions were needed for the UI side and committed on
`Agent/required-fields`:

| Commit | What |
|---|---|
| f08be60 | `RequiredFields.Default` (a client app's lookup when a view model's `ValidationServices` gives none); `ContractMaps` (view-model member → contract path, from the module initializer) |
| 02bf7a4 | `IValidationSource.RequiredFields` (set by the screen showing the model; raises `ConstraintsChanged`) and `IValidationSource.ValidateAll()` (default members; generated view models implement them) |
| 506d295 | a view model's own `[Requirable]` member with no other rule gets its constraints case, a field runner and its `Check` call; view models join `RequirableFields` and `ContractMaps` |

YFex.UI **302dc40**, **e807adf**: `ViewConfigurations.Requirements` = the code layers' `Field(…).Required()`, written when a
configuration is added, in the command's terms (`ContractMaps`) or by the model's own `[Requirable]` members, combined with the
tenant's `validation.{Command}.required` settings (`AddRequiredSettings`, `ReloadRequiredSettings`); a field no one may require
is ignored and reported (`IgnoredRequirement`, ⚑FM72). `YForm` hands the requirements to the model it shows, so the model's own
validation marks, checks and summarizes them; `YForm.ValidateModel()` runs submit validation; `YForm.DefaultConfigurations`
sets `RequiredFields.Default`. Removed: `ValidateRequired()`, the form's own required marks and summary lines,
`FieldChange.Required`. `YFormPage` saves and wizard steps use the model's validation. `YFex.UI.Forms` now references
`YFex.Validation`. Tests: the module-layer requirement, a tenant's setting, an ignored requirement; probe
`required-from-configuration.probe` (light, dark).

Findings:

- FM62-1-g: a form hands its requirements to the root model only; a child model shown as a section reads
  `RequiredFields.Default` (the app-wide configurations).
- FM62-1-h: `IFormDefaults` requirements per value type (`For<T>().Required()`) are not written as requirements (no field path).
- Still not built (from 11): the server's stage-1 lookup in YFex.Cqrs/YFex.Messaging and a per-tenant settings read.

### 7.2 Merge into main: waiting (2026-11-27)

Not merged. YFex.Validation's main checkout holds another session's uncommitted RecordAccess work in files `Agent/required-fields`
also changes, and YFex.UI and YFex.Mvvm need the new Validation API. The user chose to wait and merge everything together once
that work is committed: fast-forward `Agent/forms-part2` (YFex.UI, core, Mvvm, State, NavigatR, Persistence, Localization) and
`Agent/required-fields` (Validation, Domain, Rules) into main (every main was still at its branch base), commit the forms docs,
then rerun the YFex.UI suite and the FormsSpike AOT smoke.
