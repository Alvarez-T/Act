# 04 — Consumers and migration

Breaking changes are accepted (⚑AC4, user 2026-11-05: nothing is in production). Order follows the layer order of the repo split
(core → Actors → Authorization.Public → the rest). Each row is done in the phase named.

| Library | What changes | Breaking | Phase |
|---|---|---|---|
| **YFex core** | nothing (Actors references core: `ILogValue`, `ICanonicalValue`, `TimeProvider` users) | no | — |
| **YFex.Actors** (new) | 02 §2–§9 | — | AC0 |
| **YFex.Authorization.Public** | delete `Actor.cs` types; reference YFex.Actors; `ActorScope.From(caller)` → extension `caller.ToActorScope()`; `IAuthorizationContext.Actor/RealActor/Relation` use the moved types; approval `Approver` becomes `ActorIdentity` | yes (namespace, `Kind` numbers, approval token layout) | AC1 |
| **YFex.Authorization** | `AuthorizationContext.SubjectOf` → `ActorClaims`; accessors and middleware keep being the writers | yes (internal) | AC1 |
| **YFex.Audit** | delete `AuditActorKind`, `AuditActor`, `ActorRelation`; entries hold `ActorIdentity`; codec writes `ActorIdentity` canonical bytes; `IAuditActorSource` default = `ActorContext.Current` (⚑RA7 adapter becomes trivial); ⚑A29 directory = `IActorDirectory` | yes (entry layout, clean break) | AC1, AC2 |
| **YFex.Offline** | envelope + session encode the actor through 02 §5; replay uses `ActorContext.Restore`; golden tests regenerated | yes (format, clean break) | AC1 |
| **YFex.Authentication** | principal shape unchanged; `AccountNameSource : IActorNameSource` (User); session sign-in creates `ActorIdentity.User` | additive | AC1, AC2 |
| **YFex.Messaging(.Rpc)** | `CallContext.SubjectOf` → `ActorClaims`; outbox rows keep `ActorSnapshot`; offline outbox replays with `Restore` | yes (outbox row) | AC1 |
| **YFex.Cqrs, NavigatR, Search, Capabilities** | namespace change only (`using YFex.Actors;`) | yes (namespace) | AC1 |
| **YFex.Tenancy** | projector keeps `string? userId`, filled by `ActorClaims.SubjectOf` (Tenancy must not reference YFex.Actors for one string) | no | AC1 |
| **YFex.Jobs** | D2b uses `ActorIdentity.Job(...)`, `ActorScope.Delegated`, `Capture/Restore` for stored runs; `JobNameSource` (Job) | additive (D2b not built) | AC1, AC2 |
| **YFex.Installations** | `InstallationNameSource` (Device) | additive | AC2 |
| **YFex.Logging** | log viewer shows names through the directory; enrichers read `ActorContext` instead of their own user strings | additive | AC2 |
| **YFex.Notifications** (designed) | `Recipient.Actor(ActorIdentity)` replaces `Recipient.Reference("user:…")` for actors; outbox `Sender = ActorSnapshot`; contact sources resolve actors | design edit | AC2 |
| **YFex.Files** (designed) | `UploadedBy` metadata, partner = `Service` actor | design edit | AC2 |
| **YFex.Licensing** (designed) | counts seats by `ActorKind` (users vs devices), masks `Service` actors (06 §1) | design edit | AC2 |
| **YFex.Privacy** | erasure marks `User` subjects erased in the directory (`ActorState.Erased`); `DataSubjectReference` from `user:` subjects | additive | AC2 |
| **YFex.Data** | `ActorReference` column mapping (text form); `[ActorStamps]` fill in the generator (⚑AC10) | additive | AC3 |
| **YFex.UI** | `YActorLabel` (name + avatar + "via …" for on-behalf) over `IActorDirectory`; used by ⚑A29 history | additive | AC3 |
| **Apps (Farma, ERP)** | POS operator name from session + directory; entities adopt stamps | app work | AC3 |

## Prerequisites and order

1. AC0 builds YFex.Actors alone (tests, golden bytes, benchmarks).
2. AC1 moves Authorization.Public first (everything else references its callers), then Audit and Offline (formats), then the
   namespace-only repos. The ecosystem builds after each repo; the atlas F05 finding closes at the end of AC1.
3. Jobs D2b can start after AC1 (it needs `Job` kind, `Delegated`, `Capture/Restore`).
4. AC2 needs AC1; Audit ⚑A29 and Notifications N2 build on it.

## Docs to update when AC1 lands

authorization-consolidation 10 (⚑RA10/⚑RA11 resolved here), audit 02 §3, offline envelope docs, notifications 02 §3 / 05,
Jobs Plan D §6.1, licensing 06, privacy 07; atlas F05 → done.
