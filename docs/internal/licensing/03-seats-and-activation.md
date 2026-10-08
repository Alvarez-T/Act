# 03 — Seats, activation, moving a seat, trial

## 1. What a seat is (⚑LC6)

A **seat** is used by every installation that is

- of a **counted role** of the product (`CountSeats(...)`, default `Terminal` + `SiteBroker`; `Central` never counts: central instances
  scale up and down and are the vendor's or the customer's server, not a workplace), and
- `Enrolled` or `Provisional` in the Installations registry (a `Revoked` installation frees its seat at once), and
- of the License's product.

Seats are counted per **scope unit**: the installation's enrolled scope cut to `ScopeUnitLevel` (default 1). For FarmaViva:

| Installation | Enrolled scope | Scope unit | Seat |
|---|---|---|---|
| PC-GERENTE broker service | `0=farmaviva/1=loja-centro` | `…/1=loja-centro` | `SiteBroker` |
| PC-GERENTE desktop app, CAIXA-01, CAIXA-02, CAIXA-03 | `0=farmaviva/1=loja-centro` | `…/1=loja-centro` | `Terminal` × 4 |
| a stock-room handheld enrolled at `…/1=loja-centro/2=deposito` | level 2 | `…/1=loja-centro` (cut to level 1) | `Terminal` (counts for Loja Centro) |
| NOTEBOOK-REGIONAL | `0=farmaviva/1=regiao-sul` | `…/1=regiao-sul` | `Terminal` — and `regiao-sul` uses one of the 40 scope units (see §1.1) |
| central containers | `0=farmaviva` | — | none |

The License limits:

```csharp
Seats:  [ new SeatLimit(InstallationRole.Terminal,   PerScope: 4, Total: 120),
          new SeatLimit(InstallationRole.SiteBroker, PerScope: 1, Total: 40) ]
Scopes: new ScopeLimit(Level: 1, Count: 40)        // scope units with at least one counted installation
```

A role with no `SeatLimit` in the License is **unlimited**; a counted role must still be activated (§3), so the vendor always knows
how many there are.

### 1.1 Known edge: scope units that are not stores

FarmaViva's tenancy tree puts regions and stores at the same level, so the regional laptop's scope uses a scope unit. Options for the
vendor, without any library change: size `ScopeLimit` with that in mind ("40 stores + 3 regions" = 43), or enroll regional machines
at the tenant scope (`0=farmaviva`, then they are in the tenant-level activation list). A Tenancy "scope kind" (store vs region) would
allow `ScopeLimit` per kind; that is an open question for YFex.Tenancy, not needed for v1.

## 2. Enforcement at central: the enrollment policy (⚑I23 hook 2)

```csharp
// YFex.Licensing.Central — registered by AddYFexLicensingCentral
internal sealed class LicensingEnrollmentPolicy(ISeatCounter seats, ICentralLicenses licenses) : IEnrollmentPolicy
{
    public async ValueTask<EnrollmentDecision> DecideAsync(EnrollmentCandidate candidate, CancellationToken cancellationToken)
    {
        // candidate: tenant, scope, role, product(s) of the app, Replaces (optional), hint — inside the registry's enrollment transaction
        if (!licenses.TryGet(candidate.Tenant, out License? license)) return EnrollmentDecision.Allow;   // identity ≠ licensing (§2.1)
        SeatCount count = await seats.CountAsync(license, candidate.Scope, candidate.Role, excluding: candidate.Replaces, cancellationToken);
        return count.WouldExceed(license)
            ? EnrollmentDecision.Refuse("licensing.seats")          // or "licensing.scopes" when it would open a 41st scope unit
            : EnrollmentDecision.Allow;
    }
}
```

- **Counting is atomic with the insert.** Installations calls the policy inside the registry's enrollment transaction (per-tenant
  lock), so two checkouts enrolling at the same second cannot both take the last seat (request in [10](10-phases-and-acceptance.md) §2).
