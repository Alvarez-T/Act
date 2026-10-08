# 07 — Data-access permissions: Privacy, Audit, Logging

These designs are not implemented yet (Privacy Tier 1 is landing now: classification + masks, no reveal). This file fixes how they
**plug into** the consolidated model, so they never grow a private permission check.

## 1. Reveal = classification (Privacy) × requirement (Authorization)

Division of work:

- **YFex.Privacy** owns *what* data is (`[Sensitive]` once on the value object or the record type) and the **`RevealPolicy`**
  (which requirement reveals which sensitivity/category, per tenant; privacy 04 §11, 08 §1).
- **YFex.Authorization** owns *evaluating* that requirement for the caller (permissions, impersonation restrictions, justification,
  approvals) and emits the `Revealed` authorization event.

```csharp
// ⚙ YFex.Privacy (future) — library default, expressed with Authorization requirements (tenant overrides tighten it)
public static RevealPolicy Default { get; } = RevealPolicy.Create(policy => policy
    .For(DataSensitivity.Personal).Clear()                                                   // anyone who can open the screen
    .For(DataSensitivity.PersonalIdentifier).Require(PrivacyPermissions.RevealIdentifier)
    .For(DataSensitivity.SensitivePersonal).Require(PrivacyPermissions.RevealSensitive).WithJustification()
    .For(DataSensitivity.Financial).Never()                                                   // always ****1234
    .For(DataSensitivity.Secret).Never());
// RevealPolicy.Compile() → one CompiledPolicy per (sensitivity, category) — same evaluator, same verdicts (03 §5)
```

```csharp
// ✍ Dev writes — NOTHING per screen or per member (api-naming rule: classify on the type)
[Sensitive(DataSensitivity.SensitivePersonal, Category = "health")]                  // privacy ⚑P24, record-level
public sealed partial record ControlledDispensing(Guid Id, CustomerRef Customer, MedicationRef Medication, decimal Quantity);
```

**Result:**

- **UI** (privacy 08 §1): the presenter asks `IPolicyEvaluator.IsVisible(reveal.For(classification), caller)`. Without
  `privacy.reveal.sensitive` the grid shows masks. With it, the eye button calls `Authorize`, gets `JustificationRequired`, asks for
  a reason, then reveals. The `Revealed` event goes to audit with the real actor.
- **Server-side omission** (privacy ⚑P17): the query stage uses the same compiled reveal policies per member classification
  (generated `PrivacyHints`), so the client never receives what it cannot see.
- **While impersonating**: reveal permissions are in `ImpersonationRestrictions` (06 §3), so support staff see masks.
- **Health data screens**: no `[RequirePermission]` needed for reading masked lists. Opening the *clear* record is the reveal
  requirement. A screen that must not open at all for some roles still uses a route permission (04 §3).

## 2. One reveal permission family (⚑Z15)

The audit design defines `audit.reveal` and `audit.reveal.sensitive` (audit 08 §5), parallel to `privacy.reveal.identifier/sensitive`.
Recommendation: **one family**. Revealing a CPF is the same act whether it appears on a customer screen or inside an audit entry.

| Permission | Owner catalog | Meaning |
|---|---|---|
| `privacy.reveal.personal` / `.identifier` / `.sensitive` | `PrivacyPermissions` | see values of that sensitivity in clear, anywhere (UI, exports, audit viewer) |
| `privacy.detokenize`, `privacy.requests.handle`, `privacy.legal-hold.manage`, `privacy.notices.publish` | `PrivacyPermissions` | as in privacy 08 §10 |
| `audit.read` | `AuditPermissions` | read audit entries of the caller's scope (values masked unless a reveal permission also applies) |
| `audit.read.all` | `AuditPermissions` | platform staff: all tenants; each use audited with the target tenant |

Audit's viewer then evaluates `audit.read` AND the reveal policy per value. It needs no `audit.reveal*`.

## 3. Consent is not a permission (unchanged, restated)

`RequirePermission` checks the **caller**. `RequireConsent(purpose)` (privacy 04 §10, `.Privacy(...)` in aggregate configuration)
checks the **data subject**. Server order: authorization stage (caller may do it?) → privacy stage (subject agreed / legal basis?)
→ rules → handler. A missing consent is `Error.ConsentRequired`, never a `Forbidden` verdict.

## 4. Logging

`LoggingPermissions` (`logging.verbose.enable`, `logging.records.read`, `logging.records.export`) gate the remote control channel
and the viewer through normal message policies. The scoped verbose override keeps its own TTL and audit (06 §2, last paragraph).
Logging reads the actor from `ActorContext` (06 §4) and logs denials it receives as authorization events (§5).

## 5. Authorization events (⚑Z17, confirmed: through the core probes)

Authorization references no engine. It emits point events through the existing core probe contract (`YFex.Diagnostics.Operations`,
the same contract every library uses for logging), and later also through Audit's core contract when Audit Phase 0 exists. With no
engine installed the cost is one `IsEnabled` check.

```csharp
// (sketch) YFex.Authorization runtime — one descriptor per event kind, registered once
internal static class AuthorizationEvents
{
    public static readonly OperationDescriptor<AuthorizationEvent> Denied           = …;   // authoritative (server) denials only, rate-limited per subject
    public static readonly OperationDescriptor<AuthorizationEvent> ApprovalIssued   = …;
    public static readonly OperationDescriptor<AuthorizationEvent> ApprovalAccepted = …;   // includes Flagged for offline approvals
    public static readonly OperationDescriptor<AuthorizationEvent> GrantChanged     = …;   // issued / revoked / expired
    public static readonly OperationDescriptor<AuthorizationEvent> ImpersonationStarted = …;
    public static readonly OperationDescriptor<AuthorizationEvent> Revealed         = …;   // subject index, member, screen, justification
}
// emit:  Operations.MarkWith(AuthorizationEvents.ApprovalIssued, in authorizationEvent, LogLevel.Information);
// AuthorizationEvent implements ILogValue<AuthorizationEvent>: actor, real actor, relation, permission, fingerprint, terminal, reason.
```

```
14:02 maria (supervisor) approves VoidSale #123  → ApprovalIssued   {actor: maria, permission: sales.void.approve, fingerprint, terminal}
14:02 joao (cashier) resends with the token      → ApprovalAccepted {actor: joao, approver: maria, flagged: false}
14:05 joao calls ReportsFiscal (no permission)   → Denied           {actor: joao, missing: reports.fiscal.read}
```

Menu items hidden by the quick (visibility) check are **not** events. YFex.Audit maps these descriptors to its `Security` category
(audit design amendment); YFex.Logging records them as diagnostic records. This closes the audit-inventory gap "approval-token
issuance is not audited".
