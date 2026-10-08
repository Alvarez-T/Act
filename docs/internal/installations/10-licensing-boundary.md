# 10 — Licensing: out of scope, with two hooks (⚑I23)

User question (2026-10-07): *will this be used, or can it be used, to control my license too? A YFex.License, or out of scope?*

**Answer: licensing is a separate library, but it would be built on this one.** Installations answers *"which installed copy is this,
and is that other copy ours?"*. Licensing answers *"what has this customer bought, and may this copy run this feature?"*. The second
needs the first; the first must not know about products, plans or prices (domain-agnostic rule).

## 1. What a license library would get from Installations

| Licensing need | From Installations |
|---|---|
| "this license is activated on **this** copy" | `InstallationId` + the evidence key in the TPM/Keychain: activation binds to an installation that cannot be cloned with a disk image, not to a hardware hash that a VM can copy |
| seat counting ("3 checkouts for Loja Centro") | the central **registry** of enrolled installations per scope and role; enrollment is the natural point to count |
| delivering the license file to every machine, also offline | a **signed bundle** kind (`licensing.entitlements`) with version floors, gossiped on the mesh `bundles` channel |
| expiry and grace periods that a changed Windows clock cannot extend | YFex.Time `GetTrustedNow()` (+ rollback flags) |
| "this new installation is the old CAIXA-02 re-installed" (do not charge a new seat) | the keyed hardware hint (⚑I16) |
| features per module (fiscal, loyalty, delivery) | not Installations: Authorization requirements / Capabilities read the entitlements |

## 2. The one thing that makes licensing different: **who signs**

Everything in Installations is signed by the **tenant's** central (FarmaViva's CA and bundle keys). A license must be signed by **you,
the vendor**, with a key the customer never holds — otherwise an on-premises customer (who runs Central on their own server, offline
02 §6) could sign themselves a bigger license.

```
 vendor (you)              ── licensing key (never leaves your servers) ──► signs "FarmaViva: 40 stores, 120 checkouts, fiscal + loyalty, until 2027-12-31"
 tenant central (FarmaViva) ── tenant CA + bundle keys                     ──► signs rosters, revocations, rule data…
 installation              ── verifies each bundle kind against the RIGHT publisher (vendor key pinned in the app binary)
```

## 3. The two hooks to add now (⚑I23), so licensing never needs to change Installations

```csharp
// Hook 1 — bundle publishers: a kind can only be verified with the keys of its publisher (YFex.Installations)
installations.Bundles(bundles => bundles
    .Publisher("vendor", vendorPublicKeys)                       // pinned in the app; owned by the licensing library
    .Kind(LicensingBundleKinds.Entitlements, publisher: "vendor"));
// default publisher for every other kind = the tenant central (unchanged)

// Hook 2 — enrollment policy at central (YFex.Installations.Central)
public interface IEnrollmentPolicy
{
    ValueTask<EnrollmentDecision> DecideAsync(EnrollmentCandidate candidate, CancellationToken cancellationToken);   // e.g. seats left?
}
```

- Hook 1 is a small generalization of `IBundleVerifier` (one key directory per publisher instead of one for all kinds). Without it, a
  vendor license would need its own second bundle format.
- Hook 2 lets a licensing library refuse "the 121st checkout" with `EnrollmentRefusal.PolicyRefused("seats")`, and lets other
  policies (hardware keys for brokers) use the same seam.

## 4. What stays in a future licensing library (not designed here)

Products/editions/modules, seats per role and scope, trial, activation and deactivation (moving a seat to a new PC), offline validity and
grace, vendor check-in for on-premises customers, usage metering, the vendor's issuing service, UI, and the honest limit: code running on
a customer's machine can be patched, so licensing protects honest customers and detects abuse; it cannot be made uncrackable.

**Name:** the user wrote `YFex.License`; following the naming of `YFex.Installations`/`YFex.Networking` (activity/area names), the
recommendation is **`YFex.Licensing`**. Its design is a separate task, after the user decides to start it.

> **2026-10-07:** the design now exists → [licensing/README.md](../licensing/README.md) (⚑LC1–LC22, awaiting the user). It needs the
> two hooks above plus small details listed in [licensing 10 §2.1](../licensing/10-phases-and-acceptance.md) (R1–R7: policy inside the
> registry transaction and at code creation, `Replaces` on enrollment codes, `PolicyCode` on the refusal, registry read API + events,
> forwarding of other publishers' bundles).
