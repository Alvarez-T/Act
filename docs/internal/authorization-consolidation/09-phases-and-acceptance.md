# 09 — Migration phases and acceptance

## 0. Working rules (every phase)

1. **The ⚑Z decisions in the README must be confirmed first.** A phase that depends on an open decision does not start.
2. `C:\Dev\YFex` is **not** a git repository: edits are live. Before a phase, copy every file the phase will touch to
   `C:\Temp\authz-backup\<phase>\<relative path>`. Write multi-line edit scripts to `C:\Temp\yfex-edits\*.py` (heredocs mangle
   backslashes and quotes).
3. **Other sessions:** YFex.Logging Phase 2 will touch `YFex.Cqrs` (logging stage), `YFex.Messaging.Rpc` (`TracingHandlerInvoker`,
   `CallContextMiddleware` propagation). Privacy Tier 1 is editing YFex core (`src/YFex/Privacy`) and `YFex/CLAUDE.md`. Before
   editing a shared file, check its modification time (`find … -newermt`). If it changed in the last hours, re-read it and coordinate.
   List every touched file in the implementation log below.
4. Build/test with `--artifacts-path C:\Temp\authz-art -p:NuGetAudit=false` (RoselYn locks generator DLLs in `bin/`).
5. After each phase, record test counts (before → after) in the log. A phase is done only when every listed suite is green, except
   the 5 known NavigatR failures (01 §4).
6. AOT: `YFex.Capabilities.AotSmoke` and `YFex.Validation.Smoke` publish with 0 trim/AOT warnings at the end of phases 1, 2, 5.

## Phase 0 — Leaf contracts and `ValidityPeriod` (⚑Z1, ⚑Z2, ⚑Z3, ⚑Z13)

- YFex core: `ValidityPeriod` (`src/YFex/ValidityPeriod.cs`, converters, `ILogValue`) + tests in `YFex.Tests`.
- `YFex.Authorization.Public`: `ActorIdentity`, `ActorKind`, `ActorRelation`, `ActorScope`, `ActorContext` (AsyncLocal, `Enter`
  returns a restoring activation) + tests (flow across `await`, nesting, restore).
- **Leaf guard:** a test in Authorization.Tests asserts `.Public` references only `YFex` + BCL assemblies (no Tenancy, no DI).
- `YFex.Rules.csproj` gains a reference to `.Public` (nothing uses it yet); `YFex/CLAUDE.md` layer rule: "Rules may reference
  `YFex.Authorization.Public` only" (coordinate: the Privacy session also edits CLAUDE.md — edit only that line).

**Acceptance:** every suite of 01 §4 has the same counts; `YFex.Tests` and Authorization.Tests = previous + new; no behaviour change.

## Phase 1 — Authorization core upgrade (repo + mechanical renames in consumers)

- Catalog syntax (⚑Z2): `[Permission("id", Name, Description)]` on `static partial Permission` properties; generator emits the
  property bodies, `Ids` consts, `All`, `Manifest`, module-initializer registration. Migrate every existing catalog (test catalogs,
  `ShopPermissions`, Capabilities/Search tests) and `Values.X` → `X`, attribute arguments → `Ids.X`.
- `PermissionKey.Declare`/`Ordinal` + process interner; `PermissionSet` (+ builder); unknown token ids dropped and counted.
- `[Union]` `Requirement`, `CompiledPolicy`, `AuthorizationVerdict`, `Denial`; evaluator `IsVisible/Authorize/AuthorizeAsync`.
- `IPermissionSource` + built-in sources; roles resolved once into a case-insensitive role set (D4).
- `IAuthorizationContext` rename (⚑Z4) everywhere (incl. `CallContext.Caller`); `Scope` as canonical text.
- `IAuthorizationConfiguration`/`AuthorizationBuilder` moved to `.Public`; `PolicyKey → CompiledPolicy` registry built lazily from DI.
- AspNetCore: `YFexAuthorizationMiddleware` + `AuthorizationContextCache`; accessor reads only (D6); problem details with reason.
- Generator: merge all `[RequirePermission]`, view-model command rows, metadata-name VM detection, catalog properties,
  AUTH006–007 (AUTH005 reserved, ⚑Z6).
- New `YFex.Authorization.Testing` and `YFex.Authorization.Benchmarks` (⚑Z18; budgets of 03 §8).

**Acceptance:** Authorization.Tests ≥ 41 + new (catalog syntax, sets, sources, verdicts, generator snapshots, analyzers);
AspNetCore.Tests ≥ 3 + (401/403 reason, one context per request); budgets met; all consumer suites unchanged.

## Phase 2a — One message gate (⚑Z5–⚑Z8, ⚑Z17)

- Cqrs builder verbs record `Requirement`s; `.Authorize(Func<IAuthorizationContext,T,bool>)`, `.Authorize<TRequirement>()`,
  `.RequirePolicy`, `.RequireAnyPermission`; delete `AuthorizerKind/AuthorizerDescriptor/ICommandAuthorizer/IQueryAuthorizer`,
  `CommandPolicy.Authorize/QueryPolicy.Authorize`, `Build` evaluator parameters, `RequireRoles(string[])`.
- `CqrsAuthorizationConfiguration` exported automatically (message attributes merged, ⚑Z6); `IMessageAuthorization` called by
  LocalDispatcher, ValidatingHandlerInvoker, CallContextMiddleware, WolverineLocalDispatcher (full check) and FusionMessageBus (quick).
- Delete `CallAuthorization` + `[ThreadStatic]`, `AddYFexCqrsAuthorization`. Fail-closed startup check.
- Denials map to the **existing** `Error` for now (`ErrorType.Unauthorized` + message naming the missing permission) — the typed
  cases arrive in 2b.
- Authorization event descriptors (07 §5) + `Denied` emission through `Operations.MarkWith` (rate-limited per subject).

**Acceptance (new tests):**
- D1: requirements declared through `b.ForMessage<T>()`, through Cqrs configuration and through `[RequirePermission]` on a record
  are denied on LocalDispatcher, on a real RPC round-trip (Messaging.Rpc.Tests fixture) and on Wolverine.
- D3: `.RequirePolicy` with a registered handler allows and denies on dispatch; `.Authorize<TRequirement>()` sync + async.
- D4: role case-insensitivity identical on every path. D5: an uncatalogued Cqrs permission fails startup, listing it.
- `Denied` event captured with `LogCapture` (YFex.Logging.Testing) or a fake engine.
- Counts: Messaging.Tests ≥ 150 (+2 skipped), Messaging.Rpc.Tests ≥ 41, Rules.Admin.Tests 22 (adapted to the new verbs).

## Phase 2b — `[Union]` Cqrs `Error` (⚑Z11)

- `Error` → `[Union]` tagged struct with `ErrorKind` and the cases of 03 §5.1; `Error.From(verdict)`; factories kept
  (`Validation`, `ApprovalRequired`); `ErrorResultException` unchanged in shape.
- Mechanical migration of the 114 `ErrorType` references (24 files: Cqrs, Messaging, Messaging.Rpc, Rules.Admin, Capabilities.Cqrs,
  tests); `RpcResultFormatters` write tag + case payload (wire v3); `WireFormatTests` updated.
- Client/VM helpers (`ApplyServer`, result extensions) switch on `Kind`.

**Acceptance:** `Forbidden(Denial)` with the missing permission and `Unauthenticated` round-trip over RPC; `ApprovalRequired` and
`ValidationProblem` round-trip unchanged in content; all suites of 2a still green with the same counts.

## Phase 3 — Snapshots, versions, signals, actor writers (⚑Z9, ⚑Z3; split by decisions P1–P7, README)

### 3a — ✅ DONE 2026-10-04

- Move versions/signals/snapshot/context cache to Authorization; Rpc subscribes (invalidate `ICallPermissions`, disconnect on revoke).
- `ICallPermissions` returns `PermissionSnapshot` (payload only, P2); regenerate and re-check the proxy golden test.
- Client `UseServerPermissions()` → `SnapshotPrincipalAccessor`, **online only** (P1, P3).
- `ActorContext` written by the adapters (HTTP, RPC, desktop session P4, client feed); Cqrs per-user cache scope reads it (P5).

**Acceptance:** existing D29 tests (versioned reload, client refresh, revoke disconnect, anti-spoofing) pass unchanged in meaning;
new: client sees server grants and follows a signalled change; desktop host signals without RPC; HTTP applies a signalled change
on the next request; handlers run inside the caller's actor scope; a per-user query is never cached without a subject.

### 3b — ✅ DONE 2026-10-15 (decisions S1–S6, README)

- The server signs each snapshot it hands out as a `SignedBundle` (kind `authorization.snapshot`, offline 05 §2) through
  YFex.Cryptography (`SignedPayload`, key rings, rotation); the client verifies before use and persists the signed bytes.
- Validity in the envelope (default 72 h, configurable); checked against trusted time when `YFex.Time` exists (P7, `../time`).
- Offline start uses a persisted snapshot only after an offline sign-in established the subject (P3, offline Phase 3).

**Acceptance (moved from 3):** offline start within/after validity; **a tampered snapshot (permission added, validity extended,
other subject) is rejected**.

## Phase 4 — Rules, Rules.Admin, approvals (⚑Z2, ⚑Z12 as amended by crypto ⚑K12; decisions A1–A5, README)

### 4a — ✅ DONE 2026-10-05 — no cryptography

- Typed `Permission` in Rules directives/tables/`ApprovalRequest`; Rules.Dynamic resolves ids at publish (unknown id → violation).
- `RuleAdminPermissions` catalog, typed options, gate requirements (`TablePublishRequirement` and friends); delete
  `Demand/PermissionFor`; actor and approver ids are subjects, not display names.

**Acceptance:** Rules.Tests 65, Rules.Admin.Tests ≥ 22, Messaging `RulesStageTests` green; new: unknown permission in a dynamic rule
rejected at publish; RuleAdmin publish actions visible only to holders of a publish permission (`IMessageAuthorization.IsVisible`);
option remap honoured; undeclared table permission fails startup.

### 4b — ✅ DONE 2026-10-16 — approvals on YFex.Cryptography (decisions A1–A9)

- Token family → Authorization as `ApprovalToken` v2 (A3, A4, A6) on YFex.Cryptography (`MessageAuthentication`, ring
  `approvals/server`, `SignedPayload` with the installation evidence key for device tokens); **v1 dropped (A9)**: `OverrideToken*`,
  `IDeviceKeyStore`, the v1 golden and its tests deleted, no cut-off option; split offline caps (amount cap in Rules, age policy in
  Authorization); `IDecisionBound` → Cqrs; `IApprovalBound` + `.RequireApproval`; `YFex.Cqrs.Approvals` (A2) with
  `Approve(permission, digest)` (A5); `IPermissionDirectory` replaces `IApproverDirectory` (A8 actions); `IApprovalPolicySource` +
  separation of duties (A7); `ApprovalEvents.Issued`/`Accepted`.

**Acceptance:** ~~golden test of v1 tokens~~ (dropped by A9); `.RequireApproval` round-trip incl. offline device token flagged; refusals
of 06 scenarios (other decision, other bound value, other scope, expired, tampered). All met — log below.

## Phase 5 — Capabilities, Search, NavigatR, view-model commands (⚑Z19) — ✅ DONE 2026-10-29

- Capabilities on `CompiledPolicy` per `PolicyKey`; `ActionGate` `[Union]` + `NeedsApproval`; remove reflection; search filters.
- NavigatR: verdict-based denial text; optional re-check on signals.
- State generator hook `__OnCanExecute_<Command>` + Authorization generator implementation (⚑Z19) + re-check on signals.

**Acceptance:** Capabilities 28 / 7 / 5 ≥, NavigatR 129 passed (+ new), State tests unchanged + hook tests; D12 regression test (a
capability whose message needs two permissions is hidden from a caller holding one); AotSmoke clean.

## Phase 6 — Impersonation (⚑Z14) — ✅ DONE 2026-10-29

- `PolicyTenantImpersonationGate`, `ImpersonationRestrictions`, `ActorContext` relation on HTTP; RPC `AllowImpersonation()`;
  `ImpersonationStarted` event.

**Acceptance:** Tenancy.Tests 27 ≥, Web.Data.Tests 30 ≥; new: role-granted `tenant.impersonate` works; restricted permission
denied with `RestrictedWhileImpersonating`; real actor visible in `ActorContext` for the whole request; RPC parity.

## Phase 7 — With the features that need them (Tier 2)

Temporary grants service + store (⚑Z13), justification requirement, Audit core-contract emission of the authorization events (⚑Z17),
`YFex.Authorization.Data` (⚑Z18), user impersonation, Privacy reveal integration (07). Each lands with its feature and its own
acceptance tests.

