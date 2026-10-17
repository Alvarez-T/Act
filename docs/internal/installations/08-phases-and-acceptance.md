# 08 — Phases, acceptance and implementation log

## 1. Working rules (every phase)

- `C:\Dev\YFex` is **not a git repository** and edits are live. Before touching an existing file, copy it to
  `C:\Temp\installations-backup\<phase>\` with the same relative path.
- Check modification times before editing shared files. Parallel sessions own: YFex.Networking (its repo + core `YFex.Networking`),
  YFex.Offline design/implementation, Authorization consolidation (Rules approvals, Messaging.Rpc `CallContext`), YFex.Time,
  YFex.Cryptography. Coordinate through `ListAgents`/`SendMessage` and their implementation logs.
- Use the RoselYn MCP tools for C# exploration and `dry_run_compile` before writing non-trivial edits.
- No new NuGet package without asking. Every key, signature, certificate and keyed hash goes through YFex.Cryptography (its
  architecture test must stay green with **no** new allow-list entry).
- Build with `--artifacts-path C:/Temp/installations-artifacts`; native AOT smoke at 0 warnings for every new library.
- Record test counts before and after in §5 and update the memory file `yfex-installations.md`.

## 2. Dependencies

| Needs | From | Status | Phase here |
|---|---|---|---|
| `ISigningKeyStore`, `CngSigningKeyStore`, `SignedPayload`, `SignatureVerifier`, `IPublicKeyDirectory`, `CertificateAuthority`, `CertificateTrust`, `MessageAuthentication`, `SealedValue`, `Hashing` | YFex.Cryptography Phases 0–3 | ✅ built | I1–I3 |
| CNG-held `IRecipientKey` (key-agreement key) | YFex.Cryptography (crypto 13 §6, not built) | open | I5 |
| `FileSigningKeyStore` (Linux/macOS v1) and an Apple Keychain/Secure Enclave store (09 §5) | YFex.Cryptography | designed 2026-10-07 (crypto 14): file store approved, not coded; Apple store = spike on a Mac | I1 (Linux/macOS) |
| ⚑I21–I23 | user | ✅ 2026-10-07 (user wrote L21–L23; approved recommendations and implementation) | I1 |
| `IAnnouncementSigner/Verifier`, beacons, `InMemoryNetwork`, `Hysteresis`, `NetworkFingerprint` | YFex.Networking Phases 2/5 | ✅ built (Networking Phase 5, 2026-10-13; API confirmed by that session for I3) | I3 ✅ |
| core `YFex.Time` types, `TimeBuilder` | YFex.Time T0–T2 | ✅ built; ⚑T13 rule added to `TimeSelection` in I4 (no Time session was running; logged in time README §10) | I4 ✅ |
| `ScopeIdentity` | YFex.Tenancy | ✅ | I1 |
| `CallContext` extension point | Messaging.Rpc (authorization session) | ✅ exists | I2 |
| user decisions ⚑I1–I3, ⚑I5–I20 | user | ✅ 2026-10-07 | — |
| ⚑I4 (storage of keys/state, README §6.1), ⚑O25–O30, ⚑T13 | user | ✅ 2026-10-07; peers vote with NTP | prerequisites approved |

## 3. Phases

### Phase I0 — Core types — DONE 2026-10-07

- `YFex/src/YFex/Installations/`: `InstallationId` (`ICanonicalValue`, `ILogValue`, formatting/parsing), `InstallationRole`,
  `InstallationStatus`, `InstallationIdentity`, `LocalInstallation`; `BundleKind`, `BundleAudience`, `BundleAudienceKind`,
  `BundleHeader` (⚑I9). `CLAUDE.md` solution map row.
- **Acceptance:** YFex.Tests + new tests (canonical golden bytes of an id, round-trip text, `LocalInstallation.Publish` raises
  `Changed` once per change). Core AOT unchanged.

### Phase I1 — Repo, local state, keys, trust — DONE 2026-10-07 (spike deferred, see §5)

- **Platform spike first** (09 §4 R1–R5): TLS 1.3 / 1.2 negotiation and client certificates with non-exportable keys on Windows 10,
  Windows 11, Linux (container) and macOS; macOS Local Network permission; atomic writes with Unix modes. Results decide ⚑I22.
- Repo `C:\Dev\YFex\YFex.Installations` (`installations.slnx`): `YFex.Installations`, `.Windows`, `.Testing`, `.Tests`,
  `.Windows.Tests`, `.AotSmoke`, `.Benchmarks`.
- Local state (id minted at first run, `AdoptExistingId`, sealed `installation.state`, publish `LocalInstallation`),
  `IInstallationKeyStore` (CNG + in-memory), `InstallationCertificate` (URN read/write), `InstallationTrust` + `PeerCheck`, roster and
  revocation payloads + codec, `SignedBundle` codec, `IBundleVerifier`, `IBundleStore` + floors, `InstallationKeyDirectory`.
- **Acceptance:** golden encodings (roster, revocations, enrollment request, assertion, beacon payload); TPM key non-exportable
  (Windows test); tampered state file → `StateUnreadable`; validator refuses foreign root, revoked, expired-past-grace, other scope
  not in roster, wrong role; bundle verifier refuses older-than-floor and same-version-different-hash; bundle publishers (⚑I23): a vendor-kind bundle signed
  by the tenant key is refused. **The test suite runs on Windows and Linux** (and macOS when a machine is available); state files
  are 0600/0700 on Unix. AOT 0 warnings. Crypto architecture test green.

### Phase I2 — Central and enrollment — DONE 2026-10-08, reworked 2026-10-13 (see §5)

- `YFex.Installations.Central`: enrollment codes, issuing CA operation (root + installation issuing CA), registry (in-memory + SQL
  later), enrollment/renewal endpoints, revocation, roster publication, `InstallationPermissions`, hint key derivation, `IEnrollmentPolicy` seam (⚑I23:
  seat limits for a future YFex.Licensing, hardware-key policy for brokers).
- **Licensing requests (licensing 10 §2.1)** to build in I2: R2 `IEnrollmentPolicy` inside the registry's enrollment transaction under a
  per-tenant lock and at code creation; R3 `EnrollmentCandidate { Tenant, Scope, Role, AppName, Replaces, Hint }`; R4
  `CreateEnrollmentCode(..., Replaces)` with atomic revocation of the replaced installation; R5 `EnrollmentRefused.PolicyCode`; R6 registry
  read API (counts by tenant/role/scope prefix) + durable `Enrolled/Revoked/EvidenceKeyChanged` events; R7 central forwards other
  publishers' bundles unchanged (gossip part in I4). R1 (bundle publishers with a publisher-owned `IPublicKeyDirectory`) is done in I1.
- `EnrollmentClient` + renewal loop; `YFex.Installations.Rpc` (assertion on the client, `ctx.WithInstallation()`, scope pinning,
  `RequireInstallation(role)`). No command-line tool: the installer is a future **YFex.Installer** (user, 2026-10-07).
- **Acceptance (harness):** enroll a terminal end to end; idempotent retry; id takeover refused; renewal rotates the transport key;
  revocation reaches a connected installation within one push; a call from CAIXA-02 (loja-centro) with scope loja-norte is refused
  online; `RequireHardwareKeys(SiteBroker)` refuses a software-key broker.

### Phase I3 — Friends and the peer endpoint — DONE 2026-10-14 (TPM server credential and Linux/macOS open, see §5)

- `PeerEndpoint` (SslStream, ALPN, frames, channel registry, rate limits), `IInstallationFriends`, `FriendSet`, discovery order
  (last address → roster addresses → verified beacons → typed), `InstallationAnnouncementSigner/Verifier` on YFex.Networking,
  TLS helpers (`CreateServerOptions/CreateClientOptions`), `InstallationHarness` on `InMemoryNetwork`.
- **Acceptance:** 10 simulated installations on one LAN find each other; the "customer laptop" (no certificate) never appears in
  `FriendSet`; forged/replayed beacons rejected and counted; ARP-spoof simulation (candidate address points to a rogue host) →
  refused handshake; Loja Norte terminal on Loja Centro's LAN gets only `Tenant` channels; mTLS TLS 1.3 with TPM keys (Windows
  test); benchmark: handshake + first request ≤ 50 ms with software keys on the dev PC.

### Phase I4 — Mesh channels — DONE 2026-10-14 (consumers in their sessions; Linux/macOS open, see §5)

- `time` channel + `UseSiteAnchor()` / `UsePeerTime()` (`ITimeSource`s; YFex.Time applies T13 when amended), `relay` channel
  (`IInstallationRelay`, hold + retry + dedupe), `bundles` gossip (audience-checked offers), sharing scopes from the roster.
- Consumers (with their sessions): Logging.Shipping `ThroughFriends()`, Audit relay + node-head signing, Offline Phase 4 broker
  services on peer channels (Offline's decision).
- **Acceptance:** S-time: internet down → terminals anchored to the broker with its uncertainty; broker off → peers fill in flagged
  `Unauthenticated`, `IsUsableForSecurity` false; relay: CAIXA-01 without internet delivers an audit batch through CAIXA-02, a friend
  that drops it causes a retry through another friend, central sees it once; gossip: revocation reaches every friend within one beacon
  interval + one request with central unreachable; a snapshot addressed to CAIXA-02 is never offered to CAIXA-01.

### Phase I5 — Pairing, hardening, key agreement

- SAS pairing + site issuing CA + provisional status; hardware-key policy; key-agreement key (after crypto); rotation drills (issuing
  CA yearly, root overlap); chaos suite (partitions, restarts, disk-image restore) in the harness; firewall-rule presence reported by `IInstallationFriends`/diagnostics (creating it = future YFex.Installer).
- **Acceptance:** offline S7 (spare PC paired with SAS during an outage, confirmed later) passes; a MITM during pairing produces
  different digits; provisional installation cannot issue approvals; all scenario tests green; AOT 0 warnings.

## 4. Docs to update when phases land

`YFex/CLAUDE.md` (solution map row for core `YFex.Installations` + repo, common-task row "installation identity / friends"),
`docs/index.md` row status, offline 02/03/04/05/11 (pointer notes now, details when Offline Phase 1 starts), crypto 12 §5 handoff
(addressee), time README §8 (T13, `UseSiteAnchor` home), logging 02/05 (`InstanceId` = installation id, relay), audit 05/06 (node id,
evidence key, relay), privacy 04 §5/§7 and 05 §5.1 (installation id + evidence key), authorization-consolidation 06/09 (4b directory),
Devices plan §12 + `YFex.Devices/docs/04-serial.md:102`.

## 5. Implementation log

### 2026-10-07 — I0 core contracts complete

- Approved I21–I23 as recommended; interpreted the user's “L21 to L23” as the three pending installation decisions.
  O25–O29 rows accidentally removed by the previous approval edit were restored from the pre-edit backup.
- Added YFex/src/YFex/Installations/: InstallationId, role/revocation enums, native status union, immutable identity,
  LocalInstallation, bundle kind/audience/header. No enrollment, I/O, cryptography implementation or OS code in core.
- Id canonical form is exactly 16 big-endian Guid bytes; default text is Guid D. UTF-16 and UTF-8 parsing enforce the same
  36-character contract. Existing persisted Guid values remain representable, including legacy logging ids that are not v7.
  Runtime first-run creation will use v7. Span round trips allocate zero bytes after warm-up in the test.
- Id logging is registered through the existing core module-initializer convention and writes the Guid as a search key.
- LocalInstallation.Publish uses atomic compare-and-exchange and structural identity equality: equal snapshots do not notify.
  Event callbacks run synchronously after publication, outside locks; concurrent callbacks may overlap. Explicit identities must
  be used for additional hosts in a multi-installation test process. This ambient slot is metadata, never proof of enrollment.
- Native unions can contain an unset/default case. Tests and smoke explicitly handle it; runtime trust validation must reject it.
- Added YFex/src/YFex.Installations.AotSmoke to yfex.slnx. The sibling runtime repo is not created by I0.
- Verification: **YFex.Tests 208 → 233, all passing**; **Cryptography ArchitectureTests 2/2 passing**;
  Windows x64 **NativeAOT publish: 0 warnings**, executable exit **0**.
- RoselYn get_names worked, but get_minimal_context_for and dry_run_compile returned internal errors (also on retry at project
  scope). Used lapisGen for API inspection and an isolated compiler validation of the candidates before adding core files;
  that build had 0 warnings/errors. Full Release tests and native compilation then validated the written code.
- Tool environment: VSTest could not connect to its child inside the sandbox; the authorized external run passed. The shell
  omits the OS environment variable; NativeAOT needed -p:OS=Windows_NT for this Windows host, with no change to library code.
- Artifacts: C:\Temp\installations-artifacts\I0 and I0-tests. Existing-file backups:
  C:\Temp\installations-backup\implementation-2026-10-07-I0.
- **Next:** I1 runtime (local state, key-store adapter, certificate policy, roster, signed bundles and publisher validation).
  Non-Windows key stores remain a Cryptography dependency; Linux/macOS behavior has not been run here.
- User preference: work and communicate in English.

### 2026-10-07 — Takeover review of the Codex work (Claude)

The user asked Claude to take over from Codex, check what was done and report mistakes.

**I0 (core) — reviewed, correct.** Types match 02 §4 / §4.1; YFex.Tests 233/233 green. One structural mistake fixed: Codex added
`YFex/src/YFex.Installations.AotSmoke` to the core repo and `yfex.slnx`, and created a second, empty project with the same name in
the sibling repo. Convention (YFex.Time, YFex.Networking) keeps AOT smokes in the sibling repo's `tests/`. Its checks were moved into
`YFex.Installations/tests/YFex.Installations.AotSmoke/Program.cs`; the core project was removed from `yfex.slnx` and `CLAUDE.md`.

**I1 as left by Codex (after its I0 log entry, not logged):** the repo did not compile and was not runnable.
- `InstallationStateStore.cs:9` used `readonly union struct …`, which is not valid syntax: 5 compile errors. Fixed to `readonly union`.
- The test project and the AotSmoke project had no source files (an `Exe` without `Main` cannot build).
- `Directory.Build.props` did not follow the sibling-repo conventions (`YFexRoot`, shared versions, FluentAssertions, explicit
  core reference).
- The state store itself (sealed `installation.state`, Guid v7 at first run, adopt-on-first-run, 0600/0700 on Unix, protected ACL
  on Windows, atomic create, tampered or unknown state reported and preserved) is sound and kept. The only change was moving the
  application-name validation to a public `InstallationStorageOptions.ValidateApplicationName` so key and state names share it.
  It had no tests; 9 were added.

### 2026-10-07 — I1 runtime built (Claude)

**Built** in `C:\Dev\YFex\YFex.Installations` (`installations.slnx`):
- `YFex.Installations`:
  - `InstallationDomains`: every signature and MAC domain.
  - `IInstallationKeyStore` / `InstallationKeyStore` over any `ISigningKeyStore`: names `installation/transport` and
    `installation/evidence`.
  - `InstallationCertificate`: URN profile; claims parsed from the SAN extension with `AsnReader`, because the BCL has no URI
    enumeration. Exactly one name of each kind is required, and the scope must be canonical (parse + format + escape round trip).
  - `SignedBundle`: create, encode, decode; the decode is canonical-only.
  - `BundleCheck`, and `BundlePublishers` (⚑I23 hook 1: a kind verifies only against its publisher's keys).
  - `BundleVerifier`: signature as of `IssuedAt`, payload hash, audience rules, validity window.
  - `BundleStore`: version floors, `ConflictingSameVersion`, re-verify at every load. A damaged floor throws instead of resetting.
  - `IBundleStorage` + `InMemoryBundleStorage`.
  - `InstallationRoster` (+ codec) and `InstallationRevocations` (+ codec).
  - `RosterKeyDirectory : IPublicKeyDirectory`: evidence keys of members and visitors, with revocation time.
  - `Friend`, `PeerCheck` union, `InstallationTrust`.
- `YFex.Installations.Windows`: `WindowsInstallationKeys.Create(app, scope)` over `CngSigningKeyStore` (TPM first), with
  key-name prefix `YFex.Installations.{app}.`.
- `YFex.Installations.Persistence`: `KeyValueBundleStorage` over YFex.Persistence `IKeyValueStore` (⚑I4).
- `YFex.Installations.Testing`: `TestTenantAuthority` (root → installation issuing CA → site CA; issues certificates with the
  URN profile; signs rosters and revocations), `TestSiteAuthority`, `TestInstallation`.
- Tests: `YFex.Installations.Tests` **44**, `YFex.Installations.Windows.Tests` **2** (CNG keys, sign/verify, reuse after a
  "restart"). Coverage:
  - Loja Centro trust scenarios: same site, foreign root with the same names, revoked by id and by serial, roster-revoked, grace
    ±, other store only on tenant channels, visitor channels, role mismatch, TLS usage per side.
  - Provisional pairing: switch off/on, broker role, other store.
  - Bundle tampering, vendor-vs-tenant publisher, audience, validity, key revoked before/after issue, floors across restart,
    re-verify on load, damaged floor.
  - Regression goldens of the bundle header, roster and revocations.
- AOT smoke: `dotnet publish -r win-x64` → **0 IL warnings** (`IlcTreatWarningsAsErrors`), exit **0**.
- Cryptography `ArchitectureTests` 2/2: no keyed primitive outside YFex.Cryptography. YFex.Tests 233 unchanged.

**Security fix found by the tests (my own code, before it shipped):** with pairing switched off, a certificate issued by a site
issuing CA still chained to the tenant root, because the peer sends the site CA itself. It was then classified `OtherSite` and
accepted on tenant-wide channels (time, relay). Now the leaf's issuer must be one of the tenant's **installation issuing CAs**, or a
roster-listed site CA while pairing is on; anything else is refused.

**Deviations from the design (with reason):**
1. The roster gains `InstallationIssuingCertificateAuthorities`. The tenant's issuing CAs travel signed in the roster so the yearly
   issuing-CA rotation reaches peers; `InstallationTrust` also takes the CAs pinned at enrollment and requires at least one.
2. Roster entries and visitors carry the **evidence public key** (SPKI), not only its id, so LAN peers can verify beacons and
   evidence without central. Roster entries also carry the optional keyed network fingerprint (Networking Phase 3).
3. `Friend` in I1 holds identity and relation only (+ `Provisional`, `InCertificateGrace`); reachability, offered channels and
   "reaches central" come with the friend set in I3.
4. The bundle store depends on a small `IBundleStorage` interface, with the `IKeyValueStore` adapter in its own package (the
   YFex.Time `.Persistence` pattern). This keeps MemoryPack/FusionCache out of the main package.
5. `BundleVerifier.Verify` takes `DateTimeOffset` (and a `TrustedInstant` overload); `BundleStore` takes a `TimeProvider`, which
   is the trusted clock in production.
6. Not in I1: transport-key rotation and the TLS-certificate binding (I2 renewal / I3 channel), the central
   `InstallationKeyDirectory` (I2, with the registry), and enrollment, assertion and beacon formats (I2/I3).
7. **Platform spike (09 §4) not run:** this PC has no Docker or WSL and no Mac is available. Windows 11 mutual TLS 1.3 with CNG
   keys is already proven by YFex.Cryptography's Windows tests. Linux/macOS runs and R1–R5 move to the start of I3 (peer channel),
   where they decide ⚑I22; they need a Linux container or a Mac.

**Native union testing note:** FluentAssertions' `BeOfType<Case>()` sees the union struct, not its case. The tests use
`ShouldAccept/ShouldRefuse/ShouldLoad/ShouldBeUnreadable` helpers (`UnionAssertions.cs`).

**Next: I2** — `YFex.Installations.Central`: enrollment codes, issuing CA operation, registry + `InstallationKeyDirectory`,
`IEnrollmentPolicy`, renewal, revocation, roster publication. Also the enrollment client, enrolled state v2, and
`YFex.Installations.Rpc` (assertion + scope pinning). That phase touches the Messaging.Rpc `CallContext` owned by the
authorization session: coordinate first.

### 2026-10-08 — I2 central enrollment and RPC installation checks complete

- Coordinated the Messaging.Rpc boundary with the Authorization session before implementation. Installation assertion state stays
  in a separate `InstallationCallContext`; the adapter does not change or overload the actor-oriented `CallContext` sources.
- Added `YFex.Installations.Central`: one-time enrollment codes, tenant CA issuance, challenge/proof verification, registry reads,
  renewal, revocation, evidence-key changes, signed roster/revocation publication, permissions and the external bundle forwarder.
- Implemented Licensing R2–R7: policy evaluation at code creation and inside the serialized per-tenant enrollment transaction;
  the full candidate contract; atomic replacement/revocation; stable `PolicyCode`; filtered counts plus cursor events; and exact-byte
  forwarding for bundles owned by other publishers.
- Added the client-side enrollment and renewal runtime: canonical enrollment/renewal protocols, state format v2 (v1 remains readable),
  named A/B transport-key rotation, save-before-delete crash ordering, and the renewal loop.
- Added `YFex.Installations.Rpc`: short evidence-key assertions, server validation against the central key directory, revocation and
  descendant-scope checks, client header integration, middleware ordering around Authorization, and `RequireInstallation(role)`.
- Acceptance evidence: 55 solution tests pass (44 core, 2 Windows, 6 Central, 3 RPC). The new tests cover end-to-end enrollment,
  concurrent last-seat enforcement, idempotent retry and identity-takeover refusal, replacement, hardware-key refusal,
  renewal/revocation, event cursors, assertion tampering, revocation and sibling-scope refusal. The I2 NativeAOT smoke publishes
  and executes successfully on `win-x64`.
- Build noise is limited to existing/unrelated warnings: unavailable NuGet vulnerability feeds, existing CQRS analyzer RS1032 and
  nullability warnings, and the installed .NET 11 preview SDK notice.

**Next: I3** — peer endpoint, friends/discovery and the deferred cross-platform transport spike.

### 2026-10-13 — I2 review and rework (Claude, session "I2 central enrollment verification")

The user asked for a full review of the 2026-10-08 I2 work, then for every bug to be fixed and the code improved to the ecosystem
rules (performance, AOT, memory, no boxing/LINQ in shared code, own types, unions, current .NET APIs, domain-neutral, every time or
limit configurable). Backup of the previous state: `C:\Temp\installations-backup\I2-review-2026-10-09` (repo, the touched
Messaging.Rpc files and these docs).

**Bugs found and fixed. Each one was reproduced first, or is now covered by a test:**
1. Enrollment codes came from `KeyId.New()` (Guid v7): about 16 bits of entropy when the creation time is known, and the first group
   was constant for ~65 s. Codes now use `RandomNumberGenerator.GetItems` (5 bits/char, 2–4 groups), are stored only as a SHA-256
   digest, and failed attempts are limited per tenant (`IEnrollmentAttemptLimiter`). Nonces, the hint key and the site tag also
   used Guid v7; all are random or derived now.
2. A renewal retried after a lost answer was refused forever (`InvalidProof`). Central now keeps `PreviousTransportKey` and accepts
   the retry idempotently. An enrollment retried after a lost answer (code already used or expired) is recognized by the installation
   id + code digest (`EnrollmentChallengeRequest`) instead of failing at the challenge.
3. A display name different from the code's was refused as `RoleMismatch`. Now the code's name wins, and the installation's name is
   used when the code has none.
4. Bundle versions were process-local counters: they restarted at 1 after a central restart (installations' floors would refuse
   everything) and could race (a newer version with older content). Versions and the decision to re-issue now come from the
   registry transaction (`PublishBundlesAsync`). Bundles are re-issued before expiry (`BundleRefreshBefore`, hosted
   `BundleRefreshService`). New versions raise `CentralInstallationBundlePublisher.Published` (the push hook).
5. RPC assertions only worked in-process: no client path to central's challenge. There is now the RPC service
   `IInstallationAssertionChallenges` (checked-in ActualLab proxy, golden-tested). Challenges hold no server state
   (`InstallationChallenges` = `MessageToken` over the purpose, the audience and 16 random bytes, shared ring
   `installations/challenges`), so they work on every node and survive restarts. Freshness is the server's clock only.
6. `ChangeEvidenceKeyAsync` dropped the old evidence key. Evidence keys are now kept forever (`EvidenceKeys`). The replaced key's
   validity ends at the change and, on revocation, every key carries the revocation time. `FindByEvidenceKeyAsync` and
   `InMemoryInstallationRegistry.EvidenceKeyDirectory` (`IPublicKeyDirectory`) serve central verifiers.
7. The event cursor could skip events (sequence assigned outside the lock). Sequences are now contiguous; reads are O(1) slices.
8. The client stored central's answer unchecked. `ProvisioningValidation` now checks, before saving:
   - the certificate chains to the pinned root (unchanged at renewal);
   - it names this id, role and scope and holds the transport key just proved;
   - the roster and revocations verify with the bundle keys;
   - the tenant, scope and application are unchanged.

   Otherwise the result is `InvalidProvisioning` and nothing is stored.
9. The renewal loop died on an `HttpClient` timeout or any exception. Transports now report `CentralUnreachable` themselves; the
   loop reports exceptions (`RenewalError`), backs off exponentially and sleeps until the due date.
10. Certificates were issued before the code and policy checks (and outside the tenant). Issuing now happens inside the registry
    transaction after every check (`InstallationCertificateIssuer`), and `InstallationAuthority.Issue` refuses scopes of other
    tenants.
11. `UseInstallationAssertion` took Messaging.Rpc's single `SendToken` slot (the actor token). It now uses the new additive
    `CallContextClientBuilder.SendHeader(name, provider)` (see "Messaging.Rpc" below).
12. RPC refusals threw exceptions. They are now `Unauthenticated` / `Forbidden(Denial(ResourceDenied, "installation.scope"))` results
    through `RpcErrorResults<T>`, like the CallContext gate. Fusion compute calls (no headers) and the challenge service are exempt
    from `RequireEnrolledInstallations`.
13. The I2 work had deleted the I1 XML documentation of `InstallationDomains`, `InstallationStateStore` and
    `InstallationKeyStore`. It is restored.

**Missing I2 scope now built:**
- the HTTP binding: `HttpInstallationEnrollmentTransport` (client) and the new package
  `YFex.Installations.Central.AspNetCore` (`MapYFexInstallationsEnrollment`, request delegates only, AOT-safe);
- the canonical wire codecs of challenge requests and answers;
- DI: `AddYFexInstallations(...)` (state, keys, enrollment client, hosted start-up + renewal; `.UseWindowsKeyStore()`,
  `.UseWindowsHardwareHints()` in `.Windows`), `AddYFexInstallationsCentral(...)` (multi-tenant `CentralInstallations`),
  `AddYFexInstallationRpcServer/Client`;
- the hardware hint (04 §7):
  - tenant key derived with `KeyDerivation` (`InstallationHint.DeriveTenantKey`), delivered with the enrollment challenge only
    for a valid code;
  - `IHardwareHintSource` with `LinuxHardwareHintSource` (main package, file reads) and `WindowsHardwareHintSource` (MachineGuid
    + SMBIOS UUID via `GetSystemFirmwareTable`);
  - a changed hint at renewal raises `InstallationHintChangedEvent` (a finding);
- the tenant-wide revocation list (stream = tenant root scope, audience Tenant), so a revoked installation of another store is
  refused on tenant channels too. Retention: `RevocationRetention`.
- `.Testing`: `TestCentral`, `LossyEnrollmentTransport`, `TestClock`;
- metrics: meters `YFex.Installations` and `YFex.Installations.Central`.

**Ecosystem rules applied:**
- names: `ApplicationName` instead of `AppName`, also in licensing's R3 `EnrollmentCandidate`;
- one refusal type `EnrollmentRefused` with stable codes `EnrollmentRefusals.Code` (`PolicyCode` only for `PolicyRefused`), and new
  reasons `AlreadyEnrolled`, `NotEnrolled`, `ApplicationMismatch`, `ReplacementUnavailable`, `TooManyAttempts`,
  `InvalidProvisioning`;
- unions for every result: `EnrollmentResult`, `RenewalResult` (+ `RenewalNotDue`), `ChallengeResponse`, `ProvisioningResponse`,
  `RegistryEnrollmentResult`, `InstallationAssertionResult`;
- `EnrollmentSucceeded` carries the identity again (03 §2); the provisioning stays internal to the client;
- every time and limit is in options: `CentralInstallationOptions`, `EnrollmentClientOptions`, `InstallationAssertionOptions`;
- no LINQ in library code (also removed from I1's `ValidateApplicationName`); `SearchValues` for character sets;
- pooled buffers for signed and canonical-check encodings; the payload is encoded once per verification;
- the state store caches the last read until the file changes;
- `X500DistinguishedNameBuilder` instead of hand escaping.

**Moved:** the assertion format, validator, options and client provider moved from `.Rpc` to the main package (namespace
`YFex.Installations`) so any transport can use them. `.Rpc` keeps the Messaging.Rpc binding. This also keeps ActualLab out of the
NativeAOT smoke: ActualLab.Core's generated MessagePack resolver raises IL3050 under `IlcTreatWarningsAsErrors`, and no ecosystem
AOT smoke covers Messaging.Rpc yet. That is a Messaging.Rpc question, not an Installations one.

**Messaging.Rpc (Authorization session's area; additive, coordinated by log because no Authorization session was running):**
- `CallContextClientBuilder.SendHeader(headerName, provider)` (reserved: token, scope, idempotency; no duplicates);
- `FusionMessageBus` sends the extra headers;
- test `FusionMessageBus_sends_extra_headers_without_taking_the_token_slot`.

**Tests:**

| Project | Before | After |
|---|---|---|
| Installations.Tests | 44 | 60 |
| Windows.Tests | 2 | 3 |
| Central.Tests | 6 | 19 |
| Rpc.Tests | 3 | 8 |

- Central.Tests run end to end with the real `EnrollmentClient`. They cover:
  - lost answers on enrollment and renewal;
  - A/B key slots;
  - revocation reaching an installation by one push, and blocking renewal;
  - the last-seat race and the refusal at code creation;
  - atomic replacement with a gap-free cursor;
  - hardware-key policy for site brokers;
  - brute-force limit;
  - role/application binding;
  - refusal of a foreign answer;
  - the HTTP binding;
  - evidence-key history;
  - bundle versions across a publisher restart;
  - counts.
- Rpc.Tests: a real two-hub RPC round trip (challenge over RPC, handler sees the asserted installation, scope pinning,
  `RequireEnrolledInstallations`, revocation at the next call) plus the proxy golden test.
- Messaging.Rpc.Tests 69 → 70.
- NativeAOT smoke (enroll + renew + assertion through the wire codecs): 0 IL warnings, exit 0.
- Cryptography architecture test 2/2.

**Still open (not I2 regressions; recorded for the user):**
- the SQL registry (YFex.Data);
- CQRS admin commands + permission enforcement in hosts;
- Logging/Audit entries per enrollment step (the registry events are the hook);
- a proof-of-possession protocol for evidence-key changes (the registry API expects a verified key);
- revoking the superseded certificate's serial at renewal;
- a macOS hint source;
- push delivery over RPC/mesh (I3/I4 bind to `Published`);
- AOT for Messaging.Rpc/ActualLab;
- Linux/macOS runs (still no container or Mac here).

### 2026-10-14 — I3 friends and the peer endpoint (Claude, session "YFex.Installations Phase I3: friends and peer endpoint")

**Coordination.** The Networking session confirmed its Phase 5 API as built:
- the verdict default is `NotChecked`;
- the per-listener sequence filter advances only after the verifier passes, so the signed issue time is our only replay defence across
  restarts;
- a kind allowed on Public networks needs a verifier and a signer;
- one machine shows up once per address family, so dedupe by installation id;
- payload + proof ≤ 512 bytes.

It agreed that the in-memory stream fabric lives in `.Testing` for now. The design session ("Evaluate and design YFex.Installations
library") was not running, so start and finish are recorded here. Messaging.Rpc `CallContext` was not needed.
At the finish, no session was reachable (the Networking session had ended), so the finish is recorded here only.
Backups: `C:\Temp\installations-backup\I3` (repo, the three touched YFex.Cryptography files, these docs, memory).
Artifacts: `C:\Temp\installations-artifacts\I3*`.

**Built** (main package, folder `Peers/`; all limits and timings in options with documented defaults):
- `PeerEndpointOptions` (+ `PeerTlsVersions` = ⚑I22 policy, `PeerListenMode` = ⚑O25), `PeerFrame` wire v1, `PeerRequestStatus`/`PeerReply`
  (struct, no exception on the request path), `IPeerTransport` + `TcpPeerTransport`.
- `InstallationTls`: `CreateServerOptions(channel)`, `CreateClientOptions(expectedPeer, channel)`, `TryGetFriend(stream)` and `IsAcceptable`.
  TLS 1.2 is accepted only with ECDHE-ECDSA AES-GCM, checked after the handshake on every OS. ALPN `yfex-peer/1`, no SNI.
  `InstallationTrust` is the only validator in both directions. Sets the macOS `UseNetworkFramework` switch.
- `LocalPeer`: the certificate with the transport key attached **through the key store**, the evidence key and the site-tag key.
  `IInstallationKeyStore.CopyWithTransportKeyAsync` is a default interface method; `InstallationKeyStore` uses the new crypto
  `ICertificateKeyStore`.
- `InstallationTrustSource`: the current `InstallationTrust` and a `Changed` event. `Apply(bundle)` takes a newer roster (own scope) or
  revocation list (tenant). It refuses an older version (`OlderThanFloor`) and the same version with other content
  (`ConflictingSameVersion`).
- Connections: one per friend pair, shared by every channel. `PeerChannelRegistry` + `IPeerChannelHandler` (a local `maximumScope` can only
  narrow), `FriendConnection`, `WellKnownPeerChannels` (beacon mask).
- `InstallationFriends : IInstallationFriends` (`FriendSet` snapshot, `Changed`, `ConnectAsync`) and `PeerEndpoint`:
  - discovery order: last address → roster addresses → verified beacons → typed (`CandidatesOf`);
  - reachability `Connected` / `Seen` / `Unreachable` through Networking's `Hysteresis`;
  - a trust change closes the connections of friends that are no longer trusted.
- Beacons: `InstallationBeaconPayload` v1, `InstallationAnnouncementSigner`, `InstallationAnnouncementVerifier` (rejections counted by
  reason), `InstallationSiteTag`, `InstallationBeaconOptions`. `CreateBeaconAnnouncement` and `ListenToBeaconsAsync` work with Networking's
  `BeaconService`.
- Limits:
  - frames/s and bytes/s per friend are **paced**;
  - messages/s per friend and channel are **refused** beyond the rate;
  - requests in flight (`Busy`), frame size, handshakes per address per minute (before TLS), connections, idle close.
- Metrics: `installations.peer.{handshakes,refused,malformed,rate_limited,tls12}` and `installations.beacons.{verified,rejected}`.
- DI:
  - `InstallationsBuilder.Peers(...)` registers `InstallationPeersService` (`IInstallationFriends` + hosted). It starts once the
    installation is enrolled; a renewal presents the new certificate and applies its bundles; `ApplyBundle` is the receiving end of
    central's push.
  - `BeaconsBuilder.AnnounceInstallation()`.
- `.Testing`:
  - `InMemoryPeerFabric`: streams over `InMemoryNetwork` with probes, partitions and `ChaosStream`; `Redirect` simulates ARP spoofing;
  - `InstallationHarness`: N installations, sites, visitors, strangers, `PublishRosters`, `Revoke`;
  - `TestTenantAuthority` additions: tenant key, `IssueCertificate` for an external key, tenant revocations;
  - `TestInstallation.CreateLocalPeer`.
- `benchmarks/YFex.Installations.Benchmarks` (BenchmarkDotNet 0.15.8 as in Networking and Cryptography; planned in README §3).
- **YFex.Cryptography (additive; logged in crypto 12 §5):** `ICertificateKeyStore`, implemented by `CngSigningKeyStore` and
  `InMemorySigningKeyStore`. The architecture test is unchanged, with no allow-list entry.

**Acceptance:**

| 08 I3 item | Result |
|---|---|
| 10 simulated installations on one LAN find each other | ✅ `Ten_installations_…`: broker + 9 terminals, half on adapters Windows calls "Public". All reach `Seen` through verified beacons, then all 45 pairs talk over mutual TLS, with addresses taken only from beacons |
| the customer laptop (no certificate) never appears in `FriendSet` | ✅ its unsigned beacon is counted as malformed; TLS without a certificate, and with a foreign-root "PC-GERENTE" certificate, is refused and counted; no friend set contains it |
| forged / replayed beacons rejected and counted | ✅ a stranger's key (unknown key); CAIXA-01's key claiming PC-GERENTE, and a garbage signature (forged ×2); a captured genuine beacon replayed after newer ones (replayed). Stale by a clock ±35 s off is a separate test. Networking's `RejectedByVerifier` agrees |
| ARP spoof → refused handshake | ✅ the roster address redirected to a rogue host with a foreign-root certificate → refused; redirected to a real member (CAIXA-02) → `UnexpectedInstallation`; once the spoof ends, the real broker answers |
| Loja Norte terminal on Loja Centro's LAN gets only `Tenant` channels | ✅ time and relay open. The broker refuses bundles even though Loja Norte's deliberately permissive roster calls them tenant-wide. The broker lists the terminal as `OtherSite` while connected. Each side's beacons are unknown keys to the other |
| mTLS 1.3 with TPM keys (Windows test) | ⚠ **partly.** The terminal's transport and evidence keys **in the TPM** complete mutual TLS 1.3 as the client ✅. Schannel refuses a user-scope TPM key as the **server** credential (09 §4 R6; guard test; the endpoint counts `ServerCredentialFailures`). Machine scope is not verified (needs elevation) → ⚑I25 |
| handshake + first request ≤ 50 ms, software keys | ✅ test (20 samples, loopback TCP): median 19.8 ms, p90 24.6 ms. BenchmarkDotNet: see below |

Also covered:
- the regional notebook as a visitor: tenant channels and the roster-listed `offline.feed`, not bundles;
- a revocation closes the connection, removes the friend and rejects its beacons;
- a new roster version without changes publishes nothing;
- an idle close falls back to `Seen`; unreachable after repeated failures;
- malformed frames (unknown kind, unopened channel, wrong id space, oversize) disconnect and are counted; a missing ALPN is refused;
- channel rate (`RateLimited`), in-flight limit (`Busy`), frame size in both directions;
- the TLS 1.2 fallback is accepted and flagged only under `Tls13OrTls12` (simulated on Windows 11);
- discovery order and dedup; the TLS helpers in both directions over TCP;
- DI composition with real enrollment (`PeerCompositionTests`). It lives in Rpc.Tests, the only test project with a DI container, so no
  new package was needed.

Mutation check: disabling the expected-installation check or the beacon owner check makes the ARP and forged-beacon tests fail.

**Tests:**

| Project | Before | After |
|---|---|---|
| Installations.Tests | 60 | 102 |
| Windows.Tests | 3 | 6 |
| Central.Tests | 19 | 19 |
| Rpc.Tests | 8 | 9 |
| Cryptography.Tests (incl. ArchitectureTests 2/2) | 138 | 139 |

NativeAOT smoke: 0 IL warnings, exit 0. It now also covers TLS 1.3 over TCP with a request, a 2-installation harness with beacons, and the
beacon codec.

**Benchmarks** (BenchmarkDotNet in-process, ShortRun, Windows 11 development PC):

| Benchmark | Mean | Allocated |
|---|---|---|
| handshake + first request (new TCP connection, mutual TLS 1.3, channel open, 64-byte request) | 30.7 ms (budget ≤ 50 ms ✅) | 75.7 KB |
| request/response of 64 bytes on an open connection (abuse limits raised) | 57 µs | 2.6 KB |
| `InstallationTrust.Check` of one peer certificate | 20.0 ms | 8.7 KB |
| frame header write + read | 4.7 ns | 0 B |
| beacon payload write + read | 33.5 ns | 0 B |

The trust check is dominated by YFex.Cryptography's `CertificateTrust.Validate` (Windows `X509Chain` with a custom root); a validated-chain
cache there would roughly halve the handshake (follow-up for the crypto library, crypto 12 §5).

**Deviations (with reason):**
1. **Beacons were built now**, not deferred: Networking Phase 5 had shipped the API.
2. **Frames:** kinds `Open`/`Accepted`/`Failure` were added to request/response/push/close. The opener numbers channel ids (client and server
   halves). Unknown and unshared channels give the same answer.
3. **Beacon payload v1** adds a flags byte (`ReachesCentral`, `SiteTagged`) and the revocations version: 92 bytes (05 §6 as built).
4. **Site-tag key** is derived from the tenant hint key (label `installations/site-tag`), because no delivery existed → ⚑I24. It is checked
   for members only: visitors' scopes are not in the roster.
5. `Friend` gains `Reachability`, `Offers` and `ReachesCentral` (the doc 03 names; defaults keep I1 equality). A visitor's scope stays
   empty until it authenticates.
6. **`FriendSet`** = roster members and visitors that are not revoked, plus other-site or central installations while they are connected.
7. **Replay:** an equal sequence is accepted, because Networking delivers IPv4 and IPv6 copies. A same-window replay from another address
   can at most add a candidate address, which mutual TLS refuses.
8. **Not built:**
   - network-fingerprint ordering: it needs a delivered per-tenant fingerprint key, like ⚑I24;
   - persistence of the last address: it is kept in memory only;
   - several rosters per installation: a regional notebook cannot verify the beacons of the stores it visits; it reaches them by typed
     address, or when they connect to it.
9. **No intermediates in the TLS context.** The tests found that on Windows `SslStreamCertificateContext.Create(cert, intermediates)` adds
   the intermediates to the user's CA store. Each test run minted a new same-named issuing CA. Once about 100 had piled up, chain building
   failed ("unknown chain building error") for every certificate with that issuer name.
   - Peers never need the intermediates (`InstallationTrust` adds the tenant's issuing CAs), so `LocalPeer` now passes none: no OS-store
     side effect in production.
   - Test CAs now have unique names.
   - The ~100 leftovers in the development PC's `CurrentUser\CA` store are for the user to remove (command in the session summary).
   - Measured afterwards: one trust check costs ≈ 20 ms with or without the leftovers (unique names), so that cost is intrinsic to Windows custom-root chain building, not the pollution; the earlier 18 ms was inflated by the polluted store.
10. `FriendConnection` is a channel handle and not disposable; channels live as long as the connection (idle close).
11. The default inbound pacing (1 000 frames/s per friend) also applies to responses. Mesh traffic stays far below it; the option is
    documented.
12. **Platform spike (⚑I22, 09 §4), Windows 11 only:** TLS 1.3 both ways ✅; TLS 1.2 fallback ✅ (simulated); TPM as client ✅; TPM as server
    (user scope) ❌ R6. Windows 10, Linux and macOS are **not verified** (no machine or container).

**Open (for the user):**
- ⚑I24: the site-tag key.
- ⚑I25: TPM transport keys of listening installations, and the broker hardware policy. One elevated run of the measurement test
  `Machine_scope_TPM_key_as_TLS_server_measurement` (Windows.Tests; skips without elevation) would settle machine scope.
- Remove the leftover test CAs from `CurrentUser\CA`.

**Next (I4):**
- deliver bundles: central's push over RPC or the mesh → `ApplyBundle` + `BundleStore`;
- `bundles` gossip, and the `time` and `relay` channels;
- several rosters for regional installations, fingerprint ordering, persisting the last address;
- Linux and macOS runs.

### 2026-10-14 — I4 mesh channels (Claude, session "Mesh channels Phase I4")

**Coordination.** No other session was reachable (`ListAgents`: none). The ⚑T13 rule is YFex.Time's, approved by the user on 2026-10-07 but
not built; it is a one-rule change, so it was made here and logged in time README §5 (T13 row) and §10. Backups: `C:\Temp\installations-backup\I4`
(these docs, the time README and the touched YFex.Time/core files, memory); the Installations repo is git (I3 = commit `e1a8517`).
Artifacts: `C:\Temp\installations-artifacts\I4*`.

**YFex.Time (⚑T13):** `TimeSelection.ToCandidate`: an anchored `Peer` reply (no `Unanchored`, stratum < 16) is an eligible candidate that
is **never authenticated** (even when the source says it is); unanchored replies of any kind stay evidence-only. Peers therefore vote with
NTP (Marzullo), never pull time back (T4/T10) and never make security usable (T11). Core `TimeSourceKind.Peer` doc updated.
YFex.Time.Tests 62 → 65 (anchored peer fills in unauthenticated; unanchored peer evidence only; peers vote with NTP, a lying peer is
outvoted and a peer behind never pulls time back). The existing "peers count only as evidence next to central" test still holds.

**Built** (main package, folder `Mesh/`, plus DI; the main package now references the YFex.Time library — a sibling project, no NuGet):
- **time**: `PeerTimeWire` (request = version byte; response = version + the 27-byte `TimeReply`, like `CentralTimeWire`),
  `PeerTimeChannelHandler` (answers with `TimeProvider.CreateTimeReply`: the trusted clock's flags, uncertainty and stratum; an installation
  without `AddYFexTime` answers stratum 16). Sources:
  - `SiteAnchorTimeSource` (`Kind = SiteAnchor`, authenticated): the roster's `SiteBroker`; the handshake must prove the role.
  - `PeerTimeSource` slots (`Kind = Peer`, unauthenticated): slot *n* samples the *n*-th eligible friend (terminals and visitors that are
    connected or seen and offer `time`; connected first, then by id; brokers and central are left to the site and central anchors).
  - NTP's loop rule: a peer reply whose stratum is not below this installation's own (while anchored) is ignored.
  - `TimeBuilder.UseSiteAnchor()` and `UsePeerTime(peers = 3)` (`InstallationTimeBuilderExtensions`); beacons now carry the trusted clock's
    reply by default.
- **relay**: the 03 §5 contract (`RelayItemKind`, `RelayItemId`, `RelayItem`, `RelayReceipt`, `IInstallationRelay`), plus `RelayedItem`
  (producer = the friend authenticated by mutual TLS, never a claim), `RelayDelivery`, `IRelayUplink`, `RelayWire`, `RelayOptions`, and
  `InstallationRelay` on both ends:
  - producer: its own uplink when it reaches central; otherwise friends whose beacons say `ReachesCentral` (connected first, same site
    first), up to 3, until one reports `DeliveredToCentral` (a `HeldByFriend` or `Refused` answer from a friend is a claim: the next friend is
    still tried);
  - relaying friend: deduplicates by kind + id (remembers 4 096 outcomes), delivers within 5 s or holds the bytes (1 000 items / 32 MiB,
    24 h, memory only) and retries with backoff; it relays nothing without an uplink.
- **central side** (`.Central`): `CentralRelayIngress : IRelayIngress` — the producer must be an installation of the relaying installation's
  tenant (looked up in `IInstallationKeyDirectory`); deduplicates by tenant + kind + id, also concurrent deliveries (one consumer call);
  hands items to the owners' `IRelayItemConsumer` (it verifies the signature with every evidence key the producer ever had); a consumer
  exception is transient (`Unavailable`). `CentralBundleSource : IInstallationBundleSource` answers pulls (roster of the caller's scope,
  tenant revocations, optional other owners' bundles), audience-checked. Both registered by `AddYFexInstallationsCentral`
  (`Relay` options, `PullableBundles(...)`).
- **bundles**: `BundleGossipWire` (pull = versions held, an empty stream = "unlisted streams of this kind"; response = bundles; announce push),
  `BundleGossip` (channel handler + puller):
  - a verified beacon of a roster member advertising a newer roster or revocation list → pull from it (one per friend at a time,
    ≥ 1 s apart, 4 concurrent);
  - a newer version accepted here → announced to connected same-site friends, which pull it;
  - **audience check before offering**: a bundle is offered only when its audience includes the requester's authenticated id and scope;
  - received bundles go through `InstallationTrustSource.Apply` (roster, revocations) and `BundleStore` (floors; other gossiped kinds);
  - `Carry`: a broker keeps bundles addressed to **another member of its site** (central pushed CAIXA-02's snapshot) and offers them to that
    member only; same version with other content = `ConflictingSameVersion`.
- **RPC** (`.Rpc`): `IInstallationCentralLink` (`RelayAsync`, `PullBundlesAsync`, byte wire forms; proxy checked in, `ProxyGoldenTests` now a
  theory over both services), `InstallationCentralLinkService`, `RpcRelayUplink`, `RpcCentralBundleRefresh` (pull at start, after enrollment,
  at every reconnect and every 5 minutes), `AddYFexInstallationCentralLink()` (central), `peers.RelayThroughCentralRpc()` (installation).
  Plain proxy calls get no `CallContext` headers, so both client pieces attach the installation assertion themselves (`RpcOutboundCallSetup`,
  as `FusionMessageBus` does).
- **DI**: `InstallationPeersService` now also is `IInstallationRelay`, serves `time`, `relay` and `bundles` (an app's own handler for one of
  these names wins), builds a `BundleStore` when an `IBundleStorage` is registered (floors survive restarts; a newer stored roster/revocation
  list wins over the enrollment receipt's), `ApplyBundleAsync` (central's push/pull → trust + store + gossip). Builder: `Relay`/`Gossip`
  options, `RelayThrough`, `GossipBundles(kinds)`, `Publishers(...)`, `Services`.
- Smaller: `PeerChannelRegistry.Contains`, `InstallationFriends.BeaconObserved`, `BundleVerifier.IsAddressedTo` public.
- `.Testing`: the harness serves the mesh channels on every installation (a test's own handler for a name wins), `HarnessCentral` (real
  `CentralRelayIngress`, `RecordingRelayConsumer`, `IsUp`, `FailDeliveries`), per installation `HasInternet`, `Relay`, `Store`, `Gossip`,
  `Clock`; `RevokeAtCentral`, `PushAsync(bundle, recipients)`, `SignBundle`, public `SignRoster`.

**Acceptance:**

| 08 I4 item | Result |
|---|---|
| S-time: internet down → terminals anchored to the broker with its uncertainty | ✅ broker anchored to central (stratum 1) before the outage; CAIXA-01 synchronizes over the `time` channel: source `SiteAnchor`, no flags, uncertainty ≥ the broker's, usable for security, stratum 3 |
| broker off → peers fill in flagged `Unauthenticated`, `IsUsableForSecurity` false | ✅ the broker is partitioned from CAIXA-01; the site source has no answer; two anchored peers (one central-anchored, one NTP-anchored) → source `Peer`, `Unauthenticated`, not usable for security. An unanchored peer never anchors (separate test) |
| relay: CAIXA-01 without internet delivers an audit batch through CAIXA-02 … central sees it once | ✅ delivered through CAIXA-02 (producer = CAIXA-01 from mutual TLS, bytes unchanged); a retry is answered from the friend's memory; the same batch sent directly once CAIXA-01's internet is back is deduplicated by central (mutation check: without central's dedupe this fails) |
| … a friend that drops it causes a retry through another friend | ✅ CAIXA-02 answers "held" and throws the item away; when CAIXA-03 reaches central, the producer's next attempt is delivered through it; central has it once |
| gossip: revocation reaches every friend within one beacon interval + one request with central unreachable | ✅ only the broker receives the new list; 4 terminals have it (and drop the revoked spare PC) within 2 beacon intervals + 1 s (asserted; beacons every 1 s in the harness, typically ≈ 1 s); floors stored |
| a snapshot addressed to CAIXA-02 is never offered to CAIXA-01 | ✅ the broker carries it; CAIXA-01's pull and an explicit request for that stream return nothing (withheld counted); CAIXA-02 pulls it once (mutation check: without the audience check this fails) |

Also covered: friends' bundles are verified like central's (a revocation list signed by a foreign key and a conflicting same-version roster
are refused; nobody is revoked); a newer roster reaches a terminal through the broker's announcement; held items survive central failing and
are delivered once it recovers; central refuses an unknown kind, a bad item (consumer refuses), an unknown or other-tenant producer;
concurrent deliveries reach the consumer once; a failing consumer is transient. Over a real ActualLab RPC connection: a relayed item reaches
central once, named by the relayer's assertion (none → refused); an installation pulls only newer bundles; with DI,
`AddYFexTime(t => t.UseSiteAnchor().UsePeerTime())` anchors a terminal to a broker that runs `TrustDeviceClock()`.

**Tests:**

| Project | Before | After |
|---|---|---|
| Installations.Tests | 102 | 124 |
| Windows.Tests | 6 | 6 |
| Central.Tests | 19 | 22 |
| Rpc.Tests | 9 | 13 |
| YFex.Time.Tests | 62 | 65 |
| Cryptography ArchitectureTests | 2 | 2 (no allow-list entry) |

NativeAOT smoke (win-x64): 0 IL warnings, exit 0; it now also covers time from the broker into a `TimeSynchronizer`, a relay through a friend
into the real ingress, revocation gossip and the three wire forms. Solution build: 0 warnings.
Note: `dotnet test installations.slnx` runs the test assemblies in parallel and the TCP/TLS tests then flake (seen once at the baseline,
before any I4 change); run the projects one by one.

**Deviations (with reason):**
1. **Who serves time to whom.** `UsePeerTime()` skips brokers and central (they are the site and central anchors); a composition without
   `UseSiteAnchor()` gets no time from the broker through the peer slots.
2. **Two I3 tests changed expectation**: harness installations now serve `relay` (beacon offers = time, relay, bundles), and the "not served
   here" frame test uses `devices.serial` (shared by the roster, served by nobody) instead of `bundles`.
3. **Friend receipts are claims.** `HeldByFriend` and `Refused` from a friend do not stop the producer from trying the next friend; owners
   that need proof use central's own acknowledgement (Offline's signed receipts R1/R2, the audit chain).
4. **Held items live in memory** (the producer keeps its copy until central's ack — the custody rule); central's duplicate memory is per
   process and bounded (100 000 ids per tenant): consumers stay idempotent (Offline: `IdempotentExecution`).
5. **No push from central over RPC**: pulls at start, after enrollment, at every reconnect and every 5 minutes, plus the mesh gossip, reach
   every installation; `CentralInstallationBundlePublisher.Published` stays the hook for a later push.
6. **The relaying installation is identified by its RPC assertion**; the producer named in a relayed item is the friend that mutual TLS
   authenticated (central checks it is an installation of the same tenant; the owner verifies the signature).

**Not built (with the reason):**
- consumers — Logging `Shipping(s => s.ThroughFriends())`, the Audit relay and node-head signing, Offline Phase 4 broker services on peer
  channels — belong to their sessions; the contract they use is `IInstallationRelay` / `IRelayItemConsumer` / `GossipBundles(kind)` (03 §5);
- from the I3 list: several rosters for regional installations, network-fingerprint ordering (needs a delivered fingerprint key, like ⚑I24),
  persisting the last address;
- peer-raised `RollbackDetected` and "peers strictly after NTP" (YFex.Time sub-options of ⚑T13, 06 §2.3);
- Linux, macOS and Windows 10 runs (no machine or container).

**Open (for the user):** ⚑I24 (site-tag key), ⚑I25 (TPM transport keys of listening installations) and the leftover test CAs in
`CurrentUser\CA` are unchanged from I3.

**Next (I5):** SAS pairing + site issuing CA + provisional status, hardware-key policy, key-agreement key (after crypto), rotation drills,
chaos suite, firewall-rule presence in diagnostics.

### 2026-10-15 — Note from the Offline session (docs only, not an Installations phase)

The user confirmed Offline ⚑O2: the site coordinator is chosen **automatically by central while online** and saved in the roster
(`RosterEntryStatus.Active` for one `SiteBroker` installation, `Standby` for the other eligible PCs, which run a dormant broker); an admin
may pin one; nothing changes during an outage (offline 02 §4.1). Note added to 06 §4. Asks of this library, needed by Offline Phase 4:
terminals report friend reachability to central; firewall-rule presence in diagnostics (already an I5 item); ⚑I25 solved for any
installation that may become `Active` (or software keys allowed for that role). No code changed.
