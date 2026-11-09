# YFex.Actors — who acts, in every library and every app — Plan

> **Status:** PROPOSED 2026-11-05; **AC0 BUILT 2026-11-05** (the package only, no consumer moved — see 05 log), revised the same day
> for ⚑AC13–⚑AC17 (decided by the user); **AC1a BUILT 2026-11-05** (consumers moved, old types deleted, F05 closed;
> AC1b — string scopes → `TenancyPath`, `JobKey`, origin writers — next). Open decisions: ⚑AC5–⚑AC12 (AC0 followed the recommendations of ⚑AC5, ⚑AC6, ⚑AC8, ⚑AC12);
> ⚑AC3 is superseded by ⚑AC13.
> **Repo:** `C:\Dev\YFex\YFex.Actors` (sibling, own `actors.slnx`, template YFex.Resilience / YFex.Time). Created 2026-11-05.
> **Origin:** atlas F05 ("actor identity is defined three times"). ⚑RA8 (2026-11-04) decided to move the actor types out of
> `YFex.Authorization.Public`; on 2026-11-05 the user chose a **separate package** over a core folder, because the actor model will be
> used across projects (Audit names, Notifications recipients, Jobs run-as, PDV/ERP screens) and should keep one pattern from the start.
> It also settles ⚑RA10 (numbering) and ⚑RA11 (namespace) of [authorization-consolidation 10](../authorization-consolidation/10-run-as-context.md).

## Files

| File | What it covers |
|---|---|
| README (this file) | why, scope, packages, shape, decisions, phase summary |
| [01 — Inventory](01-current-state-and-inventory.md) | what exists today (file:line): the five types, Audit's copy, three codecs, three "who" readers, string actors in approvals, notifications and apps |
| [02 — Model and core types](02-model-and-core-types.md) | `ActorKind`, `ActorIdentity`, `ActorReference`, `ActorScope`, `ActorContext` (+ capture/restore), `ActorStamp`, encoding, `IActorDirectory` |
| [03 — Scenarios and examples](03-scenarios-and-examples.md) | **dev-side code first**: PDV, ERP, Files, Notifications, Jobs, agents, support, webhooks, privacy erasure, tests |
| [04 — Consumers and migration](04-consumers-and-migration.md) | **who changes**, library by library: what, breaking or not, order |
| [05 — Phases, acceptance, log](05-phases-and-acceptance.md) | phases AC0–AC4 with acceptance, gates, implementation log |

Related: [authorization-consolidation 06 §4](../authorization-consolidation/README.md) (single writer of the ambient actor),
[authorization-consolidation 10](../authorization-consolidation/10-run-as-context.md) (run-as, ⚑RA8–⚑RA11),
[audit 02 §3](../audit/02-entry-model.md) (actors in entries), [audit 09 §2](../audit/09-ui-and-logging-correlation.md) and ⚑A29 (names at
read time), [notifications 02 §3](../notifications/02-model-and-core-types.md) (recipients), [privacy README ⚑P10](../privacy/README.md)
(data subjects), Jobs Plan D §6.1 (`YFex.Jobs/YFex.Jobs.Templates-Development-Plan-D.md`).

## 1. Why

| Today | Problem |
|---|---|
| `ActorKind/ActorIdentity/ActorScope/ActorContext/ActorRelation` live in `YFex.Authorization.Public` | Audit, Logging, Cqrs, Offline, Jobs, Notifications only want "who", yet must reference Authorization |
| Audit has its own `AuditActorKind`, `AuditActor`, `ActorRelation` with **different numbers** (`User` = 1 vs 0) | two models mapped by name; every adapter converts |
| Offline envelope, offline session, Audit entry and Audit value encodings each write subject/kind/scope their own way | four encoders of one value; the Offline reader guesses "anonymous" |
| `SubjectOf(principal)` is written in Messaging `CallContext`, in Authorization `AuthorizationContext`, and again in the Tenancy projector | three readers of "who" (authentication 01 D3) |
| Approvals carry `string Approver`; Notifications use `Recipient.Reference("user:42")`; Jobs designs `job:{template}#{id}` | actors as hand-built strings with private prefixes |
| No library turns `User:8f3a…` into "João Silva"; Farma's POS shows a hard-coded `"Operador 01"` | every screen, report and message will invent its own lookup (Audit ⚑A29, Notifications, UI) |
| No `CreatedBy/ModifiedBy` convention anywhere (audit 01 inventory) | each ERP entity will add its own columns and fill them by hand |

