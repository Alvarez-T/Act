# YFex Framework

**Solution:** `yfex.slnx` — A modular .NET framework for building reactive, offline-capable MVVM applications with CQRS, real-time messaging, and type-safe navigation.

## Solution Map

```
src/
├── YFex/                              Core primitives, collections, extensions, base value types
│                                      (Money, Percentual, RawValue, FieldPath, OptionSet, Rate, StableHash64, Localization)
│                                      + YFex.Diagnostics: logging probe contract (Operations, OperationDescriptor, LogWriter,
│                                        TraceContext, ILogValue) — engine in sibling repo C:\Dev\YFex\YFex.Logging
│                                      + YFex.Privacy: data classification contract ([Sensitive], DataSensitivity, DataCategory,
│                                        IClassifiedData<T>, Masks) — generator in sibling repo C:\Dev\YFex\YFex.Privacy (docs/internal/privacy)
│                                      + YFex.Cryptography / YFex.Canonical: TYPES ONLY (Hash256, KeyId, SignatureAlgorithm,
│                                        DetachedSignature, ICanonicalValue<T>) — all logic in sibling repo C:\Dev\YFex\YFex.Cryptography
│                                        (docs/internal/cryptography)
│                                      + YFex.Time: TYPES ONLY (TrustedInstant, TimeFlags, TimeSourceKind, TimeReply, TimeSample,
│                                        ITimeSource, ITrustedClock + TimeProvider.GetTrustedNow()/WaitForAnchorAsync()) — the trusted
│                                        clock is in sibling repo C:\Dev\YFex\YFex.Time (docs/internal/time)
│                                      + YFex.Networking: TYPES ONLY (MacAddress [Sensitive], INetworkConditions, NetworkConditions,
│                                        InternetReachability, NetworkMedium, NetworkCost, NetworkConditionFlags, EndpointHealth) —
│                                        the library is sibling repo C:\Dev\YFex\YFex.Networking (docs/internal/networking)
│                                      + YFex.Installations: TYPES ONLY (InstallationId, role/status/identity, LocalInstallation,
│                                        bundle kind/audience/header) — runtime in sibling repo C:\Dev\YFex\YFex.Installations (docs/internal/installations)
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
│   ├── YFex.Messaging.Rpc/            Offline outbox (durable over a durable IKeyValueStore; caller/scope/sequence per entry), client
│   │                                  central time anchor (ICentralTime, UseCentralAnchor — YFex.Time T2),
│   │                                  IServerConnection (is our RPC link to the server up? one per Fusion peer; replaced
│   │                                  INetworkStatus, which is removed — networking ⚑N4),
│   │                                  cache, sync, dispatchers, server validation + at-most-once commands (IdempotentExecution,
│   │                                  `yfex-idempotency` header), MemoryPack result formatters, implicit RPC CallContext
│   ├── YFex.Messaging.Rpc.Tests/      Real client↔server Fusion RPC round-trips (two hubs, in-memory bridge; YFEX_RPC), call context,
│   │                                  golden test of the generated Fusion proxies vs ActualLab.Generators
│   ├── YFex.Messaging.Rpc.Wolverine/  Wolverine handler/dispatcher adapter
│   └── YFex.Messaging.Tests/          Integration tests
├── Database/
│   └── YFex.Data/                      SQL QueryBuilder (Dapper integration)
└── Persistence/
    ├── YFex.Persistence/               Persistence abstractions (IKeyValueStore, ICache, IDurableQueue)
    ├── YFex.Persistence.Generator/     Persistence source generator
    ├── YFex.Persistence.FileSystem/    File system persistence backend
    ├── YFex.Persistence.Sqlite/        SQLite IKeyValueStore (SqliteKeyValueStore)
    ├── YFex.Persistence.IndexedDb/     IndexedDB IKeyValueStore (Blazor WASM, AddYFexIndexedDbStore)
    ├── YFex.Persistence.Encryption/    IKeyValueStore decorator on YFex.Cryptography (AddYFexStorageEncryption): sealed value v3 bound to
    │                                   store+key, storage key = key ring "device/storage" wrapped by CNG/TPM (Windows) or a 0600 root-key
    │                                   file; reads the 0x01/0x02 layouts and imports offline Phase 0's .storage-key once
    ├── YFex.Persistence.Encryption.Tests/ AAD binding, golden 0x02 values, legacy import, restart, erase, reload
    └── YFex.Persistence.Tenancy/       Scope-partitioned IKeyValueStore (ScopedKeyValueStore) + .Tests
```

## Dependency Layers (bottom → top)

