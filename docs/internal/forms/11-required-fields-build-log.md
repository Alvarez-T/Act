# Forms part 3 §6: required from configuration, lower layers (build log, 2026-11-27)

> Status: **built 2026-11-27**, steps 1–4 of [10 §6](10-part3-follow-ups.md#6-required-from-configuration-fm62-1-reopened-and-decided-2026-11-27-with-fm72fm74)
> (⚑FM62-1, FM72, FM73, FM74, all decided by the user 2026-11-27). Branch `Agent/required-fields` in worktrees under
> `C:\Dev\YFex\.claude\worktrees\required\` (not merged, not pushed). Step 5 (YFex.Tenancy) not built: Tenancy has no settings
> store (§5). The YFex.UI side (`FieldRuleBuilder.Required()` writes the requirement, Forms' `ValidateRequired()` goes away)
> is for the next session, on the API in §2.

## 0. Setup and commits

| Repository | Branch `Agent/required-fields` from `main` | Commit | What |
|---|---|---|---|
| YFex (core) | 1422ea1 | **38956bc** | `IValueObject`, `IRequiredFields`; `EmailAddress`, `PhoneNumber` implement `IValueObject` |
| YFex.Domain | 98ff5b7 | **78bd08a** | `Cpf`, `Cnpj`, `CpfCnpj`, `Rg`, `FullName`, `PartyName`, `CompanyName` implement `IValueObject`; `default(Cpf).ToString()` = `""` (CPF-1); `IsDefault` → `[Obsolete]` alias |
| YFex.Validation | ed6491d | **c612612** | `[Requirable]`, YFV0031, generated lookup-guarded check and constraints, VM wiring, `IsEmpty` presence, `RequiredFieldSet` / `RequiredFieldSettings` / `RequirableFields`, RQ-1 |
| YFex.Rules | 4207ab6 | **447c37b** | `Require` directive (+ Dynamic `require`), `FieldLimit.Required`, `ConstraintsFor` sets `Required`, empty → `required` error |

Every other `YFex*` sibling is a junction in the same folder (so `..\YFex` of a junctioned repo resolves to the core worktree).
Build output: `--artifacts-path C:/Temp/yfex-required`.

## 1. Gates

| Repo | Build (Release) | Tests | AOT |
|---|---|---|---|
| core | ✅ | YFex.Tests 351/351 (2 new) | — |
| YFex.Domain | ✅ `domain.slnx`, 0 warnings | Entity.Public.Tests 28/28 (6 new) | — |
| YFex.Validation | ✅ `validation.slnx` | Validation.Tests 89/89 (22 new: behaviour + in-process generator run); **also run, not changed:** YFex.State's `YFex.Validation.Generator.Tests` 79/79 and `YFex.State.Tests` 433/433 against this branch (the VM emitter path compiles) | Smoke publishes with **0 IL warnings**; the 4 new lines pass (requirable off/on, settings, constraints, value object). The smoke then stops on finding AOT-1 (§6), which main has too |
| YFex.Rules | ✅ `rules.slnx` | Rules.Tests 153/153 (6 new), Dynamic.Tests 32/32 (3 new), Generator.Tests 35/35, Data.Tests 19/19; the decision digest golden tests are unchanged | no smoke app in the repo |

Not tested here: a `[ValidatesAs]` view model reading `IRequiredFields` from `ValidationServices` at run time (the generated
code compiles in every State test; a behaviour test needs YFex.State or YFex.UI, which this session must not touch). The UI
session should add it (§2.4).

## 2. API for the YFex.UI side

### 2.1 Core (`namespace YFex`)

```csharp
public interface IValueObject { bool IsEmpty { get; } }                        // Values/IValueObject.cs
public interface IRequiredFields { bool IsRequired(Type message, in FieldPath path); }   // Paths/IRequiredFields.cs
```

- `path` is the member's generated path **relative to `message`** (`CreateCustomerCommand.Fields.BillingCode`, a template
  without indices). A nested record is asked with its own type and path.
- ⚑FM73 name: **`IValueObject`** (the user's example). Why: it names what implements it (every YFex value object), so a form
  input or display can test `value is IValueObject { IsEmpty: true }` without knowing the type, and later value-object contracts
  can join it. Rejected: `IEmptiable`/`IHasEmpty` (a capability name for the one thing every value object must say anyway).

### 2.2 YFex.Validation

| Symbol | Use |
|---|---|
| `RequirableAttribute` | on a command member: `[property: Requirable] Cpf? Document` |
| `ViolationWriter.RequiredFields { get; set; }` and `ViolationWriter.Begin(out w, IRequiredFields? requiredFields)` | the lookup the generated checks read; `Validator.ValidateAt` passes it to nested types |
| `Validator.Check/Validate/IsValid<T>(in T value, IRequiredFields? required)` | the same with one call |
| `IValidatable<T>.TryGetConstraints(ReadOnlySpan<char> path, IRequiredFields? required, out FieldConstraints c)` (static virtual, generated) and `Validator.TryGetConstraints<T>(path, required, out c)` | constraints with the run-time requirements; the 2-argument overload = `required: null` |
| `FieldConstraints.Requirable`, `ConstraintFlags.Requirable` (256), `FieldConstraints.AsRequired()`, `FieldConstraints.Create(…, requirable: bool)` | the field may be required / is required now |
| Generated per requirable member: `Rules.X.Requirable` (const), `Rules.X.IsRequiredBy(IRequiredFields?)`, `Rules.X.ConstraintsFor(IRequiredFields?)` | what the view-model code and Forms can call |
| `RequiredFieldSet : IRequiredFields` — `Require(Type, in FieldPath)`, `Require<TMessage>(in FieldPath)`, `Remove(Type, in FieldPath)`, `event Action? Changed` | **what `FieldRuleBuilder.Required()` should write into** (module/app/client layers); thread-safe, only adds per layer |
| `RequiredFieldSettings(Func<string, string?> read, Action<Type, string>? ignored = null) : IRequiredFields` — `TryGetKey(Type, out string key)`, `Reload()`, `event Action? Changed` | settings text: key `validation.{Message}.required` = `["BillingCode", "Document"]`; a non-`[Requirable]` path is ignored and passed to `ignored` (⚑FM72) |
| `RequiredFieldsExtensions.Or(this IRequiredFields? first, IRequiredFields? second)` | combine layers (required when any says so) |
| `RequirableFields.IsRequirable(Type, ReadOnlySpan<char>)`, `Of(Type)`, `TryGetName(Type, out string)`, `TryFind(string, out Type)` | the generated registry (module initializer): which fields a configuration may require, and the settings name |
| `BuiltIn.Checks.IsEmpty<T>(T)`, `IsEmptyOrNull<T>(T?)`, `IsEmptyObject<T>(T?)` | presence on an `IValueObject` (generated code uses them) |

`{Message}` = the type name with its containing types, no namespace (`Customer.Commands.CreateCommand`); see ⚑FM62-1-b.

### 2.3 What the generator emits (view models included)

- Contract (`CreateCustomerCommand`): `Rules.BillingCode.Check(...)` (both overloads) and the owner-aware member method start
  with `if (IsRequiredBy(w.RequiredFields) && <empty>) { w.Add(path, "required"); return false; }`; the presence test is
  `string.IsNullOrWhiteSpace` for text, `Checks.IsEmptyOrNull` for `T?` value objects, `Checks.IsEmpty` for non-nullable ones,
  empty for collections. `[Required]` on an `IValueObject` member also reads `IsEmpty` now (⚑FM73).
- View model with `[ValidatesAs<CreateCustomerCommand>]`: every field run (and invariant run) opens its writer with
  `__vRequiredFields`, read **once per view model** from `ValidationServices.GetService(typeof(IRequiredFields))`; its
  `IValidationSource.TryGetConstraints` returns `AsRequired()` for a borrowed (or own) requirable member the lookup turns on.
  So the message under the field, the summary line and the "*" come from the normal validation path.

### 2.4 What the UI session has to do

1. Register one `IRequiredFields` in the services a form's view model exposes as `ValidationServices` (app and Forms setup):
   `codeLayers.Or(tenantSettings)`, with `codeLayers` a `RequiredFieldSet` that `FieldRuleBuilder.Required()` fills (the model's
   field maps to the command through `[ValidatesAs]`/`PathMap`; the requirement is written with the **command** type and path).
2. When `RequiredFieldSet.Changed` / `RequiredFieldSettings.Changed` fires, re-validate the touched fields and raise constraints
   changed (the generated VM does not listen: ⚑FM62-1-d).
3. Move tenant JSON `required` from `forms.{Model}` to `validation.{Command}.required` (`RequiredFieldSettings.TryGetKey`).
4. Remove Forms' `ValidateRequired()`; add the VM behaviour test of §1.

## 3. How the server gets the tenant's lookup

Stage 1 (`YFex.Cqrs` `MessagePipeline.CheckShape`) opens `ViolationWriter.Begin(out var w)` without a lookup today. The change
(**not built**: it touches 14 call sites in two repos, YFex.Cqrs and YFex.Messaging, whose main checkouts have other sessions'
uncommitted work):

```csharp
// YFex.Cqrs Runtime/MessagePipeline.cs
public static bool CheckShape<T>(in T message, IRequiredFields? required, out ValidationResult violations)
{
    using var scope = ViolationWriter.Begin(out var w, required);
    bool valid = ValidationSlot<T>.Validate(in message, ref w);
    violations = w.Count == 0 ? default : w.ToResult();
    return valid;
}
// + the object overload; the old overloads call these with null.
```

Callers pass `Services.GetService(typeof(IRequiredFields)) as IRequiredFields` from the **request scope**:
`CqrsDispatcher` (3 sites), YFex.Messaging `LocalDispatcher` (3), `FusionMessageBus` (5), `ValidatingHandlerInvoker` (the server
invoker decorator), `CallContextMiddleware`, `WolverineLocalDispatcher`. The server registers it **scoped**:

```csharp
services.AddScoped<IRequiredFields>(sp => new RequiredFieldSettings(
    key => sp.GetRequiredService<ITenantSettings>().Get(key),        // the caller's tenant, resolved on the server
    (message, path) => log.IgnoredRequirement(message, path)));
```

The client cannot turn a requirement off: the lookup is built on the server from the caller's resolved tenant, and the same
generated `Validate` runs there. `ValidationSlot<T>.Validate(in m, ref w)` and `ObjectValidation.Validate` need no change (the
writer carries the lookup).

## 4. YFex.Rules `Require` (⚑FM74)

```csharp
[Rule("sale.cpf-above-10k", Group = nameof(SaleRules.Checks))]
public readonly struct CpfAbove10k : IRule<SaleSituation>
{
    public static bool Applies(in SaleSituation s) => s.Total > new Money(10_000m);
    public static void Evaluate(in SaleSituation s, ref DirectiveWriter d) => d.Require(SaleSituation.Fields.CustomerDocument);
}
```

```json
{ "code": "tenant.cpf-above-10k", "group": "Checks", "when": { ">": [ { "var": "Total" }, 10000 ] },
  "then": [ { "require": { "field": "CustomerDocument" } } ] }
```

- `RequireDirective(SituationFieldRef Field)`, `DirectiveCase.Require`, `DirectiveWriter.Require<TS, T>(field)` / `Require(SituationFieldRef)`,
  `DynamicDirectives.Require` (part of `Constraints`), `DirectiveSpecKind.Require`.
- `FieldLimit.Required`, `FieldLimit.RequiredSource`; `Decision.ConstraintsFor(field)` and `TryGetConstraints(name, out c)` set
  `Required`, so `[ConstraintsFrom]` marks the field with no UI change.
- After all stages, a required field whose value is empty (none, or blank text) adds `required` (Error) at the field's path,
  with the rule as its source: the decision is Denied, the message is the normal `required` text.

## 5. YFex.Tenancy (step 5): not built

Tenancy owns scope identity and resolution (`IScopeContext`, `TenantContext`, `ITenantStore`) but **no settings store**: no
`ITenantSettings`, no key-value read per tenant. Forms part 2 also takes settings as a `Func<string, string?>`
(`JsonFormChangeSource`). So the lookup over settings lives in YFex.Validation (`RequiredFieldSettings`, store-agnostic), and the
host passes its store. Missing piece for the server: a per-tenant settings read (`ITenantSettings.Get(key)` or similar) — in
YFex.Tenancy, or wherever tenant settings are decided to live (the tenancy design docs/internal/tenancy has ⚑TN1–18 pending;
none covers settings). Recorded as ⚑FM62-1-f.

## 6. Decisions taken here (recommended option picked) and findings

| ⚑ | Question | Picked | Alternative |
|---|---|---|---|
| FM62-1-a | How the lookup reaches generated `Validate` | **On the writer** (`ViolationWriter.RequiredFields`), set by whoever opens it: the server per request, the VM from `ValidationServices`. Explicit, no ambient state, AOT-safe, free when null | an ambient `AsyncLocal` (hidden, costs per call); a parameter on every generated method (breaks `IValidatable`) |
| FM62-1-b | Settings name of a message | **Type name with containing types, no namespace** (`Customer.Commands.CreateCommand`), like Forms' `forms.{Model}`. Con: renaming the C# type changes the key; two modules with the same short name collide (the registry keeps the last) | full name with namespace (long, still rename-sensitive); an explicit name attribute (another identity to declare; take it when YFex.Cqrs gets a message identity) |
| FM62-1-c | `[Requirable]` on a non-nullable member | **Error YFV0031, except** a value type that is an `IValueObject` (its default is empty, so a `Cpf Document` can be required) and a non-nullable collection (can be empty). Also an error with `[Required]` on the same member | an error for every non-nullable member (10 §6.2 literally) |
| FM62-1-d | Lookup changes at run time | **Change events on the two implementations** (`RequiredFieldSet.Changed`, `RequiredFieldSettings.Changed`); the UI re-validates. No change contract in core yet | an `INotifyRequiredFieldsChanged` in core the generated VM listens to (add when the UI needs it) |
| FM62-1-e | Requirable fields of nested and element types | Nested records are asked with **their own type and path** (`Inner.BillingCode` → `(typeof(Inner), "BillingCode")`); `[Requirable]` on members of collection elements is checked the same way when the element type is validatable | the root message's path (`Lines[].X`): would need the root type inside nested validation |
| FM62-1-f | Where per-tenant settings come from | Not built: a settings read per tenant is missing (§5) | — |
| FM72-a | "ignored and logged" | **Reported through a callback** (`RequiredFieldSettings(read, ignored)`); the host logs. Libraries do not reference the logging engine | a log call inside Validation |
| FM73-a | Which core types implement `IValueObject` | **`EmailAddress`, `PhoneNumber`** (form fields whose default is "none"). Not the Guid identities (`InstallationId`, `KeyId`) or `TenancyPath`: their `ToString` of default is a wire/log format, not "" | all core value types (numbers/Money have no empty: 0 is a value) |
| FM73-b | Empty `Cpf`/`Cnpj` in JSON | **Written as `null`, read `null` back as empty** (before: `Cpf` wrote an unreadable `"00000000000"` and threw on null; `Cnpj` wrote `""`). `CpfCnpj` already did this | keep throwing on null |
| FM73-c | Domain types beyond the three in the plan | **`Rg`, `FullName`, `PartyName`, `CompanyName` too** (same `IsDefault` pattern); `FullName.Display` of default was `" "`, `CompanyName.ToString()` of default was null: now `""` | only Cpf/Cnpj/CpfCnpj |
| FM74-a | `Require` in decision tables | **Not built**: no `DirectiveKind.Require` table column; code rules and Dynamic rules only | a `[Require]` column (add on request) |
| FM74-b | Fingerprint and the signed digest stream | A marker byte is appended **only for a required field**, so every decision without a requirement hashes exactly as before (golden digest tests unchanged); no new version tag | a `v2` tag (every approval digest changes) |
| FM74-c | Empty, for the engine | `RawValue.None` or blank `Text`. A value object whose raw form of "empty" is something else is not seen as empty | a raw-type hook "is empty" |
| FM74-d | "Why is this required?" | **Not built**: `Explanation` / `IWhySource` do not list `Require` lines yet (the violation carries the rule as its source, so `RefOf` names it) | follow-up in Rules explanations |

Findings:

- **AOT-1 (pre-existing, main too):** the Validation AOT smoke throws `InvalidCastException: a Decimal value is not a money` in
  `Order.Rules.Total..cctor` (`[Range(0, 100000)] Money` → `RawValue.As<Money>()`); JIT passes. Not caused by this branch
  (checked by publishing main's smoke). Owner: YFex.Validation range bounds over `IRawValue` types / core `RawValue`.
- **Privacy masks an empty CPF as `******`** (its fixed placeholder when the value does not fit the mask). Safe for logs; a
  screen that masks would show stars for "no CPF". Request to YFex.Privacy: `Classified.Mask` of an `IValueObject` that
  `IsEmpty` → `""`.
- Side fix: the VM parser now reads a contract member's attributes from its record's positional parameter too (`[MapsTo]`
  borrow and YFV0023 "looser than contract" used to miss `[Length]` written on a positional parameter without `property:`).
- RQ-1 fixed: `TryGetConstraints` of a generated member merges `RuntimeRules<T>` constraints (most restrictive).

## 7. Additions from the UI session (2026-11-27)

Committed on `Agent/required-fields` in YFex.Validation by the session that built the YFex.UI side (10 §7.1): **f08be60**
`RequiredFields.Default`, `ContractMaps`; **02bf7a4** `IValidationSource.RequiredFields` and `ValidateAll()`; **506d295** own
`[Requirable]` members of view models registered, constrained and checked. Validation 89/89, State's Validation generator tests
79/79, State 435/435 after each.
