# YFex Framework

**Solution:** `yfex.slnx` — A modular .NET framework for building reactive, offline-capable MVVM applications with CQRS, real-time messaging, and type-safe navigation.

## Solution Map

```
src/
├── YFex/                              Core primitives, collections, extensions, base value types
│                                      (Money, Percentual, RawValue, FieldPath, OptionSet, Rate, StableHash64, Localization)
├── YFex.Tests/                        Tests for YFex core
├── Validation/
│   ├── YFex.Validation/               Shape validation runtime: Violation, ViolationWriter, ValidationResult,
│   │                                  IValidatable, rule contracts, FieldConstraints (refs YFex core only)
│   ├── YFex.Validation.Generator/     Source generator: IValidatable<T>, constraints, module initializer, message catalogs
│   ├── YFex.Validation.Testing/       Framework-agnostic assertion helpers (ShouldHave, ShouldHaveOnly…)
│   ├── YFex.Validation.Smoke/         Native AOT smoke app (not a library)
│   ├── YFex.Validation.Tests/         Runtime + generated-code behaviour tests (design: docs/internal/validation)
│   ├── YFex.Validation.Generator.Tests/ Driver tests: emission, diagnostics YFV*, caching
│   ├── YFex.Rules/                    Rules engine: Situation, rule sets/groups/stages, Directives, Decision (verdict, limits,
│   │                                  adjustments + per-line allocation, trace, explanation), decision tables, RuleSetHost
│   │                                  (published versions per app-declared [RuleTier] path / effective date), approval tokens (refs YFex core + YFex.Validation)
│   ├── YFex.Rules.Generator/          Source generator: ISituation<T>, rule set Set/Evaluate/Catalog, table impls, "why not"
│   │                                  condition splitting, [SituationSource] BuildSituation()
│   ├── YFex.Rules.Tests/              Engine + generated doc-12 sale scenario + view model tests
│   ├── YFex.Rules.Admin/              Runtime rule editing module: RuleAdmin publish/history/diff/what-if/approval messages,
│   │                                  publish pipeline (+audit sink), client RuleSetSync + device cache (→ Rules, Cqrs, Messaging.Rpc)
│   ├── YFex.Rules.Admin.Tests/        Publish pipeline, audit, simulation, offline sync tests
│   ├── YFex.Rules.Data/               SQL IRuleStore (SqlRuleStore) over YFex.Data — repo C:\Dev\YFex\YFex.Data
│   ├── YFex.Rules.Data.Tests/         IRuleStore contract suite (in-memory + SQLite)
│   ├── YFex.Rules.Dynamic/            Runtime-authored rules (JSON spec, arithmetic, functions incl. [RuleFunctions] contracts) compiled
│   │                                  once per version; plugs in with host.UseRuntimeRules(new DynamicRules<TS>(functions)); authoring
│   │                                  catalog + rule-spec JSON Schema for editors/AI (→ Rules, Persistence)
│   ├── YFex.Rules.Dynamic.Tests/      Dynamic rules, generated functions, AI schema tests
│   └── YFex.Rules.Generator.Tests/    Driver tests: emission, diagnostics YFR*, caching
├── UI/
│   ├── YFex.UI.Abstractions/          IDialog, IMessageBox, IToast, INotification, union types
│   └── YFex.Mvvm/                     PageViewModel, ViewModel base classes (bridges State + NavigatR)
├── State/
│   ├── YFex.State/                    Reactive state engine ([Observable], [Computed], [StateCommand], etc.)
│   ├── YFex.State.Generator/          Source generator for State attributes
│   ├── YFex.State.Mvvm/              MvvmStateObject (INotifyPropertyChanged bridge)
│   ├── YFex.State.Collections/        StateList<T> — observable pooled list
│   ├── YFex.State.Blazor/             StateComponent<TViewModel> for Blazor
│   ├── YFex.State.History/            Undo/redo system (UndoContext, UndoManager)
│   ├── YFex.State.History.Generator/  Source generator for [Undoable]
│   ├── YFex.State.History.Persistence/ MemoryPack serialization for undo snapshots
│   ├── YFex.State.History.Testing/    Fluent assertions for undo tests
│   ├── YFex.State.Testing/            StateRecorder<T> for state change assertions
│   └── YFex.State.Smoke/             Smoke tests (not a library)
├── Navigation/
│   ├── YFex.NavigatR/                 Type-safe async navigation framework
│   ├── YFex.NavigatR.SourceGenerator/ Route + prefetch code generation
│   └── YFex.NavigatR.Tests/           Navigation tests
├── Cqrs/
│   ├── YFex.Cqrs/                     CQRS contracts, configuration builders, compiled registry, validation pipeline (→ YFex.Validation)
│   ├── YFex.Cqrs.FluentValidation/    Optional FluentValidation adapter (the only FluentValidation reference) + .Tests
│   ├── YFex.Cqrs.SourceGenerator/     Static call helpers, DI registration, RPC contract generation
│   ├── YFex.Cqrs.Analyzer/            Roslyn diagnostics (YFCACHE, YFQUE, YFINV)
│   └── YFex.Cqrs.SourceGenerator.Test/ Generator tests
├── Messaging/
│   ├── YFex.Messaging/                IEventBus, [Live], [Subscribe<>], MessagingHost
│   ├── YFex.Messaging.Generator/      Source generator for [Live] / [Subscribe<>]
│   ├── YFex.Messaging.Fusion/         FusionLiveState<T>, FusionStateBinding<T>
│   ├── YFex.Messaging.Rpc/            Offline outbox, client cache, sync, dispatchers, server validation, MemoryPack result formatters,
│   │                                  implicit RPC CallContext (caller + tenant, stage 2 server-side, permission versions)
│   ├── YFex.Messaging.Rpc.Tests/      Real client↔server Fusion RPC round-trips (two hubs, in-memory bridge; YFEX_RPC), call context,
│   │                                  golden test of the generated Fusion proxies vs ActualLab.Generators
│   ├── YFex.Messaging.Rpc.Sqlite/     SQLite storage backend
│   ├── YFex.Messaging.Rpc.IndexedDb/  IndexedDB storage backend (Blazor WASM)
│   ├── YFex.Messaging.Rpc.Encryption/ AES-GCM encryption decorator
│   ├── YFex.Messaging.Rpc.Wolverine/  Wolverine handler/dispatcher adapter
│   └── YFex.Messaging.Tests/          Integration tests
├── Database/
│   └── YFex.Data/                      SQL QueryBuilder (Dapper integration)
└── Persistence/
    ├── YFex.Persistence/               Persistence abstractions
    ├── YFex.Persistence.Generator/     Persistence source generator
    └── YFex.Persistence.FileSystem/    File system persistence backend
```

