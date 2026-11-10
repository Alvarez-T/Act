# YFex.Licensing — products, modules, seats, activation, expiry and the vendor service — Design

> **Status:** ⚑LC1–LC22 approved 2026-11-09 (§6). **LC-P1 BUILT 2026-11-09** (repo `C:\Dev\YFex\YFex.Licensing`: catalog,
> documents, vendor keys, evaluator; 50 tests on Windows and Linux, AOT 0 warnings) — see [10 §5](10-phases-and-acceptance.md).
> Next: LC-P2 gating, blocked on Authorization A1–A4 (not requested yet).
> **Why:** the user asked (2026-10-07, Installations session) whether YFex.Installations could control the license. The agreed answer
> ([installations 10](../installations/10-licensing-boundary.md), ⚑I23): licensing is a **separate library built on Installations**.
> Installations answers *"which installed copy is this, and is that other copy ours?"*; Licensing answers *"what has this customer
> bought, and may this copy run this feature?"*.
> **Repo (proposed):** `C:\Dev\YFex\YFex.Licensing` (sibling, like YFex.Installations, YFex.Time, YFex.Cryptography). **No YFex core
> types** (⚑LC1): nothing outside licensing needs to name them.
> **No NuGet package** is added: BCL, YFex.Cryptography for every key and signature, YFex.Installations for identity, registry and
> bundles, YFex.Time for trusted time, YFex.Authorization for gating.
> **Audience:** the user first, then the sessions that implement the phases ([10](10-phases-and-acceptance.md)).
> **Normative vs sketch:** names in code blocks are normative unless marked `// (sketch)`.

## 1. The one rule that makes licensing different: the **vendor** signs