**One library owns the actor model and the mechanics around it.** Owners keep their meaning:

| Owner | Keeps |
|---|---|
| YFex.Authorization | callers, permissions, roles, `CallerContext`, run-as resolver, approval tokens (now naming `ActorIdentity`) |
| YFex.Authentication | accounts, credentials, sessions, run-as grants (⚑RA9); **supplies** user names to the directory |
| YFex.Audit | entries, chains, codec (writes the actor through YFex.Actors' encoding) |
| YFex.Installations / Jobs / Capabilities | **supply** device, job and agent names to the directory |
| Apps (PDV, ERP) | their domain records; they *point to* actors (stamps, assignments), they never copy names |

## 2. Scope

**In:** actor kinds (one numbering); identity, reference text and canonical bytes; the scope (actor, real actor, relation, session);
the ambient context with capture/restore across stored work; stamps (`CreatedBy`/`ChangedBy`) for app records; the directory contract
(name, avatar key, state) with a composite and cache; reading the actor from a `ClaimsPrincipal`; privacy classification of subjects;
test helpers.

**Out:** permissions, roles, groups and their membership (Authorization); accounts and credentials (Authentication); how an approval is
proven (Authorization); device keys (Installations); contact destinations (Notifications' contact book, which *resolves* actors);
storing names in records (never — names are resolved at read time).

## 3. Packages

| Package | TFM | Contents | References |
|---|---|---|---|
| `YFex.Actors` | net11.0 | kinds, identity, reference, scope, context (+ capture), stamp, encoding (`ICanonicalValue`), principal reader, `IActorDirectory` + `ActorDisplay` contracts | YFex core only |
| `YFex.Actors.Directory` | net11.0 | composite directory over per-kind sources, cache, erased-subject rendering, DI (`AddYFexActors`) | `YFex.Actors`, DI/caching abstractions (⚑AC7) |
| `YFex.Actors.Testing` | net11.0 | `TestActors.As(...)`, session helpers, in-memory directory | `YFex.Actors.Directory` |
| `YFex.Actors.Generator` *(⚑AC9, optional)* | netstandard2.0 | declared service/system actors → constants + directory entries; analyzer for the single-writer rule | Roslyn |

The base package stays a **leaf above core**: no Tenancy (the home is core's `TenancyPath`, ⚑AC13), no Cryptography (encoding through core's
`ICanonicalValue<T>`), no DI. Every library that names an actor references only `YFex.Actors`.

## 4. Shape in one screen

```csharp
// who is acting (ambient, read anywhere)
ActorScope? now = ActorContext.Current;              // Actor = João, RealActor = Job:sales-report#42, Relation = Delegation

// building actors (no hand-written prefixes)
ActorIdentity cashier = ActorIdentity.User(accountId, home: TenancyPath.Parse("0=farmaviva/1=loja7"));
ActorIdentity pos     = ActorIdentity.Device(installation.Id);          // InstallationId (⚑AC15)
ActorIdentity job     = jobKey.ToActor(home);                           // YFex.Jobs' JobKey (⚑AC14, AC1)

// a stored pointer and its text form
ActorReference r = cashier;                          // "user:8f3a2c…@0=farmaviva/1=loja7"

// "who did this" in a record
public ActorStamp Created { get; init; }             // (Actor, RealActor?, At)

// names at read time
ActorDisplay who = await actors.DescribeAsync(entry.Actor, ct);   // "João Silva", or "usuário removido (#h:3f…)"

// work stored for later keeps its actor
var snapshot = ActorContext.Capture();               // persisted with the message / job / outbox row
using (ActorContext.Restore(snapshot)) await HandleAsync(ct);
```

## 5. Phase summary (details in 05)

| Phase | What | Unblocks |
|---|---|---|
| AC0 ✅ 2026-11-05 | repo, types (Audit numbering), reference text, encoding, principal reader, context capture/restore, stamps | F05, Jobs D2b |
| AC1 (a ✅ 2026-11-05, b next) | move consumers: Authorization.Public, Audit, Offline, Authentication, Messaging, Cqrs, NavigatR, Capabilities, Jobs, Search | delete old types |
| AC2 | directory contract + composite + Testing; sources in Authentication, Installations, Jobs, Capabilities | Audit ⚑A29, Notifications N2 |
| AC3 | YFex.Data stamp convention (generated fill from `ActorContext`), UI "who" control | ERP screens |
| AC4 | generator/analyzer (⚑AC9), if accepted | — |

## 6. Decisions

| # | Question | Recommendation | Alternative |
|---|---|---|---|
| ⚑AC1 | Own package or core folder | **Decided 2026-11-05 (user): own package `YFex.Actors`** | core `YFex/src/YFex/Actors` |
| ⚑AC2 | `ActorKind` numbering (= ⚑RA10) | **Decided 2026-11-05 (user): Audit's superset** `Anonymous 0, User 1, Service 2, Device 3, Job 4, Agent 5, System 6` | Authorization's numbers + appended kinds |
| ⚑AC3 | `HomeScope` type (= ⚑RA11) | **Superseded by ⚑AC13** (was: canonical scope text as a string) | Tenancy's `ScopeIdentity` |
| ⚑AC4 | Stored formats that change | **Decided 2026-11-05 (user): clean break** — nothing is in production; Offline envelope/session and Audit entries move to the new encoding without v1 readers | version 2 + v1 readers |
| ⚑AC5 | A `Partner`/`External` kind for organisations (Files Exchange partners, marketplace webhooks, suppliers' EDI)? | **no new kind**: they act as `Service` actors named `service:partner/acme` (03 §5) | add `ActorKind.External = 7` |
| ⚑AC6 | Groups as actors (`group:managers`)? | **no**: groups are Authorization roles or Notifications recipient lists; an actor is always one party | `ActorKind.Group` |
| ⚑AC7 | Directory cache | per-process memory cache, TTL 10 min, invalidated by a `ActorChanged` event the sources raise | FusionCache (distributed) |
| ⚑AC8 | Subject privacy | the subject is an opaque id (`DataSensitivity.Internal`, no attribute), as `ActorIdentity`'s log value says today; it is still a **data-subject key** for erasure (privacy ⚑P10), so Privacy maps `user:` subjects to its `DataSubjectReference`; names never enter records or logs | classify subjects `Personal` and mask them in logs |
| ⚑AC9 | Generator / analyzer | **analyzer only** (single writer of `ActorContext`, no hand-built `"job:"` strings); declared-actor generator later if service actors multiply | full generator now |
| ⚑AC10 | Stamps in YFex.Data | Data's generator fills `ActorStamp` columns from `ActorContext` on insert/update (opt-in per row type) | apps fill stamps by hand |
| ⚑AC11 | Name snapshot (audit Q14) | names at read time only; erased people render as "removido (#h:…)" | encrypted name snapshot per entry |
| ⚑AC12 | Maximum subject length | 128 UTF-8 bytes (fits `stackalloc` in the canonical encoding) | 256 |
| ⚑AC13 | Type of the home | **Decided 2026-11-05 (user): a core value object `TenancyPath`** (namespace `YFex.Tenancy`, name chosen to avoid "scope" next to DI/ambient scopes): canonical text `{rank}={value}/…`, core owns the grammar, Tenancy's `ScopeIdentity` parses with it and converts; replaces every string scope (actor home, `InstallationIdentity.Scope`, Authentication home + `scope_home` claim, approval tokens, Offline) | keep a string |
| ⚑AC14 | Job keys | **Decided 2026-11-05 (user): a `JobKey` value object owned by YFex.Jobs** (template + optional instance id, replaces `JobKeys` string helpers); YFex.Actors keeps a raw subject and no `#` rule; Jobs adds `jobKey.ToActor(home)` (AC1) | key shape in Actors |
| ⚑AC15 | Device subjects | **Decided 2026-11-05 (user): `ActorIdentity.Device(InstallationId)`** (subject = Guid D text, `TryGetInstallation` reads it back) | string ids |
| ⚑AC16 | Where work ran | **Decided 2026-11-05 (user): an optional origin `InstallationId`** on `ActorStamp` and `ActorSnapshot`, carried by `ActorScope.Origin`; set by the writer that enters the scope, never guessed; restored work keeps its original origin. Hosts, placements and stores are not actors | none |
| ⚑AC17 | Home on every kind + identity rule | **Decided 2026-11-05 (user): every factory takes a home** (incl. `Job`, `System`); identity = kind + subject + home ("a subject is unique within kind + home") | home on some kinds only |