```
Layer 0: YFex (core primitives)
Sibling: YFex.Cryptography (→ YFex core only; C:\Dev\YFex\YFex.Cryptography). YFex.Rules/Validation/Authorization.Public must NOT reference
         it (they name core Hash256/KeyId and hash with the BCL); runtimes (Privacy, Offline, Audit engine, Authorization, Persistence.Encryption,
         Messaging.Rpc, Web.Data) do
Layer 0.5: YFex.Validation (→ YFex core only; must NOT reference State/Cqrs/Rules)
Layer 1: YFex.UI.Abstractions, YFex.State (→ YFex.Validation), YFex.State.Collections,
         YFex.Rules (→ YFex core, YFex.Validation; may reference YFex.Authorization.Public only — never State/Cqrs/the Authorization runtime)
Layer 2: YFex.State.Mvvm, YFex.State.History, YFex.NavigatR, YFex.Cqrs (→ YFex.Validation, YFex.Rules)
Layer 3: YFex.Mvvm, YFex.Messaging
Layer 4: YFex.Messaging.Fusion, YFex.Messaging.Rpc
Layer 5: YFex.Messaging.Rpc.Wolverine (storage backends + encryption are YFex.Persistence.* — any layer may use them)
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
| Instrument a library operation (no engine reference) | Probe YFex core: `var op = Operations.Start(in message); try { …; op.Complete(outcome); } catch (Exception ex) { op.Fail(ex); throw; }` with a descriptor in `OperationDescriptorFor<T>` (generated in Phase 2); value objects implement `ILogValue<T>` or get `[LogValue]` (docs/internal/logging/04, 11) |
| Classify personal data (masks in logs/UI) | `[Sensitive(DataSensitivity.X, Category, Mask, MaskFormat)]` ONCE on the value-object struct or on a record classified as a whole; reference `YFex.Privacy.Generator` as analyzer; `Classified.Mask(value)` (docs/internal/privacy/00, 02, 13) |
| Audit who changed an entity (field before/after) | `[AuditChanges]` ONCE on the entity type (repo C:\Dev\YFex\YFex.Audit; reference `YFex.Audit.Public` + `YFex.Audit.Generator` as analyzer); in the write path `var scope = audit.Begin(descriptor); AuditChanges.Updated(before, after); await audit.CompleteAsync(scope, outcome);` (`IAuditTrail` from DI; composition root `services.AddYFexAudit(...)`); tests: `AuditCapture` + `AuditSchemaSnapshot.Verify(assembly)` (docs/internal/audit, log in 12) |
| Encrypt / seal / key rings / canonical bytes | `YFex.Cryptography` (repo C:\Dev\YFex\YFex.Cryptography): `services.AddYFexCryptography(c => c.UseCngRootKey().StoreKeyRingsInFiles(dir).KeyRing("name", r => r.RotateEvery(…)))`, `SealedValue.Seal/Open(ring, bytes, SealContext.Write(buffer, domain, parts…), …)` → `OpenResult` union, `Hashing.Sha256` → core `Hash256`, `CanonicalWriter`. MACs/tokens: `MessageAuthentication.ComputeTag/VerifyTag(key or ring, domain, …)`, `MessageToken.Issue/Read` (ring `For(KeyPurpose.MessageAuthentication)`; event-channel audiences: `cryptography.EventAudienceTokens()`). Per-tenant/scope keys (any service): `cryptography.ScopedKeyRings("web/secrets", s => s.DedicatedRings("tenant/{scope}/secrets"))` + `ScopedKeyRings.GetAsync(family, scope)`. Signatures: server keys = ring `For(KeyPurpose.Signing)` → `rings.GetSigningKeysAsync(name)` (`SigningKeyRing`: `.Current`, `.PublicKeys`), `SignedPayload.Sign/Verify` + `SignatureVerifier(IPublicKeyDirectory)`; TPM keys `CngSigningKeyStore` (.Windows). Passwords: `PasswordHashing`. Seal for a recipient that cannot read it back (offline intake of a CPF for the server): ring `For(KeyPurpose.KeyAgreement)` → `rings.GetRecipientKeysAsync(name)` (`.Current.PublicKey` to publish), `RecipientSealing.Seal(in publicKey, bytes, context)` / `RecipientSealing.Open(ring, …)`. Tamper evidence: `HashChain.Genesis/Next`, `MerkleTree.ComputeRoot/WriteInclusionProof/VerifyInclusion` (sign root + size together). Certificates: `CertificateAuthority.CreateRoot(signingKey, …)` / `new CertificateAuthority(cert, signingKey).Issue(request)` (TPM keys work), `CertificateTrust.Validate(…)` → `CertificateCheck` union (custom roots only), `CertificateFiles.LoadPkcs12/ExportPkcs12`, TLS key: `CngSigningKeyStore.CopyWithPrivateKey`. XML signatures (NF-e/NFC-e RSA-SHA1, eSocial RSA-SHA256): package `YFex.Cryptography.Xml` — `XmlSignature.Load(xml)`, `XmlSignature.SignEnveloped(infNFe, certificate, rsaKey)`, `XmlSignature.Verify(signature)` → `XmlSignatureVerified(SignedElement, Signer)` (never `SignedXml`: not AOT-safe, banned). Never `new AesGcm`/`HMACSHA256`/`ECDsa.Create`/`IDataProtector` outside that repo (architecture test in YFex.Cryptography.Tests; allow-list with reasons). Storage at rest: `services.AddYFexStorageEncryption(storage => storage.StoreName = "farma")` (docs/internal/cryptography, 10 + 12 §5) |
| Trusted time (clock that can't be set back) | Composition root: `services.AddYFexTime(time => time.UseCentralAnchor().UseNetworkTime("a.ntp.br", "b.ntp.br").PersistHighWater())` (repo C:\Dev\YFex\YFex.Time; `UseCentralAnchor()` from Messaging.Rpc = central's clock over the RPC connection; replaces the registered `TimeProvider`; central servers: `.TrustDeviceClock()`, served by `UseYFexMessagingRpcServer`); code keeps injecting `TimeProvider` — `GetUtcNow()` for a better now, `time.GetTrustedNow()` (core `YFex.Time`) + `.IsUsableForSecurity(max)` for validity decisions, `await time.WaitForAnchorAsync(timeout)` before e.g. the first NFC-e; tests: `FakeDeviceClock.ChangeSystemClock` + `FakeTimeSource` (YFex.Time.Testing) (docs/internal/time) |
| Network conditions / probes ("is there internet?", metered, captive portal, printer reachable) | Composition root: `services.AddYFexNetworking(network => network.ProbeInternet(new Uri("https://central/health/204")).WatchEndpoint("central", uri, essential: true))` (repo C:\Dev\YFex\YFex.Networking; no third-party probe default); code reads core `INetworkConditions.Current` (`Internet`: NoNetwork / LocalNetworkOnly / CaptivePortal / Internet, `Medium`, `Cost`, `.PrefersSmallTransfers`) — consumers take it optionally from DI; targeted probes `NetworkProbe.TcpAsync(host, port)` / `PingAsync` → `ProbeOutcome` union (≤ 32 local targets, 10/s process budget; sweeps = `YFex.Networking.Survey`); endpoint health `IEndpointMonitor` (+ `Record(...)` for passive samples); "is our RPC link up?" is Messaging.Rpc `IServerConnection`, not this; Windows: `.UseWindowsConditions()` (YFex.Networking.Windows: NLM conditions/cost, categories, Wi-Fi `IWiFiInformation`, ARP `INeighborTable`, `IFirewallRules` read-only); MAC vendor `MacVendors.Find(mac)` (YFex.Networking.Vendors, embedded IEEE snapshot — refresh with `dotnet run --project tools/UpdateMacVendors -- download`); `WakeOnLan.SendAsync(mac)`; tests: `FakeNetworkConditions`, `FakeLocalNetworkReader`, `ScriptedProbeTransport` (YFex.Networking.Testing) (docs/internal/networking) |
| Log from an app/service | Composition root: `services.AddYFexLogging(log => …)` (repo YFex.Logging); code: `ILog<T>` + `log.Information($"…")`, `log.Operation("Name")`, `[Log]` partial methods; tests: `LogCapture.Start()` (YFex.Logging.Testing) |
| Caller identity / tenant on the server | Implicit `CallContext` (validation D29): `UseYFexMessagingRpcServer(rpc => rpc.CallContext(ctx => ctx.FromConnection().OrFromHeader().WithTenant()))`; handlers read `IPrincipalAccessor` / `IScopeContext` as usual; `.RequirePermission(...)` in the baseline is enforced server-side; signal grant changes with `IAuthorizationSignals.PermissionsChanged/RevokedAsync` (YFex.Authorization, any host; RPC invalidates the pushed snapshot and closes revoked connections); client UI with the server's grants: `AddYFexMessagingRpcClient(…).UseServerPermissions()`; ambient actor for libraries: `ActorContext.Current` |
| Require permissions (who may do/see what) | Catalog `[PermissionCatalog] static partial class X { [Permission("id", Name = "…")] public static partial Permission P { get; } }` (repo C:\Dev\YFex\YFex.Authorization + its generator); CQRS: `b.Command<C, R>().RequirePermission(X.P)` (also `.RequireRoles/.RequirePolicy/.Authorize(...)/.Authorize<TRequirement>()`); VMs/commands: `[RequirePermission(X.Ids.P)]`; host: `services.AddYFexAuthorization(...)` — every dispatcher enforces the one message policy (`IMessageAuthorization`); denials are `ErrorKind.Unauthenticated`/`Forbidden(Denial)` (docs/internal/authorization-consolidation, YFex.Authorization/README.md) |

## Documentation

See `docs/` for the full wiki:
- `docs/public/` — Public API guides (capabilities, patterns, usage)
- `docs/internal/` — Internal implementation details