Everything Installations distributes is signed by the **tenant's** central (FarmaViva's CA and bundle keys). An on-premises customer
runs that central on its own server, so it could sign itself anything. A license is therefore signed **only by the vendor** (the
user's company), with a key the customer never holds, and every installation verifies it against **vendor public keys pinned in the
app binary**. The tenant central only **forwards** vendor-signed bundles; it cannot create or change them (⚑I23 hook 1, bundle
publishers).

```
 VENDOR (your company) — YFex.Licensing.Vendor, hosted only by you
   customer accounts · edition templates · license issuing · check-in endpoint · activation lists
   signing key "licensing/vendor" (HSM/TPM, never leaves your servers)
        │  signs: License (licensing.entitlements)  +  ActivationList per scope (licensing.activations)
        ▼
 TENANT CENTRAL (FarmaViva) — YFex.Licensing.Central over YFex.Installations.Central
   SaaS (you run it)       : asks the vendor service in-process at every enrollment
   on-premises (they run it): checks in with the vendor daily over HTTPS (or by file when disconnected)
   counts seats from the installation registry · refuses the 5th checkout of a 4-seat store (IEnrollmentPolicy, ⚑I23 hook 2)
   forwards the vendor bundles unchanged to installations (IBundleStore push + mesh gossip)
        ▼
 INSTALLATIONS (PC-GERENTE broker, CAIXA-01, CAIXA-02 at Loja Centro) — YFex.Licensing
   verify the License and their scope's ActivationList against the PINNED vendor keys (never the tenant's)
   evaluate with YFex.Time trusted time · mask unavailable modules out of every PermissionSet (Authorization hook, ⚑LC14)
```

## 2. Vocabulary

| Term | Meaning | Example |
|---|---|---|
| **Product** | one app the vendor sells, declared in code by the vendor's developers | `yfex-farma` (YFex Farma desktop + central) |
| **Module** | a sellable part of a product = a set of **permissions** (+ optional named limits and meters) | `fiscal`, `loyalty`, `delivery`, `controlled-medicines` |
| **Base** | every permission **not** in any module: available while the product is licensed (and for a declared period after expiry) | open a sale, search products |
| **Edition** | a **vendor-side template** ("essencial", "completo") that expands to modules + seats when a license is issued. Installations never resolve editions | "completo" = all four modules |
| **License** | the vendor-signed document for one tenant and one product: modules, seats, limits, periods, hosting, check-in policy | FarmaViva, `yfex-farma`, 40 stores, 120 checkouts, until 2027-10-31 |
| **Seat** | the right of one installation of a role to use the licensed modules, counted per scope | 4 `Terminal` seats per store |
| **Activation** | the vendor's acceptance of an installation into a seat, published in a signed `ActivationList` for its scope | CAIXA-02 activated 2026-11-03 |
| **Check-in** | an on-premises central's periodic exchange with the vendor: usage out, fresh license + activations in | daily at 03:00, or a file every 90 days |
| **Grace** | a period where the license (or a seat) is not in order but nothing is removed yet | 15 days after the subscription ends |
| **Meter** | an optional named counter for usage reporting (never blocks work) | `fiscal.documents-issued` |

Pharmacy words (NFC-e, SNGPC, loja) appear **only in the examples**. The library knows modules, permissions, roles, scope levels and
named limits/meters (`yfex-libs-domain-agnostic`).

## 3. Shape and packages

| Package | Contents | Depends on |
|---|---|---|
| `YFex.Licensing` | catalog builder (`LicensedProduct`, `ModuleId`, `LimitId`, `MeterId`), document types + canonical codecs, pinned vendor keys, `LicenseEvaluator` (states, trusted time, running-time allowance), `ILicense`, the Authorization availability provider (⚑LC14), local meter counters | YFex core, YFex.Cryptography, YFex.Installations, YFex.Time (extension only), YFex.Authorization(.Public) |
| `YFex.Licensing.Central` | seat counting over the installation registry, `IEnrollmentPolicy` (seats, scope units), activation pending/replace/release, distribution of vendor bundles, check-in client (online + file), meter aggregation, `LicensingPermissions` catalog, admin messages | the above + YFex.Installations.Central, YFex.Cqrs |
| `YFex.Licensing.Vendor` | the vendor's issuing service: customer accounts, edition templates, issuing, activation codes for on-premises centrals, check-in endpoint, activation lists, abuse findings, vendor signing ring, `LicensingVendorPermissions` | the above + YFex.Data (later `.Data` store), YFex.Audit |
| `YFex.Licensing.Testing` | test vendor keys, `TestVendor` (issues licenses in memory), `LicensingHarness` on Installations.Testing + YFex.Time.Testing | the above + YFex.Installations.Testing |
| tests, AotSmoke, Benchmarks | `.Tests` (runs on Windows and Linux), `.AotSmoke` (0 warnings), BenchmarkDotNet (`InProcessEmitToolchain`) | |

**No `.Windows`/`.MacOS`/`.Linux` package** (⚑LC20): licensing has no OS code. Keys live behind YFex.Cryptography's
`ISigningKeyStore` and identity behind YFex.Installations, which carry the per-OS parts ([09](09-platforms-and-cryptography.md)).

## 4. What goes where

| Concept | Where | Why not elsewhere |
|---|---|---|
| installation id, keys, certificates, registry, roster, bundle format, floors, gossip, hardware hint | **YFex.Installations** (unchanged) | identity is not licensing (installations 10) |
| bundle publishers per kind, `IEnrollmentPolicy` | **YFex.Installations** hooks (⚑I23) + small additions requested in [10](10-phases-and-acceptance.md) §2 | generic seams; brokers' hardware-key policy uses the same seam |
| "this permission is not available in this deployment" (mask + `DenialKind.Unavailable`) | **YFex.Authorization** generic hook `IPermissionAvailability` (⚑LC14) | sources can only add permissions; feature flags can use the same hook |
| hiding unavailable features in search and in the AI agent | **YFex.Capabilities**, unchanged (it already filters by permission) | — |
| products, modules, editions (templates), licenses, seats, activation, grace, check-in, meters, issuing | **YFex.Licensing** | — |
| prices, invoices, payment of the subscription | **out of scope** — the vendor's billing system reads seat counts and meters from `.Vendor` | money is a domain concept; billing has its own rules |
| which pharmacy features belong to which module | **the app** (`YFexFarma.Licensing`, shared by app and vendor service) | domain-agnostic rule |

## 5. Reading order

1. [01 — API and examples](01-api-and-examples.md) (dev side first: FarmaViva, Loja Centro, CAIXA-01/02)
2. [02 — Products, editions and modules](02-products-editions-modules.md) and the documents' format
3. [03 — Seats, activation, moving a seat, trial](03-seats-and-activation.md)
4. [04 — Validity, expiry, grace and offline](04-validity-expiry-grace-offline.md) (YFex.Time)
5. [05 — Vendor check-in and the issuing service](05-vendor-check-in-and-issuing.md)
6. [06 — Feature gating](06-feature-gating.md) (Authorization, Capabilities, code paths without an actor)
7. [07 — Usage metering (optional)](07-usage-metering.md)
8. [08 — Threat model and the honest limit](08-threat-model-honest-limit.md)
9. [09 — Platforms and cryptography](09-platforms-and-cryptography.md)
10. [10 — Phases, acceptance, requests to other libraries, implementation log](10-phases-and-acceptance.md)

## 6. Decisions to confirm (⚑LC)

Prefix **⚑LC** because ⚑L is YFex.Logging's.

**All approved by the user on 2026-11-09**, as recommended, with these additions:

- **LC10:** checked: this is the same clock YFex.Notifications uses (⚑N16): core `ITrustedClock` / `TimeProvider.GetTrustedNow()` from `YFex/src/YFex/Time`, engine YFex.Time. `TrustedInstant.IsUsableForSecurity(maximumUncertainty)` needs a bound, so Licensing gets an option `LicensingOptions.MaximumClockUncertainty` (default 10 minutes, as in 04 §2).
- **LC12:** the values 30 / 45 / 180 days are **defaults, configurable**. The vendor sets them per license, and the product catalog may change the defaults. Central cannot extend them.
- **LC15:** stays optional; some features may switch metering on.
- **LC18:** accepted **for now**; stronger anti-tamper measures will be discussed later.
- **LC19:** the check-in client and the activation-delta outbox run inside **YFex.Hosting** (one service host per machine, ⚑H1) once it exists; until then a hosted service that can be registered there.
- ⚑I23 was already approved (installations README).

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **LC1** ✅ | Where does it live? | Sibling repo **`C:\Dev\YFex\YFex.Licensing`**, packages §3. **No YFex core types**: only licensing code and app catalogs name them; Authorization gets a generic hook instead | core namespace `YFex.Licensing` (core grows with types no other library reads) | §3 |
| **LC2** ✅ | Who signs licenses? | **Only the vendor.** Two kinds with publisher `vendor` (⚑I23): `licensing.entitlements` and `licensing.activations`. Each license also pins the tenant's **root key hash**, so a copied license is useless under another tenant CA | tenant central signs (an on-premises customer signs itself a bigger license) | §1, 02 §5 |
| **LC3** ✅ | How are products sold? | Code declares **product → modules → permissions** (+ named limits, meters). Permissions in no module are **base** (always available). **Editions are vendor-side templates**: the license lists the resolved modules, the edition name is display only | editions resolved in the app (changing what "completo" contains needs an app update on every checkout) | 02 §1–§3 |
| **LC4** ✅ | How does a developer declare the catalog? | One **builder** per product in a small shared assembly (`YFexFarma.Licensing`) referenced by the app **and** the vendor service; modules reference whole permission catalogs or single permissions; explicit string ids; a committed **catalog manifest** snapshot reviewed in PRs. No attributes on permissions | `[Module("fiscal")]` on every permission (per-member attributes, `api-naming-and-annotation-style`) | 02 §2 |
| **LC5** ✅ | One signed document or two? | **Two**: the **License** (changes a few times a year; stream per tenant + product, audience tenant) and an **ActivationList** per scope (changes when a checkout is added; stream per scope, audience scope, so each shop receives only its own list) | one document re-signed at every enrollment and sent to every machine of the tenant | 02 §5, 03 §3 |
| **LC6** ✅ | What is a seat and how is it counted? | A seat = an `Enrolled` or `Provisional` installation of a **counted role** (default `Terminal`, `SiteBroker`; `Central` never) in a scope at the license's **scope level**. Limits `SeatLimit(Role, PerScope, Total)` and `ScopeLimit(Level, Count)` ("40 stores"). Counted by **central from the installation registry**; enforced at enrollment through `IEnrollmentPolicy` | count from the roster on each installation (the roster is tenant-signed: no stronger than central); count only at the vendor (enrollment needs the internet) | 03 §1–§2 |
| **LC7** ✅ | Does enrollment wait for the vendor? | **No.** A new installation works at once with an **activation-pending grace** (default 7 days; SaaS activates in seconds). The vendor-signed `ActivationList` is the independent check: an installation not in its scope's list after the grace is treated as unlicensed: modules off, base per the product's `UnlicensedBehavior` (YFex Farma: read-only), keep-lists always on | vendor countersigns synchronously (a shop cannot replace a dead checkout while its internet is down) | 03 §3 |
| **LC8** ✅ | Moving a seat (new PC, reinstall)? | **Replace** at central: the enrollment code may name the installation it replaces (Installations' hint suggests it, ⚑I16); the old one is revoked and the seat moves in one step, no extra seat used. **Release** frees a seat. Moves are unlimited; the vendor **flags** more than 3 moves of one seat in 30 days | hardware-bound activations (a motherboard swap or VM move breaks honest customers); a hard move limit (a shop with a broken PC calls support on a Saturday) | 03 §4 |
| **LC9** ✅ | Trial? | **Vendor-issued** trial licenses (`LicenseKind.Trial`, default 30 days, 1 scope, 2 terminals, 1 broker), one per customer account (deduplicated by the vendor). **No anonymous trial built into the binary.** Without a valid license the product does what it declares: `UnlicensedBehavior.BaseOnly` (free core) or `ReadOnly` (only the keep-lists: view, close the day, export). The app always opens | embedded trial (reinstall = new trial forever, bound to nothing) | 03 §5 |
| **LC10** ✅ | Which clock? | Every check uses `TimeProvider.GetTrustedNow()`, never the device clock. **Removing features needs time usable for security** (⚑T11). When it is not usable, the evaluator keeps the current state and spends a separate **offline allowance** counted as **trusted elapsed time or accumulated running time, whichever is larger** — a clock set back cannot extend it, a CMOS clock set to 2031 cannot expire it | device clock (set it back = free license); refuse when not anchored (a shop without internet stops selling) | 04 §2 |
| **LC11** ✅ | What happens at expiry? | **Degrade, never brick.** Active → InGrace (reason, end) → Expired. Expired = licensed modules unavailable **except** each module's `KeepWhenUnavailable` permissions (view, export, legal duties for work already done); **base continues** for a product-declared period (YFex Farma: 30 days), then the product's `UnlicensedBehavior` applies. Default grace after the subscription: **15 days** (per license). A bad new bundle never replaces the last good one | hard stop (a pharmacy cannot sell or issue fiscal documents; legal and reputational risk) | 04 §3 |
| **LC12** ✅ | Offline validity? | Per license, set by the vendor: **SaaS** installations need a fresh License from central every **30 days**; **on-premises** central checks in **daily**, a License is valid **45 days** after the last check-in; **disconnected** (no internet at all) = file check-in, valid **180 days** | one fixed value (air-gapped customers impossible, or SaaS too loose) | 04 §4 |
| **LC13** ✅ | Vendor check-in? | On-premises central → `POST /licensing/check-in` over HTTPS, request signed by the central's **licensee key** (ISigningKeyStore, registered once with an activation code); sends seat counts, activation deltas (installation id, role, evidence key id, keyed hint), meters, trusted time; receives the new License + ActivationLists. **Same bytes as a file** for disconnected sites. No personal data | no check-in (over-use on-premises invisible; licenses must be long-lived) | 05 §2–§4 |
| **LC14** ✅ | How are features gated? | A **generic Authorization hook** `IPermissionAvailability` (owned by the Authorization session): effective permissions = granted ∩ available; the gate denies with **`DenialKind.Unavailable`** + the module id as `Code`. `[RequirePermission]`, `VisibleWith`, Capabilities search and agent then respect the license with **no new code**. `ILicense.IsAvailable(module)` for code without an actor (background services) | a licensing `IPermissionSource` (sources only add); checks in every handler (scattered, forgotten) | 06 |
| **LC15** ✅ | Usage metering? | **Optional, off by default.** App declares meters; installations count locally (durable), central aggregates per day and scope, the vendor receives totals (SaaS directly, on-premises at check-in). **Soft quotas only**: warn and flag, **never block** work | hard quotas (a sale refused because a counter ran out) | 07 |
| **LC16** ✅ | Vendor issuing service? | `YFex.Licensing.Vendor`, hosted only by the vendor: accounts, editions, issuing, activation codes, check-in, activation lists, findings; signing ring `licensing/vendor` in an `ISigningKeyStore` (TPM/HSM); every action audited (YFex.Audit); `LicensingVendorPermissions`. A **SaaS central calls it in-process** (`IVendorLicensing`), no HTTP | a third-party licensing service (customer data leaves, offline mode unclear) | 05 §5 |
| **LC17** ✅ | Vendor key pinning and rotation? | The app binary pins **two** vendor public keys: **active** and **standby** (standby private key offline, cold storage). Rotation = a `licensing.vendor-keys` bundle signed by the active key that adds the next standby; compromise = the standby signs a key list that **retires** the active one. No certificate chain | one pinned key (compromise = app update on every machine); a certificate chain (more machinery, same result) | 09 §2 |
| **LC18** ✅ | The honest limit? | Licensing **protects honest customers and detects abuse**; it is not uncrackable. No obfuscation, anti-debugging or packers in the library. Findings (duplicate installation, over-count, frequent moves, clock rollbacks, missing check-ins) go to the vendor; responses are **commercial**, never a remote kill | obfuscation/anti-tamper tools (AOT conflicts, antivirus false positives, broken honest installs); remote disable | 08 |
| **LC19** ✅ | Hosting modes? | `LicenseHosting` closed **enum** `{ VendorHosted, CustomerHosted, Disconnected }` in the License (the vendor decides it, central cannot choose) | central configuration decides (an on-premises customer declares itself "VendorHosted") | 05 §1 |
| **LC20** ✅ | Operating systems? | **No OS package.** Verification is BCL ECDSA P-256 via YFex.Cryptography (Windows/Linux/macOS); the on-premises central's licensee key uses the platform key store Installations picks; running-time counter in `IKeyValueStore`. Depends on the crypto `FileSigningKeyStore` request already open (installations 09 §5) for Linux centrals | per-OS packages (nothing OS-specific to put in them) | 09 |
| **LC21** ✅ | Permissions at the tenant? | `LicensingPermissions` in `.Central`: `licensing.view`, `licensing.activate` (enter the vendor's activation code), `licensing.check-in` (run now, import/export file). Replacing and releasing a seat **are** Installations' enroll (with `Replaces`) and revoke — no second command. Licensing never masks base, keep-lists or these permissions | reuse `installations.*` (a store manager who may enroll should not see the contract); a separate "release seat" (an enrolled but unlicensed-on-purpose installation) | 03 §4, §6 |
| **LC22** ✅ | Several products per tenant? | Yes: one License per (tenant, product); an app registers its product(s); central keeps one per product. Seats are counted per product (the delivery tablet app and the PDV are different products if sold separately) | one license per tenant for everything (products sold separately cannot be expressed) | 02 §4 |

## 7. Files

| # | File | Content |
|---|---|---|
| 01 | [01-api-and-examples.md](01-api-and-examples.md) | **dev-side code first**: catalog, composition roots (CAIXA, central SaaS and on-premises, vendor), issuing FarmaViva's license, gating, the 5th checkout, replacing CAIXA-02, expiry screen, tests |
| 02 | [02-products-editions-modules.md](02-products-editions-modules.md) | catalog model, modules ↔ permissions, base, limits, editions as templates, catalog manifest, document types and wire format |
| 03 | [03-seats-and-activation.md](03-seats-and-activation.md) | seat counting per role and scope, enrollment policy, activation lists, pending grace, replace/release, trial, permissions |
| 04 | [04-validity-expiry-grace-offline.md](04-validity-expiry-grace-offline.md) | license and seat states, trusted-time rules, running-time allowance, grace, degrade-never-brick, offline windows, scenarios |
| 05 | [05-vendor-check-in-and-issuing.md](05-vendor-check-in-and-issuing.md) | hosting modes, licensee key, check-in protocol (online and file), issuing service, SaaS in-process path, vendor data and LGPD |
| 06 | [06-feature-gating.md](06-feature-gating.md) | the Authorization hook, denials, caches and versions, Capabilities, `ILicense` for background code, limits |
| 07 | [07-usage-metering.md](07-usage-metering.md) | optional meters: counting, aggregation, reporting, soft quotas |
| 08 | [08-threat-model-honest-limit.md](08-threat-model-honest-limit.md) | what can and cannot be prevented, attacks and answers, findings, development keys |
| 09 | [09-platforms-and-cryptography.md](09-platforms-and-cryptography.md) | Windows/Linux/macOS, every crypto use mapped to YFex.Cryptography, vendor keys and rotation |
| 10 | [10-phases-and-acceptance.md](10-phases-and-acceptance.md) | working rules, dependencies, requests to Installations/Authorization/Time, phases LC-P0…LC-P6, empty log |

## 8. Open questions (not blocking the decisions)

- **Named-user or concurrent-user licensing** (e.g. "10 back-office users"): not seats of installations. Could later count
  Authorization sessions; out of v1.
- **Upsell in search:** should Capabilities show unavailable features greyed out ("not in your plan") instead of hiding them? Needs a
  Capabilities option; the denial already carries the module id.
- **Product brand and vendor endpoint** in the examples (`yfex-farma`, `licenciamento.yfex.com.br`) are placeholders.
