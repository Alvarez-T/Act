# YFex.Authorization — Consolidation of every "who may do/see what" mechanism — Design

> **Status:** APPROVED (2026-10-02). All decisions **⚑Z1–Z19 are confirmed** by the user (table at the end, with the final wording).
> Implementation follows [09-phases-and-acceptance.md](09-phases-and-acceptance.md) (Phases 0–2b and 3a done; Phase 3 decisions
> P1–P7 at the end). No NuGet package is
> added: everything uses the BCL (`HMACSHA256`, `ECDsa`, `FrozenDictionary`) and projects that already exist.
> **Audience:** the sessions that implement the phases. Read this file, then the file your phase needs.
> **Normative vs sketch:** names in code blocks are normative unless marked `// (sketch)`. Where a decision changed a detail of a
> later file, **this README's decision table wins**.
> **Supersedes:** `../authorization-design.md` and `../authorization-capabilities-overview.md` (most of them was implemented;
> [01](01-current-state-and-inventory.md) §1 lists what exists).
> **Out of scope, separate design:** offline operation on a standalone device vs a shop LAN with a local "store server", including
> how cached authorization data is protected against tampering — `../offline/` (separate session). This design only
> defines the snapshot format and its integrity hook (05 §2).

## Why

YFex already has a good authorization library. Most libraries consume its contracts. The problems are the **seams around it**
(full inventory with paths: [01](01-current-state-and-inventory.md)):

- **D1 (security):** `[RequirePermission]` on a message and `b.ForMessage<T>()` fill a table that **no CQRS dispatcher reads**. Only
  HTTP enforces it.
- **D2/D3:** Cqrs has its own authorizer vocabulary over `ClaimsPrincipal`. `.RequireAuthorization("x")` and `.Authorize<T>()`
  **always deny**. Messaging.Rpc needs a `[ThreadStatic]` hand-off to avoid rebuilding the caller's context.
- **D7/D8:** permission versions, change signals and the permission snapshot live inside the RPC transport. Desktop/HTTP hosts and
  client UIs cannot use them.
- **D9/D10:** Rules has a private approval-token system and Cqrs a second "does user X hold P" directory. Privacy reveal,
  impersonation and plain commands need the same mechanism.
- **D11:** impersonation reads a raw case-sensitive claim, ignores roles, works only over HTTP and loses the real actor.
- **D12:** search/AI show actions using one permission per capability, then dispatch denies them.
- **D5:** Cqrs and Rules permissions are never checked against catalogs, so a typo is an approval nobody can give.

## The mental model

```
   DECLARE (once, where it belongs)            COMPILE (startup, one table)                 ASK (runtime, one evaluator)
 ┌───────────────────────────────────┐    ┌───────────────────────────────────────┐    ┌────────────────────────────────────────┐
 │ catalogs   [PermissionCatalog]    │    │ catalog check (Cqrs, Rules, routes…)  │    │ IMessageAuthorization  (dispatchers)   │
 │ messages   aggregate configuration│───►│ PolicyKey → CompiledPolicy            │───►│ IPolicyEvaluator.IsVisible (UI/search) │
 │            (attribute also works) │    │   permission mask + roles + resource  │    │ verdict: Allowed | Unauthenticated |   │
 │ screens    [RequirePermission] VM │    │   + policy + approval + justification │    │  Forbidden(missing) | ApprovalRequired │
 │ commands   [RequirePermission]    │    │ caller context per (subject, scope,   │    │  | JustificationRequired               │
 │            on [StateCommand]      │    │   version) — bitset, cached           │    │ events → core probes (logging/audit)   │
 │ data       [Sensitive] (Privacy)  │    │                                       │    │                                        │
 └───────────────────────────────────┘    └───────────────────────────────────────┘    └────────────────────────────────────────┘
```

## One example, end to end

```csharp
// ✍ Dev writes — the catalog declares Permission properties; the generator implements them (⚑Z2)
[PermissionCatalog]
public static partial class SalesPermissions
{
    [Permission("sales.void", Name = "Cancelar venda")]                 public static partial Permission SaleVoid { get; }
    [Permission("sales.void.approve", Name = "Aprovar cancelamento")]   public static partial Permission SaleVoidApprove { get; }
}

b.Command<VoidSale, Receipt>()                                                     // aggregate configuration
    .RequirePermission(SalesPermissions.SaleVoid)
    .RequireApproval(SalesPermissions.SaleVoidApprove, bind: command => command.SaleId);

public partial class SaleViewModel : PageViewModel
{
    [StateCommand, RequirePermission(SalesPermissions.Ids.SaleVoid)] private Task VoidAsync(CancellationToken ct) => …;   // attributes use Ids
}
```