- **Asked earlier too:** the admin screen calls `ISeatAvailability.GetAsync(scope, role)` before creating an enrollment code, and
  Installations also asks the policy at code creation (candidate without keys), so the admin learns about the limit before going to
  the shop (01 §5).
- The refusal reaches the installation as `EnrollmentRefused(EnrollmentRefusal.PolicyRefused, PolicyCode: "licensing.seats")`.
- **No overdraft:** the honest path is "replace" (§4) or a bigger license, which the vendor issues in minutes.

### 2.1 No License at central yet

Enrollment is identity, not licensing: with no License (an on-premises central before its activation code was entered), enrollment is
**allowed** and nothing is counted. The installations run with the product's `UnlicensedBehavior` until a License arrives; then the
vendor's activation lists decide which installations hold seats (oldest enrollment first, deterministic), and central shows which ones
are over the limit.

## 3. Activation: the vendor's independent check (⚑LC7)

Central's policy stops honest over-enrollment, but an **on-premises central is the customer's code on the customer's server**. The
installation therefore also requires to be listed in the **vendor-signed `ActivationList` of its scope unit**:

```
 CAIXA-03 enrolls at FarmaViva's central ──► registry event InstallationEnrolled
     │                                          │
     │ works at once: SeatPending (7 days)      ├─ SaaS: IVendorLicensing.ActivateAsync in-process ─┐
     │                                          └─ on-premises: activation delta queued; central     │
     │                                             starts a check-in now when online (05 §3) ────────┤
     │                                                                                               ▼
     │                                   VENDOR: within PerScope / Total / ScopeLimit? → add to Loja Centro's list, Version+1, sign
     │                                                                                               │
     ◄─── licensing.activations bundle (stream "0=farmaviva/1=loja-centro") pushed by central, gossiped by PC-GERENTE ◄───┘
 CAIXA-03: listed with its evidence key id → SeatActivated
```

```csharp
// YFex.Licensing — the seat state of THIS installation (native union: cases carry data)
public sealed record SeatActivated(DateTimeOffset Since);
public sealed record SeatPending(DateTimeOffset Until);                     // enrolled, not listed yet; modules work
public sealed record SeatNotActivated(SeatRefusal Reason);                  // after the pending grace, or refused by the vendor
public sealed record SeatNotCounted;                                        // role not counted (Central)
public union SeatState(SeatActivated, SeatPending, SeatNotActivated, SeatNotCounted);

public enum SeatRefusal : byte { NotListed = 1, OverSeatLimit = 2, OverScopeLimit = 3, EvidenceKeyMismatch = 4 }
```

| Seat state | Modules | Base | Keep-lists |
|---|---|---|---|
| `SeatActivated`, `SeatNotCounted` | per License state (04) | per License state | yes |
| `SeatPending` | per License state, plus a banner "ativação pendente até …" | per License state | yes |
| `SeatNotActivated` | **no** | product's `UnlicensedBehavior` | yes |

- **Pending starts** when the licensing library first sees itself enrolled and not listed; the instant is kept in licensing's sealed
  local state (crypto `SealedValue`, ring `device/storage`). Pending ends at `PendingSince + License.Timings.ActivationGrace`,
  evaluated with trusted time ([04](04-validity-expiry-grace-offline.md) §2).
- **The vendor may list refusals** (`ActivationList.Refused` with `SeatRefusal`), so the installation shows the exact reason during
  its grace instead of a generic "pending".
- **Evidence key bound:** an entry names the installation id **and** its evidence `KeyId`; an installation whose evidence key changed
  (re-keyed after a TPM reset) is `EvidenceKeyMismatch` until central reports the new key (it does so automatically at renewal).
- **Downgrades:** if the vendor issues a smaller License (120 → 80 terminals) while 97 are enrolled, the lists keep all 97 for
  `ActivationGrace`, central's admin screen lists the 17 newest as "to be released", and after the grace the vendor drops the newest
  (deterministic) — unless central releases others first. The vendor's issuing screen warns before such an issue.

