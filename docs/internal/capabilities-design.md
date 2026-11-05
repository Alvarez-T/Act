# YFex.Capabilities — System Design

**Status:** Proposed (design/plan). Companion: [`authorization-design.md`](authorization-design.md). Framing + cross-track reconciliations: [`authorization-capabilities-overview.md`](authorization-capabilities-overview.md).

> **Reconciliation note.** The shared types `Permission`, `IPolicyEvaluator`, `IAuthContext`, `IPrincipalAccessor`, `RequirePermissionAttribute`, and `AuthorizationManifest` are **owned by YFex.Authorization** and defined authoritatively in `authorization-design.md`. This document references them; where an earlier draft sketched a simpler shape, the authz definitions win (notably: the evaluator is `IPolicyEvaluator.IsAllowed(in Permission, IAuthContext)`, not `IsGranted(Permission, ClaimsPrincipal)`; `Permission` wraps a `PermissionKey`; and `AuthorizationManifest` is catalog *display* metadata, distinct from this track's per-command `IAuthorizationView`).

## 1. Purpose & scope

A single **capability manifest** that describes, in one uniform shape, every *actionable or queryable* surface of the whole system — **not just ViewModels**. Each capability carries a human/AI description, its parameters, a side-effect class, the required permission, and search keywords. The manifest is fed by pluggable **capability sources** (§3A):

- **ViewModels** — `[StateCommand]` actions + `[Observable]` properties (the reference source, detailed in §6–§7).
- **CQRS** — `ICommand` / `IQuery<T>` / `IEvent` messages.
- **Messaging** — `[Live]` / `[Subscribe<T>]` events and RPC endpoints.
- **HTTP APIs** — inbound endpoints (and, where useful, outbound clients).
- **Jobs** — background/scheduled jobs (`YFex.Jobs`).
- **Navigation** — routes.
- …and anything else that implements `ICapabilitySource`.

A single set of authored/derived declarations serves three decoupled consumers:

1. a universal **search bar** (keywords + display, permission-filtered),
2. an **AI agent** (verbose, secure action descriptions gated by side-effect + permission) — able to discover and safely invoke actions across the *entire* system, not only the current screen,
3. **authorization/visibility**.

Non-goals: this system does **not** implement authorization decisions, does **not** own the permission catalog, and does **not** re-declare permissions. Those belong to `YFex.Authorization`. It *reads* the shared `[RequirePermission]` attribute (and each source's existing permission metadata) and *references* the shared `Permission` value object.

### Locked decisions baked in

- **D1** — Permission is declared once via `[RequirePermission("orders.edit")]` (owned by `YFex.Authorization`) on the VM class or the `[StateCommand]` method. The argument is a **string literal** (a `const` from the catalog). That raw string lives **only** at the attribute boundary and its generated skeleton mirror; it is wrapped into the `Permission` value object at exactly **one** point (`CompiledCapabilityRegistry.Build`). Every downstream surface uses the value object, never a raw string.
- **D2** — The profile's `.Requires(...)` is kept as an **override-only escape hatch**, not the normal path. `Build()` fails fast if a `Mutating`/`Irreversible` command has no permission from either source.
- **D3** — Capabilities are keyed by `CapabilityKey = (CapabilityKind Kind, Type OwnerType, MemberName Member)`. `OwnerType` is the VM / message record / endpoint request / job type; `Member` is a VM property/command name, or `None` for **type-level** capabilities (a CQRS command, a job, an endpoint — where the type *is* the capability). ViewModels attach 0..N routes as metadata (via `RouteRegistry`); other sources attach their own (endpoint route, job schedule, …). A capability is emitted for anything a registered `ICapabilitySource` contributes — ViewModels are just one source.
- **D4** — Value objects follow the authz track's rule: **no implicit string conversion**; construction and formatting are explicit only.

---

## 2. Project layout

```
src/
  Capabilities/
    YFex.Capabilities/                 (runtime library + built-in ViewModel source)
    YFex.Capabilities.Generator/       (netstandard2.0 generator — ViewModel skeletons)
    YFex.Capabilities.Cqrs/            (ICapabilitySource bridge over CompiledMessagingRegistry)
    YFex.Capabilities.Messaging/       (ICapabilitySource bridge over [Live]/[Subscribe]/RPC)
    YFex.Capabilities.Web/             (ICapabilitySource bridge over inbound HTTP endpoints)
    (Jobs source lives in the YFex.Jobs repo as YFex.Jobs.Capabilities — built 2026-11-04, YFex.Jobs Plan D §7A)
  State/
    YFex.State.Generator.Abstractions/ (netstandard2.0 — NEW, shared discovery; recommended)
    YFex.State.Generator/              (existing — refactored to consume Abstractions)
  Authorization/                        (parallel track — external dependency)
    YFex.Authorization/                 (owns [RequirePermission], Permission, IPolicyEvaluator, AuthorizationManifest)
```

| Project | Target | Responsibility | References |
|---|---|---|---|
| `YFex.Capabilities` | multi-target per repo props | Value objects (incl. `ResourceKey`), `ICapabilityProfile<T>`/`ICapabilityBuilder<T>`, compiled records, the three registry interfaces + implementations, `CompiledCapabilityRegistry.Build`, `ICapabilityAgentApi`, `ICapabilityLocalizer`. | `YFex.State` (attribute FQNs, `StateObject`), `YFex.NavigatR` (`IRouteRegistry`), `YFex.Authorization` (`RequirePermissionAttribute`, `Permission`, `IPolicyEvaluator`, `IPrincipalAccessor`, `AuthorizationManifest`), **`Microsoft.Extensions.Localization.Abstractions`** (localization seam, §4.6) |
| `YFex.Capabilities.Generator` | `netstandard2.0` | Incremental generator: discovers members + XML docs + `[RequirePermission]`, emits `StructuralManifest` partial implementing `IStructuralCapabilityProvider`. Analyzer only — no runtime refs. | `Microsoft.CodeAnalysis.CSharp`; `YFex.State.Generator.Abstractions` |
| `YFex.State.Generator.Abstractions` (recommended) | `netstandard2.0` | Extracted shared member-discovery: `CommandParser`, `ObservableParser`, raw models, `EquatableArray<T>`. Guarantees the capability generator derives command property names **identically** to `YFex.State.Generator`. | `Microsoft.CodeAnalysis.CSharp` |

**Capability sources are thin bridges.** Each `YFex.Capabilities.<Subsystem>` implements `ICapabilitySource` (§3A), depends on both that subsystem and `YFex.Capabilities`, and is registered in DI; the subsystem itself never references `YFex.Capabilities`. Ship them incrementally — the registries and consumers don't change when a source is added.

**Why the shared abstractions library matters:** the skeleton's command `Name` must equal the command *property* that `YFex.State.Generator` actually emits (`SaveAsync` → `SaveCommand`, via the "trim `Async` + append `Command`" rule in `YFex.State/src/YFex.State.Generator/CommandParser.cs:120-124`). If the capability generator re-derives that name independently and the rule ever changes, profile selectors (`x => x.SaveCommand`) silently break. Sharing `CommandParser` eliminates that drift. If the extraction is deferred, the capability generator MUST copy the naming rule verbatim and a test must assert parity.

---

## 3. The shared seam with YFex.Authorization

Three shared artifacts, all **owned by `YFex.Authorization`** and defined in `authorization-design.md` — this track only references them:

- `RequirePermissionAttribute(string permission)` — the single permission declaration; its argument is a raw string only at the attribute boundary (D1).
- `Permission` — `readonly record struct` wrapping a `PermissionKey`, full value-object interface set (`authorization-design.md` §4.2). Constructed only via `Permission.Parse(...)` / `new Permission(PermissionKey)`. **No implicit string conversion** (D4).
- `IPolicyEvaluator` — the shared decision engine. Sync visibility: `bool IsAllowed(in Permission permission, IAuthContext ctx)`. `IAuthContext` is obtained from `IPrincipalAccessor.Current`.
- `AuthorizationManifest` — **catalog display metadata** (id/display/description per permission). It is *not* a per-capability map; this track's per-command map is `IAuthorizationView` (§8).

Coexistence: source generators are pure/additive. `YFex.Authorization`'s generator and `YFex.Capabilities.Generator` both *read* `RequirePermissionAttribute` and never write to each other. This track's `IAuthorizationView` is a thin per-command projection (VM-type + member → `Permission`); allow/deny is always answered by the shared `IPolicyEvaluator`, never re-implemented here. It is complementary to authz's `CompiledAuthorizationRegistry.ByViewModelType` (which gates whole-screen navigation).

---

## 3A. Capability sources — the whole system, not just ViewModels

The manifest is **source-pluggable**. The three registries (§8) are built by fanning in every registered `ICapabilitySource`; ViewModels are one source, CQRS / messaging / APIs / jobs / routes are others, and third parties can add more without touching the core.

```csharp
public interface ICapabilitySource
{
    // Contributes capabilities in the common shape. Runs once at CompiledCapabilityRegistry.Build.
    IEnumerable<CapabilityDescriptor> Contribute(ICapabilityBuildContext ctx);
}

// The common, source-agnostic unit every source emits and every registry is projected from.
public sealed record CapabilityDescriptor(
    CapabilityKey Key,
    ResourceKey Display,
    ResourceKey AiDescription,
    ResourceKey KeywordsKey,
    SideEffect SideEffect,
    Permission? RequiredPermission,             // value object; sources wrap raw strings once (§7.1)
    IReadOnlyList<ParameterDoc> Parameters,
    CapabilityFlags Flags,
    IReadOnlyList<Type> Routes);                // 0..N attached routes/endpoints (may be empty)

[Flags] public enum CapabilityFlags : byte
{
    None       = 0,
    Deferred   = 1,   // IQueueable command / job: may execute later / offline
    Cacheable  = 2,   // ICacheable query
    Background = 4,   // job / fire-and-forget
    Hidden     = 8,   // discoverable by AI/authz but excluded from the search bar
}
```

**Source families and where each field comes from.** Permission is always the shared `[RequirePermission]` / the subsystem's existing authorizer metadata; the default description is the member's XML doc; side-effect/keyword overrides live in each subsystem's own idiomatic config (so nothing is declared twice):

| Source (bridge project) | OwnerType / Member | SideEffect | Params | Description / keywords authored in |
|---|---|---|---|---|
| **ViewModel** — `YFex.Capabilities` (§6–§7) | VM type / property or command name | profile `.SideEffect` | command params | `ICapabilityProfile<TVM>` + XML docs |
| **CQRS** — `YFex.Capabilities.Cqrs` | message record / `None` | `IQuery`→ReadOnly; `ICommand`→Mutating (config may raise to Irreversible); `IQueueable`→+Deferred; `ICacheable`→+Cacheable | record properties | `IAggregateConfiguration<T>` (new `.Describe/.SideEffect/.Keywords`) + XML docs |
| **Messaging** — `YFex.Capabilities.Messaging` | event / handler type / `None` | events ReadOnly (notifications); RPC per contract | event / RPC payload props | `[Live]`/`[Subscribe<T>]` + config + XML docs |
| **HTTP API** — `YFex.Capabilities.Web` | endpoint request type / `None` | HTTP verb: GET→ReadOnly, POST/PUT/PATCH→Mutating, DELETE→Irreversible | request DTO / route+query params | endpoint metadata + XML docs |
| **Jobs** — `YFex.Jobs.Capabilities` (YFex.Jobs repo) | job type / `None` | Mutating +Background (+Deferred) | job argument props | job attribute/config + XML docs |
| **Navigation** — folded into the ViewModel/route source | route type / `None` | ReadOnly (navigation) | route params | `RouteAttribute.DisplayName` + config |

Each source is a **thin bridge**: it reuses that subsystem's *already-compiled* metadata (e.g. CQRS's `CompiledMessagingRegistry.ByMessageType`, the routing table, the jobs registry) plus a one-line XML-doc harvest, and projects it into `CapabilityDescriptor`s. **No subsystem gains a dependency on `YFex.Capabilities`**; the bridge project depends on both and is registered in DI. This is the open/closed extension point — a new subsystem becomes AI-describable and searchable by adding one `ICapabilitySource`.

**Per-source authoring stays idiomatic** — semantic metadata is authored where that subsystem already configures itself:
- ViewModels → `ICapabilityProfile<TVM>` (§5).
- CQRS → extend `IAggregateConfiguration<T>`'s command/query builders with `.Describe(ai)`, `.SideEffect(...)`, `.Keywords(...)` (matches the "configuration over attributes for CQRS" convention).
- Messaging / API / Jobs → the config object each already has, plus XML docs as the default description.

**AI-safety note.** Non-VM sources are *more* powerful than VM commands: a CQRS `ICommand` or a job is a real domain mutation, and `IQueueable`/`Background` ones may execute **later / offline**. The gating in §9 is unchanged (permission + side-effect), but the `Deferred`/`Background` flags let the agent (and the confirmation UX) tell the user an action will run asynchronously rather than implying it completed synchronously.

---

## 4. Data model (final)

### 4.1 Value objects (YFex.Capabilities)

```csharp
public enum CapabilityKind : byte
{
    // ViewModel source
    View = 0, ViewModelProperty = 1, ViewModelCommand = 2,
    // other sources
    CqrsCommand = 10, CqrsQuery = 11, CqrsEvent = 12,
    MessagingEvent = 20, MessagingRpc = 21,
    ApiEndpoint = 30,
    Job = 40,
    Route = 50,
}

// Member is a VM property/command name; None for type-level capabilities
// (CQRS message, API endpoint, job, route — where the OwnerType itself is the capability).
public readonly record struct MemberName : ISpanFormattable
{
    private readonly string _value;
    public static readonly MemberName None = default;
    private MemberName(string value) => _value = value;
    public static MemberName Of(string name) => new(name);
    public bool IsNone => string.IsNullOrEmpty(_value);
    public string Value => _value ?? "";
    public string ToString(string? f, IFormatProvider? p) => Value;
    public bool TryFormat(Span<char> d, out int w, ReadOnlySpan<char> f, IFormatProvider? p)
        => Value.AsSpan().TryCopyTo(d) ? (w = Value.Length) >= 0 : ((w = 0) < 0);
}

// D3 — system-wide key: kind + owning type + optional member.
public readonly record struct CapabilityKey(CapabilityKind Kind, Type OwnerType, MemberName Member) : ISpanFormattable
{
    public static CapabilityKey ForType(CapabilityKind kind, Type owner) => new(kind, owner, MemberName.None);
    public static CapabilityKey ForMember(CapabilityKind kind, Type owner, string member) => new(kind, owner, MemberName.Of(member));
    public string ToString(string? f, IFormatProvider? p) =>
        Member.IsNone ? $"{Kind}:{OwnerType.Name}" : $"{Kind}:{OwnerType.Name}#{Member.Value}";
    public bool TryFormat(Span<char> d, out int w, ReadOnlySpan<char> f, IFormatProvider? p)
        => ToString(null, null).AsSpan().TryCopyTo(d) ? (w = ToString(null,null).Length) >= 0 : ((w=0)<0);
}

// Localization: the manifest stores KEYS, never translated text (see §4.6). This replaces the
// earlier inline LocalizedText { Pt, En } / KeywordSet { Pt, En } (hardcoded two languages,
// translations baked into the frozen tables). ResourceKey follows D4 (no implicit string conversion).
public readonly record struct ResourceKey : ISpanFormattable
{
    private readonly string _key;
    public string Value => _key ?? "";
    public static readonly ResourceKey None = default;
    private ResourceKey(string key) => _key = key;
    public static ResourceKey Parse(string s) =>
        string.IsNullOrEmpty(s) ? throw new ArgumentException("ResourceKey cannot be empty.", nameof(s)) : new(s);
    public override string ToString() => Value;
    public string ToString(string? f, IFormatProvider? p) => Value;
    public bool TryFormat(Span<char> d, out int w, ReadOnlySpan<char> f, IFormatProvider? p)
        => Value.AsSpan().TryCopyTo(d) ? (w = Value.Length) >= 0 : ((w = 0) < 0);
}

public enum SideEffect : byte { ReadOnly = 0, Mutating = 1, Irreversible = 2 }  // closed set → enum, not [Union]
```

### 4.2 Structural feedstock (emitted by the generator; the *boundary* artifact)

Raw permission strings are permitted here and only here (plus the attribute). This type is generated code, not a consumer surface.

```csharp
public interface IStructuralCapabilityProvider { static abstract StructuralManifest Structural { get; } }

public sealed record StructuralManifest(
    Type ViewModelType,
    string? ClassRawPermission,                 // class-level [RequirePermission], raw string
    ImmutableArray<StructuralProperty> Properties,
    ImmutableArray<StructuralCommand> Commands);

public sealed record StructuralProperty(string Name, Type Type, string? XmlDoc, bool IsReadOnly);
public sealed record StructuralCommand(
    string Name, string MethodName, string? XmlDoc,
    string? RawPermission,                       // method-level [RequirePermission], raw string
    ImmutableArray<StructuralParameter> Parameters);
public sealed record StructuralParameter(string Name, Type Type, string? XmlDoc);
```

### 4.3 Profile overlay model (produced by the builder)

```csharp
public sealed record CapabilityProfileModel(
    Type ViewModelType,
    ViewOverlay View,
    ImmutableDictionary<string, PropertyOverlay> Properties,   // key: member name
    ImmutableDictionary<string, CommandOverlay>  Commands);

// Overlay holds DEFAULT-culture literals authored in the profile. At Build these seed the neutral
// resource and their derived ResourceKeys go into the compiled records; other cultures live in the store (§4.6).
public sealed record ViewOverlay(string Display, string? AiDescription, ImmutableArray<string> Keywords);
public sealed record PropertyOverlay(string? DisplayOverride, ImmutableArray<string> Keywords);
public sealed record CommandOverlay(
    SideEffect SideEffect,
    ImmutableArray<string> Keywords,
    string? AiDescriptionOverride,
    Permission? PermissionOverride,              // D2: from .Requires — wrapped here at authoring time
    ImmutableDictionary<string, string> ParameterDescriptions);   // param name → default-culture description
```

### 4.4 Compiled records (post-Build; value-object permission everywhere)

```csharp
// These records are the **ViewModel source's** grouped projection. The generalized, source-agnostic
// unit is CapabilityDescriptor (§3A) — every source emits it and every registry is built from it.
// All text/keyword fields are language-neutral ResourceKeys — resolve via ICapabilityLocalizer (§4.6).
public sealed record ViewCapability(
    Type ViewModelType, IReadOnlyList<Type> Routes,
    ResourceKey Display, ResourceKey AiDescription, ResourceKey KeywordsKey,
    IReadOnlyList<PropertyCapability> Properties, IReadOnlyList<CommandCapability> Commands);

public sealed record PropertyCapability(MemberName Name, Type Type, ResourceKey Display, ResourceKey KeywordsKey, bool IsReadOnly);

public sealed record CommandCapability(
    MemberName Name, ResourceKey Display, ResourceKey AiDescription,
    SideEffect SideEffect, Permission? RequiredPermission,   // value object; null only when ReadOnly
    IReadOnlyList<ParameterDoc> Parameters, ResourceKey KeywordsKey);

public sealed record ParameterDoc(string Name, Type Type, bool Required, ResourceKey Description);
```

### 4.5 AI outcome union

```csharp
[Union]                                            // payload-bearing → [Union] is correct here
public partial struct ActionGate
{
    public partial struct Allowed();
    public partial struct NeedsConfirmation(string Reason);
    public partial struct Denied(Permission Missing);        // value object
}
```

### 4.6 Localization — resource keys, pluggable store, cross-culture keywords

**Confirmed 2026-09-02.** The manifest is **language-neutral**: every display/description/keyword slot is a `ResourceKey`, never embedded translated text; translation lives in the app's resource store and is resolved at read time. This replaces the earlier inline `LocalizedText { Pt, En }` / `KeywordSet { Pt, En }`, which hardcoded exactly two languages and baked translations into the frozen registries. Rationale confirmed against current Microsoft guidance (Localization docs, updated 2026-03): `.resx`/`IStringLocalizer` remains standard, but the dependency to take is the **abstraction**, and the backing store is pluggable.

**Pluggable backing store (decision: abstraction only).** The framework depends solely on `Microsoft.Extensions.Localization.Abstractions` (`IStringLocalizer` / `IStringLocalizerFactory`). The app chooses the store, with no framework change:
- `.resx` + `IStringLocalizer` — the standard model (culture-fallback, satellite assemblies). Fine for JIT hosts.
- a **source-generated or JSON `IStringLocalizerFactory`** — trimming/NativeAOT-safe (no `ResourceManager`/satellite reflection), matching the framework's "source generators over reflection, AOT-friendly" ethos. Microsoft explicitly supports swapping the factory.

**Key derivation.** Keys are derived deterministically from `CapabilityKey` + slot: `cap:{VMType}#{member}:display`, `:ai`, `:keywords`; parameters `…:{param}:desc`. Per the modern .NET workflow, a missing translation returns the key, so the authored **default-culture literal can double as its own key**. The capabilities generator emits a **default-culture `.resx`/JSON seed** from the authored literals + XML docs, so translators start from a populated neutral file; the profile therefore authors only the default culture (no per-language arrays).

**Resolution seam.**
```csharp
public interface ICapabilityLocalizer
{
    string Display(ResourceKey key);                 // CURRENT culture (view/command/property/param) + .NET fallback
    IReadOnlyList<string> Keywords(ResourceKey key); // ALL configured cultures, unioned + split
    // impl wraps IStringLocalizerFactory; the configured culture set comes from LocalizationOptions / app config
}
```
- **Display/descriptions** resolve for the *current* UI culture, using .NET culture-fallback.
- **Keywords** resolve across **every configured culture at once** and are unioned (decision), so a query in any supported language matches — e.g. a pt-BR user typing "save" still hits a command whose en synonym is "save", matching the mixed-language search examples. Each culture's keyword resource value is a delimiter-separated synonym list, split on load. Done once per culture-set at registry build and cached — never per keystroke.

---

## 5. Authoring API

Mirrors `YFex.Cqrs.Configuration.IAggregateConfiguration<T>` (`YFex.Cqrs/src/YFex.Cqrs/Configuration/IAggregateConfiguration.cs`).

```csharp
public interface ICapabilityProfile          // non-generic base for collection at startup
{
    Type ViewModelType { get; }
}
public interface ICapabilityProfile<TViewModel> : ICapabilityProfile where TViewModel : notnull
{
    void Configure(ICapabilityBuilder<TViewModel> builder);
    Type ICapabilityProfile.ViewModelType => typeof(TViewModel);
}

// All authored literals are DEFAULT-culture values that double as resource keys (§4.6);
// other cultures live in the app's resource store. No per-language arrays in the profile.
public interface ICapabilityBuilder<TViewModel>
{
    // View-level — default-culture display + AI text (act as keys / seed the neutral resource)
    IViewSpec Describe(string display, string ai);

    // Member selectors — expression-based, compile-time-checked
    IPropertySpec Property<TProp>(Expression<Func<TViewModel, TProp>> selector);
    ICommandSpec  Command(Expression<Func<TViewModel, object?>> selector);
}

public interface IViewSpec     { IViewSpec Keywords(params string[] keywords); }   // default-culture synonyms
public interface IPropertySpec { IPropertySpec Keywords(params string[] keywords);
                                 IPropertySpec Display(string display); }           // default-culture literal = key
public interface ICommandSpec
{
    ICommandSpec SideEffect(SideEffect sideEffect);                // required per command
    ICommandSpec Keywords(params string[] keywords);              // default-culture synonyms
    ICommandSpec Describe(string ai);                             // default-culture AI text (overrides XML-doc)
    ICommandSpec Parameter(string name, string description);     // default-culture param description
    ICommandSpec Requires(Permission permission);                // D2 — ESCAPE HATCH, normally omitted
}
```

**Selector → member name:** the builder unwraps the expression body (`MemberExpression`, tolerating the `Convert` node that `object?` boxing introduces) to `member.Name`. Commands resolve to the generated command property name (`SaveCommand`); properties to the observable property name (`CustomerName`). This is the sole place member identity is bound, and it is compile-time checked.

Authoring example (domain repo, next to the VM — never inside it):

```csharp
public sealed class OrderEditorCapabilities : ICapabilityProfile<OrderEditorViewModel>
{
    public void Configure(ICapabilityBuilder<OrderEditorViewModel> vm)
    {
        // Default culture here is pt-BR; other cultures (e.g. en) live in the resource store,
        // keyed by the derived ResourceKey (§4.6). Cross-culture keyword matching pulls those in.
        vm.Describe("Editor de pedido", ai: "Tela para visualizar e editar um pedido de venda.")
          .Keywords("pedido", "editar pedido");

        vm.Property(x => x.CustomerName).Keywords("cliente");

        vm.Command(x => x.SaveCommand)                     // permission from [RequirePermission] on the method
          .SideEffect(SideEffect.Mutating)
          .Keywords("salvar", "gravar");

        vm.Command(x => x.CancelOrderCommand)
          .SideEffect(SideEffect.Irreversible)
          .Describe(ai: "Cancela o pedido. Não pode ser desfeito; estoque liberado, cliente notificado.")
          .Parameter("reason", "Motivo do cancelamento.")
          .Keywords("cancelar pedido");
    }
}
```

---

## 6. Generator design (`YFex.Capabilities.Generator`)

### 6.1 What it reads

- `[Observable]` properties and `[StateCommand]` methods — reusing `ObservableParser`/`CommandParser` from `YFex.State.Generator.Abstractions`.
- **XML docs**: `<summary>` for properties (already captured — `YFex.State/src/YFex.State.Generator/ObservableParser.cs:59,67`) and, after the extension below, for commands and their `<param>` entries.
- **`[RequirePermission]`**: read as a sibling attribute off the method symbol and off the class symbol, via `ISymbol.GetAttributes()` matching `AttributeClass.ToDisplayString() == "YFex.Authorization.RequirePermissionAttribute"`, extracting `ConstructorArguments[0].Value as string`.

### 6.2 CommandParser XML/`<param>` extension

`CommandParser.Transform` (`…/CommandParser.cs:30`) currently reads only `[StateCommand]` named args and `method.Parameters`. Extend it:

```csharp
// inside Transform, after CT detection
string? xmlDoc = method.GetDocumentationCommentXml();               // mirrors ObservableParser
// parse <summary> and <param name="..."> from xmlDoc (shared XmlDocReader helper)
string? rawPermission = ReadRequirePermission(method);              // sibling attribute
// params: pair method.Parameters[i] with <param> text
```

Add `XmlDoc`, `RawPermission`, and a `Parameters` array (name/type/paramDoc) to `CommandMethodRawModel` (`…/CommandParser.cs:213`). `CommandParser.GroupByClass` (`…/CommandParser.cs:153`) already groups by containing type — the capability generator consumes those groups directly. A small `XmlDocReader` (parse the `<summary>`/`<param>` XML fragment) lives in Abstractions.

### 6.3 Incremental pipeline

Two providers joined; targeted predicates only (honoring the existing generator's "no broad `CreateSyntaxProvider`" rule in `StateGenerator.cs:6-9`):

```
A. members  = FAWMN("YFex.State.ObservableAttribute")  ⟶ ObservableParser.Transform
            ⊕ FAWMN("YFex.State.StateCommandAttribute") ⟶ CommandParser.Transform
            .Collect() ⟶ GroupByClass ⟶ per-VM structural members
                         (+ read class-level [RequirePermission] on the containing type)

B. profiled = SyntaxProvider.CreateSyntaxProvider(
                 predicate: node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
                 transform: extract TViewModel from ICapabilityProfile<T> base, else null)
              .Where(t => t is not null)

Join A × B on VM type ⟶ emit StructuralManifest ONLY for VM types present in B   // D3
context.RegisterSourceOutput(joined, EmitSkeleton)
```

Predicate cost note: predicate B fires only on class declarations that have a base list, then the transform confirms the `ICapabilityProfile<T>` interface — cheap and IDE-safe. (If profile classes are later marked with a `[CapabilityProfile]` attribute, switch B to `ForAttributeWithMetadataName` for an even cheaper gate — recommended follow-up.)

### 6.4 Member-name resolution order (per command)

Permission for a command, resolved at Build from skeleton + overlay:

1. `CommandOverlay.PermissionOverride` — profile `.Requires(...)` (escape hatch, D2).
2. `StructuralCommand.RawPermission` — method-level `[RequirePermission]`.
3. `StructuralManifest.ClassRawPermission` — class-level `[RequirePermission]` (inherited default).
4. none.

### 6.5 Exact emitted skeleton

For the VM in §7.1 the generator emits:

```csharp
// <auto-generated/> by YFex.Capabilities.Generator
#nullable enable
namespace Acme.Orders;

partial class OrderEditorViewModel : global::YFex.Capabilities.IStructuralCapabilityProvider
{
    static global::YFex.Capabilities.StructuralManifest
        global::YFex.Capabilities.IStructuralCapabilityProvider.Structural => __Structural;

    private static readonly global::YFex.Capabilities.StructuralManifest __Structural = new(
        ViewModelType: typeof(global::Acme.Orders.OrderEditorViewModel),
        ClassRawPermission: null,
        Properties: global::System.Collections.Immutable.ImmutableArray.Create(
            new global::YFex.Capabilities.StructuralProperty(
                "CustomerName", typeof(string), "The customer's display name.", false)),
        Commands: global::System.Collections.Immutable.ImmutableArray.Create(
            new global::YFex.Capabilities.StructuralCommand(
                "SaveCommand", "SaveAsync", "Saves all pending edits to the order.",
                "orders.edit",                                   // raw string — boundary only
                global::System.Collections.Immutable.ImmutableArray<global::YFex.Capabilities.StructuralParameter>.Empty),
            new global::YFex.Capabilities.StructuralCommand(
                "CancelOrderCommand", "CancelOrderAsync", "Cancels the order permanently.",
                "orders.cancel",
                global::System.Collections.Immutable.ImmutableArray.Create(
                    new global::YFex.Capabilities.StructuralParameter("reason", typeof(string), "Reason for cancellation.")))));
}
```

### 6.6 Why not reflection (recorded rationale)

1. **XML doc comments are not in runtime metadata** — `///` compiles to a side `.xml` file, never into the assembly. Reflection can see `SaveCommand` but can never recover its description or `<param>` text. The descriptions are the entire point of the manifest → a compile-time generator is mandatory.
2. **AOT/trimming** — the framework mandates "source generators over reflection, AOT-friendly." A generated static array is trim-safe; a startup reflection crawl over every VM is not.
3. **Startup determinism** — one frozen static per VM, zero per-startup scanning.

The skeleton feeds **both** the runtime registries (search + authz-view) and the AI-describe registry. It is not "just docs."

---

## 7. Compilation — `CompiledCapabilityRegistry.Build`

Mirrors `YFex.Cqrs.CompiledMessagingRegistry.Build` (three-phase register→compile→frozen; `YFex.Cqrs/src/YFex.Cqrs/CompiledMessagingRegistry.cs`).

```csharp
public sealed class CompiledCapabilityRegistry
{
    public ISearchRegistry     Search   { get; }
    public IAiDescribeRegistry Describe { get; }
    public IAuthorizationView  Authz    { get; }

    public static CompiledCapabilityRegistry Build(
        IEnumerable<ICapabilitySource> sources);        // §3A — every source fans in (ViewModel, CQRS, messaging, API, jobs, …)
}
```

`Build` fans in every registered `ICapabilitySource`; each yields `CapabilityDescriptor`s that are projected into the three frozen registries. The **ViewModel source** (built into `YFex.Capabilities`, §7.2) is one implementation — it wraps `ICapabilityProfile` overlays + generator skeletons + `IRouteRegistry`; other sources implement `Contribute` directly over their subsystem's compiled metadata.

### 7.1 The single permission wrapping point (D1)

Every `CapabilityDescriptor` carries `RequiredPermission` as a **value object**. Sources that already hold a `Permission` (e.g. the CQRS source, reading `CompiledMessagingRegistry` policies) contribute it directly. Only sources that read raw `[RequirePermission]` strings — the **ViewModel skeleton** — wrap, and they do it at exactly one site inside their `Contribute`:

```csharp
// THE ONLY place a permission string becomes a Permission value object (ViewModel source).
static Permission? WrapPermission(CommandOverlay? overlay, StructuralCommand cmd, string? classPerm)
{
    if (overlay?.PermissionOverride is { } o) return o;               // already a value object (D2)
    string? raw = cmd.RawPermission ?? classPerm;                     // resolution order 6.4
    return raw is null ? null : Permission.Parse(raw);               // ← single wrapping site
}
```

Downstream — registries, `ICapabilityAgentApi`, `ActionGate`, `IAuthorizationView` — never see a raw permission string; every source hands off `Permission`.

### 7.2 The ViewModel source (skeleton × profile join)

The built-in ViewModel source implements `ICapabilitySource.Contribute` by joining generator skeletons (§6) with `ICapabilityProfile` overlays and emitting one `CapabilityDescriptor` per view/property/command. Other sources (§3A) implement `Contribute` directly over their subsystem's already-compiled metadata (e.g. the CQRS source reads `CompiledMessagingRegistry.ByMessageType`) — same output shape, no skeleton needed.

```
for each profile P (owner = P.ViewModelType):
    S = skeletons[P.ViewModelType]                     // must exist (generator emits for profiled VMs)
    model = P.Configure(builder) → CapabilityProfileModel
    routes = ResolveRoutes(routes, P.ViewModelType)    // 0..N (see 7.4)

    View  = join(S view-level, model.View, class XmlDoc)      → CapabilityDescriptor(Kind=View)
    Props = S.Properties  ⟕ model.Properties  (by name)      → CapabilityDescriptor(Kind=ViewModelProperty)
    Cmds  = S.Commands    ⟕ model.Commands    (by name)      → CapabilityDescriptor(Kind=ViewModelCommand)
                                                               // permission via WrapPermission; SideEffect from overlay

    yield each descriptor, keyed CapabilityKey.ForMember(kind, owner, member) / ForType(View, owner)
```

### 7.3 Fail-fast validation rules (throw `CapabilityConfigurationException`)

1. **Missing skeleton** — profile references a VM with no `IStructuralCapabilityProvider` (VM not partial / generator disabled).
2. **Unknown member** — `model.Commands`/`Properties` names a member absent from the skeleton (typo / renamed).
3. **Permission required but absent (D2)** — command `SideEffect ∈ {Mutating, Irreversible}` and `WrapPermission(...) is null`.
4. **Missing side-effect** — a skeleton command has no overlay entry and thus no `SideEffect`. Policy: default a command that carries a `[RequirePermission]` to `Mutating` (so it is gated), otherwise `ReadOnly`; a `Mutating` default with no permission then trips rule 3. (Confirm default — see open decisions.)
5. **Duplicate profile** for one VM type.

Rules 1–2 can also surface as generator diagnostics when profile and VM share an assembly (the common case); the `Build` throw is the cross-assembly backstop, exactly as `CompiledMessagingRegistryValidationExtensions` backstops CQRS.

### 7.4 Route attachment (D3)

```csharp
static IReadOnlyList<Type> ResolveRoutes(IRouteRegistry routes, Type vm)
{
    var rt = routes.ResolveRouteType(vm);      // current API is 1:1 → 0..1
    return rt is null ? Array.Empty<Type>() : new[] { rt };
}
```

`IRouteRegistry.ResolveRouteType(viewModelType) -> Type?` (`YFex.NavigatR/src/YFex.NavigatR/IRouteRegistry.cs`) is currently **1:1**, so today `Routes` is 0..1. The compiled model exposes `IReadOnlyList<Type> Routes` to be forward-compatible with a future multi-route registry without a data-model change. Reusable child/component VMs resolve to zero routes and are fully valid manifest entries.

---

## 8. The three frozen registries

Each consumer depends on exactly one interface. There is **no** public API returning the whole manifest (god-object guard).

```csharp
public interface ISearchRegistry
{
    FrozenDictionary<CapabilityKey, SearchEntry> Entries { get; }   // spans ALL sources
    IReadOnlyList<CapabilityKey> ByOwner(Type ownerType);           // optional secondary index
}
public sealed record SearchEntry(
    CapabilityKey Key, CapabilityKind Kind, Type OwnerType, IReadOnlyList<Type> Routes,
    ResourceKey Display, ResourceKey KeywordsKey, Permission? RequiredPermission, CapabilityFlags Flags);
// Display/keywords are ResourceKeys, resolved via ICapabilityLocalizer at read time (§4.6):
// Display = current culture; keywords = union across all configured cultures. The table stays language-neutral.
// Flags.Hidden entries are excluded from the search index (still visible to AI/authz).

public interface IAiDescribeRegistry
{
    FrozenDictionary<CapabilityKey, CapabilityDescriptor> Capabilities { get; }   // every source
    IReadOnlyList<CapabilityDescriptor> ByOwner(Type ownerType);                  // group an owner's capabilities
    // ViewCapability (§4.4) remains a convenience projection for the ViewModel source (grouped view+props+commands).
}

public interface IAuthorizationView
{
    FrozenDictionary<CapabilityKey, Permission> RequiredPermissions { get; }
    bool TryGet(CapabilityKey key, out Permission permission);
}
```

Concrete data for `OrderEditorViewModel` (the ViewModel source; `Kind`/`Flags` elided in the tuples for brevity). Entries from **other sources sit in the same tables** — see the CQRS/job examples below.

**ISearchRegistry.Entries** — the frozen table stores ResourceKeys (language-neutral); text below the entries shows what they resolve to.
```
(OrderEditorVM, View)              → Display:key("cap:OrderEditorVM:display"),  KeywordsKey:key("cap:OrderEditorVM:keywords"),  Routes[OrderEditorRoute], Perm:null
(OrderEditorVM, "SaveCommand")     → Display:key("cap:OrderEditorVM#SaveCommand:display"),  KeywordsKey:key("cap:OrderEditorVM#SaveCommand:keywords"),  Perm:Permission("orders.edit"), Kind:Command
(OrderEditorVM, "CancelOrderCommand") → Display:key("cap:OrderEditorVM#CancelOrderCommand:display"),  KeywordsKey:key("cap:OrderEditorVM#CancelOrderCommand:keywords"),  Perm:Permission("orders.cancel")

# resolved at read time via ICapabilityLocalizer, e.g. for "SaveCommand":
#   Display(current=pt-BR) → "Salvar";   Display(current=en) → "Save"
#   Keywords(all cultures) → { "salvar", "gravar", "save" }   (pt-BR ∪ en resources)

# entries from OTHER sources live in the SAME table (Kind, OwnerType, Member):
(CqrsCommand, ApproveOrderCommand, None)   → Display:key("cap:ApproveOrderCommand:display"), Perm:Permission("orders.approve"), Flags:None
(Job,         NightlyInvoiceSyncJob, None) → Display:key("cap:NightlyInvoiceSyncJob:display"), Perm:Permission("billing.sync"),  Flags:Background|Deferred
```
**IAiDescribeRegistry.ByOwner(typeof(OrderEditorViewModel))** (the ViewModel source's grouped `ViewCapability` convenience) — `Ai`/text fields shown resolved for readability are ResourceKeys in the table.
```
ViewCapability{ Routes:[OrderEditorRoute], Ai:"Screen for viewing and editing one sales order.",
  Properties:[ CustomerName: string, "The customer's display name." ],
  Commands:[ SaveCommand{ Mutating, Permission("orders.edit"), "Saves all pending edits…", params[] },
             CancelOrderCommand{ Irreversible, Permission("orders.cancel"),
               "Cancels the order. Cannot be undone…", params[ reason:string "Reason for cancellation." ] } ] }
```
**IAuthorizationView.RequiredPermissions**
```
(OrderEditorVM, "SaveCommand")        → Permission("orders.edit")
(OrderEditorVM, "CancelOrderCommand") → Permission("orders.cancel")
```

Why three, not one: the search index must not carry the AI prose corpus (bloat) and must not evaluate authz (only tag+filter); the AI registry needs verbose text + side-effect but must obtain allow/deny through the evaluator, not invent it; authz-view is a tiny key→`Permission` map with no descriptions (minimal surface). Single authoring, zero permission duplication, isolated concerns.

---

## 9. AI consumer — `ICapabilityAgentApi`

```csharp
public interface ICapabilityAgentApi
{
    // Works for ANY owner across ANY source — a VM, a CQRS command, a job, an endpoint…
    IReadOnlyList<ActionDescriptor> ListActions(Type ownerType);
    IReadOnlyList<ActionDescriptor> ListActions(CapabilityKind? kind = null);   // system-wide discovery, optionally filtered
    ActionDescriptor Describe(CapabilityKey key);
    ActionGate CanInvoke(CapabilityKey action, IAuthContext ctx);               // ctx from IPrincipalAccessor.Current
}
// Resolved (localized) projection of CapabilityDescriptor for the agent; spans all sources.
public sealed record ActionDescriptor(
    CapabilityKey Key, CapabilityKind Kind, string Name, string AiDescription,
    SideEffect SideEffect, CapabilityFlags Flags,
    IReadOnlyList<ParameterDoc> Parameters, Permission? RequiredPermission);
```

`CanInvoke` composes `IAuthorizationView` + shared `IPolicyEvaluator` + side-effect policy:

```csharp
public ActionGate CanInvoke(CapabilityKey key, IAuthContext ctx)
{
    var cmd = /* IAiDescribeRegistry lookup */;
    if (_authz.TryGet(key, out var perm) && !_policy.IsAllowed(perm, ctx))
        return new ActionGate.Denied(perm);
    return cmd.SideEffect == SideEffect.Irreversible
        ? new ActionGate.NeedsConfirmation(cmd.AiDescription)   // Irreversible always confirms
        : new ActionGate.Allowed();
}
```

`ReadOnly`/`Mutating` (granted) → `Allowed`; `Irreversible` (granted) → `NeedsConfirmation`; not granted → `Denied`. Confirmation is derived from the side-effect class, so it cannot be forgotten per command. A `Deferred`/`Background` capability (an `IQueueable` CQRS command or a job) is gated identically, but the agent surfaces it as *will run later / offline* so the user isn't misled that it executed synchronously — this matters precisely because non-VM sources are real domain mutations, not UI affordances.

---

## 10. Search bar consumer — filter-before-rank

The search-bar VM depends on `ISearchRegistry` + shared `IPolicyEvaluator` (+ `IPrincipalAccessor`) + `ICapabilityLocalizer` only.

```csharp
// deps: ISearchRegistry _search, IPolicyEvaluator _policy, ICapabilityLocalizer _loc
IEnumerable<SearchEntry> Query(string text, IAuthContext ctx)     // ctx = _principal.Current
{
    var toks = Tokenize(text);                                   // lowercase, accent-fold
    return _search.Entries.Values
        .Where(e => Matches(e, toks))                            // match against ALL-culture keywords + current-culture display
        .Where(e => e.RequiredPermission is not { } p || _policy.IsAllowed(p, ctx))  // FILTER before rank
        .OrderByDescending(Rank)                                 // exact-keyword > display-substring; command > view
        .Take(20);

    bool Matches(SearchEntry e, string[] toks)
    {
        var kws = _loc.Keywords(e.KeywordsKey);                  // union across configured cultures (§4.6), cached
        var display = _loc.Display(e.Display);                   // current culture, for display-substring match
        return toks.All(t => kws.Any(k => Fold(k).Contains(t)) || Fold(display).Contains(t));
    }
}
```

Filtering before ranking guarantees a user never sees, and cannot rank up, an action they may not perform. The permission that filters here is the same `Permission` the authz pipeline enforces at execution — one declaration.

---

## 11. End-to-end walkthroughs

**(a) "save order" typed in the search bar.** Tokenize → match `SaveCommand` (kw "save") and the view entry (display). Filter: `SaveCommand.RequiredPermission = Permission("orders.edit")` → `IPolicyEvaluator.IsAllowed(perm, ctx)` → kept (or silently dropped if denied). Rank: command with exact keyword first. Execute: entry carries `OwnerType` + `MemberName.Of("SaveCommand")` (Kind=`ViewModelCommand`); UI obtains the live VM (`RouteRegistry.ResolveViewModel` when routed) and invokes the generated `SaveCommand`. The authz pipeline re-checks `orders.edit` at execution.

**(b) AI agent on the order screen → cancel.** `ListActions(typeof(OrderEditorViewModel))` returns Save (Mutating) + CancelOrder (Irreversible, verbose text, `reason:string`). Agent chooses cancel → `CanInvoke((OrderEditorVM,"CancelOrderCommand"), ctx)`: look up `Permission("orders.cancel")` in `IAuthorizationView`; `IPolicyEvaluator` → if denied `ActionGate.Denied(orders.cancel)` and the agent stops; if granted, `SideEffect==Irreversible` → `ActionGate.NeedsConfirmation("Cancels the order. Cannot be undone…")`. Agent surfaces confirmation; on human approval invokes `CancelOrderCommand(reason)`; runtime authz re-checks the same permission.

**(c) Developer adds a new `[StateCommand]`.** They add the method with `///` doc and `[RequirePermission("orders.send")]`. Automatically: `YFex.State.Generator` emits `EmailOrderCommand`; `YFex.Capabilities.Generator` re-emits the skeleton with the new `StructuralCommand` (Xml doc + `"orders.send"` + `reason`/`address` params); `YFex.Authorization` wires `orders.send`. Developer adds to the profile only `vm.Command(x => x.EmailOrderCommand).SideEffect(Mutating).Keywords(...)`. If they forget `[RequirePermission]` on a mutating command, or forget the profile entry so it defaults to `Mutating` with no permission, `Build()` throws at startup. They never declare permission twice, nor restate member existence/type/params/description.

---

## 12. Testing strategy

- **Generator snapshot tests** (mirror `YFex.State.Tests` + `YFex.Cqrs.SourceGenerator.Test`): golden-file the emitted `StructuralManifest` for VMs with/without class-level permission, async commands (`Async` trim), commands with params, properties with/without XML docs, VMs with zero routes.
- **Command-name parity test**: assert the capability generator's command `Name` equals `YFex.State.Generator`'s emitted command property for a matrix of method names — guards the shared-naming contract (§2).
- **XML-doc extraction unit tests**: `XmlDocReader` for `<summary>` and `<param>` (including missing/multiline/entities).
- **Permission wrapping test**: assert `Permission` appears in registries and *no* raw string leaks — a reflection test over the compiled records asserts no `string`-typed permission field exists downstream (enforces D1).
- **Build validation tests**: each fail-fast rule (§7.3) throws with an actionable message; escape-hatch `.Requires` override wins over the attribute; class-level attribute inherited when method-level absent.
- **Projection tests**: one authored profile → assert exact `SearchEntry`/`ViewCapability`/`IAuthorizationView` contents (the §8 literals).
- **Agent gate tests**: `ReadOnly`/`Mutating`/`Irreversible` × granted/denied → correct `ActionGate` variant; `Irreversible` always `NeedsConfirmation` when granted.
- **Search filter tests**: denied entries never surface; PT and EN tokens both match; ranking order.
- **AOT/trim smoke** (mirror `YFex.State.Smoke`): publish a NativeAOT sample consuming the registries; assert no trim warnings and correct data.

## 13. Phased rollout

1. **P1 — Shared discovery**: extract `YFex.State.Generator.Abstractions`; refactor `YFex.State.Generator` onto it (no behavior change; existing tests green). Extend `CommandParser` with XML/`<param>`/`[RequirePermission]` reads.
2. **P2 — Value objects + skeleton**: land `YFex.Capabilities` value objects, `StructuralManifest`/`IStructuralCapabilityProvider`, and `YFex.Capabilities.Generator`; snapshot tests. Depends on `YFex.Authorization` exposing `RequirePermissionAttribute` + `Permission` (coordinate).
3. **P3 — Compilation**: `ICapabilityProfile`/builder, `CompiledCapabilityRegistry.Build`, single wrapping point, fail-fast rules, three registries; projection tests.
4. **P4 — Consumers**: `ICapabilityAgentApi`; search-bar integration with `IPolicyEvaluator`; align `IAuthorizationView` with the authz side.
5. **P5 — Adoption**: profiles for a pilot domain screen set; AOT smoke; docs.
6. **P6 — Additional capability sources** (§3A): ship `ICapabilitySource` bridges incrementally — CQRS first (highest AI value, richest existing metadata), then Web/API, Jobs, Messaging. Each is additive; registries and consumers don't change. Add the CQRS-side `.Describe/.SideEffect/.Keywords` builder methods on `IAggregateConfiguration<T>` in this phase.

## 14. Open decisions to lock

1. Default `SideEffect` when a profile entry is omitted (proposal: `Mutating` if `[RequirePermission]` present, else `ReadOnly`).
2. Whether to keep `.Requires` at all, or make the attribute the sole permission path (D2 keeps it as escape hatch).
3. Catalog encoding stays `const string` (attribute-legal, both generators read it). Enum/`typeof(marker)` alternatives rejected for now.
4. Switch profile discovery from `CreateSyntaxProvider` to a `[CapabilityProfile]` attribute + `ForAttributeWithMetadataName` (cheaper gate).
5. Whether `IAuthorizationView` **mirrors** or **delegates to** the authz side (single source of the map vs. two aligned projections).
6. Which capability sources ship in v1, and whether CQRS describe/side-effect/keyword metadata lives on `IAggregateConfiguration<T>` (recommended — matches "configuration over attributes for CQRS") vs. a separate CQRS capability profile.
7. Default side-effect derivation per source (verb-based for HTTP, `ICommand`/`IQuery` for CQRS) and whether a config override is always required for `Irreversible`.

---

### Critical Files for Implementation
- `YFex.State/src/YFex.State.Generator/CommandParser.cs` — extend for XML/`<param>` + `[RequirePermission]`; source of the command-name derivation to share.
- `YFex.State/src/YFex.State.Generator/ObservableParser.cs` — `GetDocumentationCommentXml()` pattern + property model to share.
- `YFex.State/src/YFex.State.Generator/StateGenerator.cs` — `ForAttributeWithMetadataName` wiring the capability generator replicates.
- `YFex.Cqrs/src/YFex.Cqrs/CompiledMessagingRegistry.cs` — the register→compile→frozen-projection + validation blueprint for `CompiledCapabilityRegistry.Build`.
- `YFex.Cqrs/src/YFex.Cqrs/Configuration/IAggregateConfiguration.cs` — the `ICapabilityProfile<T>`/builder authoring-surface pattern.
- `YFex.NavigatR/src/YFex.NavigatR/IRouteRegistry.cs` — `ResolveRouteType`/`ResolveViewModel` for route attachment (currently 1:1) and live-VM resolution.
