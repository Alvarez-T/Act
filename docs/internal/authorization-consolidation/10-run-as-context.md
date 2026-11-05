# 10 — Run-as context: a caller for a stored actor (proposal)

> **Status:** PROPOSED 2026-11-04, revised the same day against the Audit, Offline, Authentication, Licensing and Logging designs and
> code. **⚑RA1–⚑RA8 decided by the user 2026-11-04** (§7): RA2–RA7 as recommended; **RA1 extended** to configurable run-as sources
> (§2.6); **RA8 = move the actor types to YFex core** (§2.7). New open points ⚑RA9–⚑RA11 (§7). Docs only, nothing built.
> **Asked by:** YFex.Jobs Plan D2 (`YFex.Jobs/YFex.Jobs.Templates-Development-Plan-D.md` §6.1 run-as, §6.4 command templates).
> **Kind:** additive request to YFex.Authorization, plus one line in YFex.Messaging.Rpc and a small adapter in YFex.Audit.
> Plan D §10: Jobs does not work around it.
> **Fits:** 09 Phase 7 ("with the features that need them"); the feature is Jobs D2.

## 1. Problem

A user-defined job runs later, with nobody signed in, **as the actor who created it** (`RunAs`, Plan D ⚑J22). A command template
(Plan D §6.4) dispatches `IDispatcher.CommandAsync` from the job body, so the Cqrs pipeline must see that actor as the caller, and
Audit must record who did it. Today there is no public way to build such a caller or make it current.

| Fact (read from code 2026-11-04) | Where |
|---|---|
| `IAuthorizationContextFactory.Create/CreateAsync` needs a `ClaimsPrincipal` | `YFex.Authorization/AuthorizationContextFactory.cs:15` |
| `IPrincipalAccessor` has only `Current`; no built-in accessor has a per-flow override. `AmbientPrincipalAccessor` = one process-wide session (desktop/POS); `HttpContextPrincipalAccessor` = `Anonymous` outside a request; Rpc `CallContextPrincipalAccessor` = `CallContext.Current?.Caller ?? inner` | `.Public/IAuthorizationContext.cs:57`, `AmbientPrincipalAccessor.cs`, `.AspNetCore/HttpContextPrincipalAccessor.cs`, YFex `Messaging.Rpc/CallContext/CallAuthorization.cs:91` |
| Cqrs reads the caller from `IPrincipalAccessor.Current` | YFex `Cqrs/Runtime/ApprovalStage.cs:142` |
| `PermissionResolution` has `ActorIdentity Subject` and a nullable `Principal`; the claim sources skip a null principal, the role map reads `Roles` | `PermissionSources.cs:13, 93, 120` |
| `IPermissionDirectory` (one permission) and `IRoleDirectory.IsInAnyRoleAsync` (given roles, Authentication R6) answer `Unavailable / Holds / DoesNotHold / UnknownSubject`; neither can **list** a subject's roles | `.Public/Approvals.cs:231-250`, `.Public/RoleDirectory.cs:10` |
| **Offline central already runs commands as a stored actor**, privately: `AuthorizationContext.FromSnapshot(envelope caller)` and then `new CallContext(caller, scope, …).Enter()`, i.e. it borrows the **RPC** call context as a run-as scope | `YFex.Offline.Central.Rpc/OfflineCentralRpc.cs:156-163` |
| **YFex.Audit has its own actor model** (`AuditActorKind { Anonymous, User, Service, Device, Job, Agent, System }`, its own `ActorRelation`, `IAuditActorSource`) and does not reference Authorization. The only built-in source is `FixedAuditActorSource`. Nothing turns Authorization's `ActorContext` into an Audit actor | `YFex.Audit.Public/AuditFacts.cs`, `YFex.Audit/AuditTrail.cs:131-145, 213` |
| Authorization's `ActorKind` is `{ User = 0, Service = 1, Device = 2, Agent = 3 }`: **no `Job`, no `System`**, and the numbers differ from Audit's | `.Public/Actor.cs:4` |
| Licensing (design) masks permissions **after** every source, also for service actors (`IPermissionAvailability.Restrict`) | `docs/internal/licensing/06-feature-gating.md` §1–§2 |

