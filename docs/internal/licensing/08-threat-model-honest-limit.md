# 08 — Threat model and the honest limit (⚑LC18)

## 1. The goal, stated plainly

Code that runs on the customer's machine can be changed by the customer. A patched binary can skip every check in this library, and
an on-premises customer runs central too. **Licensing cannot be made uncrackable, and this design does not try.** It aims for:

1. **Honest customers are never hurt.** A wrong clock, a dead disk, a week without internet or a lost license file never stops a shop;
   data is always readable; the app always starts.
2. **Cheating requires changing our code**, not editing a file, copying a folder, setting a clock or blocking a host name.
3. **Abuse is visible to the vendor** through findings, so the answer can be a phone call and a contract.

## 2. Attacks and answers

| # | Attack | Answer | Residual |
|---|---|---|---|
| 1 | edit the License (more modules, later date) | vendor signature, keys pinned in the binary (02 §5, 09 §3) | none without the vendor key |
| 2 | copy FarmaViva's License to another customer | `Tenant` + `TenantRoot` must match the installation's pinned tenant root (02 §5) | none |
| 3 | replay an older, bigger License | Installations' version floors; freshness by age (04 §4) | a brand-new installation fed only the old one gets at most the rest of its `OfflineAllowance` |
| 4 | on-premises central patched to ignore seat limits | the vendor's activation lists; extra installations are `SeatNotActivated` after 7 days (03 §3) | the customer must also patch every installation |
| 5 | central patched to hide enrollments from the vendor | same: never listed → never activated | same |
| 6 | disk image of an activated checkout copied to more PCs | TPM keys do not move: the copy cannot sign the installation assertion, central refuses it (⚑I11). With software keys both copies work, but central sees **one id asserting from two places** → `DuplicateInstallation` finding | an offline-only clone with software keys |
| 7 | clock set back or forward | E = trusted time or the proven lower bound (04 §2) | none |
| 8 | vendor host blocked by the firewall | the License ages: grace, then modules stop (04 §4); the vendor sees missing check-ins | none |
| 9 | delete licensing's local state while offline | `L` falls back to `IssuedAt`, still ages with running time (04 §2) | at most one allowance per deletion, only while fully disconnected |
| 10 | patch the client so the mask does nothing | **not preventable on the client.** The server-side mask still applies to every call reaching central (06 §2): strong for SaaS, as weak as the customer's server on-premises | features that run purely locally |
| 11 | replace the pinned vendor keys in the binary and self-sign | same as 10 (it is a patch) | same |
| 12 | fake vendor endpoint (DNS, proxy) | responses and every bundle are vendor-signed; the response is bound to the request nonce (05 §3) | none |
| 13 | vendor signing key stolen | standby key recovery (09 §3); HSM/TPM for the active key; two-person rotation | an attacker holding **both** keys = app update |
| 14 | a LAN device injects licensing bundles | Installations' verifier with the vendor publisher; last good stays (04 §1.1) | none |
| 15 | endless trials with new accounts | the vendor's `ITrialEligibility` (business rule) | depends on the vendor's sign-up checks |
| 16 | test vendor keys shipped in a release | §5 | none if the release test runs |

## 3. What the library deliberately does not do

| Not done | Why |
|---|---|
| obfuscation, anti-debugging, assembly self-checks, native packers | AOT and trimming conflicts; antivirus false positives on shop PCs; crackers remove them anyway. An app may add a commercial obfuscator in its own publish step — never in the library |
| hardware-fingerprint binding | a motherboard swap, a VM move or a BIOS update would break honest customers; Installations' keys already stop disk copies where a TPM exists |
| remote kill switch, refusing to start | a pharmacy that cannot sell or finish legal fiscal duties is a legal and reputational risk; the strongest lever is not renewing |
| collecting personal data in check-ins | not needed (05 §6) |

## 4. Findings (detection)

```csharp
// YFex.Licensing.Vendor
public enum LicensingFindingKind : byte
{
    DuplicateInstallation = 1,     // one installation id asserting from two places at once (central counts, check-in reports)
    SeatCountMismatch = 2,         // reported counts differ from the vendor's activation state
    FrequentReplacement = 3,       // > 3 replacements in one seat lineage in 30 days (03 §4)
    ScopeChurn = 4,                // > 2 × PerScope activations in one scope unit in 30 days without Replaces
    MissedCheckIns = 5,            // on-premises: no successful check-in for 3 × CheckInInterval
    StaleLicenseInUse = 6,         // installations report an older Sequence than the vendor issued, after a push window
    HostingMismatch = 7,           // a VendorHosted License checked in from outside, etc.
    FrequentReactivation = 8,      // central activated with new codes more than twice in a year
    ClockRollbacks = 9,            // many RollbackDetected flags reported by installations of one scope
    RejectedBundles = 10,          // installations dropping vendor bundles (tampering or misconfiguration)
}
```

Findings have a severity, a first/last seen time and an acknowledgement; they appear in the vendor's dashboard. **No finding changes
a License automatically.** Installations report their applied License `Sequence` and seat state in the daily usage report (07 §2) or,
with metering off, in a tiny daily status call to central, so `StaleLicenseInUse` works without meters.

## 5. Development and test keys

- `YFex.Licensing.Testing` contains **test** vendor keys and `TestVendor`; they never appear in a production package.
- Developers run the app with licenses issued by the vendor's **staging** issuing service, whose keys the app pins **only** when built
  with the `YFEX_LICENSING_STAGING` constant.
- The app's release pipeline runs `VendorKeysSnapshot.Verify(YFexFarmaLicensing.VendorKeys, "vendor-keys.release.json")`, which
  fails if the pinned keys are not the committed production fingerprints (or if the staging constant is set). A development key
  accepted in a release build would be a backdoor.
