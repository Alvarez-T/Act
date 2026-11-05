# 01 — Current state and inventory (2026-11-05)

Searched: every sibling repo under `C:\Dev\YFex` (`*.cs`, `*.csproj`, design docs), `bin/obj/_Backup/_ignore` excluded. Numbers are
2026-11-05 counts.

## 1. The actor types today

| Type | Where | Notes |
|---|---|---|
| `ActorKind : byte` `User 0, Service 1, Device 2, Agent 3` | `YFex.Authorization/src/YFex.Authorization.Public/Actor.cs:4` | no `Anonymous`, `Job`, `System`; `default` = `User` |
| `ActorRelation : byte` `None 0 … AgentOnBehalf 4` | same file `:17` | identical names and values in Audit |
| `ActorIdentity(Subject, Kind = User, HomeScope?)` + `ILogValue` | same file `:36` | `Anonymous` = `default` (empty subject); `ToString` = `Kind:Subject@scope` |
| `ActorScope(Actor, RealActor?, Relation, SessionId?)` | same file `:58` | `From(IAuthorizationContext)` ties it to Authorization |
| `ActorContext` (AsyncLocal scope + process `Session`, `Enter`, `SetSession`) | same file `:90` | single writer rule in its doc comment (authorization-consolidation 06 §4) |
| `AuditActorKind : byte` `Anonymous 0, User 1, Service 2, Device 3, Job 4, Agent 5, System 6` | `YFex.Audit/src/YFex.Audit.Public/AuditFacts.cs:4` | the superset chosen by ⚑AC2 |
| `ActorRelation` (Audit copy) | `AuditFacts.cs:16` | duplicate of Authorization's |
| `AuditActor(Kind, Id, HomeTenant?)` + `User/Job/System` factories | `AuditFacts.cs:33` | the only factories that exist |
| `IAuditActorSource` | `YFex.Audit/src/YFex.Audit.Public/AuditTrailContracts.cs:142` | "the ambient actor context proposed for core will become the first source" |

Files naming the actor types per repo: Authorization 21, Offline 16, Messaging 13, Authentication 10, Audit 8, Capabilities 2, Jobs 2,
Cqrs 1, NavigatR 1, Search 1.

## 2. Writers of the ambient actor (single-writer rule)

| Writer | File |
|---|---|
| ASP.NET Core middleware `using (ActorContext.Enter(ActorScope.From(feature.Caller) ?? ActorScope.Anonymous))` | `YFex.Authorization/src/YFex.Authorization.AspNetCore/YFexAuthorizationMiddleware.cs:41` |
| Desktop session (`SetSession`) | `YFex.Authorization/src/YFex.Authorization/AmbientPrincipalAccessor.cs:72, 94` |
| Snapshot accessor (`SetSession`) | `YFex.Authorization/src/YFex.Authorization/SnapshotPrincipalAccessor.cs:46` |
| RPC call context, delegation scopes | YFex.Messaging.Rpc `CallContext` (enters per call) |

No analyzer enforces the rule.

## 3. Encoders of an actor (four)

| Encoder | File | Layout |
|---|---|---|
| Offline envelope | `YFex.Offline/src/YFex.Offline/Envelopes/OfflineEnvelopeCodec.cs:179-191` | text subject · tag kind (Authorization numbers) · optional text scope; reader treats `("", User, null)` as anonymous |
| Offline session | `YFex.Offline/src/YFex.Offline/SignIn/OfflineSession.cs:79, 153` | same idea, own code |
| Audit entry | `YFex.Audit/src/YFex.Audit/AuditEntryCodec.cs:114-121, 277-282` | byte kind (Audit numbers) · string id · optional string tenant |
| Audit value encoding | `YFex.Audit/src/YFex.Audit.Public/AuditValueEncoding.cs:105` | own byte kind |

The canonical writer lives in YFex.Cryptography (`YFex.Canonical.CanonicalWriter`); core only declares `ICanonicalValue<TSelf>`
(`YFex/src/YFex/Canonical/ICanonicalValue.cs`), which `ActorIdentity` can implement without referencing Cryptography.

## 4. Readers of "who" from a principal (three)

| Reader | File |
|---|---|
| `CallContext.SubjectOf(principal)` = `NameIdentifier ?? "sub"` | `YFex.Messaging/src/YFex.Messaging.Rpc/CallContext/CallContext.cs:72` |
| `AuthorizationContext.SubjectOf(principal)` (same body) | `YFex.Authorization/src/YFex.Authorization/AuthorizationContext.cs:128` |
| Tenancy projector `Project(tenant, string? userId)` | `YFex.Tenancy/src/YFex.Tenancy.Resolution/DefaultTenantScopeProjector.cs:17` |

Authentication's inventory lists this as D3 ("principal shape, one place").

## 5. Actors as hand-built strings

| Where | Today | Should be |
|---|---|---|
| Approval tokens and events | `string Approver` (`YFex.Authorization/src/YFex.Authorization.Public/Approvals.cs:36`, `Approvals/ApprovalEvents.cs:9`) | `ActorIdentity Approver` |
| Notifications recipients (designed) | `Recipient.Reference("user:42")`, `"group:managers"` (notifications 02 §3) | `Recipient.Actor(ActorIdentity)`; groups stay recipient lists (⚑AC6) |
| Jobs run-as (designed) | `job:{template}#{id}` (authorization 10 §2.3, Plan D §6.1) | `ActorIdentity.Job(template, id)` |
| Offline authority allowances | `string Subject` ("*" or a subject id) (offline 06) | stays text (it also holds customers, products) |
| Farma POS | `_operatorName = "Operador 01"` (`YFex.Farma/app/YFexFarma.Desktop.Presentation/POS/POSViewModel.cs:78`) | `ActorContext.Session` + directory name |

## 6. Names at read time (designed, not built)

| Need | Where |
|---|---|
| Audit history shows names, never stores them; erased person = "usuário removido (#h:…)" | audit 09 §2, ⚑A29 (`IAuditActorDirectory`), audit 12 Q14 |
| Notifications: actor → contacts (contact sources), "sent by" | notifications 02 §3–4 |
| UI entity history control (`YEntityHistory`) | audit ⚑A29 |
| Logging viewer: actor of a trace | logging 08 |

## 7. "Who changed this record"

Audit's inventory (audit 01, line 38): *no* generic `CreatedBy/ModifiedBy` convention; only `PaymentRecord` and `Brain.Entity` have
ad-hoc fields. Every ERP entity would add its own.

## 8. Tests that build actors by hand

`ActorContextTests` + an xUnit `ActorContextCollection` (static session), and actor scopes built in tests of Cqrs, Messaging,
NavigatR and Rules.Admin.

## 9. No name conflicts

No repo, project or namespace `YFex.Actors`; no actor-model framework (Orleans, Akka.NET, Dapr Actors, Proto.Actor) referenced.
Other "Actor" names: `ActorLevel` (Rules test enum), `ActorStub` (Messaging test fixture), `IOfflineActorPermissions` (Offline
reconciliation) — none collide.