**Consequence today:** a background job on a desktop/POS host that dispatches a command runs **as whoever is signed in at the
keyboard**; on a server it runs as anonymous. Offline had the same defect (offline README F4) and fixed it locally. The same gap
will reach a resumed saga (YFex.Orchestration captures no actor today) and the AI agent loop (Audit 02 §3 `AgentOnBehalf`).

## 2. Proposal

### 2.1 Resolve a caller for a stored actor

```csharp
// (sketch) YFex.Authorization.Public — leaf (core + BCL); resolved in the YFex.Tenancy scope in effect, like the factory
public interface IActorCallerResolver
{
    ValueTask<ActorCallerResolution> ResolveAsync(ActorIdentity actor, ActorIdentity? realActor = null,
        ActorRelation relation = ActorRelation.None, CancellationToken cancellationToken = default);
}
public enum ActorCallerStatus : byte { Unavailable = 0, Resolved = 1, UnknownSubject = 2 }   // 0 = Unavailable, as PermissionDirectoryAnswer
public readonly record struct ActorCallerResolution(ActorCallerStatus Status, IAuthorizationContext? Caller);

// (sketch) the role list comes from the existing directory family (⚑RA1) — additive member, default = Unavailable
public interface IRoleDirectory
{
    ValueTask<PermissionDirectoryAnswer> IsInAnyRoleAsync(string subject, string? scope, IReadOnlyList<Role> roles, CancellationToken ct = default);
    ValueTask<RoleDirectoryEntry> GetRolesAsync(string subject, string? scope, CancellationToken ct = default)
        => new(new RoleDirectoryEntry(PermissionDirectoryAnswer.Unavailable, null));             // existing implementers compile, fail closed
}
public readonly record struct RoleDirectoryEntry(PermissionDirectoryAnswer Answer, IReadOnlyList<Role>? Roles);
```

The runtime `ActorCallerResolver` (registered by `AddYFexAuthorization` when an `IRoleDirectory` is registered):

1. Asks `IRoleDirectory.GetRolesAsync` in the effective scope. `Unavailable` / `UnknownSubject` are returned as they are.
2. Builds `PermissionResolution(actor, Principal: null, scope, roles, now)` and runs it through **the factory's own pipeline**. The
   factory is refactored into one internal path that both `CreateAsync` and the resolver use, so the sources and any later
   step (Licensing's `Restrict`, ⚑Z13 temporary grants) apply to both. The claim sources contribute nothing; the role map and
   subject-keyed sources contribute as usual. No source changes.
3. The context gets a synthetic principal (`ClaimsIdentity("yfex.actor")` with `sub` + role claims). It is authenticated but has no
   `auth_time`, `amr` or `presence_at`, so recent-authentication, assurance and site-presence requirements deny (fail closed).
4. `Version = IPermissionVersions.Get(subject)`; cached by `AuthorizationContextCache` keyed by (subject, scope, version), using a new
   `GetAsync(ActorIdentity, …)` overload. `Invalidate(subject)` and the signals already work.
5. Relation, real actor and restrictions come from the caller of `ResolveAsync` (§2.3, ⚑RA2, ⚑RA3).

No `IRoleDirectory` registered → no resolver registered. Jobs D2 then fails **start-up** when a template needs run-as, as it does for
`SecretRef` without a secret store (Plan D §6.2). The default implementation of `IRoleDirectory` belongs to the role-assignment
store that 03 §3 already plans (`YFex.Authorization.Data`); Authentication never stores roles (authentication 09 §3).

### 2.2 Make that caller current for one flow (`.Public`)

```csharp
// (sketch) YFex.Authorization.Public — AsyncLocal, flows across await, never back to the caller
public static class CallerContext
{
    public static IAuthorizationContext? Current { get; }
    public static Activation Enter(IAuthorizationContext caller);   // also enters ActorContext (ActorScope.From(caller)); dispose restores both
}
```

| Accessor | Change |
|---|---|
| `AmbientPrincipalAccessor`, `SnapshotPrincipalAccessor`, `HttpContextPrincipalAccessor`, `.Testing` `TestPrincipalAccessor` | `CallerContext.Current ?? <today's logic>` |
| YFex `Messaging.Rpc` `CallContextPrincipalAccessor` | `CallerContext.Current ?? CallContext.Current?.Caller ?? inner?.Current ?? …` (one line) |

Precedence: an entered caller (innermost) > RPC call > HTTP request > desktop session (⚑RA4).

**Writer rule (06 §4, extended):** `CallerContext.Enter` is called by Authorization adapters and by **bridges that run work for a
stored actor**: `YFex.Jobs.Templates.Authorization` first, Offline central's replay next (it may keep `CallContext` for correlation
and culture, but the caller would no longer need an RPC type), later Orchestration and the agent loop. They enter only what
`IActorCallerResolver` or `AuthorizationContext.FromSnapshot` produced. Libraries never build a caller themselves.

