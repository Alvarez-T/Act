# 05 — Phases, acceptance, log

Every phase: Release build of each touched repo, its tests, the AOT smoke of YFex.Actors (`PublishAot`, zero IL warnings), and the
atlas rescan. Breaking changes are allowed (⚑AC4).

## AC0 — the package

- Repo `YFex.Actors` (`actors.slnx`, `src/YFex.Actors`, `tests/YFex.Actors.Tests`, `benchmarks/`, `samples/YFex.Actors.AotSmoke`).
- 02 §2–§7 and §9: kinds, identity + factories + validation, text form, `ActorReference`, canonical bytes, scope + context +
  capture/restore, stamp, `ActorClaims`.
- `IActorDirectory`, `IActorNameSource`, `ActorDisplay`, `ActorState` contracts (no implementation yet).

**Acceptance:** text round-trip for every kind (property test); canonical golden bytes; `default(ActorIdentity).IsAnonymous`;
subject limits enforced; `Capture` → `Restore` across a thread and across serialization restores the same scope; nested
`Enter`/`Restore` restore correctly when disposed out of order (today's rule); `TryFormat`/`TryWriteCanonical` allocate 0 bytes
(benchmark); AOT smoke runs.

## AC1 — consumers move (closes F05)

04 rows marked AC1, in the order of 04 §"Prerequisites". Old types deleted; no `[Obsolete]` bridge.

**Acceptance:** no `ActorKind`/`ActorIdentity`/`ActorScope`/`ActorContext`/`ActorRelation`/`AuditActor*` declared outside YFex.Actors
(architecture test in YFex.Actors.Tests scanning `C:\Dev\YFex`, like Cryptography's); no `SubjectOf` outside `ActorClaims`; Audit,
Offline golden tests regenerated; every touched repo builds and passes; Authorization, Audit, Offline AOT smokes pass.

## AC2 — names at read time

`YFex.Actors.Directory` (composite, batching, cache ⚑AC7, erased/unknown rendering, `AddYFexActors`), `YFex.Actors.Testing`; name sources
in Authentication, Installations, Jobs, Capabilities; Audit ⚑A29 uses it; Notifications/Files/Licensing design edits.

**Acceptance:** a history page of 50 entries makes ≤ 1 lookup per source (batched) and 0 on the second render (cache); erased users
render "removido (#h:…)" and never their old name; a disabled source degrades to the shortened text form, never throws in a grid.

## AC3 — records and screens

YFex.Data `ActorReference` mapping and `[ActorStamps]` generator fill (⚑AC10); YFex.UI `YActorLabel`; Farma POS operator name.

**Acceptance:** insert/update fill stamps from `ActorContext` with no handler code; a row written by a job run-as shows actor + real
actor; generated code is AOT-clean.

## AC4 — analyzer (⚑AC9, if accepted)

Diagnostics: `ActorContext.Enter/SetSession/Restore` outside allowed writer types (configurable list); string literals starting with a
kind prefix (`"user:"`, `"job:"`) passed where an actor is expected.

## Implementation log

*(append per phase: date, what was done, deviations, gotchas, test counts)*

- 2026-11-05 — plan written (README, 01–05). ⚑AC1 (own package), ⚑AC2 (Audit numbering) and ⚑AC4 (clean break) decided by the user.
- 2026-11-05 — **AC0 built** (package only; no other repo's code touched). Repo `C:\Dev\YFex\YFex.Actors` (`git init`, `main`, not
  committed): `actors.slnx`, `Directory.Build.props` (Time style: net11.0, preview, Nullable, warnings as errors, `$(YFexRoot)`),
  `src/YFex.Actors` (references YFex core only), `tests/YFex.Actors.Tests`, `benchmarks/YFex.Actors.Benchmarks`,
  `samples/YFex.Actors.AotSmoke`, README, AGENTS.md, CLAUDE.md. Namespace `YFex.Actors`: `ActorKind` (Audit numbering) and
  `ActorRelation`; `ActorIdentity(Kind, Subject, HomeScope?)` with factories, validation, `ILogValue` (text form) and
  `ICanonicalValue`; `ActorText` + `ActorReference`; `ActorScope` (`Of/Delegated/Impersonated/OnBehalf`, no `From`); `ActorContext`
  (today's semantics + `Capture`/`Restore`) and `ActorSnapshot`; `ActorStamp`; `ActorClaims`; directory contracts.
  - **Decisions followed:** ⚑AC3 home scope as canonical text; ⚑AC5/⚑AC6 no extra kinds; ⚑AC8 subject logged as an opaque id
    (no attribute); ⚑AC12 128-byte subjects. ⚑AC7/⚑AC9–⚑AC11 do not touch AC0.
  - **Additions beyond 02 (additive, needed by AC0 acceptance or AC1 readers):** public `TryReadCanonical` on `ActorIdentity`
    and `ActorSnapshot` (core's `ICanonicalValue` has no reader; Audit/Offline decoders need one); constants
    `ActorIdentity.MaxSubjectBytes` (128) / `MaxHomeScopeBytes` (512), `ActorText.MaxLength` (651, for `stackalloc`),
    `ActorScope.MaxSessionIdBytes` (256); `ActorSnapshot.Of(ActorScope?)`, `ToScope()`, `IsAnonymous`; `ActorClaims`
    constants `SubjectClaimType` (`sub`), `KindClaimType` (`yfex.kind`), `HomeScopeClaimType` (`scope_home`, what Authentication
    issues today).
  - **Deviations:** `IActorDirectory.DescribeManyAsync` takes `ReadOnlyMemory<ActorIdentity>` / `Memory<ActorDisplay>` instead of
    spans — a span cannot be kept across `await`, so the 02 §8 signature could not be implemented asynchronously. Fixed-width
    length fields (scope, session) are little endian, as Cryptography's `WriteFixed64`. Texts are encoded as given (no NFC), since
    equality is ordinal.
  - **Rules settled while building:** anonymous has an empty subject (a subject with kind anonymous throws); empty home scope /
    session id = null (so `@` with nothing after it never parses); `Job(key)` and the template of `Job(template, id)` reject `#`;
    lone surrogates and C0/C1/DEL controls are rejected; kind names parse ordinal lower-case only; `ActorScope` validates
    `Relation` and `SessionId` also through `with`; `Delegated`/`Impersonated` reject an anonymous real actor and `OnBehalf`
    requires an `Agent` kind (real actor = agent, actor = person); `ActorClaims.FromPrincipal` fails closed to anonymous (unknown
    kind, invalid subject/scope, unauthenticated); restoring an anonymous snapshot enters `ActorScope.Anonymous`, so replayed work
    never runs as the host's `Session`. The `ActorIdentity` properties are get-only (no `with` that bypasses validation; use
    `WithHomeScope`).
  - **Gotchas:** xUnit replaces lone surrogates in `[InlineData]` strings with U+FFFD — build such inputs in code. A snapshot's
    `IsAnonymous` must compare the whole value (an anonymous actor can carry a home scope); the property test found it.
  - **Tests: 134 pass** (Release): golden texts + strict-parse rejections; seeded property tests (4 000 cases per kind) for text and
    canonical round-trips, byte/text equality ⇔ value equality, random texts and bytes only ever decode into valid actors; canonical
    golden bytes for every kind, the bounds and rejected encodings, snapshot golden bytes; `default` = anonymous; subject/scope/
    session limits (incl. 2- and 4-byte UTF-8); `ActorContext` behaviours ported from Authorization (flow across await, nesting,
    no leak from async methods, out-of-order dispose) + session; capture → restore across a non-flowing thread, an unsafe
    thread-pool item and bytes (plus 2 000-case property); nested `Enter`/`Restore` disposed out of order; stamps; claims;
    directory contract; `AllocationTests` (0 bytes for `TryFormat`, both `TryWriteCanonical`, the log value, `Capture`).
  - **AOT:** `dotnet publish samples/YFex.Actors.AotSmoke -c Release -r win-x64` — 0 IL2xxx/IL3xxx warnings; the executable exits 0.
  - **Benchmarks** (in-process, ShortRun): `TryFormat` 5.6 ns / 0 B, `Format` 14.9 ns / 144 B (the string), `TryParse` 74 ns / 160 B,
    log write 25 ns / 0 B, identity `TryWriteCanonical` 14 ns / 0 B, snapshot `TryWriteCanonical` 48 ns / 0 B, identity
    `TryReadCanonical` 84 ns / 160 B, `Current` 1 ns / 0 B, `Capture` 17 ns / 0 B, `Enter`+dispose 5 ns / 0 B, `Restore`+dispose
    38 ns / 232 B (the new `ActorScope`).
  - Atlas: F05 note updated, `YFex.Actors` component added, infrastructure map row added; regenerated with `yfexAtlas --bundle`.
  - **Next:** AC1 (04 order): `ActorScope.From` becomes Authorization's `caller.ToActorScope()`; Audit drops `AuditActor*`;
    Offline envelope/session and Audit codec write `TryWriteCanonical`; `SubjectOf` callers use `ActorClaims`.
- 2026-11-05 — **AC0 revised for ⚑AC13–⚑AC17** (decided by the user after an analysis of home scope, job keys, installations, hosts
  and where jobs run). Touched: YFex core (new type), YFex.Tenancy (grammar + conversions), YFex.Actors.
  - **⚑AC13 `TenancyPath`** (core `YFex/src/YFex/Tenancy/TenancyPath.cs`, namespace `YFex.Tenancy`; the user chose the name over
    `ScopePath` to avoid "scope" next to DI/ambient scopes): strict canonical text (ranks 0–255 without leading zeros, strictly
    increasing, non-empty values, only `%25`/`%2F` escapes, no controls, ≤ 8 levels, ≤ 512 UTF-8 bytes), `IsValid`, `IsWithin`,
    `Parent`, `Depth`, `ISpanFormattable`/`ISpanParsable`, canonical `length LE16 · UTF-8`, log value. YFex.Tenancy: `ScopeFormat`
    rejects any text `TenancyPath.IsValid` rejects (one grammar), `ScopeIdentity.ToTenancyPath()`, `ScopeIdentity.From(path)`,
    `path.ToScopeIdentity()`. Stricter than before: leading-zero ranks and empty values no longer parse; no literal in the ecosystem
    used them (scan of every `ScopeIdentity.Parse("…")`), Jobs' 172 tests pass.
  - **YFex.Actors:** `ActorIdentity(Kind, Subject, TenancyPath Home)` (`HomeScope`/`WithHomeScope`/`MaxHomeScopeBytes` removed,
    `WithHome`); every factory takes a home (⚑AC17); `Device(InstallationId)` + `TryGetInstallation` (⚑AC15); `Job(string jobKey,
    home)` only — the `#` rule and `Job(template, instanceId)` are gone, YFex.Jobs' `JobKey` owns the shape (⚑AC14, AC1);
    `ActorScope.Origin`, `ActorSnapshot.Origin` (bytes gain `origin presence (1) [· installation id (16, big endian)]`),
    `ActorStamp.Origin` (⚑AC16); `ActorClaims.HomeClaimType` (was `HomeScopeClaimType`), the claim parses as a `TenancyPath`.
    Identity canonical bytes are unchanged (the home's canonical form is the old scope layout).
  - **Deviation from the analysis text:** I had said a restored snapshot records the *replaying* machine as origin. Built instead:
    origin lives on `ActorScope`, is set only by the writer that enters the scope (never read from `LocalInstallation` inside
    `ActorContext`), and `Restore` keeps the **original** origin — an offline sale replayed at central still stamps Caixa 3; the
    replay is Offline provenance. Deterministic and testable (no hidden process-global read).
  - **Tests:** core 276 (42 new `TenancyPathTests`: grammar, limits, within/parent, golden bytes, 3 000-case property, 0-allocation
    format/canonical, log value); Tenancy 34 (5 new conversion tests); Actors **146** (new: per-home identity, device installation,
    job subject shape, home grammar in the text form, origin through capture → bytes → restore → stamp, origin never guessed,
    snapshot origin golden bytes and rejections, property test with random homes and origins). AOT smoke: 0 IL warnings, exit 0.
  - **Benchmarks:** unchanged within noise — `TryFormat` 6.1 ns / 0 B, identity `TryWriteCanonical` 14 ns / 0 B, snapshot (with
    origin) 48 ns / 0 B, log write 27 ns / 0 B, `Capture` 17 ns / 0 B, `Restore`+dispose 50 ns / 256 B (the new `ActorScope`).
  - **For AC1 (now larger):** replace string scopes with `TenancyPath` in `InstallationIdentity.Scope` (core), Installations,
    Authentication (home + `scope_home` claim + `home_scope` column), Authorization approvals, Offline; Jobs gets `JobKey`
    (replacing `JobKeys` and `JobInstanceIds` strings) with `ToActor(home)`, and `SchedulerOptions.InstallationId` becomes an
    `InstallationId`; the HTTP/RPC middleware, desktop sign-in and job runner set `ActorScope.Origin`.

- 2026-11-05 — **AC1a built: consumers moved, old types deleted, F05 closed.** Before touching them, the five repos with old
  uncommitted work were built, tested and committed alone (user decision): Authorization `7013e0a`, Offline `acb3749`,
  Capabilities `0f4920d`, Jobs `b486626`, Search `67cce6f`.
  - **Authorization:** `Actor.cs` deleted; `.Public` references YFex.Actors (LeafGuardTests now allow core + YFex.Actors);
    `ActorScope.From(caller)` → `caller.ToActorScope()` (`ActorScopeExtensions` in `.Public`); `AuthorizationContext.SubjectOf`
    deleted (→ `ActorClaims.SubjectOf`); **approval `Approver` is an `ActorIdentity`** (`ApprovalToken`, `ApprovalGrant`,
    `ApprovalEvent`); token body tag 2 → **3** (approver = length-prefixed actor canonical bytes), golden re-pinned; directory and
    history checks pass `Approver.Subject`. Actor-facing scope strings convert with `TenancyPath.Parse` at the boundary.
  - **Audit:** `AuditActorKind`, `AuditActor` and Audit's `ActorRelation` deleted; entries, scopes, `IAuditActorSource`,
    `FixedAuditActorSource`, `AuditCapture` use `ActorIdentity`/`ActorRelation`; the codec writes `length (LE16) · ActorIdentity
    canonical bytes`; the engine reads **`ActorContext.Current` first** (actor, real actor, relation and now the session id) and asks
    registered sources only when no scope is active (new test).
  - **Offline:** envelope, session and site-coordination formats write the actor through `CanonicalWriter.Write(in actor)`; the
    "empty subject + User = anonymous" guess is gone; any kind may act offline (limits are ActorIdentity's); version tags
    **1 → 2** for envelope, session and coordination (the tests' own rule: "bump the version tag"), goldens re-pinned; the
    coordination approval carries the approver's text form.
  - **Authentication / Messaging / Cqrs.Approvals:** `new ActorIdentity(x)` → `ActorIdentity.User(x)`; `CallContext.SubjectOf`
    deleted; RPC enters `context.Caller.ToActorScope() ?? ActorScope.Anonymous`; the MemoryPack `ApprovalToken` formatter writes and
    parses the approver's text form (wire version 1 → 2). Authentication's `.Public` leaf rule allows YFex.Actors.
  - **Cqrs, NavigatR, Capabilities, Jobs, Search:** `using YFex.Actors;` and test fakes.
  - **New, found while migrating:** commands carrying approval tokens are JSON-serialized, and `TenancyPath.Parent` sent
    System.Text.Json into endless recursion. Added `[JsonConverter]`s (text form) on `TenancyPath` (core), `ActorIdentity` and
    `ActorReference`; `ActorSnapshot` deserializes through its primary constructor (`[method: JsonConstructor]`); tests in core and
    Actors and a source-generated `JsonSerializerContext` in the Actors AOT smoke.
  - **Architecture test** (`YFex.Actors.Tests/EcosystemArchitectureTests`): scans every `C:\Dev\YFex\YFex*` repo (pruned walk,
    ~1 s) — no actor type or `AuditActor*` declared outside YFex.Actors, no `static string? SubjectOf(` reader outside
    `ActorClaims`; guards that > 100 files were scanned.
  - **Tests (Release):** Actors 153 · core 278 · Tenancy 34 · Authorization 168 · Audit 33 · Offline 144 · Authentication 212 ·
    Messaging 307 (2 skipped, as before) · Cqrs 37 · Capabilities 48 · Search 58 · Jobs 172 · NavigatR 139 with the **5 pre-existing
    failures** listed in its AGENTS.md (unchanged). EventSourcing, Installations, Mvvm, Orchestration, Rules and Web build. Messaging's
    `YFex.Messaging.Rpc.AotSample` fails restore on NU1902 (MessagePack 3.1.7 advisory) — pre-existing, not AC1; published once with
    `-p:NuGetAudit=false` it has 0 IL warnings and runs (exit 0).
  - **AOT smokes** (`dotnet publish -c Release -r win-x64`, then run): Actors, Audit, Offline, Authentication, Capabilities, Jobs —
    0 IL2xxx/IL3xxx warnings, exit 0 each. Authorization has no AOT smoke project (its code is covered through the Authentication,
    Offline and Capabilities smokes).
  - **Gotchas:** an idempotent rewrite matters (a second pass re-swapped `(Kind, Subject)` arguments); target-typed `new("x")` for an
    `ActorIdentity` property compiles against the old ctor only — search for it; xUnit/FluentAssertions golden messages carry the
    actual bytes, so re-pinning was scripted from the failure output and every changed golden was reviewed as a version/actor change.
  - **Not done → AC1b (next):** replace the remaining **string scopes with `TenancyPath`** (core `InstallationIdentity.Scope`,
    Authentication home + `home_scope` column + `scope_home` claim, `IAuthorizationContext.Scope`, `ApproverScope`, Offline scopes);
    **`JobKey`** in Jobs (replacing `JobKeys`/`JobInstanceIds` strings) with `ToActor(home)`, and `SchedulerOptions.InstallationId`
    as an `InstallationId`; **origin writers** (HTTP/RPC middleware from the caller's installation, desktop sign-in, job runner);
    Offline envelope creation from `ActorContext.Capture()` and replay through `Restore`; Messaging outbox `ActorSnapshot`; Tenancy
    projector fed by `ActorClaims.SubjectOf`. Design docs still to point at YFex.Actors (04 list): authorization-consolidation 10
    (has another session's uncommitted edits — left alone), audit 02 §3, offline envelope docs, notifications 02/05, Jobs Plan D
    §6.1, licensing 06, privacy 07.
