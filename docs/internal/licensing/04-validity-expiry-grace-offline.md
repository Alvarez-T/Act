# 04 — Validity, expiry, grace and offline (YFex.Time)

## 1. License state on an installation

```csharp
// YFex.Licensing — native unions (cases carry data and the UI treats each one differently; csharp-union-usage)
public sealed record LicenseActive(License License, LicenseNotices Notices);
public sealed record LicenseInGrace(License License, LicenseGraceReason Reason, DateTimeOffset EndsAt, DateTimeOffset? LastCheckIn);
public sealed record LicenseExpired(License License, LicenseGraceReason Reason, DateTimeOffset Since, DateTimeOffset? BaseUntil);
public sealed record LicenseMissing;                                   // never received a valid License for this product
public sealed record LicenseRejected(LicenseRejection Reason);         // only when no good License exists at all (§1.1)
public union LicenseState(LicenseActive, LicenseInGrace, LicenseExpired, LicenseMissing, LicenseRejected);

public enum LicenseGraceReason : byte { SubscriptionEnded = 1, CheckInOverdue = 2, OfflineTooLong = 3, TrialEnded = 4 }
public enum LicenseRejection : byte { WrongTenant = 1, WrongTenantRoot = 2, WrongProduct = 3 }

[Flags] public enum LicenseNotices : byte
{
    None = 0,
    RenewalDue = 1,          // subscription ends within 30 days
    CheckInLate = 2,         // on-premises: central has not checked in for 2 × CheckInInterval (shown at central only)
    ClockUntrusted = 4,      // trusted time is not usable for security: removals wait for the proven lower bound (§2)
    ModuleEndingSoon = 8,    // an add-on module period ends within 7 days
}

public interface ILicense                                              // one per registered product; central: ICentralLicenses per tenant
{
    LicensedProduct Product { get; }
    LicenseState State { get; }
    SeatState Seat { get; }                                            // 03 §3
    bool IsAvailable(ModuleId module);
    ModuleAvailability GetAvailability(ModuleId module);               // why not (06 §4)
    long? GetLimit(LimitId limit);                                     // null = not granted
    ValueTask WaitUntilAvailableAsync(ModuleId module, CancellationToken cancellationToken);
    event Action<LicenseChange>? Changed;
}
```

### 1.1 Bad documents never replace good ones

A bundle that fails Installations' verifier (signature, audience, floor, conflict) is dropped before licensing sees it. A
verified bundle that fails licensing's checks (wrong tenant, tenant root or product) is a **finding** and is also dropped. In both
cases the **last good License stays in effect**. `LicenseRejected` appears only when the installation never had a good one (an app
pointed at the wrong central, a test License in production). A corrupted store is not "no license" forever: central pushes the
current bundles again on the next connection.

## 2. Which instant decides (⚑LC10)

Every decision uses `TimeProvider.GetTrustedNow()` (core `YFex.Time`), never `DateTime.Now`. The rule:

```
 E = now.IsUsableForSecurity(maximumUncertainty: 10 min)       (⚑T11: authenticated anchor, or AuthenticatedFloor without rollback)
       ? now.Value
       : L                                                       ← the proven lower bound

 L = max( LastTrustedEvaluation + RunningTimeSince, License.IssuedAt )
       LastTrustedEvaluation : the last E computed with usable time (sealed local state, only moves forward)
       RunningTimeSince      : monotonic running time accumulated since then (Stopwatch, persisted every 5 min)
```

| What someone does to the clock | `now` from YFex.Time | E used by licensing | Effect |
|---|---|---|---|
| nothing, online | anchored, authenticated | `now.Value` | exact |
| sets Windows back a year (online) | anchored, `RollbackDetected` | `now.Value` (the anchor ignores the device) | none |
| sets it back while offline | high-water, `RollbackDetected` | `L` | none: `L` never reads the device clock |
| sets it **forward** to 2031 while offline (or the CMOS battery is wrong) | forward value adopted, floor dropped → not usable | `L` | **nothing is removed early** — an honest shop is not punished for a wrong clock |
| offline 3 weeks, honest | `Unanchored` + `AuthenticatedFloor` → usable | `now.Value` (bounded below by the high-water) | exact enough |
| machine switched off for 2 months, then offline | possibly unusable | `L` (switched-off time is not counted) | removals come later than the calendar — in the customer's favour |

- **Warnings use E too**, so a wrong clock cannot show "your license has expired" banners either. `LicenseNotices.ClockUntrusted`
  is set while `E = L` so the screen can say why.
- **The residual (honest limit):** a machine that never reaches any trusted source and whose licensing state is **deleted** on
  purpose falls back to `L = License.IssuedAt` each time. Deleting needs administrator rights and the License still ages with running
  time from there; it gains at most one `OfflineAllowance` per deletion, only while fully disconnected, and the next contact with
  central shows the gap (08 §3).
- **Platforms:** on Windows the monotonic clock includes sleep, on Linux/macOS it does not (YFex.Time gotcha) — either way the running
  time is a lower bound, which is the safe direction.

## 3. Expiry: degrade, never brick (⚑LC11)

```
 Subscription.Until            + SubscriptionGrace (15 d)          + BaseContinuesAfterExpiry (product, YFex Farma 30 d)
 2027-10-31 ──────────────────────── 2027-11-15 ─────────────────────────────── 2027-12-15 ─────────────────────────►
 LicenseActive (RenewalDue    │ LicenseInGrace                │ LicenseExpired (BaseUntil = 12-15)  │ LicenseExpired (BaseUntil = null)
 from 2027-10-01)             │ (SubscriptionEnded)           │ modules OFF (keep-lists on)         │ UnlicensedBehavior:
 everything on                │ everything on, banner         │ base ON (selling continues)         │  ReadOnly → keep-lists only
                              │                               │                                     │  BaseOnly → base + keep-lists
```