**Result, with no other code:** the Void button is disabled for cashiers without `sales.void`. Search and the AI agent offer "Cancelar
venda" only to holders. The server (LocalDispatcher, RPC, Wolverine, HTTP) denies non-holders with `Error` case
`Forbidden(missing: sales.void)`. A holder gets `ApprovalRequired`, which opens the same supervisor dialog the rules engine already
uses. The approval is a signed token bound to that sale. Offline tokens are capped and flagged. Issuance, use and server-side denials
are emitted as authorization events through the core probes, with the real actor, even during impersonation.

## Files

| # | File | Content |
|---|---|---|
| 01 | [01-current-state-and-inventory.md](01-current-state-and-inventory.md) | every seam with paths, defects D1–D19, false positives, test baseline |
| 02 | [02-target-model.md](02-target-model.md) | home, layering, ownership table, thin adapters, public surface |
| 03 | [03-permissions-and-evaluation.md](03-permissions-and-evaluation.md) | catalogs, interned ordinals + `PermissionSet` (with real usage examples), caller context + sources, `Requirement`/`CompiledPolicy`, verdict + `[Union]` `Error`, startup validation, analyzers, budgets |
| 04 | [04-gates-messages-routes-capabilities.md](04-gates-messages-routes-capabilities.md) | CQRS single gate (before/after), Rules.Admin, NavigatR, view-model commands, Capabilities/Search/AI, HTTP |
| 05 | [05-snapshots-versions-signals.md](05-snapshots-versions-signals.md) | what moves out of Messaging.Rpc, signed `PermissionSnapshot`, client + offline hook, signals |
| 06 | [06-approvals-grants-impersonation.md](06-approvals-grants-impersonation.md) | approvals (from Rules), temporary grants + core `ValidityPeriod`, impersonation, `ActorContext`, justification |
| 07 | [07-data-access-privacy-audit-logging.md](07-data-access-privacy-audit-logging.md) | reveal = classification × requirement, one reveal family, consent ≠ permission, logging, events through core probes |
| 08 | [08-old-to-new-mapping.md](08-old-to-new-mapping.md) | every old type/member → new, with phase |
| 09 | [09-phases-and-acceptance.md](09-phases-and-acceptance.md) | working rules, phases 0–7, acceptance criteria, doc updates, implementation log |

## Open questions (not blocking Phases 0–6)

- **Farms:** which shared store backs `IPermissionVersions` (Postgres row, Redis, bus-fed in-memory)? Today in-memory only.
- **Roles catalog:** should app roles get a `[RoleCatalog]` (display names + default role → permission map for an admin screen),
  like permissions? Today roles are free strings from the identity provider.
- **Identity provider claims shape** for `scoped_role`/`scoped_permission` and the RFC 8693 `act` claim (user impersonation).
- **Offline/LAN topology** — owned by the `offline` (YFex.Offline) design (see the status block).

## Decisions (⚑Z) — CONFIRMED 2026-10-02