- **Site presence (⚑Z20, 2026-10-20):** `Requirement.SitePresence(maxAge)`, `DenialKind.SitePresenceRequired`, Cqrs `ErrorKind` + RPC
  mapping (like `ReauthenticationRequired`), `.RequireSitePresence(TimeSpan)`, `IAuthorizationContext.PresenceAt` reading the
  `presence_at` claim. **✅ Built 2026-10-29** ahead of its producer (authentication AN-P7 / installations I6), tested with a fake claim. **Acceptance:** fresh presence allows; missing, old or
  unparseable presence denies with the typed reason; visibility unchanged; AOT 0 warnings.

## Documentation updates (end of each phase)

`YFex.Authorization/README.md`; `YFex/CLAUDE.md` (solution map + Common Tasks rows "Caller identity", "Require permissions");
`docs/internal/authorization-design.md` and `authorization-capabilities-overview.md` status → "Superseded by
authorization-consolidation/"; audit 02 (ActorContext lives in `YFex.Authorization.Public` per ⚑Z3, `audit.reveal*` dropped per ⚑Z15, events via core probes per ⚑Z17); privacy
08 §10 (catalog registration via module initializer); validation 13 (D34 tokens moved/renamed).

## Implementation log

_(append per phase: date, files touched, deviations, test counts before → after)_

### Baseline re-run (2026-10-02, before Phase 0)

Same counts as 01 §4, plus two suites now tracked because they consume authorization: YFex.Tests (core) 140, YFex.Search.Tests 57.
Runner: `C:\Temp\yfex-edits\run-tests.sh <label> [suite…]` (logs in `C:\Temp\authz-testlogs\<label>`).

### Phase 0 — DONE 2026-10-02