| State | Licensed modules | Module keep-lists | Base | Product keep-list | Licensing permissions |
|---|---|---|---|---|---|
| `LicenseActive` / `LicenseInGrace` | per module period | yes | yes | yes | yes |
| `LicenseExpired`, `BaseUntil` in the future | **no** | yes | yes | yes | yes |
| `LicenseExpired`, base continuation over; `LicenseMissing`; `LicenseRejected`; `SeatNotActivated` | **no** | yes | `BaseOnly`: yes / `ReadOnly`: **no** | yes | yes |

- **The app always starts**, central always answers, data can always be read and exported, and work already done can be finished
  where the app declared it (YFex Farma: `fiscal.transmit-pending`, `sales.close-day`). This is a library rule, not an option.
- **Trials** (`Kind = Trial`) have no subscription grace and no base continuation: `TrialEnded` goes straight to the last row.
- **Add-on modules** (`ModuleGrant.Period`) end at their own date without grace (they are usually promotions); `ModuleEndingSoon`
  warns 7 days before.
- Every state change raises `ILicense.Changed` and an Authorization signal, so open screens update at once (06 §3).

## 4. Offline validity and freshness (⚑LC12)

A License is **fresh** while `E − IssuedAt ≤ OfflineAllowance`. The vendor keeps it fresh by **re-issuing the same content with
`Sequence + 1`** (a new `IssuedAt`); installations replace it automatically.

| Hosting | Who re-issues, when | `OfflineAllowance` (default) | `CheckInInterval` | Last 7 days of the allowance | After the allowance |
|---|---|---|---|---|---|
| `VendorHosted` (SaaS) | the vendor service, when the tenant's central asks for a License older than 1 day (in-process) | **30 days** | — | `LicenseInGrace(OfflineTooLong)` | `LicenseExpired(OfflineTooLong)` → modules off, base continues |
| `CustomerHosted` (on-premises) | every successful check-in (daily) | **45 days** | **1 day** | `LicenseInGrace(CheckInOverdue)` (or `OfflineTooLong` when this installation, not central, lost contact) | `LicenseExpired(CheckInOverdue)` |
| `Disconnected` | every file check-in | **180 days** | **90 days** | `LicenseInGrace(CheckInOverdue)` | `LicenseExpired(CheckInOverdue)` |

- **Freshness travels through the shop:** the broker (PC-GERENTE) gossips the newest License to every checkout on the LAN
  (Installations `bundles` channel), so a checkout whose own route to central is broken stays fresh while any friend reaches central.
- **Central sees trouble first:** on-premises central raises `CheckInLate` after 2 failed days and shows it on the admin's license
  screen; installations only show the last 7 days of the allowance, so cashiers are not alarmed by a one-day outage.
- **Why not "always valid offline until the subscription ends":** an on-premises central that is cut off from the vendor forever would
  hide over-use forever. The allowance is the only lever that does not depend on the customer's code being honest.

## 5. Scenarios (Loja Centro unless said otherwise)

| # | What happens | License / seat on CAIXA-01 | What people see |
|---|---|---|---|
| 1 | internet down 3 days (SaaS) | Active | nothing |
| 2 | internet down 25 days (SaaS) | day 23 InGrace(OfflineTooLong), day 30 Expired: modules off, base on | banner from day 23; delivery/loyalty/fiscal stop on day 30 except keep-lists; selling continues 30 more days |
| 3 | someone sets CAIXA-01's clock back a year | unchanged | nothing (YFex.Time flags `RollbackDetected`; Audit finding) |
| 4 | CMOS battery dies, clock at 2001, no internet | unchanged, `ClockUntrusted` | "relógio não confiável" notice; Fiscal's own rules may block NFC-e (time README scenario 6) |
| 5 | clock jumps to 2031, no internet | unchanged (E = L) | `ClockUntrusted` notice, no expiry banner |
| 6 | FarmaViva's on-premises central loses internet 10 days | central `CheckInLate` from day 2; installations unaffected | IT sees the warning; may use the file check-in (05 §4) |
| 7 | FarmaViva blocks the vendor host for good | day 38 InGrace(CheckInOverdue), day 45 Expired, day 75 ReadOnly | vendor sees missing check-ins from day 2 and calls the customer |
| 8 | FarmaViva stops paying | §3 timeline | renewal notices from 30 days before |
| 9 | hospital pharmacy without internet by policy (`Disconnected`) | file every 90 days, valid 180 | IT exports a request file, the vendor portal returns a response file |
| 10 | a tampered License arrives through the mesh | dropped by the verifier; last good stays | Installations security finding |
| 11 | on-premises central pushes an **older, bigger** License to a new checkout | refused below the floor where seen; elsewhere it is old → OfflineTooLong quickly | vendor sees the newest Sequence was not acknowledged (08 §4) |
| 12 | new CAIXA-04 enrolled while the store is offline (on-premises) | SeatPending 7 days | banner "ativação pendente"; activation arrives at the next check-in |

## 6. When the evaluator runs

At start-up (after `LocalInstallation` and the bundle store are ready; before hosted services start, so the first `PermissionSet` is
already masked), whenever a licensing bundle is accepted, on YFex.Time anchor changes, on `LocalInstallation.Changed`, and once a
minute (one `GetTrustedNow()` and a few comparisons; the running-time counter is persisted every 5 minutes). Evaluation is pure and
allocation-free except when the state changes.