### 2.3 How a run is recorded (aligned with Audit 02 §3)

Authorization's `Actor` means "whose permissions apply"; Audit's means "the effective principal". For a run-as job both are the
user. The job is the mechanism that acted for them:

| Situation | Actor | Real actor | Relation |
|---|---|---|---|
| User job (template instance) run as its owner | the owner (`User`) | `job:{template}#{id}` (`Job`) | `Delegation` |
| User job run as a service actor (`jobs.instances.edit-any`, Plan D §6.1) | the service actor (`Service`) | `job:{template}#{id}` (`Job`) | `Delegation` |
| System job (code, no run-as) — Audit 02 §3 row as written | `job:{key}` (`Job`) | the user who overrode it, when known | `Delegation` or `None` |

So no new `ActorRelation` value is needed: Audit already documents `Delegation` as covering the "scheduled job owner". **Direction
differs from Audit's examples:** audit 02 §3 and audit 11 §13 write the *job* as actor and the user as real actor. That is right
for a system job, whose permissions are its own. For a run-as row the user's permissions apply, so the user is the actor. If
⚑RA2 is accepted, audit 02 §3 gets a "user job run as its owner" row. Two values
are appended to Authorization's `ActorKind`: `Job = 4`, `System = 5`. Mapping to Audit is **by name, never by cast**, because the
numbers differ (⚑RA2, ⚑RA8).

### 2.4 Audit sees the actor (⚑RA7)

A small `ActorContextAuditActorSource : IAuditActorSource` in **YFex.Audit**. Audit is the reader, and 06 §4 already says readers
reference only the leaf `.Public`. It maps `ActorContext.Current` (actor, real actor, relation, kinds by name) to `AuditActor`, and is
registered by `AddYFexAudit()` before `FixedAuditActorSource`. This also fixes today's gap for HTTP, RPC and desktop sign-ins: none of
them reach Audit now.

### 2.5 What Jobs then does (replaces the `IJobRunAsCheck` sketch of Plan D §6.1)

```csharp
// (sketch) YFex.Jobs.Templates.Authorization — generated adapter, before the body; the engine already entered the row's scope
var resolved = await actors.ResolveAsync(instance.RunAs, realActor: JobActors.For(instance.Key), ActorRelation.Delegation, ct);   // §2.3
switch (resolved.Status)
{
    case ActorCallerStatus.Unavailable:    return JobOutcome.Deferred(now + recheck, "run-as directory unavailable");
    case ActorCallerStatus.UnknownSubject: return Forbidden();                     // jobs.run-as.forbidden, no retry, notify (⚑J22)
}
if (!resolved.Caller!.HasPermission(templatePermission)) return Forbidden();
using (CallerContext.Enter(resolved.Caller))
    return await template.RunAsync(ctx, parameters, ct);                           // commands are authorized and audited as RunAs
```