## Dependency Layers (bottom → top)

```
Layer 0: YFex (core primitives)
Layer 0.5: YFex.Validation (→ YFex core only; must NOT reference State/Cqrs/Rules)
Layer 1: YFex.UI.Abstractions, YFex.State (→ YFex.Validation), YFex.State.Collections,
         YFex.Rules (→ YFex core, YFex.Validation; must NOT reference State/Cqrs/Authorization)
Layer 2: YFex.State.Mvvm, YFex.State.History, YFex.NavigatR, YFex.Cqrs (→ YFex.Validation, YFex.Rules)
Layer 3: YFex.Mvvm, YFex.Messaging
Layer 4: YFex.Messaging.Fusion, YFex.Messaging.Rpc
Layer 5: YFex.Messaging.Rpc.Sqlite/IndexedDb/Encryption/Wolverine
```

## Key Architectural Principles

1. **Source generators over reflection.** Every framework feature uses Roslyn incremental generators. No runtime reflection on hot paths. AOT-compatible.
2. **Three-phase pipeline.** Registration → compilation → runtime. All metadata is collected, validated, and pre-compiled into frozen lookup tables at startup.
3. **Zero-allocation hot paths.** `FrozenDictionary<Type, Policy>` lookups, `PropertyBitmap64` for batch dispatch, `ReadOnlySpan<T>` iteration, pooled arrays.
4. **Discriminated unions via `[Union]` attribute.** Result types, navigation results, and dialog outcomes use struct unions for type-safe exhaustive matching without boxing.
5. **Activation lifecycle.** `StateObject.Activate()/Deactivate()` cascades through the object graph. NavigatR drives activation via `OnNavigation`/`OnSuspend`/`OnResume`.
6. **Offline-first CQRS.** Commands marked `IQueueable` are enqueued when offline and replayed on reconnect. Queries marked `ICacheable` serve from local storage when disconnected.