| ⚑ | Decision (final) | Details |
|---|---|---|
| **Z1** | Repo **`YFex.Authorization`** is the single home. **No authorization contract in YFex core.** The leaf assembly `YFex.Authorization.Public` (references only YFex core + BCL) carries the types lower libraries need. YFex core only gains the generic `ValidityPeriod` (Z13). | 02 §2 |
| **Z2** | `Permission`/`PermissionKey` stay in `.Public`. YFex.Rules may reference **`.Public` only** (YFex/CLAUDE.md layer rule updated in Phase 0) and takes typed `Permission`; dynamic JSON specs keep strings, resolved against declared ids at publish ("both options"). **Catalog syntax:** `[Permission("id", Name = "…")] public static partial Permission X { get; }`; the generator implements the property and emits `Catalog.Ids.X` (`const string`) for attribute arguments. Code uses `SalesPermissions.SaleVoid` (no `Values.`); attributes use `SalesPermissions.Ids.SaleVoid`. Ids are always explicit (never derived from names). | 03 §1 |
| **Z3** | `ActorIdentity`, `ActorKind`, `ActorRelation`, `ActorScope`, `ActorContext` live in **`.Public`** (owner = the writer = Authorization adapters; Audit/Logging/Tenancy/Cqrs only read). `.Public` must stay a leaf (core + BCL; a test enforces it), so `IAuthorizationContext.Scope` is the canonical scope **text**, not `ScopeIdentity`. Supersedes `YFex.Auditing.ActorContext` in audit 02. | 06 §4 |
| **Z4** | Hard renames, no shims: `IAuthContext`→`IAuthorizationContext` (+ `AuthorizationContext`, `IAuthorizationContextFactory`), `[PermissionMeta]`→`[Permission(...)]` (Z2), Capabilities `Authz`→`Authorization`, `CallContext.Auth`→`Caller`. | 08 |
| **Z5** | One message policy table: Cqrs configuration verbs record `Requirement`s exported to the Authorization registry; every dispatcher asks `IMessageAuthorization`. Behaviour change: `ForMessage<T>()` rows and `[RequirePermission]` on message types become enforced on CQRS dispatch. | 04 §2 |
| **Z6** | **No AUTH005 for now.** `[RequirePermission]` on CQRS records keeps working and feeds the same table (enforced per Z5); configuration stays the recommended place. | 04 §2.3 |
| **Z7** | Break the Cqrs authorize API: lambdas get `IAuthorizationContext`; `ICommandAuthorizer/IQueryAuthorizer` → `IAuthorizationRequirement<T>` / `IAsyncAuthorizationRequirement<T>` (DI singletons, actually executed); `RequireRoles(string[])` removed; `RequireAuthorization(string)` → `RequirePolicy(PolicyName)`. | 04 §2.2 |
| **Z8** | Server dispatch = full (async-capable) check with a synchronous fast path; UI/search = quick check, undecidable = hidden, refined by `VisibleWith` on requirement classes; inline lambdas don't affect visibility. | 04 §2.4 |
| **Z9** | Versions/signals/snapshot move to Authorization; clients `UseServerPermissions()`; the persisted snapshot is **server-signed and verified before use**, offline validity configurable. Storage protection, LAN "store server" and trust/pairing are delegated to the `offline` (YFex.Offline) design. | 05 |
| **Z10** | Interned ordinals (declared ids only) + `PermissionSet` bitset; undeclared token ids dropped (never granted); no runtime-registered permissions for now (possible later "runtime catalog" for Composer). | 03 §2 |
| **Z11** | `[Union]` `AuthorizationVerdict`, **and the Cqrs `Error` becomes a `[Union]` tagged struct** (`ErrorKind` tag; cases `NotFound`, `Failed`, `Unauthenticated`, `Forbidden(Denial)`, `ValidationProblem`, `Conflict`, `ApprovalRequired`) as its own step in Phase 2 (2b). | 03 §5 |
| **Z12** | Approvals move to Authorization as `ApprovalToken*` (rename of `OverrideToken*`, byte-identical payload, golden test); `IDecisionBound` → Cqrs; `IApproverDirectory` → `IPermissionDirectory`; `.RequireApproval(permission, bind)` + `Approvals.Commands.Approve` replace `RuleAdmin.Commands.ApproveDecision`. | 06 §1 |
| **Z13** | `TemporaryGrant` (no escalation, ≤ 24 h default, signals on start/expiry, events) lives in Authorization; its validity window is the new reusable core type **`ValidityPeriod`** (also used by `ApprovalToken`, `PermissionSnapshot`, later rules/consent/legal holds/logging TTL). Logging's verbose override stays in Logging, protected by `logging.verbose.enable`. | 06 §2 |
| **Z14** | Impersonation through `PolicyTenantImpersonationGate` (`tenant.impersonate` on the real actor), `ImpersonationRestrictions` mask, `ActorContext` relation; RPC parity in Phase 6; user impersonation with its feature. | 06 §3 |
| **Z15** | One reveal family `privacy.reveal.personal/identifier/sensitive` (library-level data protection, domain-agnostic); Audit keeps only `audit.read` / `audit.read.all`. | 07 §2 |
| **Z16** | YFex.Jobs execution policy untouched, no rename. | 01 §2.7 |
| **Z17** | Authorization events are emitted through the **core probe contracts** (`YFex.Diagnostics.Operations` now; Audit's core contract when Audit Phase 0 exists). No `IAuthorizationEvents` interface. Only server-side (authoritative) denials, rate-limited per subject. | 07 §5 |
| **Z18** | New now: `YFex.Authorization.Testing`, `YFex.Authorization.Benchmarks`. Later with a role-admin feature: `YFex.Authorization.Data`. | 02 §2 |
| **Z19** | Phase 5: the Authorization generator implements a new State hook `__OnCanExecute_<Command>` + re-check on signals. | 04 §4 |

## Phase 3 decisions — CONFIRMED 2026-10-04

The cryptography design (approved 2026-10-04, after this one) reserves every signature to YFex.Cryptography (⚑K3), whose signing
APIs arrive in its Phase 2; the offline design made the persisted snapshot a `SignedBundle` of kind `authorization.snapshot`.
**User rule:** questions about signing, cryptography or hashing wait until YFex.Cryptography is done in its own session. Existing
code that already meets a need may be used; anything else is set apart in 09 ("Deferred until YFex.Cryptography").

| # | Decision (final) | Where |
|---|---|---|
| **P1** | Phase 3 is split. **3a** (now): versions, signals, context cache, snapshot moved to Authorization; `ICallPermissions` returns the (unsigned) `PermissionSnapshot`; client `UseServerPermissions()` online only; actor writers. **3b** (after YFex.Cryptography Phase 2 and the offline bundle format): signing, verification, persisted offline copy. No interim ECDsa inside Authorization. | 09 Phase 3 |
| **P2** | `PermissionSnapshot` is **payload only** (subject, authenticated, scope, version, sorted permission ids, sorted roles, relation). Issue time, validity, key id and signature belong to the signed envelope (3b). Supersedes the `Validity`/`KeyId`/`Signature` members of the 05 §2 sketch. | 05 §2 |
| **P3** | A persisted snapshot is used offline **only after an offline sign-in established the subject** (offline design: "no snapshot, no offline login"). Until then a client started without a connection is anonymous. The 09 acceptance "offline start within/after validity" moves to 3b / offline Phase 3. | 05 §3 |
| **P4** | Desktop/POS hosts get a **process-wide session actor** (`ActorContext.SetSession`/`Session`) under the `AsyncLocal` scope; `AmbientPrincipalAccessor` is process-wide (`SignIn`/`SignOut`, re-resolved after a signal). Request adapters always enter a scope (`ActorScope.Anonymous` for anonymous callers) so a server never falls back to a session. | 06 §4 |
| **P5** | `ICacheScopeContext.User : ClaimsPrincipal` → `string? Subject`; default = `ActorContext.Current` subject; a `ScopedByUser()` query with no subject is **not cached** (it used to share one key across users; `Identity.Name` was not unique either). | 08 |
| **P6** | `ICallPermissions` changes its return type (hard break, like ⚑Z7); proxies regenerated through the golden test. | 08 |
| **P7** | Snapshot validity uses a plain `TimeProvider` until trusted time exists; the reusable trusted-time library is `../time/README.md` (⚑T1–T7 approved 2026-10-04). | 05 §3, `../time` |

## Phase 4 decisions — CONFIRMED 2026-10-04

Context: YFex.Cryptography delivered the library side of approval tokens (crypto 12 §5 "Handoff to Authorization": HMAC key ring
`approvals/server`, domain `yfex.approval.v2`, v1 verification through `MessageDomain.Raw`, signing keys, v1 golden
`YFex.Rules.Tests/Golden/approval-token-v1.tsv`). **Crypto ⚑K12 supersedes the "byte-identical payload" part of ⚑Z12:** new tokens
are v2 (SHA-256 decision digest, domain separation); v1 tokens keep verifying for one validity window. **All token cryptography uses
YFex.Cryptography** (user, 2026-10-04).

| # | Decision (final) | Where |
|---|---|---|
| **A1** | Phase 4 is split. **4a** (no cryptography): typed `Permission` in Rules (directives, tables, `ApprovalRequest`; Rules.Dynamic resolves ids at publish), `RuleAdminPermissions` catalog, typed `RuleAdminOptions`, gate requirements instead of `Demand`/`PermissionFor`. **4b**: approval tokens v2 on YFex.Cryptography, `.RequireApproval`, the Approve command, `IPermissionDirectory`, approval events. | 09 Phase 4 |
| **A2** | The Approve command lives in a new project **`YFex.Cqrs.Approvals`** (YFex repo, next to Cqrs; imports the Messaging.Rpc targets like Rules.Admin so its RPC contract is generated). YFex.Cqrs cannot host it: it has no RPC generation and cannot reference the token issuer. | 06 §1.1 |
| **A3** | `IApprovalBound`, `ApprovalToken`, `IApprovalTokenIssuer`/`IApprovalTokenVerifier` live in **`.Public`** (the gate in the runtime reads the tokens off any message); `IDecisionBound : IApprovalBound` lives in YFex.Cqrs; implementations in the Authorization runtime on YFex.Cryptography. | 06 §1.1 |
| **A4** | v2 token body = kind, approver **subject** (never the display name), permission id, decision digest (`Hash256`), **approver scope** (a supervisor of store A cannot approve for store B), validity (`ValidityPeriod`), key id. | 06 §1 |
| **A5** | Wire break (like ⚑Z7/P6): `Error.ApprovalRequired` also carries the digest (RPC wire v5); `Approve(permission, digest)` replaces `ApproveDecision(permission, fingerprint)`. `.RequireApproval(p, bind: c => c.X)` digests the message type + the bound value; bound values are `string`, `Guid`, integers or core types with canonical bytes. | 06 §1.3 |

Still open (needed for 4b, not for 4a): offline device tokens on `ISigningKey` + an app-supplied `IPublicKeyDirectory` with the
age still measured at replay (F7 waits for offline envelopes + YFex.Time); the v1 cut-off option; `IPermissionDirectory` without a
default implementation.