```csharp
// YFex.Licensing.Vendor — what the vendor applies per delta (SaaS in-process, on-premises in a check-in)
public sealed record ActivationDelta(InstallationId Installation, InstallationRole Role, string Scope, KeyId EvidenceKey,
    Hash256 HardwareHint, DateTimeOffset EnrolledAt, InstallationId? Replaces, ActivationDeltaKind Kind);
public enum ActivationDeltaKind : byte { Enrolled = 1, Revoked = 2, EvidenceKeyChanged = 3 }
```

## 4. Moving a seat (⚑LC8)

| Situation | What the admin does | What licensing does |
|---|---|---|
| CAIXA-02's disk died, a new PC takes its place | creates an enrollment code with **`Replaces: CAIXA-02`** (Installations' keyed hint may suggest it, ⚑I16) | the policy counts CAIXA-02 as free; when the new one enrolls, Installations revokes the old one and the delta carries `Replaces`; the vendor swaps the entry in one list version |
| Windows reinstalled on CAIXA-02 | same (a reinstall is a new installation, installations 02 §3) | same |
| a checkout is retired | **revokes** the installation (Installations, `installations.revoke`) | the seat is free at once at central; the vendor drops the entry at the next delta |
| moving CAIXA-02 to Loja Norte | revoke at Loja Centro, enroll at Loja Norte | Loja Centro's list loses it, Loja Norte's gains it (both PerScope limits apply) |

There is **no separate "release seat" command**: a seat is released by revoking the installation, so there is never an installation
that is enrolled but "unlicensed on purpose". There is **no hardware binding** and **no hard move limit**. The vendor keeps a
**lineage** per seat through `Replaces` and flags (08 §4):

- more than **3 replacements in one lineage within 30 days**;
- more than **2 × PerScope activations in one scope unit within 30 days** without `Replaces` (enroll/revoke churn);
- a hint that appears in two scope units at once.

Flags are findings for the vendor's account manager, never an automatic block.

## 5. Trial (⚑LC9)

- A trial is a **License with `Kind = Trial`**, issued by the vendor: self-service sign-up on the vendor's site creates a customer
  account, a tenant (SaaS) or an activation code (on-premises) and a trial License from the edition's trial shape (default 30 days,
  1 scope unit, 2 terminals, 1 broker, all modules).
- **One trial per customer account**, decided by the vendor service through `ITrialEligibility` (the vendor's rule: e-mail, tax id,
  sales approval — business data, outside the library). The library guarantees only that a trial License is tenant- and
  tenant-root-bound like any other, so it cannot be copied to another installation network.
- At the end of a trial there is **no subscription grace and no base continuation** (`Kind = Trial` → straight to the product's
  `UnlicensedBehavior`); the data stays readable and exportable.
- **Conversion:** the vendor issues a `Commercial` License with a higher `Sequence` for the same tenant and product; the vendor
  re-signs the activation lists for the new License id with the same entries, so no checkout notices anything.
- **No anonymous trial in the binary:** anything shipped in the installer is the same for everybody and resets with every reinstall.

## 6. Permissions at the tenant (⚑LC21)

```csharp
// YFex.Licensing.Central
[PermissionCatalog]
public static partial class LicensingPermissions
{
    [Permission("licensing.view", Name = "Ver licença e uso de assentos")] public static partial Permission View { get; }
    [Permission("licensing.activate", Name = "Ativar a central com o código do fornecedor")] public static partial Permission Activate { get; }
    [Permission("licensing.check-in", Name = "Sincronizar com o fornecedor (agora ou por arquivo)")] public static partial Permission CheckIn { get; }
}
```

Replacing and revoking stay `installations.enroll` / `installations.revoke`: they are acts on installations; licensing only reacts.
Base permissions, keep-lists and these permissions are **never masked** by the license (a customer must always be able to see and
fix its licensing).
