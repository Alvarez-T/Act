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
| `IAnnouncementSigner/Verifier`, beacons, `InMemoryNetwork`, `Hysteresis`, `NetworkFingerprint` | YFex.Networking Phases 2/5 | proposal (agreed in draft) | I3 (beacons; manifest addresses work without it) |
| core `YFex.Time` types, `TimeBuilder` | YFex.Time T0–T2 | ✅ built | I4 |
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

### Phase I2 — Central and enrollment

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

### Phase I3 — Friends and the peer endpoint

- `PeerEndpoint` (SslStream, ALPN, frames, channel registry, rate limits), `IInstallationFriends`, `FriendSet`, discovery order
  (last address → roster addresses → verified beacons → typed), `InstallationAnnouncementSigner/Verifier` on YFex.Networking,
  TLS helpers (`CreateServerOptions/CreateClientOptions`), `InstallationHarness` on `InMemoryNetwork`.
- **Acceptance:** 10 simulated installations on one LAN find each other; the "customer laptop" (no certificate) never appears in
  `FriendSet`; forged/replayed beacons rejected and counted; ARP-spoof simulation (candidate address points to a rogue host) →
  refused handshake; Loja Norte terminal on Loja Centro's LAN gets only `Tenant` channels; mTLS TLS 1.3 with TPM keys (Windows
  test); benchmark: handshake + first request ≤ 50 ms with software keys on the dev PC.

### Phase I4 — Mesh channels (only after ⚑O25–O30, ⚑T13)

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
