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