Backups: `C:\Temp\authz-backup\phase0\`. No other session had touched the shared files since 2026-10-02 10:50 (Privacy) / 2026-10-01
(Logging).

| File | Change |
|---|---|
| `YFex/src/YFex/ValidityPeriod.cs` (new) | `ValidityPeriod` (From inclusive, Until exclusive/null), `For`/`Starting`, `Contains`/`IsExpired`/`IsPending`/`Remaining`/`Overlaps`/`CapEnd`, ISO 8601 interval text (`from/until`, `from/..`), 16-byte binary form (UTC ticks), type + JSON converters, `ILogValue` |
| `YFex/src/YFex/Diagnostics/NodeIdentity.cs` | `LogValueWriters.RegisterCoreTypes` also registers `ValidityPeriod` (one line) |
| `YFex/src/YFex.Tests/Values/ValidityPeriodTests.cs` (new) | 10 tests |
| `YFex.Authorization/src/YFex.Authorization.Public/Actor.cs` (new) | `ActorKind`, `ActorRelation`, `ActorIdentity` (+ `ILogValue`), `ActorScope`, `ActorContext` (AsyncLocal, `Enter` → restoring `Activation`) |
| `YFex.Authorization/tests/YFex.Authorization.Tests/ActorContextTests.cs`, `LeafGuardTests.cs` (new) | flow across await, nesting/restore, out-of-order dispose, no leak to caller; leaf guard (assembly references + csproj) |
| `YFex/src/Validation/YFex.Rules/YFex.Rules.csproj` | references `YFex.Authorization.Public` (unused yet) |
| `YFex/CLAUDE.md` | only the Rules layer-rule line |

Deviations: `ActorIdentity` gets default values for `Kind`/`HomeScope` and an `Anonymous`/`IsAnonymous` helper; `ActorScope` gets
`EffectiveRealActor`/`IsOnBehalf`. `ValidityPeriod` is a plain struct (not a positional record) so `with` cannot bypass the
"Until > From" check; `default(ValidityPeriod)` is "from MinValue, forever" — callers that sign it always construct it explicitly.

| Suite | Before | After |
|---|---|---|
| YFex.Tests | 140 | **150** |
| YFex.Authorization.Tests | 41 | **48** |
| everything else of 01 §4 + Search | unchanged | unchanged (NavigatR 129 + the 5 known failures) |

### Phase 1 — DONE 2026-10-02

Backups: `C:\Temp\authz-backup\phase1\` (whole YFex.Authorization repo + every consumer file below). No other session touched
the consumer files (checked mtimes before editing).

**YFex.Authorization repo**

| File | Change |
|---|---|
| `.Public/PermissionKey.cs` | `Declare` (process interner, lock-free reads, copy-on-write ordinal array), `Ordinal` (−1 = undeclared), `IsDeclared`, `TryGetDeclared`, `Resolve`; `Parse/TryParse` never intern; equality stays by id |
| `.Public/PermissionSet.cs` (new) | bitset: `Contains`, `ContainsAll`, `ContainsAny`, `FirstMissing`, `Union/Except/Intersect`, `Of`, `ToIds`, allocation-free enumerator, `Builder` (drops + records undeclared ids) |
| `.Public/Attributes.cs` | `[PermissionMeta]` → `[Permission("id", Name, Description)]` on properties; `[RequirePermission]` also on structs |
| `.Public/AuthorizationManifest.cs` | `PermissionMetaEntry` → `PermissionEntry` (`Name`); a manifest declares its ids; `PermissionCatalogs` (module-initializer registry, `Merged`, `DisplayText`) |
| `.Public/Requirement.cs` | `[Union]` tagged struct (tag + one `PermissionKey` + one reference); cases Authenticated/Permission/AnyPermission/Roles/Claim/Resource/Policy; `Bind(IServiceProvider)` |
| `.Public/ResourceRequirement.cs` (new) | `IAuthorizationRequirement<T>` / `IAsyncAuthorizationRequirement<T>` (+ `VisibleWith`), `ResourceRequirement` (delegate, instance, DI-resolved class) |
| `.Public/CompiledPolicy.cs` (new) | `PolicyTarget`, `PolicyKey`, `CompiledPolicy` (mask + auth flag + remaining, `IsSyncOnly`, `FirstRequiredPermission`) |
| `.Public/AuthorizationVerdict.cs` (new) | `[Union]` `AuthorizationVerdict` (`AccessAllowed` / `AccessUnauthenticated` / `Denial`), `Denial`, `DenialKind` |
| `.Public/IAuthorizationContext.cs` (new), `IPolicyEvaluator.cs` | `IAuthorizationContext` + `IPrincipalAccessor`; `IPolicyEvaluator` (`IsVisible/Authorize/AuthorizeAsync`), `IMessageAuthorization`, `IViewModelPolicyProvider` (CompiledPolicy) |
| `.Public/AuthorizationBuilder.cs` (moved from runtime) | `IAuthorizationConfiguration`, `AuthorizationBuilder` (`ForMessage<T>/ForMessage(Type)/ForEndpoint/ForRoute/ForCommand`), `RequirementBuilder<T>` (+ `RequireAnyPermission`, `Authorize(lambda)`, `Authorize<TRequirement>()`) |
| `.Public/Actor.cs` | (Phase 0) |
| deleted from `.Public` | `AuthorizationResult.cs`, `IAuthContext.cs`, `IPermissionProvider.cs`, `IViewModelPolicyProvider.cs` |
| runtime `AuthorizationContext.cs`, `AuthorizationContextFactory.cs`, `PermissionSources.cs`, `PolicyEvaluator.cs`, `CompiledAuthorizationRegistry.cs`, `MessageAuthorization.cs`, `AuthorizationContextCache.cs`, `AmbientPrincipalAccessor.cs`, `ServiceCollectionExtensions.cs` | context (role set, actor, scope text, version), factory over `IPermissionSource`s (`RolePermissionMapSource`, `PermissionClaimSource`, `ScopedClaimSource`), `UndeclaredPermissions` counter, evaluator (fast path + named policies + resource requirements), one `PolicyKey → CompiledPolicy` table built lazily from DI (all unknown ids listed at once, requirement classes bound), cross-request context cache; `IsAotCompatible` |
| deleted from runtime | `AuthContext.cs`, `AuthContextFactory.cs`, `AuthorizationBuilder.cs`, `PermissionProviders.cs` |
| `Generator/*` | catalog properties (+ `Ids`, `All`, `Manifest`, module initializer, containing types), all `[RequirePermission]` merged, `[StateCommand]` rows (`ForCommand<VM>("Void")`), `YFex.State.StateObject` metadata-name detection, catalog accessors in rows; analyzer AUTH001–004 on the new syntax, AUTH006, AUTH007 (AUTH005 reserved) |
| `AspNetCore/*` | `YFexAuthorizationMiddleware` + `IAuthorizationContextFeature`, `UseYFexAuthorization()`, read-only `HttpContextPrincipalAccessor`, endpoint filter on `IMessageAuthorization` with problem+json (`reason`, `missing`, `missingName`) |
| `src/YFex.Authorization.Testing` (new) | `TestCaller`, `TestPrincipalAccessor`, `TestCatalog.Declare`, verdict assertions |
| `benchmarks/YFex.Authorization.Benchmarks` (new) | budgets of 03 §8 |
| `tests/*` | Authorization.Tests rewritten for the new API + new suites; AspNetCore.Tests +3; new `YFex.Authorization.Generator.Tests` |
| `authorization.slnx` | + Testing, Generator.Tests, Benchmarks |

**Consumers (mechanical renames, no behaviour change)**: `YFex.Cqrs/CompiledMessagingRegistry.cs`; Messaging.Rpc `CallContext.cs`
(`Auth` → `Caller`), `CallContextMiddleware.cs`, `CallPermissions.cs` (ids from `Caller.Permissions.ToIds()`, no downcast),
`CallAuthorization.cs`, `RpcMessagingExtensions.cs`; `YFex.NavigatR/Navigator.cs` (`IsVisible`, denial text from
`FirstRequiredPermission`); `YFex.Rules.Admin/RuleAdminHandlers.cs`; Capabilities `Agent.cs`, `CapabilitySearchService.cs`,
`CqrsCapabilitySource.cs`, AotSmoke `Program.cs`; Search `CapabilityCatalogProvider.cs`, `IFrecencyStore.cs`, `ISearchOrchestrator.cs`,
`ISearchProvider.cs`, `Frecency.cs`, `KeyValueFrecencyStore.cs`. Tests: Messaging `AuthorizationGateTests.cs`,
`CqrsAuthorizationWiringTests.cs`; Messaging.Rpc `CallContextSupport.cs` (`ShopPermissions` is now a `[PermissionCatalog]`,
`TestPermissionProvider` → `TestPermissionSource`), `CallContextTests.cs`, `RpcFixture.cs`, csproj (+ Authorization generator);
NavigatR `AuthorizationGateTests.cs`; Rules.Admin `AdminFixture.cs`; Capabilities `Fakes.cs`; Search `TestFakes.cs`,
`CapabilityCatalogProviderTests.cs` (the allow-list moved from the fake evaluator to the fake caller).

**Deviations (logged, no public API beyond the design's intent)**

1. `IPermissionSource`/`PermissionResolution` live in the runtime project, not `.Public`: they carry Tenancy's `ScopeIdentity` and
   `.Public` must stay a leaf (⚑Z3 wins over the 03 §3.1 sketch).
2. `PermissionSet.Builder` is a class (sources contribute asynchronously; a `ref struct` cannot cross `await`).
3. Verdict case types are `AccessAllowed` / `AccessUnauthenticated` / `Denial` (not `Allowed`/`Unauthenticated`/`Forbidden`): the
   bare names collide with `YFex.Rules.Allowed` and with the 2b Cqrs `Error` cases in files that import both namespaces. Kinds
   and factories keep the design names (`AuthorizationVerdictKind.Forbidden`, `AuthorizationVerdict.Forbidden(denial)`).
   `ApprovalRequired`/`JustificationRequired` verdict cases join with their phases (4/7).
4. Any identity-based requirement (authenticated, permission, any-permission, role, claim) makes an anonymous caller
   `Unauthenticated` (401) rather than `Forbidden` — same rule the old endpoint filter used.
5. Requirement classes (`.Authorize<TRequirement>()`) must be registered in DI; the registry build fails listing the missing ones
   (they belong in server overrides when they need server I/O, like `.Validate<TContextRule>()`).
6. The generated configuration class list is `GeneratedAuthorizationConfigurations.All` (was `GeneratedAuthConfig.All`: no
   abbreviations). View-model command keys use the command name without `Async` (`"Void"`).
7. `IPolicyEvaluator.IsAllowed(Permission, caller)` is gone: Capabilities/Search call `caller.HasPermission(p)` until Phase 5 moves
   them to `IsVisible(policy, caller)`.
8. Undeclared-id reporting is the `UndeclaredPermissions` counter + `FirstSeen` event; the core-probe event joins the 2a events.
9. `ActorContext` is not written yet by the adapters (Phase 3 per 09).
10. Benchmarks use BenchmarkDotNet 0.15.8 — the package/version already in the ecosystem (YFex.Logging.Benchmarks, ⚑L21); ⚑Z18
    approved the project.

**Budgets (03 §8, BenchmarkDotNet ShortRun, Release):** `HasPermission` 0.68–0.76 ns / 0 B (≤ 2 ns); message gate,
permissions-only, cached context 7.3 ns / 0 B (≤ 25 ns); `IsVisible` × 1 000 = 3.1 µs / 0 B (≤ 15 µs); context resolution (20
roles → 200 permissions, claims-only) 1.5 µs / 4.1 KB (≤ 20 µs).

**AOT:** YFex.Validation.Smoke publishes native, 0 trim/AOT warnings. YFex.Capabilities.AotSmoke: build with
`IsAotCompatible` 0 IL warnings, and a real NativeAOT publish (`PublishAot` set temporarily in the sample only — a global
`-p:PublishAot=true` breaks the netstandard2.0 generator projects, NETSDK1207, pre-existing) 0 IL warnings; both run.

| Suite | Before | After |
|---|---|---|
| YFex.Authorization.Tests | 48 | **70** |
| YFex.Authorization.Generator.Tests (new) | — | **10** |
| YFex.Authorization.AspNetCore.Tests | 3 | **6** |
| YFex.Tests | 150 | 150 |
| Messaging 150 (+2 skipped), Messaging.Rpc 41, NavigatR 129 (+5 known), Rules 64, Rules.Admin 22, Capabilities 28/7/5, Tenancy 27, Web.Data 30, Search 57 | = | = |

### Phase 2a — DONE 2026-10-03

Backups: `C:\Temp\authz-backup\phase2a\`. Coordination: the offline-design session ("Design offline + local-network store server
mode") started Phase 0 meanwhile and announced edits to the Messaging.Rpc outbox files, `FusionMessageBus.cs` (HeadersAsync) and
`RpcMessagingExtensions.cs` (one decorator); it was told which files 2a changed and which 2b will touch. Its temporary
`AesGcmValueProtector` change broke `Messaging.Tests` compilation for a few minutes (fixed by that session; not ours).

| File | Change |
|---|---|
| Cqrs `Configuration/CommandConfigurationBuilder.cs` (both builders), `QueryConfigurationBuilder.cs` | verbs record `Requirement`s: `RequireAuthenticated`, `RequireRoles(Role[])`, `RequirePermission`, `RequireAnyPermission`, `RequirePolicy(PolicyName)`, `Authorize(Func<IAuthorizationContext,T,bool>)`, `Authorize<TRequirement>()`; removed `RequireRoles(string[])`, `RequireAuthorization(string)`, the `ClaimsPrincipal` lambda and `ICommandAuthorizer`/`IQueryAuthorizer` forms |
| Cqrs `Registration/Descriptors.cs`, `Configuration/Interfaces.cs` | deleted `AuthorizerKind`, `AuthorizerDescriptor`, `ICommandAuthorizer<T>`, `IQueryAuthorizer<T>` |
| Cqrs `Registration/*RegistrationMetadata.cs`, `Runtime/CommandPolicy.cs`, `Runtime/QueryPolicy.cs` | `Requirement[]? Authorization`; `Authorize` delegate removed from the policies |
| Cqrs `CompiledMessagingRegistry.cs` | `Build` without evaluator/principal factory; `AuthorizationRequirements` + `HasAuthorizationRequirements`; `BuildCompositeAuthorizer` deleted; capability permission from the first permission requirement |
| Cqrs `CqrsAuthorizationConfiguration.cs` (new) | `CqrsAuthorizationConfiguration` export + `AddYFexCqrsAuthorizationConfiguration()` (idempotent) |
| Cqrs `Error.cs` | `Error.From(in AuthorizationVerdict)` (2a: `ErrorType.Unauthorized` + the verdict text, e.g. "Requires permission 'x'.") |
| Messaging.Rpc `CallContext/CallAuthorization.cs` | `CallAuthorization` static + `[ThreadStatic]` deleted; `CallPrincipalSource` = caller + `IMessageAuthorization` (`AuthorizeAsync` full, `IsVisible` quick, `EnsureConfigured` fail-closed) |
| Messaging.Rpc `LocalDispatcher.cs`, `ValidatingHandlerInvoker.cs` (now authorizes every message, CQRS-registered or not), `CallContext/CallContextMiddleware.cs`, Wolverine `WolverineLocalDispatcher.cs` | full check through the one gate |
| Messaging.Rpc `FusionMessageBus.cs` | quick check (`IsVisible`) |
| Messaging.Rpc `RpcMessagingExtensions.cs`, Wolverine `WolverineRpcExtensions.cs` | `AddYFexCqrsAuthorization()` (second registry build) deleted; client/server/Wolverine registrations call `AddYFexCqrsAuthorizationConfiguration()` |
| Authorization `.Public/AuthorizationConfigurations.cs` (new), generator | generated `[RequirePermission]` configurations register themselves (module initializer); the registry merges them |
| Authorization `AuthorizationEvents.cs` (new), `MessageAuthorization.cs` | `AuthorizationEvent` (+ capture), `AuthorizationEvents.Denied` through `Operations.MarkWith`, `SubjectRateLimiter` (20/subject/minute); emitted on every authoritative denial of the gate |
| Tests | Messaging: `Cqrs/AuthorizationGateTests.cs` rewritten (18 tests), `CqrsAuthorizationWiringTests.cs` deleted, fixtures `ShopAggregate.cs`/`CheckoutAggregate.cs`/`TestDispatcherFixture.cs` wire the gate, csproj (+ Testing, generator, Wolverine); Messaging.Rpc: `RpcFixture.cs` (no evaluator parameters, client registry + export, server hook), `ShopAggregate.cs` (+ `ArchiveCustomer` with `[RequirePermission]`, `ExportCustomers`), `CallContextSupport.cs` (+2 catalog permissions), `CallContextTests.cs`, new `MessageGateTests.cs`; Rules.Admin `AdminFixture.cs` wires the gate |

**Behaviour changes (release note):** (1) `ForMessage<T>()` rows and `[RequirePermission]` on message records are now enforced on
every CQRS dispatcher (D1). (2) Dispatchers fail at construction when the registry declares any requirement (even
`RequireAuthenticated`) and no `IMessageAuthorization` is registered or it misses the Cqrs export — previously `RequirePermission`
silently denied at run time and `RequireAuthenticated` worked without YFex.Authorization. (3) The client bus blocks a message the
quick check hides (named policies/async requirements in the shared baseline are therefore client-blocked: declare them in server
overrides).

**Deviations**

1. Generated configurations self-register (`AuthorizationConfigurations`), so an attribute on a record is enforced without
   `cfg.AddConfigurations(...)` — the design only specified module-initializer registration for catalogs.
2. The export is registered by `AddYFexCqrsAuthorizationConfiguration()`, called by the Messaging.Rpc/Wolverine registrations;
   hosts that build dispatchers by hand call it themselves (the startup check names it).
3. `ValidatingHandlerInvoker` also authorizes messages without a Cqrs policy (a `ForMessage<T>()` row is enough).
4. The `Denied` event is emitted inside `MessageAuthorization` (single authoritative choke point, also used by the HTTP filter).

**Acceptance (new tests):** D1 — Cqrs configuration, `ForMessage<T>()` and `[RequirePermission]` on a record denied on
LocalDispatcher (`AuthorizationGateTests`), on a real RPC round-trip (`MessageGateTests`) and on Wolverine (two tests); server
invoker path; D3 — `.RequirePolicy` with a registered `INamedPolicyHandler` allows/denies, `.Authorize<TRequirement>()` sync and
async, inline predicate; D4 — role case-insensitivity on dispatch (+ evaluator tests of Phase 1); D5 — an uncatalogued Cqrs
permission fails startup naming the id and the message; fail-closed without gate / without export; `Denied` captured by a fake
`IOperationEngine` (the YFex.Logging `LogCapture` would add a cross-repo test dependency).

| Suite | Before | After |
|---|---|---|
| YFex.Messaging.Tests | 150 (+2 skipped) | **158** (+2 skipped) — 10 old gate tests replaced by 18 |
| YFex.Messaging.Rpc.Tests | 41 | **44** |
| YFex.Rules.Admin.Tests | 22 | 22 |
| everything else (Authorization 70, Generator 10, AspNetCore 6, Core 150, NavigatR 129 + 5 known, Rules 64, Capabilities 28/7/5, Tenancy 27, Web.Data 30, Search 57) | = | = |

### Phase 2b — DONE 2026-10-03

Backups: `C:\Temp\authz-backup\phase2b\`. The offline-design session finished its Messaging.Rpc edits (outbox, idempotency
headers/decorator) during 2b; FusionMessageBus/RpcMessagingExtensions/ValidatingHandlerInvoker were re-read before 2b touched them
and only the stage-2 lines were changed. Its counts are included below (Messaging.Tests and Messaging.Rpc.Tests gained tests of
theirs).

| File | Change |
|---|---|
| Cqrs `Error.cs` | `[Union]` tagged struct: `ErrorKind` tag; cases `NotFound`, `Failed`, `Unauthenticated`, `Forbidden(Denial)`, `ValidationProblem(ViolationList)`, `Conflict(Message, Violations)`, `ApprovalRequired(Approvals, DecisionFingerprint, Violations)`; storage = tag + message + violations + one reference (boxed `Denial` / approval payload); `TryGetValue` per case, implicit conversions, `WithMessage`, `Validation`, `ApprovalRequired`, `From(verdict)`; `string` → `Failed` |
| Cqrs `ErrorType.cs` | deleted |
| Cqrs `Result.cs`, `QueueableResult.cs`, `CacheableQueryResult.cs` | factories build cases; `Unauthorized(msg)` → `Unauthenticated(msg)` + `Forbidden(denial)` |
| Cqrs `Runtime/RulesStage.cs` | `Conflict` case with the mismatch violation; union-safe `HasValue/Value` instead of `is { } x` |
| Messaging.Rpc `Serialization/RpcResultFormatters.cs` | wire **v4**: kind tag + message + case payload (`Forbidden` = denial kind, missing id, policy, code); v1–v3 still readable |
| Messaging.Rpc `LocalDispatcher.cs`, `ValidatingHandlerInvoker.cs`, Wolverine `WolverineLocalDispatcher.cs`, `WolverineExceptionMiddleware.cs` | union-safe patterns; `Failed` |
| Messaging.Rpc `FusionMessageBus.cs`, `CallContext/CallAuthorization.cs`; Authorization `IPolicyEvaluator.cs` (`IMessageAuthorization.QuickCheck`), `MessageAuthorization.cs` | client quick check returns the reason → `Error.From(verdict)` |
| Rules.Admin `RuleAdminHandlers.cs` | `NotFound`/`Conflict`/`Unauthenticated`/`Forbidden(Denial)` (missing permission → `Denial.MissingPermissionOf`, tier → `ResourceDenied` with the original text) |
| Validation.Generator `Emitter.Vm.cs` | generated `ApplyServer` switches on `ErrorKind` (codes `server.unauthenticated`, `server.forbidden`, `server.approval` added; `server.unauthorized` gone) |
| Tests | Messaging `StructuredErrorTests.cs` (+3 union tests, renamed the 401 test), `PipelineStageTests.cs`, `QueryCoercionTests.cs`, `RulesStageTests.cs`, `AuthorizationGateTests.cs`; Messaging.Rpc `WireFormatTests.cs` (+3), `MessageGateTests.cs` (+2: Forbidden with the missing permission and Unauthenticated over a real RPC round-trip), `CallContextTests.cs`, `RpcRoundTripTests.cs`; Rules.Admin `PublishTests.cs`, `DynamicRuleAdminTests.cs`, `RuntimeEditingTests.cs` |

**Deviations**

1. Wire version is **4**, not 3: v3 was already taken by the RawValue change (validation D35–D38).
2. `Conflict` carries optional `Violations` (the rules stage reports the fingerprint mismatch as a conflict with a violation).
3. `Error.Violations`/`HasViolations` stay as accessors (meaningful for ValidationProblem, Conflict, ApprovalRequired) — the
   generated `ApplyServer` and many clients read them; approvals/fingerprint are only reachable through `TryGetValue(out ApprovalRequired)`.
4. `IMessageAuthorization.QuickCheck(type, caller)` added (IsVisible + the reason) so the client bus answers "Requer: …" without a
   round trip; never authoritative, never an event.
5. **Language gotcha:** on a `[Union]` type, `x is { } e` / property patterns match the union's case `Value` (typed `object`), not
   the struct — use `x.HasValue` / `x.Value` (fixed in RulesStage and the dispatchers; told the offline session).

**Acceptance:** `Forbidden(Denial)` with the missing permission (+ its catalog display name) and `Unauthenticated` round-trip over
RPC; `ApprovalRequired` and `ValidationProblem` round-trip unchanged in content (existing wire tests, adapted to the cases); every
2a suite green. AOT (end of Phase 2): YFex.Validation.Smoke and YFex.Capabilities.AotSmoke (PublishAot set temporarily in the
sample, restored byte-identical) publish native with 0 IL/AOT warnings and run.

| Suite | Before (end of 2a) | After |
|---|---|---|
| YFex.Messaging.Tests | 158 (+2 skipped) | **167** (+2 skipped) — +3 ours, +6 of the offline session |
| YFex.Messaging.Rpc.Tests | 44 | **55** — +5 ours, +6 of the offline session |
| YFex.Rules.Admin.Tests | 22 | 22 |
| YFex.Validation.Tests / Validation.Generator.Tests (generated `ApplyServer` changed) | 62 / 50 | 62 / 50 |
| Core 150, Authorization 70, Authorization.Generator 10, AspNetCore 6, NavigatR 129 (+5 known), Rules 64, Capabilities 28/7/5, Tenancy 27, Web.Data 30, Search 57 | = | = |

### Totals Phase 0 → 2b (against the 01 §4 baseline)

Authorization 41 → 70, Authorization.Generator — → 10, AspNetCore 3 → 6, YFex.Tests 140 → 150, Messaging 150 → 167 (+2 skipped;
9 of these are ours net of the 10 replaced gate tests, 6 are the offline session's), Messaging.Rpc 41 → 55 (8 ours, 6 offline),
everything else unchanged (NavigatR 129 + the same 5 pre-existing failures).

### Open items / handoff to Phase 3

- Documentation of 09 "Documentation updates" not done yet (other sessions are editing those files): YFex/CLAUDE.md solution map
  (Authorization repo is outside it), `authorization-design.md`/`authorization-capabilities-overview.md` already point here;
  audit 02 (ActorContext in `.Public`), privacy 08 §10 (catalog module initializer — implemented), validation 13 (D34 tokens:
  Phase 4). One row was added to YFex/CLAUDE.md Common Tasks ("Require permissions").
- `ActorContext` is defined but not written by the adapters yet (Phase 3).
- Capabilities/Search still use `caller.HasPermission(firstPermission)` (Phase 5 → `IsVisible(policy)`); `CqrsCapabilityInfo.RequiredPermission` is a display hint.
- Rules `ApprovalRequest.Permission` and Rules.Admin option permissions are still strings (Phase 4).
- `AuthorizationEvents` has only `Denied`; `UndeclaredPermissions` reports through a counter/event, not yet through the probe.

### Phase 3a — DONE 2026-10-04

Decisions P1–P7 confirmed by the user the same day (README). Backups: `C:\Temp\authz-backup\phase3a\`. No other session had
touched the code files since Phase 2b (checked mtimes; the cryptography session had only changed core `Cryptography/*`,
`Canonical/*` and `Messaging.Tests/Encryption`). `YFex/CLAUDE.md` and `docs/index.md` (edited by the cryptography session at 10:1x):
one line/row each.

| File | Change |
|---|---|
| Authorization `.Public/Actor.cs` | `ActorContext.Session` + `SetSession` (process-wide, under the `AsyncLocal` scope, P4); `ActorScope.From(caller)`, `ActorScope.Anonymous` |
| Authorization `.Public/PermissionSnapshot.cs` (new) | payload-only record (P2): sorted ids/roles, `From`, `Has`, `IsInRole`, sequence equality |
| Authorization `.Public/AuthorizationSignals.cs` (new) | `IPermissionVersions`, `IAuthorizationSignals`, `AuthorizationChange(Kind)`, `IAuthorizationChangeObserver` |
| Authorization `AuthorizationSignals.cs` (new) | `InMemoryPermissionVersions`, `AuthorizationSignals` (bump → observers → `Changed`; failures aggregated) |
| Authorization `SnapshotPrincipalAccessor.cs` (new) | client accessor: `Apply(snapshot)`, `Clear()`, `Changed`, session actor, local signals |
| Authorization `AmbientPrincipalAccessor.cs` | process-wide session: `SignIn(principal)`, `SetCurrent`, `SignOut`, re-resolved when the subject's version moved; writes the session actor |
| Authorization `AuthorizationContext.cs` | `FromSnapshot(snapshot)` (ids → declared permissions, unknown dropped and reported) |
| Authorization `AuthorizationContextCache.cs` | version read from `IPermissionVersions` when not passed (`long? version`); is an `IAuthorizationChangeObserver` |
| Authorization `ServiceCollectionExtensions.cs` | `AddYFexAuthorizationSignals()`; cache observer; accessor with factory + versions |
| AspNetCore `YFexAuthorizationMiddleware.cs`, `HttpContextPrincipalAccessor.cs` | current version (was 0 → stale up to the cache TTL); request runs inside `ActorContext.Enter` |
| Messaging.Rpc `CallContext/CallContextSignals.cs` → `CallContextConnections.cs` | `IPermissionsVersions`, `PermissionsVersions`, `CallContextSignals` deleted; `RpcAuthorizationChangeObserver` (invalidate the pushed snapshot, disconnect on revoke) |
| Messaging.Rpc `CallContext/CallPermissions.cs` + `ActualLabProxies/*` | `ICallPermissions` returns `PermissionSnapshot`; `CallPermissions` record deleted; proxies regenerated by `ProxyGoldenTests` |
| Messaging.Rpc `CallContext/CallContext.cs` | `Activation` also enters the caller's actor scope (anonymous → `ActorScope.Anonymous`) |
| Messaging.Rpc `CallContext/CallContextMiddleware.cs` | resolver on `IPermissionVersions`; per-identity fast path, a miss goes through `AuthorizationContextCache` (async) |
| Messaging.Rpc `CallContext/ServerPermissionsFeed.cs` (new), `RpcMessagingExtensions.cs` | hosted feed (live computed → `Apply`); `UseServerPermissions()`; server registers the signals + observer |
| Messaging.Rpc `CallContext/CallAuthorization.cs` | `CallContextPrincipalAccessor.Decorate` without `ActivatorUtilities` (a type registration stays a container registration) |
| Messaging.Rpc `CallContext/CallContextOptions.cs` | doc reference |
| Messaging.Rpc `Serialization/RpcResultFormatters.cs` | `PermissionSnapshotFormatter` (layout v1, registered with the result formatters) |
| Messaging.Rpc `QueryCacheKey.cs`, `FusionMessageBus.cs` | per-user key = `ICacheScopeContext.Subject` ?? actor subject; no subject → not cached (P5); the client bus skips the write when there is no key |
| Cqrs `ICacheScopeContext.cs`, Cqrs.Tenancy `TenancyCacheScopeContext.cs` | `User : ClaimsPrincipal` → `Subject : string?` |
| Tests | Authorization `SignalsAndSnapshotTests.cs` (+6, collection `ActorContext` shared with `ActorContextTests`); AspNetCore `InboundAuthorizationTests.cs` (+2); Messaging.Rpc `ServerPermissionsTests.cs` (+5), `RpcFixture.cs`, `CallContextTests.cs`, `ShopAggregate.cs` (`WhoAmIHandler.LastActor`); Messaging `Cqrs/UserScopedCacheTests.cs` (+2); Cqrs.Tenancy `TenancyCacheScopeContextTests.cs` (adapted) |
| Docs | README (P1–P7), 05 (notes), 08 (rows), this file; repo `YFex.Authorization/README.md`; `YFex/CLAUDE.md` (Caller identity row); `docs/index.md`; offline 11 §2 (phase numbers, time pointer); new `docs/internal/time/README.md` (plan, ⚑T1–T7) |

**Behaviour changes (release note):** (1) `CallContextSignals` is gone: inject `IAuthorizationSignals` (`RevokeAsync` →
`RevokedAsync`). (2) `ICallPermissions.GetMyPermissionsAsync` returns `PermissionSnapshot` (old clients must be updated together
with the server). (3) A `ScopedByUser()` query is no longer cached when there is no subject (it used to share one entry across
users); `ICacheScopeContext.User` → `Subject`. (4) `AmbientPrincipalAccessor.Current`'s setter is process-wide (was `AsyncLocal`).
(5) HTTP picks up signalled permission changes on the next request (was: after the 5-minute cache TTL).

**Deviations**

1. `IAuthorizationChangeObserver` (DI subscribers resolved on the first signal) added next to the `Changed` event of the 05 §4
   sketch: transports must subscribe without being constructed by app code, and `RevokedAsync` must await the disconnects.
2. `PermissionSnapshot.Roles` carries the principal's role names (the 05 sketch had them); `FromSnapshot` turns them into role claims.
3. Anonymous requests/calls enter `ActorScope.Anonymous` instead of no scope, so a session actor never leaks into a server unit of work.
4. The per-connection RPC cache stays (fast path); `AuthorizationContextCache` is the second level, shared across connections.
5. `CallContextPrincipalAccessor` decoration without reflection (08 listed it under Phase 3).

**Deferred until YFex.Cryptography is done (user rule 2026-10-04: signing/crypto/hashing questions wait)**

- Snapshot signing, verification, persistence and offline start (Phase 3b).
- `AuthorizationContextCache` keys on an FNV-1a hash of the claims (existing code). Crypto ⚑K3 says values that feed an
  authorization decision use canonical bytes or `Hash256`, never a non-adversarial hash. Here the subject, scope and version are
  part of the key and the claims come from a validated token, so a collision would have to be crafted inside one user's own
  claims. Check with the cryptography session whether it is in scope of their architecture test.
- Trusted time for validity checks (P7): `../time/README.md` (⚑T1–T12 approved; T0+T1 in progress 2026-10-05). Use
  `time.GetTrustedNow().IsUsableForSecurity(maximumUncertainty)` (⚑T8, T11). Its authenticated sources and the
  high-water protection also wait for crypto.

**Open items (not crypto)**

- `ScopedBySession()` has the same shape as the per-user defect: without an `ICacheScopeContext`, `SessionId` is null and the
  key is shared. Not changed (outside P5); propose the same fail-closed rule.
- `IPermissionVersions` for farms is still in-memory only (README open question).

| Suite | Before | After |
|---|---|---|
| YFex.Authorization.Tests | 70 | **76** |
| YFex.Authorization.AspNetCore.Tests | 6 | **8** |
| YFex.Messaging.Rpc.Tests | 55 | **60** |
| YFex.Messaging.Tests | 167 (+2 skipped) | **169** (+2 skipped) |
| YFex.Cqrs.Tenancy.Tests (now tracked) | 11 | 11 (2 adapted) |
| Core 158 (crypto session: 150 → 158), Authorization.Generator 10, NavigatR 129 (+5 known), Rules 64, Rules.Admin 22, Capabilities 28/7/5, Tenancy 27, Web.Data 30, Search 57, Validation 62, Validation.Generator 50 | = | = |

### Phase 4a — DONE 2026-10-05

Decisions A1–A5 confirmed 2026-10-04 (README). Backups: `C:\Temp\authz-backup\phase4a\`. **Process slip:** the first Rules edits
(Directive, DirectiveWriter, Verdict, Evaluator, Explanation, TableContracts, OverrideTokens) were made before the backup; their
pre-edit copies were rebuilt by reversing the edit script (`C:\Temp\yfex-edits\phase4a_backup_reconstruct.py`) — exact, because
the script only did literal replacements plus one inserted `using`. Coordination: the cryptography session was migrating
`Messaging.Rpc/EventChannel.cs` (C4) at the same time; files were split by message (it: EventChannel, Messaging.Rpc csproj,
EventChannelAudienceTests; this phase: RpcResultFormatters, RulesStage, CheckoutAggregate, RulesStageTests, WireFormatTests).

| File | Change |
|---|---|
| Rules `Directives/Directive.cs`, `DirectiveWriter.cs` | `LimitDirective.ApprovalPermission : Permission?`, `ApprovalDirective(Permission, …)`; `Limit/LimitRaw(…, Permission? permission)`, `RequireApproval(Permission, …)` |
| Rules `Decisions/Verdict.cs` | `ApprovalRequest.Permission : Permission`, `Requires(in Permission)` |
| Rules `Engine/Evaluator.cs`, `Tracing/Explanation.cs`, `Definition/RuleSetDefinition.cs`, `Approvals/OverrideTokens.cs` | typed internally; the fingerprint hashes the same id bytes; catalog/explanation keep the id text; tokens compare `.Value` (moved in 4b) |
| Rules `Tables/TableContracts.cs` | `TableSchema.PublishPermission : Permission?` |
| Rules.Generator `TableGen.cs`, new `PermissionArguments.cs`, `Diagnostics.cs` (YFR0108), `AnalyzerReleases.Unshipped.md` | permission arguments become typed expressions: a constant id → `Permission.Parse("id")`; `Catalog.Ids.X` of a catalog **in the same compilation** (its `Ids` does not exist while generators run) → the catalog accessor `global::Ns.Catalog.X` read from syntax; unresolvable → **YFR0108 error** (it used to become null silently, turning "needs approval" into "denied"). Row data naming an approval permission: `CheckRow` rejects undeclared ids (`authorization.permission.unknown`), evaluation of a malformed id denies (fail closed) |
| Rules.Dynamic `Compiler/RuleCompiler.cs`, `DynamicRuleCodes.cs`, `Runtime/CompiledRule.cs` | ids resolved against declared catalogs at compile (publish and load); unknown → `authorization.permission.unknown`; `CompiledRule.Permission : Permission?` |
| Cqrs `Runtime/RulesStage.cs`; Messaging.Rpc `Serialization/RpcResultFormatters.cs` | adapt to the typed request; the wire still carries the id (unchanged bytes) |
| Authorization `.Public/ResourceRequirement.cs`, runtime `PolicyEvaluator.cs` | new `IResourcePermissionRequirement<T>` (`Permission? PermissionFor(resource)`, null = none) and `ResourceRequirement.DenialFor`: a denial names the permission the resource requires (`MissingPermission`) instead of an opaque `ResourceDenied` |
| Rules.Admin `RuleAdminAuthorization.cs` (new) | `RuleAdminPermissions` catalog (`rules.tables.publish`, `rules.parameters.publish`, `rules.dynamic.publish`, `rules.override`, `rules.view`); `RuleAdminRequirements` (publish table/parameters/rule/override + simulate), `RuleAdminViewRequirement` (5 queries), `ApproveDecisionRequirement`; `AddYFexRuleAdminRequirements()` (fails at startup on an undeclared table permission) |
| Rules.Admin `RuleAdminSetup.cs`, `RuleAdminHandlers.cs`, csproj | baseline verbs `.RequireAuthenticated().Authorize<…>()`; requirements registered by `AddYFexRulesAdmin` and `AddYFexRuleSetSync`; typed `RuleAdminOptions`; `Demand`/`DemandView`/`PermissionFor` deleted; publish actor and approver = actor **subject** (was `Identity.Name`); `ApproveDecision` binds the canonical id; Authorization generator analyzer |
| Tests | Rules.Tests `RulesTestPermissions` catalog (+ generator ref), Sale/HandSaleRules/DomainNeutral/EngineScenario/GeneratedScenario adapted; Rules.Generator.Tests +2 (YFR0108, typed/accessor emission); Rules.Admin.Tests `PricingPermissions` catalog, `TestPrincipal` with a real `PermissionSet`, +5 `RuleAdminAuthorizationTests`, the rejected-audit assertion moved to "gate denies, sink sees nothing"; Authorization.Tests +1 (resource permission requirement); Messaging `CheckoutPermissions` catalog, `RulesStageTests`; Messaging.Rpc `WireFormatTests` |

**Behaviour changes (release note):** (1) Rules APIs take `Permission` (catalog properties in code, `Catalog.Ids.X` in attributes);
string literals no longer compile. (2) Dynamic rules and table rows naming an undeclared permission are rejected at publish.
(3) RuleAdmin permission checks moved from handlers to the message gate: search/AI/UI visibility follows them, a denial names the
missing permission, and an unauthorized publish no longer reaches `IRuleAuditSink.OnRejectedAsync` (it is an
`AuthorizationEvents.Denied` event). (4) `RuleAdminOptions` permissions are typed. (5) `PublishedBy` and the approver id are the
actor subject (same value when the name claim was the subject).

**Deviations**

1. `IResourcePermissionRequirement<T>` + `ResourceRequirement.DenialFor` added to `.Public`/runtime (not in the design) so option- and
   table-driven RuleAdmin permissions deny with the permission name, like static ones.
2. All RuleAdmin permissions are DI requirements (also the fixed ones of 04 §2.5's sketch) because they stay remappable through
   `RuleAdminOptions` (08 "typed option overrides"); clients register them too (`AddYFexRuleSetSync`).
3. Rules.Generator resolves same-compilation catalog constants from syntax (same gotcha the Authorization generator solved in Phase 1).

| Suite | Before | After |
|---|---|---|
| YFex.Authorization.Tests | 76 | **77** |
| YFex.Rules.Tests | 65 | 65 |
| YFex.Rules.Generator.Tests (now tracked) | 21 | **23** |
| YFex.Rules.Dynamic.Tests / Rules.Data.Tests (now tracked) | 20 / 15 | 20 / 15 |
| YFex.Rules.Admin.Tests | 22 | **27** |
| YFex.Messaging.Tests | 169 (+2 skipped) | 173 (+2 skipped) — the +4 are the cryptography session's EventChannel tests |
| YFex.Web.Data.Tests | 30 | 51 — the cryptography session's C2 work |
| Core 158, Authorization.Generator 10, AspNetCore 8, Messaging.Rpc 60, NavigatR 129 (+5 known), Capabilities 28/7/5, Tenancy 27, Search 57, Cqrs.Tenancy 11, Validation 62, Validation.Generator 50 | = | = |

### Phase 3b — DONE 2026-10-15

Decisions S1–S6 confirmed by the user the same day (README; S3 = configurable time policy, the user's answer). Backups:
`C:\Temp\authz-backup\phase3b\`. No other session had touched YFex.Installations, YFex.Authorization or Messaging.Rpc that day (mtime
check); YFex.Offline had (its Phase 2), so **Offline was not edited**.

| File | Change |
|---|---|
| **new** `YFex.Authorization.Installations` (Authorization repo) | `AuthorizationBundleKinds` (`authorization.snapshot`, `authorization.publishers`, publisher `"authorization"`, `SnapshotStream(subject, scope)` = prefix + hex(SHA-256)[..32]); `PermissionSnapshotCodec` (canonical, UTF-8 text bytes, sorted, pinned); `IPermissionSnapshotIssuer` + checked-in proxy `ActualLabProxies/IPermissionSnapshotIssuerProxy.cs`; `SignedPermissionSnapshots` (`AcceptAsync`, `TryRestoreAsync`, `RestoreAndApplyAsync`; native `union SnapshotRestore(SnapshotRestored, SnapshotMissing, SnapshotRefused)`, `SnapshotRefusal`); `ISnapshotTimePolicy` + `SnapshotTimePolicy.Refuse` (default) / `AllowFlagged` / `RefuseWhenUncertain` / `AllowFlaggedWhenUncertain`; `SignedSnapshotRefresh` (hosted); `peers.AuthorizationBundles()`, `AddYFexSignedPermissionSnapshots()` |
| **new** `YFex.Authorization.Installations.Central` | `PermissionSnapshotSigner` (+ `PermissionSnapshotSigningOptions`: 72 h, 1 h–14 d, ring `authorization/snapshots`); `PermissionSnapshotIssuerService`; `AuthorizationPublisherEndorsement`; `AddYFexPermissionSnapshotIssuer()`, `CentralInstallationsBuilder.PullableAuthorizationBundles()` |
| **new** tests `YFex.Authorization.Installations.Tests` | 34: acceptance (offline start within/after validity; tampered permission, extended validity, other subject; device clock set back), audience, roster-key refusal, missing endorsement, floors, rotation, time policies, issuer service with `CallContext` × `InstallationCallContext`, refresh, composition, codec golden, proxy golden |
| YFex.Installations `Bundles/PublisherKeySet.cs` (new) | generic `PublisherKeySet` (payload byte-identical to Offline's `OfflinePublisherKeySet`) + `EndorsedPublisherKeys : IPublicKeyDirectory` |
| YFex.Installations `DependencyInjection/InstallationPeersBuilder.cs` | `Publishers(...)` is **additive** (was: last call wins; Offline worked around it with `otherPublishers`) |
| YFex.Installations.Central `PublisherEndorsement.cs` (new) | generic `IBundlePublicationStore`, `InMemoryBundlePublicationStore`, `PublisherEndorsement` (= Offline's `OfflinePublisherEndorsement`, parameterized by kind) |
| YFex.Installations.Central `DependencyInjection/CentralInstallationsServiceCollectionExtensions.cs`, `CentralEnrollmentService.cs` | `PullableBundles(...)` **additive** + an overload built from the container; `CentralInstallations.TryGetBundleKey(tenant)`; internal `CentralEnrollmentService.Authority` |
| YFex.Installations tests | `PublisherEndorsementTests` (+3, Central.Tests 22 → 25), `PublishersBuilderTests` (+1, Installations.Tests 124 → 125) |
| Messaging.Rpc `CallContext/CallContextClientHeaders.cs` (new), `FusionMessageBus.cs` | the bus's header building moved to the public `CallContextClientHeaders` (`GetAsync`, `CallAsync`) so plain proxy calls carry token, scope and the installation assertion; the bus delegates (same headers) |
| Messaging.Rpc.Tests `CallContextClientHeadersTests.cs` (new) | +2 |
| Docs | README (S1–S6, status), 05 notes, this file, repo `YFex.Authorization/README.md`, `YFex/docs/index.md` untouched (no new public guide), offline 11 §2 rows, installations 05 note |

**Behaviour changes (release note):** (1) `InstallationPeersBuilder.Publishers` and `CentralInstallationsBuilder.PullableBundles` add
instead of replacing (an app that called them twice on purpose to override now gets both). (2) `FusionMessageBus` header building is
unchanged in content; it now lives in `CallContextClientHeaders`. Everything else is new API.

**Deviations**

1. **Generic endorsement in YFex.Installations** instead of a second copy of Offline's code in Authorization (S1). Offline still uses
   its own types; the payloads are byte-identical, so moving Offline onto `PublisherKeySet`/`EndorsedPublisherKeys`/`PublisherEndorsement`
   is a mechanical follow-up for the Offline owner.
2. **Version = issue time in Unix ms** (S5), not the permission version: `InMemoryPermissionVersions` restarts at 0, which would put
   every snapshot after a server restart below the installation's floor. Several central nodes with skewed clocks can issue a lower
   version than an installation holds; it is refused (`OlderThanFloor`) and the newer copy stays, until the next refresh.
3. **Validity is one central option** (72 h). Offline's per-site `OfflineLimitKeys.PermissionSnapshotValidity` (admin value in the
   manifest) is not read yet: Offline Phase 3 should pass it to the signer (a per-installation validity hook), clamped to 1 h–14 d.
4. **`SignedSnapshotRefresh` only implements `IAsyncDisposable`** (like `RpcCentralBundleRefresh`): containers must be disposed async
   (the generic host does).
5. The snapshot kind is **not gossiped** (audience = one installation); only the endorsement is.

**Open items**

- Offline Phase 3: call `RestoreAndApplyAsync(subject, scope, accessor)` from offline sign-in; feed the per-site validity (deviation 3).
- Offline: move to the generic endorsement types (deviation 1).
- `YFex.Installations.Windows.Tests` `Mutual_TLS_13_with_the_terminal_transport_key_in_the_TPM` failed once with a socket error when
  run in parallel with the other Installations suites (port contention), 6/6 when run alone twice. Not related to this phase.

| Suite | Before | After |
|---|---|---|
| YFex.Authorization.Installations.Tests (new, tracked from now) | — | **34** |
| YFex.Messaging.Rpc.Tests | 76 | **78** |
| YFex.Installations.Tests / Central.Tests | 124 / 22 | **125 / 25** |
| YFex.Installations.Rpc.Tests 13, Windows.Tests 6, AotSmoke (publish 0 warnings, exit 0) | = | = |
| YFex.Authorization.Tests 77, AspNetCore 8, Generator 10, Messaging 175 (+2 skipped), Offline 36 | = | = |

### Phase 4b — DONE 2026-10-16

Decisions A1–A9 (README; A9 "v1 is dropped" re-confirmed by the user on 2026-10-16). Backups: `C:\Temp\authz-backup\phase4b\`
(every pre-existing file below, plus the deleted v1 files and golden). No other session had touched these trees since the 3b session
(mtime check). Edit scripts: `C:\Temp\yfex-edits\phase4b_*.py`. **Process note:** Git Bash heredocs collapse `\\` to `\`; one
script wrote a literal NUL into `Evaluator.cs` (fixed at once); later scripts were written with the editor tool.

| File | Change |
|---|---|
| core `Helpers/StableHash64.cs` | `Create(seed, IBufferWriter<byte> capture)`: copies every appended byte to `capture` (one null check per append otherwise); the 64-bit value is unchanged |
| Rules `Decisions/Decision.cs`, `Engine/Evaluator.cs` | `Decision.Digest` (`Hash256`): SHA-256 of `"yfex.rules.decision.v1" ‖ 0 ‖ rule set name ‖ the exact outcome stream the fingerprint hashes` (captured), only when the decision has approvals. Trace and digest share one lazily-allocated slot, so ordinary decisions allocate nothing more (the allocation budget test caught the first, 32-byte version) |
| Rules `Approvals/OverrideTokens.cs` (**deleted**), `Approvals/OfflineApprovalCap.cs` (new), `Approvals/Enforcement.cs` | v1 token family gone; `OfflineApprovalCap(Permission, RawValue MaxRequested)` + `Allows`/`AllowedBy` (age moved to Authorization); `EnforceOptions.OfflineCaps`; `IDecisionBound` moved out |
| Rules.Tests `HandWritten/ApprovalTokenGoldenTests.cs`, `ApprovalTokenTests.cs`, `Golden/approval-token-v1.tsv` (**deleted**), `HandWritten/DecisionDigestTests.cs` (new) | digest determinism/sensitivity, no digest without approvals, offline cap |
| Authorization.Public `Approvals.cs` (new) | `ApprovalTokenKind {Server, Device, Site}`, `ApprovalToken` (kind, approver subject, approver scope, permission, digest, `ValidityPeriod`, key id, `InstallationId`, signature), `IApprovalBound`, `ApprovalDigest.Of(type, string/Guid/long/ICanonicalValue)`, `ApprovalGrant`, `IApprovalTokenIssuer`, `ApprovalExpectation` (+ `ApprovalOrigin`: deciding installation and decision time), `ApprovalCheck`, `ApprovalFlags`, `ApprovalVerification`, `IApprovalTokenVerifier`, `IPermissionDirectory` + `PermissionDirectoryAnswer`, `ApprovalPolicy` + `IApprovalPolicySource`, `IApprovalOriginAccessor`. Still a leaf (core + BCL, LeafGuardTests green) |
| Authorization runtime `YFex.Authorization.csproj`, `Approvals/*.cs` (new) | references YFex.Cryptography (A3). `ApprovalDomains.Version2` (`yfex.approval.v2`), `ApprovalTokenCodec.Body` (canonical, pinned), `ServerApprovalTokens` (HMAC on the ring, reload on unknown key, `FromProvider`/`WithProcessKey`), `IApprovalSignatureVerifier`, `ApprovalTokenVerifier` (kind → permission → digest → signature → validity / offline age at decision time → installation → scope → separation of duties → approver re-check), `ApprovalOptions` (15 min, 1 min–24 h; `AllowOffline`; `WhenNoDirectory`/`WhenUnknownApprover`/`WhenDirectoryUnavailable`), `OfflineApprovalPolicy`, `UnverifiedApproverAction`/`Reason`, `IUnverifiedApproverHandler`, `ApprovalEvents` (`Issued`, `Accepted`, startup warning `NoDirectory`), `AddYFexApprovals()` |
| Authorization.Installations `DeviceApprovalTokenIssuer.cs` (new) | device tokens signed with the evidence `ISigningKey` through `SignedPayload` |
| Authorization.Installations.Central `DeviceApprovalSignatures.cs` (new) | `DeviceApprovalSignatures` (installation's evidence keys from `IInstallationKeyDirectory`, verified as of the token's issue time), `InstallationCallApprovalOrigin` (RPC installation assertion), `AddYFexDeviceApprovalVerification()` |
| Authorization.Testing `FakePermissionDirectory.cs` (new) | grants per subject/scope, `Unavailable`, question counter |
| Cqrs `IDecisionBound.cs` (new), `Error.cs`, `Runtime/ApprovalStage.cs` (new), `Runtime/RulesStage.cs`, `Configuration/CommandConfigurationBuilder.cs` | `IDecisionBound : IApprovalBound` in YFex.Cqrs; `ApprovalRequired.DecisionDigest` + `Error.ApprovalRequired(approvals, fingerprint, digest, violations)`; `.RequireApproval(permission, bind, separationOfDuties = true)` for `string`/`Guid`/`long`/`ICanonicalValue` (both command builders); `ApprovalStage<TMessage>` rides the rules slot (`ApprovalStages.Combine`: after the rules when both are configured); rules approvals verified through `IApprovalTokenVerifier` against `Decision.Digest`, offline tokens also need `EnforceOptions.OfflineCaps`; policy sources applied; `RulesStage.SituationType` nullable; `IApproverDirectory` deleted |
| Messaging.Rpc `Serialization/RpcResultFormatters.cs` | **wire v5**: `ApprovalRequired` carries the digest (v4 payloads read with a zero digest); `ApprovalTokenFormatter`, `PermissionFormatter`, `Hash256Formatter` registered |
| **new** `YFex/src/Cqrs/YFex.Cqrs.Approvals` (+ `.Tests`), `yfex.slnx` | `Approvals.Commands.Approve(Permission, Hash256) : ICommand<ApprovalToken>` (RPC contract generated), `ApproveRequirement`, `ApprovalsBaseline`, `ApproveHandler` (refuses anonymous, impersonating/delegated callers and a zero digest), `AddYFexCqrsApprovals()`, `AddYFexApprovalRequirements()`, `AddApprovals()` |
| Rules.Admin `RuleAdmin.cs`, `RuleAdminHandlers.cs`, `RuleAdminSetup.cs`, `RuleAdminAuthorization.cs`; tests `AdminFixture.cs`, `PublishTests.cs` | `ApproveDecision`, its wire `ApprovalToken`, handler and requirement deleted (the test moved to Cqrs.Approvals.Tests) |
| Messaging.Tests `Fixtures/CheckoutAggregate.cs`, `Cqrs/RulesStageTests.cs`; Messaging.Rpc.Tests `WireFormatTests.cs` | v2 tokens over the decision digest; offline device approval of a rule limit capped and flagged; digest and token wire round trips |
| Authorization.Tests `ApprovalTokenTests.cs`; Authorization.Installations.Tests `DeviceApprovalTests.cs`; core `StableHash64Tests.cs` | server tokens, ring reload, scope containment, pinned body and digests, options bounds; real-key device tokens (F7 replay, decision-time age, other installation, revoked/foreign keys, tampering, A8 actions, custom handler, Site refused, DI); capture |
| YFex.Cryptography.Tests `ArchitectureTests.cs` | allow-list entry of `OverrideTokens.cs` removed (the file is gone) |
| `C:\Temp\yfex-edits\run-tests.sh` | tracks `CqrsApprovals` and `Cryptography` |

**Behaviour changes (release note):** (1) Approval tokens are v2 only: `OverrideToken*`, `HmacOverrideTokens`, `DeviceOverrideToken*`,
`IDeviceKeyStore` and Rules' `ApprovalCheck`/`ApprovalVerification` are gone; nothing in flight was affected (A9). (2) `IDecisionBound`
moved from YFex.Rules to YFex.Cqrs and carries `IReadOnlyList<ApprovalToken>`. (3) RuleAdmin `ApproveDecision(permission, fingerprint)`
is replaced by `Approvals.Commands.Approve(permission, digest)` (YFex.Cqrs.Approvals). (4) RPC wire v5. (5) `IApproverDirectory` →
`IPermissionDirectory` (scope-aware, four answers); without one, device tokens are refused (`WhenNoDirectory`). (6) A device token is
accepted only from the installation that decided the operation, within `OfflineApprovalPolicy.MaxAge` of its issue measured at the
decision time, with an offline amount cap for rule limits, and always flagged. (7) The approver may not be the requester unless the
policy opts out. (8) `[MemoryPackable]` commands that carry approvals annotate the member `[MemoryPackAllowSerialize]` (formatters live in
Messaging.Rpc, the leaf cannot reference MemoryPack).

**Deviations**

1. **`.RequireApproval` rides the rules slot** (`ApprovalStage<TMessage> : RulesStage`, combined after the rules stage) instead of a
   new verdict case in the gate: the three dispatchers (LocalDispatcher, ValidatingHandlerInvoker, Wolverine) needed no change and the
   answer is the same `Error.ApprovalRequired` as rule approvals. The gate stays a pure permission check.
2. **Rules digest by capture:** `StableHash64` got an optional byte capture so the digest hashes exactly the fingerprint's canonical input
   (no second serializer to keep in sync). Domain + rule set name prefix it.
3. **Device verification in `.Installations.Central`** (needs the central `IInstallationKeyDirectory`), issuer in `.Installations`
   (store side); the deciding installation comes from the RPC assertion until Offline Phase 3 supplies `Provenance`.
4. **Site tokens** (`ApprovalTokenKind.Site`) exist in the enum but are refused (`UnsupportedKind`) until Offline Phase 4 designs them.
5. **Approval-only flags** reach `IDecisionAuditSink` with the `ApprovalPolicy` as the "decision" (there is no rules decision).
6. `Approve` refuses callers acting for someone else (`ActorRelation != None`) now; 06 §3 had listed "issuing approvals" among the
   impersonation restrictions of Phase 6.

**Open items**

- 4c: Rules.Admin approval-policy table as an `IApprovalPolicySource` (A7).
- Offline Phase 3: an `IApprovalOriginAccessor` for replay (envelope installation + `Provenance.DecidedAt`) and the review queue behind
  `UnverifiedApproverAction.Defer` (today it refuses with `ApproverDeferred`).
- Offline Phase 4: site tokens.
- Unrelated failures seen in the full run: `YFex.Installations.Tests` `MeshAcceptanceTests.Bundles_from_a_friend_are_verified_like_bundles_from_central`
  failed once in the parallel run, passed twice alone (flaky); `YFex.Cryptography.Tests` `CertificateTests.Expiry_not_yet_valid_revocation_and_foreign_roots`
  fails deterministically (`InvalidExtension` where `PartialChain` is expected; test file from 2026-10-05, no code of this phase in its graph).

| Suite | Before | After |
|---|---|---|
| YFex.Authorization.Tests | 77 | **88** |
| YFex.Authorization.Installations.Tests | 34 | **42** |
| YFex.Cqrs.Approvals.Tests (new, tracked) | — | **12** |
| YFex.Tests (core) | 233 | **234** |
| YFex.Messaging.Tests | 175 (+2 skipped) | **176** (+2 skipped) |
| YFex.Messaging.Rpc.Tests | 78 | **79** |
| YFex.Rules.Tests | 65 | 65 (3 v1 tests deleted: golden 1 + tokens 2; 3 digest tests added) |
| YFex.Rules.Admin.Tests | 30 | **29** (ApproveDecision test moved) |
| YFex.Cryptography.Tests (now tracked) | — | 138 + 1 unrelated failure (above) |
| Authorization.Generator 10, AspNetCore 8, NavigatR 129 (+5 known), Capabilities 28/7/5, Tenancy 27, Web.Data 51, Search 57, Rules.Dynamic 20, Rules.Generator 23, Rules.Data 15, Cqrs.Tenancy 11, Validation 62, Validation.Generator 50, Installations 125 (1 flaky, above), Installations.Central 25, Installations.Rpc 13, Offline 36 | = | = |

### 2026-10-13 — Note from the Installations I2 rework (not an Authorization phase)

Additive hook in Messaging.Rpc for YFex.Installations.Rpc, made while no Authorization session was running. Please review.
- **The hook:** `CallContextClientBuilder.SendHeader(headerName, provider)`, and `FusionMessageBus.HeadersAsync` sends those headers
  after the token and the scope.
- **Validation:** reserved names (the token header, `yfex-scope`, `yfex-idempotency`) and duplicates throw.
- **Why:** the installation assertion no longer takes the single `SendToken` slot, so per-call actor tokens and installation
  assertions can be combined.
- **Test:** `FusionMessageBus_sends_extra_headers_without_taking_the_token_slot`. Messaging.Rpc.Tests 69 → 70, all green.
- **Server side:** the installation middlewares wrap `CallContextMiddleware` (priority ±5) and refuse through `RpcErrorResults<T>`
  (`Unauthenticated`, or `Forbidden(Denial(ResourceDenied, "installation.scope"))`).
- **Backup:** `C:\Temp\installations-backup\I2-review-2026-10-09\messaging-rpc`. Details in installations 08 §5.


### 2026-10-19 — Note from authentication AN-P4 (request R4, additive)

- `AuthorizationContext` now derives `RealActor` + `ActorRelation.UserImpersonation` from the principal's `ClaimsIdentity.Actor` when
  the caller did not pass them (`AuthorizationContext.RealActorOf`): an impersonation token's `act` reaches audit, logging and the
  gate without host code. Same subject as the actor = none.
- `ActorScope.From(caller)` carries the principal's `sid` claim as `SessionId`, so desktop hosts (`AmbientPrincipalAccessor`) publish
  the session in `ActorContext.Session` like the server adapters.
Test `Claims_identity_actor_becomes_the_real_actor_of_an_impersonation`. Authorization 151 tests green. R1–R3, R6 (P5) and R7 stay open.


### 2026-10-19 — Note from authentication AN-P5 (requests R1–R3, R6; R7 still open)

The user allows changing other libraries while they are not in production, with their design docs aligned. Backups:
`C:\Temp\authentication-backup\P5\other` (tar of this repo and of `YFex/src/Cqrs` + `YFex/src/Messaging` before the change).

- **R1:** `Requirement` case `RecentAuthentication` (`RequirementKind = 8`, payload `RecentAuthenticationRequirement(MaxAge, Minimum)`,
  `Requirement.RequireRecentAuthentication`), compiled as an identity requirement (needs authentication, kept in `Remaining`, sync).
  `PolicyEvaluator(handlers, TimeProvider? time)` checks it against `IAuthorizationContext.AuthenticatedAt`/`Assurance`
  (`AuthenticationFacts` reads `auth_time`/`acr` of the authenticated primary identity). Visibility ignores it. `AssuranceLevel` moved
  here from Authentication.Public (03 §4 note).
- **R2:** `DenialKind.ReauthenticationRequired = 8`, `Denial.RecentAuthentication` + `Denial.ReauthenticationRequiredOf(…)`. Cqrs:
  `ErrorKind.ReauthenticationRequired = 9`, case `ReauthenticationRequired(RecentAuthenticationRequirement)`, `Error.ReauthenticationRequired(maxAge,
  minimum)`; `Error.From(verdict)` and `new Forbidden(denial)` of that kind give the new case (payload = the boxed `Denial`). RPC
  `RpcResultFormatters` v7. HTTP: `AuthorizationEndpointFilter.Problem` answers 401 with the RFC 9470 challenge. Validation's VM
  generator maps it to `server.reauthenticate`; Offline central counts it as `Unauthorized` when a fact is forced.
- **R3:** `.RequireRecentAuthentication(TimeSpan, AssuranceLevel)` on `RequirementBuilder<T>` and the Cqrs command/query configuration
  builders. `TestCallerBuilder.AuthenticatedAt(at, assurance)` for tests.
- **R6:** `IRoleDirectory.IsInAnyRoleAsync(subject, scope, roles)` answering `PermissionDirectoryAnswer` (no default implementation,
  like `IPermissionDirectory`); `FakeRoleDirectory` in `.Testing`.

Tests: Authorization **156** (+5: `RecentAuthenticationTests` 4, AspNetCore step-up challenge 1), Messaging.Rpc 81 (+1 wire v7),
Messaging 185 + 2 skipped (+1 Cqrs gate), Cqrs.Approvals 12 — all green.

### Phase 6 + Z20 site presence — DONE 2026-10-29

Started early because they unblock YFex.Authentication (R7 security gap; R8 needed by AN-P7). 4c and Phase 5 not started. Backups:
`C:\Temp\authz-backup\phase6\`; edit scripts `C:\Temp\yfex-edits\z14_*.py`, `z20_*.py`. No touched code file had changed that day
(checked mtimes) — no other session in Cqrs/Messaging/Web/Tenancy.

**Caller syntax and consumer effect (public API changes)**

```csharp
// Z20 — per command, optional; the claim producer (presence_at) comes with AN-P7 / I6
b.Command<OpenCashDrawer>().RequirePermission(PosPermissions.DrawerOpen).RequireSitePresence(TimeSpan.FromMinutes(10));
b.ForEndpoint<OpenDrawerRequest>().RequireSitePresence(TimeSpan.FromMinutes(10));
case ErrorKind.SitePresenceRequired when error.TryGetValue(out SitePresenceRequired p): await presence.ProveAsync(p.Requirement.MaxAge); break;

// Phase 6 — nothing to call for HTTP (AddYFexAuthorization replaces the Tenancy gate); apps may add restrictions
services.AddYFexAuthorization(a => a.RestrictWhileImpersonating(SalesPermissions.RefundIssue));
// RPC parity is opt-in
services.UseYFexMessagingRpcServer(rpc => rpc.CallContext(ctx => ctx.FromConnection().OrFromHeader().AllowImpersonation()));
```

Consumer effect: `Denial` gained a trailing optional `SitePresence` (positional callers unchanged); `CallIdentity` ctor gained an
optional `impersonation`; `ErrorKind` gained 10, `RequirementKind` 9, `DenialKind` 9 (exhaustive switches over them should add the
case — Offline central and the Validation VM generator were updated); RPC wire **v8** (v7 still read); `YFex.Authorization` now
references `YFex.Tenancy.Resolution` (contracts only; no cycle). Hosts that relied on `PermissionTenantImpersonationGate` get the
policy gate automatically (a custom gate registered by the app is kept). AOT: no reflection; `.Public` stays a leaf.

| File | Change |
|---|---|
| Authorization `.Public/RecentAuthentication.cs` | `AuthenticationFacts.PresenceAtClaimType` (`presence_at`), `PresenceAtOf` (shared Unix-seconds parser); `SitePresenceRequirement(MaxAge)` (1 min future skew, unknown fails closed) |
| `.Public/Requirement.cs` | case `SitePresence = 9`: ctor, `TryGetValue`, implicit, `RequireSitePresence(TimeSpan)`, `SitePresence` accessor |
| `.Public/AuthorizationVerdict.cs` | `DenialKind.SitePresenceRequired = 9`; `Denial.SitePresence`; `SitePresenceRequiredOf`, `RestrictedWhileImpersonatingOf` |
| `.Public/IAuthorizationContext.cs` | default members `PresenceAt`, `Restricted` |
| `.Public/AuthorizationBuilder.cs`, `CompiledPolicy.cs` | `.RequireSitePresence`; compiled as an identity requirement (needs authentication, kept in `Remaining`, sync) |
| `.Public/Impersonation.cs` (new) | `AuthorizationPermissions` catalog (hand-written generated shape: `tenant.impersonate`, `user.impersonate`, `authorization.grants.issue`, `authorization.roles.manage`); `ImpersonationRestrictions` (ids, lazy `Resolve()`, `Default`/`None`/`With`) |
| Authorization `PolicyEvaluator.cs` | presence check (visible); a missing permission that is in `caller.Restricted` while `Relation != None` → `RestrictedWhileImpersonating` (denial path only) |
| `AuthorizationContext.cs` | `PresenceAt`, `Restricted` (ctor `restricted`), `ForTenantImpersonation(caller, homeScope, restrictions)` |
| `PolicyTenantImpersonationGate.cs` (new) | `tenant.impersonate` through `AuthorizationContextCache` in the scope in effect (home); refuses a caller already acting for someone |
| `AuthorizationEvents.cs` | `ImpersonationEvent`, `ImpersonationStarted` (`authorization.impersonation.started`, never rate-limited), `EmitImpersonationStarted` |
| `ServiceCollectionExtensions.cs`, csproj | registers restrictions + policy gate (removes `PermissionTenantImpersonationGate` registrations before/after); `RestrictWhileImpersonating`, `UseImpersonationRestrictions`; reference `YFex.Tenancy.Resolution` |
| `.AspNetCore` middleware, `HttpContextPrincipalAccessor`, endpoint filter | `TenantImpersonation` feature → impersonating caller for the whole request + event `http`; 403 `SitePresenceRequired` with `maxAge` |
| `.Testing/TestCaller.cs` | `PresentAt(at)` |
| Tenancy.Resolution `TenantImpersonation.cs` (new) | host-neutral record of a successful switch (real user, target, home tenant, home scope) |
| Web `TenantResolutionMiddleware.cs` | home tenant set before the gate is asked; records the `TenantImpersonation` feature (home scope from `IScopeContext`) |
| Messaging.Rpc `CallContext.cs`, `CallContextConnections.cs`, `CallContextOptions.cs`, `CallContextMiddleware.cs` | `CallIdentity.Impersonation`; captured from the upgrade request; `AllowImpersonation()` (implies `WithTenant`); resolver asks the gate in the home scope, else runs in the home scope; event `rpc` per resolved identity |
| Messaging.Rpc `RpcResultFormatters.cs` | wire v8: `SitePresenceRequired` + max age ticks |
| Cqrs `Error.cs`, `Command/QueryConfigurationBuilder.cs` | `ErrorKind.SitePresenceRequired = 10`, case `SitePresenceRequired`, `Error.SitePresenceRequired(maxAge)`; `.RequireSitePresence` on the three builders |
| Validation.Generator `Emitter.Vm.cs` | `server.presence` |
| Offline `OfflineCentralRpc.cs` | `SitePresenceRequired` counts as `Unauthorized` |
| Authentication `AotSmoke/Program.cs` | presence allow/deny + impersonation restriction checks |

**Deviations**

1. 06 §3 sketch had `contexts.GetCached(actor)`; the gate uses `AuthorizationContextCache.GetAsync` (completes synchronously on a hit).
2. Restrictions are carried on the caller (`IAuthorizationContext.Restricted`), not given to the evaluator, so every surface (HTTP,
   RPC, desktop) reports `RestrictedWhileImpersonating` without configuration. Restricted = withheld ∩ what the caller held; a
   permission the caller never had still reports `MissingPermission`.
3. The home scope reaches the real actor through the `TenantImpersonation` feature (`HomeScope` text), because the Authorization
   middleware runs after the tenant switch.
4. "Issuing approvals" is not in `ImpersonationRestrictions.DefaultIds`: `YFex.Cqrs.Approvals` already refuses `Approve` while
   impersonating (4b deviation 6).
5. RPC impersonation is opt-in (`AllowImpersonation()`): without it an impersonating identity runs in its home scope (fail closed).
6. A future `presence_at` beyond 1 min skew fails closed (`SitePresenceRequirement.ClockSkew`).

**Acceptance:** Tenancy.Tests 27 (≥ 27), Web.Data.Tests 52 (≥ 30); role-granted `tenant.impersonate` passes (theory, both
registration orders), legacy claim canonicalizes, chained `act` refused; restricted permission → `RestrictedWhileImpersonating`
(unit, HTTP 403, RPC round-trip); real actor + home scope in `ActorContext` after an `await` in an HTTP endpoint and in the RPC
handler; RPC parity incl. gate refusal and `AllowImpersonation()` absent. Z20: fresh presence allows; missing/old/unparseable/negative/
far-future denies with `SitePresenceRequired` (+ max age; Cqrs, RPC v8, HTTP 403); visibility unchanged. AOT:
`YFex.Authentication.AotSmoke` publishes native win-x64 with 0 IL2xxx/IL3xxx warnings and passes.

| Suite | Before | After |
|---|---|---|
| YFex.Authorization.Tests | 93 | **101** (+3 `SitePresenceTests`, +5 `ImpersonationTests`) |
| YFex.Authorization.AspNetCore.Tests | 9 | **12** |
| YFex.Messaging.Rpc.Tests | 81 | **87** (+4 impersonation, +2 wire) |
| YFex.Messaging.Tests | 185 (+2 skipped) | **186** (+2 skipped) |
| YFex.Web.Data.Tests | 51 | **52** |
| Authorization.Generator 10, Authorization.Installations 44, Tenancy 27, Cqrs.Approvals 12, Validation 62, Validation.Generator 50 | = | = |

**Open items:** the `presence_at` producer (AN-P7 / I6); user impersonation (Phase 7, with its feature); a Web `IScopeContext`-aware
`HomeScope` is only filled when the host registers one (YFex.Web does).

### Phase 5 — DONE 2026-10-29

Backups: `C:\Temp\authz-backup\phase5\` (tars of YFex.Capabilities, YFex.Search, YFex.Authorization + the single YFex files below);
edit scripts `C:\Temp\yfex-edits\p5_*.py`. No touched code file had changed since the Phase 6 session the same afternoon (checked mtimes).

**Caller syntax and consumer effect (public API changes)**

```csharp
// Capabilities — before: one permission per key
reg.Authz.TryGet(key, out Permission required);
var agent = new CapabilityAgentApi(reg.Describe, reg.Authz, evaluator, localizer);
if (agent.CanInvoke(key, caller).Value is ActionDenied d) Show(d.Missing);

// after: the compiled policy per key, the gate is a [Union] struct with a NeedsApproval case
reg.Policies.TryGet(key, out CompiledPolicy policy);                     // PoliciesFor(identity) replaces AuthzFor
var agent = new CapabilityAgentApi(reg.Describe, reg.Policies, evaluator, localizer);
var gate = agent.CanInvoke(key, caller);
if (gate.TryGetValue(out ActionDenied denied)) Show(denied.Missing);     // denied.Verdict: Unauthenticated | Forbidden(Denial)
if (gate.TryGetValue(out ActionNeedsApproval approval)) AskApprover(approval.Permission);

// Registry build takes the policy table (DI does it: AddYFexCapabilities reads IPolicyRegistry from AddYFexAuthorization)
CompiledCapabilityRegistry.Build(sources, context, policies: authorizationRegistry);

// View-model commands (⚑Z19) — the dev code is unchanged; the host installs the gate once
using var gate = provider.UseYFexViewModelAuthorization();

// NavigatR — NavigationDenied gained the verdict; text is "Requer: {display name}"
if (result.Value is NavigationDenied { IsAuthorizationDenial: true } d) toast(d.Reason);   // d.Verdict for custom text
navigator.ActivePageDenied += d => toast(d.Reason);                                        // signalled re-check closed the page
```

Consumer effect: `IAuthorizationView`/`AuthorizationView`, `CompiledCapabilityRegistry.Authz`/`AuthzFor` and
`CapabilitySourceBase.ReadRequirePermissionAttribute` are deleted (only Capabilities/Search used them — grep); `ActionGate` changed from
a native `union` of records to a `[Union]` struct (case types are now `readonly record struct`s; `ActionDenied(Permission)` →
`ActionDenied(AuthorizationVerdict)` with `Missing`/`Denial` accessors; `.Value` still boxes for pattern-matching code);
`CapabilityDescriptor` gained trailing optional `PolicyKey`/`Approval`, `SearchEntry` a trailing optional `Policy`, `NavigationDenied` a
trailing optional `Verdict` (positional callers unchanged); `CompiledCapabilityRegistry.Build` now **fails** for a permission hint no
catalog declares (all ids listed); the State generator emits two private members per command (hook + raise helper). AOT: no
reflection added (the attribute reflection is removed), the gate is static + weak references, no `MakeGenericType`.

| File | Change |
|---|---|
| Authorization `.Public/ViewModelAuthorization.cs` (new) | `IPolicyRegistry` (`TryGetPolicy(in PolicyKey)`), `IViewModelAuthorization` (`IsVisible`, `Changed`), static `AuthorizationGate` (`Install` → restoring `IDisposable`, `IsVisible`, weak `Watch<TOwner>` posting to the watcher's `SynchronizationContext`, `NotifyChanged`) |
| Authorization `ViewModelAuthorization.cs` (new) | default gate: command policy of `IPrincipalAccessor.Current` via the evaluator; `Changed` on signals concerning the caller and on `AmbientPrincipalAccessor.Changed` |
| Authorization `CompiledAuthorizationRegistry.cs` | implements `IPolicyRegistry` (method already existed) |
| Authorization `ServiceCollectionExtensions.cs` | registers `IPolicyRegistry`, `ViewModelAuthorization`/`IViewModelAuthorization`; `UseYFexViewModelAuthorization()` |
| Authorization `AmbientPrincipalAccessor.cs` | `event Action? Changed` raised by `SignIn`/`SetCurrent`/`SignOut` |
| Authorization.Generator `AuthorizationGenerator.cs`, `Model.cs`, `AuthorizationSymbols.cs` | pipeline C: per view model, `__OnCanExecute_<Command>` implementations (`AuthorizationGate.IsVisible(in key)`), static `PolicyKey` fields, weak `Watch` on first ask → `__RaiseCanExecuteChanged_<Command>()`; only for `[StateCommand]`+`[RequirePermission]` on a top-level, non-generic, partial type deriving from `StateObject` **of the YFex.State assembly** (`IsStateLibraryViewModel`) |
| State.Generator `CommandEmitter.cs` | per command: `partial void __OnCanExecute_<Name>(ref bool)` + `__RaiseCanExecuteChanged_<Name>()`; async and sync `CanExecute()`/`Execute` ask the hook (a refused command does not run or change `Status`); sync commands gained `NotifyCanExecuteChanged` |
| Capabilities `ActionGate.cs` | `[Union]` struct: `ActionGateKind`, cases `ActionAllowed`/`ActionNeedsConfirmation`/`ActionNeedsApproval(ApprovalPolicy)`/`ActionDenied(AuthorizationVerdict)`, typed `TryGetValue` |
| Capabilities `Agent.cs` | `CanInvoke` = `IsVisible(policy)` (hidden → `Authorize` for the reason) → declared approval → irreversible confirm → allow; fixed ctor takes `ICapabilityPolicies` |
| Capabilities `Registries.cs`, `Sources.cs`, `CompiledRecords.cs`, `CapabilityScope.cs` | `ICapabilityPolicies` replaces `IAuthorizationView`; `SearchEntry.Policy`; `CapabilityDescriptor.PolicyKey`/`Approval` |
| Capabilities `CompiledCapabilityRegistry.cs` | `Build(sources, context, IPolicyRegistry? policies)`: registered policy of the key wins, else the hint compiled as a one-permission policy, resolved once per descriptor (base + scoped) and reused by `PoliciesFor`; undeclared hints listed in one `CapabilityConfigurationException` |
| Capabilities `ViewModelCapabilitySource.cs` | views → `PolicyKey.Route(vm)`, commands → `PolicyKey.Command(vm, method without Async)` |
| Capabilities `Sources/CapabilitySourceBase.cs` | `ReadRequirePermissionAttribute` (reflection) deleted; `policyKey`/`approval` parameters |
| Capabilities `Search/CapabilitySearchService.cs` | filter = `IsVisible(entry.Policy)` (hand-built entries without a policy: hint) |
| Capabilities `DependencyInjection/CapabilityServiceCollectionExtensions.cs` | `Build(…, sp.GetService<IPolicyRegistry>())`; `Policies` projection registered instead of `Authz` |
| Capabilities.Cqrs `CqrsCapabilitySource.cs`, `CqrsCapabilityServiceCollectionExtensions.cs` | `PolicyKey.Message(type)`; hint = builder permission else first permission of the message policy (no attribute reflection); `Approval` = `command.Rules?.DeclaredApproval` |
| Cqrs `Runtime/RulesStage.cs`, `Runtime/ApprovalStage.cs` | `virtual ApprovalPolicy? DeclaredApproval` (approval stage: `Declared`; rules stage: attached approvals) |
| Search.Capabilities `CapabilityCatalogProvider.cs` | filter = `IsVisible(entry.Policy)` |
| NavigatR `Navigator.cs` | `CheckNavigation` → `NavigationDenied(DenialText(verdict), verdict)` ("Requer: {catalog display name}", else `Describe()`); optional `IAuthorizationSignals` subscription (captured `SynchronizationContext`) → `RecheckAuthorizationAsync()` closes a forbidden active page (back to the nearest allowed entry, forward entries released; none → history cleared), `ActivePageDenied` event; unsubscribe on `Dispose` |
| NavigatR `NavigationResult.cs` | `NavigationDenied(Reason, Verdict = default)` + `IsAuthorizationDenial` |
| NavigatR `UnionSupport.cs` (deleted) | stale public `UnionAttribute`/`IUnion` polyfill: collided (CS0433) with System.Runtime's in every assembly that references NavigatR and declares a `[Union]` |
| Capabilities `samples/YFex.Capabilities.AotSmoke` (`Program.cs`, csproj) | real registry + evaluator, generated catalog/rows/hook, search hidden/visible, agent allowed/denied, command gate disabled/enabled; exits 1 on mismatch |
| Tests | Authorization `ViewModelGateTests.cs` (3); Authorization.Generator `GeneratorDriverTests.cs` (+2: hook with an in-memory `YFex.State` assembly, none for source stand-ins); State `Commands/CanExecuteHookTests.cs` (3); Capabilities `PolicyGateTests.cs` (5, collection `AuthorizationGate`), `TestPermissions.cs` (catalog), `Fakes.cs` (real evaluator, permission set), `Fixtures.cs` (`Saves` counter), `GeneratorAndBuildTests.cs`/`ScopedCapabilityTests.cs` (`Policies`), csproj (+ Testing, generator); Capabilities.Cqrs `CqrsCapabilitySourceTests.cs` (+3: **D12**, unauthenticated, NeedsApproval; catalog; authorization registry), csproj; Search `TestFakes.cs`, `CapabilityCatalogProviderTests.cs` (+1 D12); NavigatR `AuthorizationGateTests.cs` (+5) |
| Docs | this entry, README of YFex.Authorization (status + §13) and YFex.Capabilities (consume section) |

**Deviations**

1. The gate sketch `AuthorizationGate.IsVisible(this, key)` became `IsVisible(in key)` + `Watch(this, …)`: view models are built without
   DI, so the gate is process-wide (like the desktop caller) and the owner is only needed for the change subscription. **No gate
   installed = visible** (like NavigatR without authorization services); the message gate stays authoritative.
2. The re-check trigger is `AuthorizationGate.Watch` (weak reference, first `CanExecute` subscribes, callbacks posted to that
   `SynchronizationContext`), not a `[ReactsTo]`-style mechanism — `[ReactsTo]` is an attribute without generator support today.
3. The Authorization generator emits the hook only for view models of the real YFex.State assembly: projects that use source stand-ins
   (its own tests) or no State generator would otherwise get CS0759. Nested and generic view models keep the (empty) State default.
4. `ActionGate.NeedsApproval` comes from the code-declared `.RequireApproval` (`RulesStage.DeclaredApproval`): approvals ride the rules
   slot (4b deviation 1), so the verdict has no approval case. An `IApprovalPolicySource` that changes the policy at run time is not seen.
   Order: denied → needs approval → needs confirmation (irreversible) → allowed.
5. The agent asks the **visibility** question (`IsVisible`, then `Authorize` only for the reason), like `IMessageAuthorization.QuickCheck`:
   no message exists yet, so named policies hide and resource requirements use `VisibleWith`; a hidden-but-allowed answer reports
   `ResourceDenied` (NavigatR too).
6. Policies resolve from the descriptor's `PolicyKey` (message / route / command); a capability without a registered policy is gated by
   its permission hint compiled into a one-permission policy (profiles' `.Requires(...)`, hosts without the runtime). Undeclared hints now
   fail `Build` — consistent with D5.
7. `BuildParameters` (property reflection for messages without a validation shape) stays: it is not an authorization path; only the
   attribute reflection was removed.
8. NavigatR's stale `UnionSupport.cs` polyfill was deleted (needed for `[Union] ActionGate`); `YFex.UI.Abstractions/UnionSupport.cs` has
   the same polyfill and was left alone (no conflict seen in these builds) — follow-up.
9. Known limitation (pre-existing, not fixed): the Capabilities generator cannot read `[RequirePermission(Catalog.Ids.X)]` when the catalog
   is generated in the same compilation (generators do not see each other's output) → `RawPermission` null → "mutating without
   permission" build error; fixtures keep string literals. The policy itself is unaffected (it comes from the authorization registry).

**Acceptance:** Capabilities 28 → 33, Capabilities.Cqrs 7 → 10, Capabilities.Generator 5 = 5; **D12** —
`D12_capability_whose_message_needs_two_permissions_is_hidden_from_a_caller_holding_one` (search hides it, agent denies naming the missing
`billing.transfer.large`; holding both shows/allows it), plus the VM-command and Search variants; NavigatR 129 → 134 passed (+ the same 5
known failures); State 357 → 360 (hook tests) and State.History 60 =; Authorization 101 → 104, Authorization.Generator 10 → 12.

**AOT:** `YFex.Capabilities.AotSmoke` (now on the real registry, evaluator, generated catalog/rows/hook) and `YFex.Validation.Smoke`
publish native win-x64 with 0 IL2xxx/IL3xxx warnings and run OK (`ok=True`; `PublishAot` set temporarily in the AotSmoke csproj only,
restored byte-identical — `cmp`; Validation.Smoke already has it). Script `C:\Temp\yfex-edits\p5_aot.sh`, logs
`C:\Temp\authz-testlogs\phase5-aot\`.

**Other consumers of the State generator change** (every `[StateCommand]` now declares the hook): YFex.Mvvm, YFex.State.Smoke and
YFex.Avalonia.Gallery build. `YFex.Persistence.Tests` does not compile — pre-existing: its csproj (2026-08-12) references
`..\YFex.State\…` under `src/Persistence` (paths that do not exist); no error involves the hook.

All runs `-c Release --artifacts-path C:\Temp\authz-art -p:NuGetAudit=false` (runner `C:\Temp\yfex-edits\run-tests-r.sh`, logs
`C:\Temp\authz-testlogs\phase5-before|phase5-after\`).

| Suite | Before | After |
|---|---|---|
| YFex.Capabilities.Tests | 28 | **33** |
| YFex.Capabilities.Cqrs.Tests | 7 | **10** (incl. the D12 regression test) |
| YFex.Capabilities.Generator.Tests | 5 | 5 |
| YFex.Search.Tests | 57 | **58** |
| YFex.NavigatR.Tests | 129 + 5 known failures | **134** + the same 5 known failures |
| YFex.State.Tests | 357 | **360** |
| YFex.Authorization.Tests | 101 | **104** |
| YFex.Authorization.Generator.Tests | 10 | **12** |
| State.History 60, AspNetCore 12, Authorization.Installations 44, Messaging 186 (+2 skipped), Messaging.Rpc 87, Rules 65, Rules.Admin 29, Validation 62, Validation.Generator 50, Cqrs.Approvals 12, Cqrs.Tenancy 11, Tenancy 27, Web.Data 52, Core 234 | = | = |

**Open items:** 4c (Rules.Admin approval-policy table) is the last consolidation item. Follow-ups outside this phase:
`YFex.UI.Abstractions/UnionSupport.cs` (same stale polyfill as NavigatR's); the Capabilities generator vs generated `Ids` constants
(deviation 9); `YFex.Persistence.Tests` project references; hosts must call `UseYFexViewModelAuthorization()` for the VM command gate.

### 2026-10-30 — Site approval tokens accepted (additive, for Offline Phase 4d)

`ApprovalTokenVerifier` no longer drops `ApprovalTokenKind.Site` at construction. A site token is checked like a device token (offline policy,
age at the decision, approver re-check, `ApprovalFlags.Offline`), except the installation test: the signer is the site broker, so the kind's
registered `IApprovalSignatureVerifier` must also implement the new `IApprovalIssuerCheck` and accept that broker for the deciding installation;
otherwise `OtherInstallation`. No verifier of the kind = `UnsupportedKind`, as before. The implementation is Offline's `SiteApprovalSignatures`
(YFex.Offline.Central.Rpc). Tests: YFex.Authorization.Tests 104 → 105.
