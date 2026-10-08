# 10 — Phases, acceptance, requests to other libraries, implementation log

## 1. Working rules (every phase)

- **No code before the user confirms ⚑LC1–LC22** (README §6).
- `C:\Dev\YFex` is **not a git repository** and edits are live. Before touching an existing file, copy it to
  `C:\Temp\licensing-backup\<phase>\` with the same relative path.
- Check modification times before editing shared files. Parallel sessions own: YFex.Installations (design + implementation),
  Authorization consolidation, YFex.Time, YFex.Cryptography, YFex.Networking, YFex.Offline. Coordinate through
  `ListAgents`/`SendMessage` and their implementation logs; never edit their code to land a hook.
- Use the RoselYn MCP tools for C# exploration and `dry_run_compile` before writing non-trivial edits.
- No new NuGet package without asking. Every key, signature and hash goes through YFex.Cryptography (architecture test green with no
  new allow-list entry).
- Build with `--artifacts-path C:/Temp/licensing-artifacts`; native AOT smoke at 0 warnings for every new package.
- Record test counts before and after in §5 and update the memory file `yfex-licensing.md`.

## 2. Requests to other libraries (hooks licensing needs; each owner decides the exact shape)

### 2.1 YFex.Installations (⚑I23 + details)

| # | Request | Why | Phase there |
|---|---|---|---|
| R1 | **bundle publishers** per kind (⚑I23 hook 1), with the publisher's keys supplied by an `IPublicKeyDirectory` the publisher owns (licensing refreshes it from `licensing.vendor-keys`) | vendor-signed kinds verified only against vendor keys | I1 |
| R2 | **`IEnrollmentPolicy`** (⚑I23 hook 2), called **inside the registry's enrollment transaction** under a per-tenant lock, and also at enrollment-**code creation** (candidate without keys) | no race on the last seat; the admin learns about the limit before the trip to the shop | I2 |
| R3 | `EnrollmentCandidate { Tenant, Scope, Role, AppName, Replaces, Hint }` (`AppName` maps to a product) | seat counting per product and role; replacement | I2 |
| R4 | `CreateEnrollmentCode(..., Replaces: InstallationId?)`; on successful enrollment of the new one, the replaced one is revoked **atomically** | moving a seat without a second seat (03 §4) | I2 |
| R5 | `EnrollmentRefused.PolicyCode` (string, e.g. `"licensing.seats"`) next to `EnrollmentRefusal.PolicyRefused` | the first-run screen can say why (01 §5) | I2 |
| R6 | registry **read API** (count by tenant, role, scope prefix) and **events** `Enrolled / Revoked / EvidenceKeyChanged` with `EnrolledAt`, consumable durably by central-side libraries | seat counts and the activation-delta outbox | I2 |
| R7 | central **forwards** received bundles of other publishers unchanged (push to the audience, gossip on the `bundles` channel) | vendor bundles reach every checkout, also offline on the LAN | I2/I4 |

### 2.2 YFex.Authorization

| # | Request | Why |
|---|---|---|
| A1 | `IPermissionAvailability` (`Restrict` after all sources, `TryExplain`), `PermissionAvailabilityRequest(ScopeIdentity, InstallationId?)` | gating without touching feature code (06 §1) |
| A2 | `DenialKind.Unavailable = 8`; the gate maps a permission removed by availability to `Denial(Unavailable, Missing, Code)` — before `MissingPermission` | "not in your plan" instead of "no permission" |
| A3 | context build on the server receives the installation from `CallContext` (⚑I11) | seat-aware masking for calls from installations |
| A4 | snapshot and quick-check paths run the same restriction | UI, offline and server agree |

### 2.3 Others

| Library | Request | Status |
|---|---|---|
| YFex.Cryptography | `FileSigningKeyStore`/file root key on Linux/macOS (shared with Installations); confirm `RandomNumberGenerator`; optional KMS/HSM `ISigningKey` | open (09 §4) |
| YFex.Time | none: `GetTrustedNow()` + `IsUsableForSecurity` are enough | ✅ built |
| YFex.Capabilities | optional "show unavailable as upsell" | open question |
| YFex.Tenancy | optional scope kinds for `ScopeLimit` per kind | open question (03 §1.1) |

## 3. Dependencies

| Needs | From | Status | Phase here |
|---|---|---|---|
| ⚑LC1–LC22 | user | **pending** | all |
| ⚑I23 confirmed | user (installations README §6.2) | pending | all |
| core `InstallationId`, `InstallationRole`, `BundleKind/Header/Audience`, `LocalInstallation` | Installations I0 | not built | P1 |
| `SignedBundle`, `IBundleVerifier`, `IBundleStore`, floors, publishers (R1) | Installations I1 | not built | P1 |
| registry, enrollment, policy (R2–R6), `.Testing` harness | Installations I2 | not built | P3 |
| `CallContext` installation (⚑I11) | Installations.Rpc | not built | P2 (server mask) |
| bundle gossip (R7) | Installations I4 | not built | P3 (LAN freshness) |
| `IPermissionAvailability`, `DenialKind.Unavailable` (A1–A4) | Authorization | not requested yet | P2 |
| `SignedPayload`, `SigningKeyRing`, `CanonicalWriter`, `Hashing`, `SealedValue` | YFex.Cryptography 0–3 | ✅ built | P1 |
| `GetTrustedNow`, `IsUsableForSecurity`, `FakeDeviceClock`, `FakeTimeSource` | YFex.Time T0–T2 | ✅ built | P1 |

**Consequence:** licensing implementation starts after Installations I1 (documents and evaluator can be written against I0 types and
an in-memory bundle verifier earlier, but would be re-tested).

## 4. Phases

### LC-P0 — Hooks in other libraries (done by their owners)

- Installations R1–R7 inside its I1/I2/I4; Authorization A1–A4 as a small phase of its own.
- **Acceptance (theirs):** a vendor-kind bundle signed by a tenant key is refused (already in Installations I1 acceptance); a policy
  refusal reaches the first-run screen with its code; `Restrict` runs after sources and denials say `Unavailable`.

### LC-P1 — Repo, catalog, documents, evaluator

- Repo `C:\Dev\YFex\YFex.Licensing` (`licensing.slnx`): `YFex.Licensing`, `.Testing`, `.Tests`, `.AotSmoke`, `.Benchmarks`.
- Catalog builder + manifest + snapshot helper; `License`, `ActivationList`, `VendorKeyList` + canonical codecs; `VendorKeys` +
  key-list rules (09 §3); `LicenseEvaluator` (04 §1–§4: states, E and L, running time, freshness); `ILicense`; `TestVendor`.
- **Acceptance:** golden encodings of the three payloads; catalog rules (a permission in two modules fails; keep-list outside its
  module fails); every scenario of 04 §5 as a test with `FakeDeviceClock`/`FakeTimeSource`; clock set forward offline removes
  nothing; wrong tenant / tenant root / product → finding and last good kept; standby-signed list beats active-signed list; revoked
  key's documents stop verifying; AOT 0 warnings; the suite runs on Windows **and Linux**.

### LC-P2 — Gating

- Licensing's `IPermissionAvailability` (precomputed masks, `TryExplain` = module id), Authorization signals on change,
  `ModuleAvailability`, `WaitUntilAvailableAsync`, `GetLimit`.
- **Acceptance:** 01 §8 test green; Capabilities search no longer returns masked commands and the agent gate denies them (test with
  Capabilities' in-memory registry); service actors masked; base/keep-list/licensing permissions never masked; `Restrict` ≤ 50 ns
  for 1 024 permissions, 0 B; `HasPermission` unchanged (benchmarks).

### LC-P3 — Central: seats and activation

- `YFex.Licensing.Central`: seat counter, `LicensingEnrollmentPolicy`, `ISeatAvailability`, activation-delta outbox, pending/refused
  handling, replace, downgrade view, forwarding of vendor bundles, `LicensingPermissions`, license admin queries.
- **Acceptance:** two concurrent enrollments for the last seat → exactly one succeeds; replace uses no extra seat; a 5th terminal is
  refused with `licensing.seats`; no License → enrollment allowed, installations read-only until a License arrives; downgrade keeps
  everyone for the activation grace, then drops the newest.

### LC-P4 — Vendor service (SaaS path)

- `YFex.Licensing.Vendor`: accounts, editions from the shared catalog, issue/change/renew/trial, activation decisions and lists,
  `IVendorLicensing` in-process, `LicensingVendorPermissions`, audit of every command, two-person key rotation (with Authorization 4b
  approvals when available; until then a documented manual procedure).
- **Acceptance:** SaaS harness: a new checkout is activated within the enrollment call; changing the edition reaches three
  installations without restart (06 §6); the audit trail names who issued what; editions naming unknown modules are refused.

### LC-P5 — Check-in (on-premises and disconnected)

- Licensee ring + activation code flow, `CheckInRequest`/`Response`, HTTPS endpoint + client with back-off, file export/import,
  freshness re-issue, findings counts in the request.
- **Acceptance:** a replayed response is refused (nonce); retries are idempotent; a blocked vendor host gives scenario 04 §5 #7 on
  schedule; the file path round-trips byte-identically; a central patched in the test to under-report produces `SeatCountMismatch`.

### LC-P6 — Metering (only when the user wants it)

- `IUsageMeter`, durable counters, signed usage reports, central aggregation, check-in totals, `UsageForBilling`.
- **Acceptance:** `Add` < 20 ns, 0 B; a crash loses ≤ 1 minute; offline days are reported at reconnect exactly once.

### LC-P7 — Findings and vendor dashboard queries

- `LicensingFindingKind` detection (08 §4), acknowledgement, queries.
- **Acceptance:** each finding kind produced by a harness scenario.

## 5. Implementation log

*(empty — nothing implemented)*