## Build & Test

```bash
dotnet build yfex.slnx
dotnet test yfex.slnx
```

Multi-targeting: most libraries target `net8.0;net9.0`. Source generators target `netstandard2.0`.

## Source Generator Projects

| Generator | Triggers on | Emits |
|---|---|---|
| `YFex.State.Generator` | `[Observable]`, `[Computed]`, `[StateCommand]`, `[ReactsTo]`, `[Poll]`, etc. | Property setters, computed deps, command wrappers, activation cascade, validation hooks (`partial void __OnValidate_X` / `__OnBatchValidated`) |
| `YFex.State.History.Generator` | `[Undoable]`, `[UndoContext]`, `[UndoableCollection]` | Delta recording, undo context fields, batch observers |
| `YFex.NavigatR.SourceGenerator` | `[Route]`, `[Prefetch]` | Route records, OnNavigation bridges, result plumbing, registration |
| `YFex.Cqrs.SourceGenerator` | `IQuery<T>`, `ICommand`, `IEvent` nested records + `IAggregateConfiguration<T>` | Static call helpers, AddYFexConfigurations, RPC contracts + their Fusion proxies (YFEX_RPC) |
| `YFex.Messaging.Generator` | `[Live]`, `[Subscribe<T>]` | Live state properties, event subscriptions, lifecycle wiring |
| `YFex.Rules.Generator` | `[Situation]`, `[RuleSet<TS>]`, `[Rule]`, `[DecisionTable<TS>]`, `[SituationSource<TS>]`, `[RuleFunctions]` | `ISituation<T>` (Fields/FieldIds/Schema/Read/Hash/WriteTo/DiffMask), rule set `Set`/`Evaluate`/`CreateHost`/`Catalog`/`RuleIds` + explain bodies, `IDecisionTable` impls (+ `GetCondition`/`ConditionDomain` for gap checks), VM `BuildSituation()`, rule function descriptors (`All` / `Contracts` + `Implement<TImpl>()`) |
| `YFex.Validation.Generator` | `[Required]`, `[Length]`, `[Range]`, `[Each]`, `[Invariant]`, `[Validatable]`, DataAnnotations; `[ValidatesAs<T>]`/`[MapsTo]` on view models; `messages*.json` | `IValidatable<T>` (rules, constraints, paths, ids, `Shape` for JSON Schema), `ValidationSlot` + `JsonSchemas` module initializer, `IMessageSource` catalogs; on StateObject VMs: the State hook bodies, `IValidationSource`, `ValidateAll`, `ApplyServer<T>`, `ToT()` |

## Conventions

- **Attributes drive behavior.** Properties are `[Observable]`, methods are `[StateCommand]`, events are `[Subscribe<T>]`. The generators emit all glue code.
- **Partial classes required.** Any class using generator attributes must be `partial`.
- **`StateObject` is the base.** ViewModels inherit `StateObject` → `MvvmStateObject` → `ViewModel` → `PageViewModel`. Each layer adds capabilities.
- **Configuration over attributes for CQRS.** Validation, authorization, caching, invalidation rules live in `IAggregateConfiguration<T>` classes, not on message records. Records carry only marker interfaces (`IQueueable`, `ICacheable`).
- **`ref struct` scopes for correctness.** `BatchScope`, `UndoTransactionScope`, `UndoSuspendScope` are ref structs preventing async misuse.

## Common Tasks