System jobs (no run-as) that dispatch commands (A2 E4, phase 9) need the same treatment with a `Job` actor whose roles come from
configuration. That is recorded here so E4 does not reinvent it; it is not part of D2.

### 2.6 Where a run-as caller comes from — configurable sources (⚑RA1, decided 2026-11-04)

User: "GetRoles, but configurable and depending on where it is connected. If it is local but needs an authenticated user, there may
be a choice to pick an already connected user or to authenticate on job creation. The same for server / machine jobs."

So `IRoleDirectory.GetRolesAsync` (§2.1) is **one** of three sources, chosen when the instance is created, from the set the app allows
for that placement (Plan D §3.2: central, broker, terminal) and narrowed per template:

| Source | At creation | At each run | Fits | Offline |
|---|---|---|---|---|
| **Directory** (§2.1) | nothing extra: `RunAs` = the creator (or a service actor) | `GetRolesAsync` in the run's scope → resolver pipeline | central / server with a role store; terminals with a replicated store | `Unavailable` → defer (⚑RA6) |
| **Signed-in user** | the creator picks a user who is **already connected on this machine** (the POS operator's session, the desktop session) | runs only while that user's session is present on the machine; its caller (already resolved, with its own sign-in facts) is entered with `CallerContext.Enter` | local desktop / POS jobs that act "as the operator on duty" | the session works offline; no session → defer |
| **Captured at creation** | the creator **authenticates when creating the job** (sign-in or step-up prompt); Authentication issues a revocable grant bound to the instance key (⚑RA9) and Jobs keeps it as an instance secret (`IJobSecrets`, D2a) | the grant is exchanged for a caller (identity proven by the grant; permissions resolved as for Directory, or from the grant's snapshot, ⚑RA9) | machines with no role store and no operator: back-office PCs, server-side machine jobs | per ⚑RA9 |

**Machine / server jobs** (system jobs, or instances that run as a service or device actor) choose among the same sources: Directory
(the service actor's roles), Captured (a service credential or the Installation's device identity, YFex.Installations), and Signed-in
user where a session exists. A machine job never borrows a session it was not configured for.

```csharp
// (sketch) YFex.Jobs.Templates.Authorization — allowed sources per placement, narrowed per template
t.UseAuthorization(a => a.RunAs(r => r
    .Allow(JobPlacements.Central, RunAsSource.Directory)
    .Allow(JobPlacements.Terminal, RunAsSource.SignedInUser | RunAsSource.CapturedAtCreation)
    .Default(JobPlacements.Terminal, RunAsSource.SignedInUser)));
// [JobTemplate(..., RunAs = RunAsSource.Directory | RunAsSource.CapturedAtCreation)]   // a template may narrow, never widen
```

Every source ends in the same place: one `IAuthorizationContext` entered with `CallerContext.Enter` (§2.2), the same recording
(§2.3, ⚑RA2) and the same restrictions (⚑RA3). The run-as check before the body (⚑RA5) uses that caller whatever the source.
`ActorCallerStatus.Unavailable` from any source defers the run; `UnknownSubject`, an expired or revoked grant, or a missing
permission fail it with `jobs.run-as.forbidden` (Plan D ⚑J22).

### 2.7 One actor model in YFex core (⚑RA8, decided 2026-11-04)

The user chose to **move the actor types to YFex core** (`YFex/src/YFex`, base types every library may reference), instead of keeping
two models mapped by name. Five-plus libraries name them today (Authorization, Audit, Authentication, Offline, Capabilities, Jobs,
Search; tests in Cqrs, Messaging, NavigatR, Rules.Admin).

| Moves to core | From | Notes |
|---|---|---|
| `ActorKind` | Authorization `.Public` + Audit `AuditActorKind` | **one numbering (⚑RA10):** recommended Audit's superset `Anonymous 0, User 1, Service 2, Device 3, Job 4, Agent 5, System 6`, so `default` = anonymous. Authorization's current numbers (`User 0 … Agent 3`) change: every place that persists or transmits the number (offline snapshots/envelopes, tokens, Audit entries) must be checked before the move |
| `ActorRelation` | both (same names and values 0–4 today) | merged as is |
| `ActorIdentity` (subject, kind, home scope) | Authorization `.Public`; replaces Audit's `AuditActor` | `HomeScope` stays text in core? core may reference YFex.Tenancy's `ScopeIdentity` only if Tenancy types are in core (⚑RA11) |
| `ActorScope`, `ActorContext` (the ambient actor) | Authorization `.Public` | readers (Audit, Logging, Cqrs cache scope) then need no Authorization reference at all |

Stays in Authorization: callers (`IAuthorizationContext`), `CallerContext`, the resolver, permissions. Stays in Audit: entries, chains,
codec (which now writes the core types). Consequences:
- ⚑RA2's "append `ActorKind.Job` / `System`" is covered by the merged enum.
- ⚑RA7's adapter becomes trivial (`ActorContext.Current` is already Audit's actor type); it still lives in YFex.Audit.
- This is a **breaking change** across the listed repos; it is done as its own step before Jobs D2b, with the backup rule of 09 §0.

## 3. Ownership check (2026-11-04)

| Piece | Owner | Why not elsewhere |
|---|---|---|
| `IActorCallerResolver`, `ActorCallerResolution`, `CallerContext` | `YFex.Authorization.Public` | callers and `ActorContext` already live there; named by Jobs, Offline, Orchestration, the agent loop, so a contract package (not the runtime). Not core: only Authorization's own types are involved |
| Resolver implementation | `YFex.Authorization` runtime | it is the factory pipeline (sources, Licensing mask) |
| Role list for a subject | `IRoleDirectory` (existing, `.Public`); default store in the planned `YFex.Authorization.Data` | a new `IActorDirectory` would duplicate R6's directory; Authentication never stores roles |
| Actor → audit actor | `YFex.Audit` adapter over `.Public` | Audit is the reader (06 §4); Authorization must not reference Audit |
| `job:` actor naming, run-as check, deferring | `YFex.Jobs.Templates.Authorization` | Jobs-specific; Authorization stays domain-free |
| Offline replay switching to `CallerContext` | YFex.Offline (optional follow-up) | its own code; not required for Jobs |

## 4. Effect on consumers, AOT

| Change | Consumers affected |
|---|---|
| new `.Public` types | none |
| `IRoleDirectory.GetRolesAsync` (default member) | implementers compile unchanged; they answer `Unavailable` until they implement it |
| `ActorKind.Job = 4`, `System = 5` (appended) | exhaustive switches over `ActorKind` add cases; existing values keep their numbers |
| accessors read `CallerContext.Current` first | behaviour changes only inside an entered caller (nobody enters one today) |
| Audit adapter | Audit entries gain the real actor on HTTP/RPC/desktop where today they are `Anonymous` or fixed |

AOT and trimming: no reflection; an `AsyncLocal` and DI registrations only. Dependency direction is unchanged: `.Public` still
references only YFex core. Audit gains a reference to the leaf `YFex.Authorization.Public`.

## 5. Offline

On a terminal that cannot reach its role store, `GetRolesAsync` answers `Unavailable`, and Jobs defers without failing (Plan D §6.1).
An app with a replicated role store on the terminal may answer from it. ⚑RA6 covers a last-known cached caller.

## 6. Acceptance (headline)

- **Resolving:** an actor holding role R in site S gets R's mapped permissions in S, nothing from claim sources, and the Licensing
  mask once it exists. `Unavailable` and `UnknownSubject` pass through unchanged.
- **Hosts:** inside `CallerContext.Enter`, a Cqrs command is authorized as the actor on a desktop host signed in as another user, on
  an ASP.NET Core host outside a request, and inside an RPC call. After dispose the previous caller is back; parallel flows do not
  see each other's caller.
- **Audit:** an entry written in that flow has actor = owner, real actor = `job:{key}`, relation = `Delegation`, kinds mapped by name.
  An HTTP request's entry gets the signed-in actor instead of `Anonymous`.
- **Step-up and changes:** recent-authentication and site-presence requirements deny a resolved actor. `IPermissionVersions.Bump`
  makes the next resolve see the change. With ⚑RA3 = restrict, a privacy reveal is denied with `RestrictedWhileImpersonating`.
- **AOT:** smoke 0 IL/AOT warnings; `.Public` references nothing new.

## 7. Open decisions

| # | Question | Recommendation | Alternative |
|---|---|---|---|
| RA1 | Where do a stored actor's roles come from? | **Decided 2026-11-04 (user): configurable sources (§2.6)** — Directory (`GetRolesAsync`), Signed-in user, Captured at creation; allowed per placement, narrowed per template. Was: **an additive `IRoleDirectory.GetRolesAsync`** (default = `Unavailable`); the default store is the planned `YFex.Authorization.Data` | a new `IActorDirectory` (duplicates R6); or one `IsInAnyRoleAsync` call per mapped role (N calls per run) |
| RA2 | How is a run recorded? | **Decided 2026-11-04 (user, as recommended).** **§2.3:** actor = owner or service actor, real actor = the `Job` actor, relation = `Delegation`; append `ActorKind.Job`, `ActorKind.System` | a new `ActorRelation.Scheduled` (Audit's enum and codec would need it too); or the job as actor (then "whose permissions" is wrong) |
| RA3 | Restrictions while running as someone | **Decided 2026-11-04 (as recommended):** **apply `ImpersonationRestrictions` by default** (no privacy reveal or detokenize: nobody is at the keyboard); per-app override | the actor's full permissions |
| RA4 | How accessors honour `CallerContext` | **Decided 2026-11-04 (as recommended):** **one line in each built-in accessor**; app-written accessors documented to do the same | a decorator registered by `AddYFexAuthorization` (one place, but order-sensitive with Rpc's own decorator) |
| RA5 | Jobs' check before the body | **Decided 2026-11-04 (as recommended):** **the resolved caller only** (one lookup, the same answer the pipeline sees) | `IPermissionDirectory` for the check + resolver for dispatch (two lookups that can disagree) |
| RA6 | Offline terminals | **Decided 2026-11-04 (as recommended):** **`Unavailable` → defer** in v1 (Directory source; the other sources in §2.6) | reuse a last-known cached caller up to a max age (runs, but on stale permissions) |
| RA7 | Who maps the actor into Audit? | **Decided 2026-11-04 (as recommended):** **an adapter in YFex.Audit over the leaf `.Public`** (§2.4) | the app registers its own `IAuditActorSource` (today's state: nobody does) |
| RA8 | One actor model or two? | **Decided 2026-11-04 (user): move the actor types to YFex core (§2.7).** Was: **keep two for now, mapped by name**, and decide unification separately: Audit 02 §3 predates authorization 06 §4, which said Audit's actor "becomes `ActorIdentity` + relation" | Audit adopts `.Public`'s types now (touches the Audit codec and entry layout); or move the actor types to YFex core (five libraries name them) |
| RA9 | The "captured at creation" grant (§2.6) | **Open.** Recommended: Authentication issues a delegation grant (audience = the instance key, holder = this Installation, sliding 90 days, revoked by sign-out-everywhere, password reset, user disabled or instance delete); permissions are resolved per run like Directory when reachable, and offline from the grant's snapshot up to a max age (default 24 h) | the grant only proves identity; offline always defers (as ⚑RA6) |
| RA10 | One `ActorKind` numbering (§2.7) | **Open.** Recommended: Audit's superset (`Anonymous 0 … System 6`, `default` = anonymous); check every persisted/transmitted number first | keep Authorization's numbers and renumber Audit's |
| RA11 | Core namespace and `HomeScope` type (§2.7) | **Open.** Recommended: namespace `YFex.Actors`; `HomeScope` stays canonical text (core does not reference Tenancy) | `ScopeIdentity` if Tenancy's identity type moves to core too |