| Task | Approach |
|---|---|
| Add observable property | `[Observable] public partial string Name { get; set; }` on a `StateObject` subclass |
| Validate a view model | Rules on the property (`[Observable, Required, Length(2, 80)]`) or borrow a command's with `[ValidatesAs<TCommand>]` (+ `[MapsTo]`); reference BOTH generators; `ValidateAll()` on Save, `ApplyServer<TCommand>(result)` after the call (docs/internal/validation/07) |
| Add async command | `[StateCommand] async Task DoSomethingAsync(CancellationToken ct)` |
| React to property change | `[ReactsTo(nameof(Name))] void OnNameChanged()` |
| Add undo support | `[Undoable] [Observable] public partial string Name { get; set; }` |
| Navigate to a page | `await navigator.NavigateTo(new MyRoute())` |
| Subscribe to event | `[Subscribe<MyEvent>] void OnMyEvent(in MyEvent e)` |
| Define CQRS query | Nested record `public partial record GetXQuery(int Id) : IQuery<X>` |
| Configure query caching | `b.Query<GetXQuery, X>().Cacheable().ScopedByUser()` in configuration class |
| Validate a CQRS message | Baseline config: `b.Command<C, R>().Validation(v => v.Field(c => c.Name).Required().Length(2, 80))`; server override: `.Validate<TContextRule>()` (docs/internal/validation/09) |
| RPC contracts (Fusion 13) | Generated with their proxies (YFEX_RPC): server `fusion.Add{Aggregate}RpcServer()`, client `fusion.Add{Aggregate}RpcClient()` (or `rpc.Add{Aggregate}RpcRemoteClient()` on a hub that also hosts the server) + `{Aggregate}RpcClientRegistration.RegisterRoutes`. Nothing to declare in source (validation D30) |
| Enforce rules (limits, options, adjustments of any amount — Money, Mass, int…, approvals) — domain-agnostic, see validation D35–D38 | Situation `[Situation] partial record struct` (`[RuleTier]` fields = app publishing hierarchy, `[RuleEffectiveDate]`; value types via `IRawValue<T>`/`IAmount<T>`) + `[RuleSet<TS>] static partial class` + `[Rule]` structs / `[DecisionTable<TS>]`; server: `.UseSituation<TS, TLoader>().Enforce(Rules.Set)`, handler `IRuledCommandHandler<TCmd, TResult, TS>`; VM: `[SituationSource<TS>]` + `[Observable, ConstraintsFrom] Decision<TS>? Policy` (docs/internal/validation/04–06, 12) |
| Edit rules at runtime (tables, parameters, overrides) | Server: `AddYFexRuleSet(host)` + `AddYFexRulesAdmin()` + an `IRuleStore` (`SqlRuleStore` from YFex.Rules.Data); client: `AddYFexRuleSetSync()` (device cache over `IKeyValueStore`) + `RuleSetSync.StartAsync()`; what-if = `RuleAdmin.Queries.Simulate` (needs `ISituationSamples`); audit = `IRuleAuditSink` (docs/internal/validation/05, 13 "Phase 4b") |
| Runtime-authored rules (tenant/Composer/AI) | Group opts in: `new RuleGroup(stage, resolve) { Dynamic = new([nameof(S.Tenant)], DynamicDirectives.Decrease, maxPercent: …) }`; host `.UseRuntimeRules(new DynamicRules<TS>(FunctionCatalog.BuiltIn.With(MyFunctions.All)))`; publish `RuleAdmin.Commands.PublishRule` (JSON spec); app functions `[RuleFunctions] static partial class` / contracts `[RuleFunctions] partial interface` + `Implement<TImpl>()` (validation 05 §8, 13 "Phase 6") |
| JSON Schema for AI tools / forms | `JsonSchemaWriter.ToJson<TCommand>()` (static rules), `JsonSchemaWriter.Merge(T.TryGetConstraints, decision.TryGetConstraints)` (live), `SituationJsonSchema.ToJson(decision)`, `RuleSetDescription.ToJson(set, compiled, catalog)`, `dynamic.SpecJsonSchema(set)` / `CatalogJson(set)`; Capabilities exposes `ActionDescriptor.ParametersSchema` |
| Caller identity / tenant on the server | Implicit `CallContext` (validation D29): `UseYFexMessagingRpcServer(rpc => rpc.CallContext(ctx => ctx.FromConnection().OrFromHeader().WithTenant()))`; handlers read `IPrincipalAccessor` / `IScopeContext` as usual; `.RequirePermission(...)` in the baseline is enforced server-side; signal grant changes with `CallContextSignals.PermissionsChanged/RevokeAsync` |

## Documentation

See `docs/` for the full wiki:
- `docs/public/` — Public API guides (capabilities, patterns, usage)
- `docs/internal/` — Internal implementation details
