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
| CNG-held `IRecipientKey` (key-agreement key) | YFex.Cryptography (crypto 13 §6) | **done 2026-10-27** (crypto 14 §10: `IRecipientKeyStore`, `StoreRecipientKeysInCng()` / `StoreRecipientKeysInFiles(…)`, key `"installation/key-agreement"`) | I5 |
| `FileSigningKeyStore` (Linux/macOS v1) and an Apple Keychain/Secure Enclave store (09 §5) | YFex.Cryptography | `FileSigningKeyStore` **built 2026-10-18** (crypto 14 §2); `YFex.Cryptography.Apple` and the Linux TPM store **written, not run** on their OS (crypto 14 §8, opt-in) | I1 (Linux/macOS) |
| `YFex.Cryptography.Android` (⚑K19): Keystore `ISigningKeyStore` + attestation, Keystore root key, `AndroidKeyPaths` | YFex.Cryptography | **built 2026-10-27** (crypto 14 §9): 11/11 on the Android 15/16 emulators, trim 0 warnings; **real handheld pending** (StrongBox/TEE timing, StrongBox with `DIGEST_NONE`, Google attestation root) | I6 |
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
  later — **⚠ OPEN 2026-11-10: only `InMemoryInstallationRegistry` exists; a durable `IInstallationRegistry` over YFex.Data is still to
  build**, see §5 "2026-11-10"), enrollment/renewal endpoints, revocation, roster publication, `InstallationPermissions`, hint key derivation, `IEnrollmentPolicy` seam (⚑I23:
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

### Phase I5 — Pairing, hardening, key agreement — BUILT 2026-10-27 (pairing, key agreement, admin approval, rotation drills, chaos suite; open items in §5)

- SAS pairing + site issuing CA + provisional status; hardware-key policy (`HardwareKeyEnrollmentPolicy`, built 2026-10-19); key-agreement
  key `installation/key-agreement` over `IRecipientKeyStore` (crypto built it 2026-10-27, 04 §1: `CngRecipientKeyStore` on Windows, files
  elsewhere), its public key in the enrollment request and the registry; rotation drills (issuing
  CA yearly, root overlap); chaos suite (partitions, restarts, disk-image restore) in the harness; firewall-rule presence reported by `IInstallationFriends`/diagnostics (creating it = future YFex.Installer) — **✅ built early, 2026-10-19** (diagnostics reports + Windows rule check, §5).
- **Acceptance:** offline S7 (spare PC paired with SAS during an outage, confirmed later) passes; a MITM during pairing produces
  different digits; provisional installation cannot issue approvals; all scenario tests green; AOT 0 warnings.

### Phase I6 — Mobile handhelds (after I5 and `YFex.Cryptography.Android`; design [11](11-mobile-handhelds.md), ⚑I26–I32)

- ~~Spike first~~: 09 §4 R7 is **answered on emulators** (2026-10-27, crypto 14 §9.5): a Keystore key is a TLS 1.3 client credential,
  so both handheld keys stay in the Keystore. Still to run on **one real handheld** (`androidKit test … --serial`): StrongBox/TEE
  signature cost, StrongBox with `DIGEST_NONE` (a refusal other than `StrongBoxUnavailableException` needs a crypto fix), Google attestation root.
- **I6a — not blocked by I5** (crypto Android is built) — **BUILT 2026-10-27** (§5 entry "I6a"; real handheld still pending):
  - `YFex.Installations.Android` (`net11.0-android`, own `installations.android.slnx` so the main solution builds without the Android
    workload; device-test project run with androidKit): `UseAndroidKeyStore()` (both keys in the Keystore, never a software transport key:
    handhelds never listen), the state directory under `NoBackupFilesDir` (`InstallationStorageOptions` has no Android default and
    throws `PlatformNotSupportedException`), an Android hint source, the key-agreement key in `StoreRecipientKeysInFiles` under the same folder.
  - **Attestation at enrollment:** `IInstallationKeyStore` passes central's challenge when a key is created (the Keystore attests only at
    creation; `EnrollmentClient` already creates the keys after the challenge, but keys left by a failed earlier attempt keep their old
    attestation); the enrollment request carries the chains (wire version bump); central records them and `EnrollmentCandidate` exposes them
    to `IEnrollmentPolicy`.
  - **Lost Keystore keys:** `AndroidKeystoreKeyLostException` (uninstall/clear data/restore onto another device) must become an
    `InstallationStateUnreadable` reason (today it escapes from the ring load `_rings.GetAsync` in `InstallationStateStore` as an exception;
    `InstallationStateFailure` has only `InvalidLength`/`CannotOpen`/`InvalidContent`), so the app wipes and shows "scan a new QR".
  - `EnrollmentLink` v1 codec + golden vectors; sponsor + pending + SAS confirmation at central (online path).
- **I6b — after I5:** the rest below (broker path, presence, revocation). **Step 1 built 2026-10-28** (§5 entry "I6b step 1"): revocation from a site
  terminal through central and self-revocation; the offline site revocation (⚑I42) built 2026-10-29. **Step 2 built 2026-10-29** (§5 entry
  "I6b step 2"): presence (`SitePresence`, channel `installations.presence`, `SponsoredBy` / `PresenceWitness` in the roster, presence on the
  assertion). **Step 3 built 2026-10-31** (§5 entry "I6b step 3"): the broker path and the sponsor push. Left: the real-handheld run.
- `YFex.Installations.Android` (`UseAndroidKeyStore()`, state directory, hint); `EnrollmentLink` v1 codec + golden vectors; sponsor and
  pending/confirm at central (`EnrollmentCodeRequest` sponsor + confirmation, push to the sponsor) and at the broker (reuses I5 SAS
  and provisional certificates); `SponsoredBy` and `PresenceWitness` in the roster; `SitePresence` + channel `installations.presence`;
  presence in the installation assertion (`InstallationCallContext.Current.Presence`); revocation from any trusted terminal and
  self-revocation.
- **Acceptance (harness + one real Android device):** QR → pending → SAS confirm → enrolled; the same QR offline → provisional via
  the broker → confirmed by central later; a second device using the same QR is refused or rejected; a QR with an unknown central host,
  a wrong root hash or a wrong broker key is refused; presence from the broker and from the sponsor verifies at central, and a
  statement for another handheld, another site, an old challenge or a non-witness is refused; revocation from a site terminal while
  offline takes effect on the site at once; the trim/AOT gate of the Android build at 0 warnings.

## 4. Docs to update when phases land

`YFex/CLAUDE.md` (solution map row for core `YFex.Installations` + repo, common-task row "installation identity / friends"),
`docs/index.md` row status, offline 02/03/04/05/11 (pointer notes now, details when Offline Phase 1 starts), crypto 12 §5 handoff
(addressee), time README §8 (T13, `UseSiteAnchor` home), logging 02/05 (`InstanceId` = installation id, relay), audit 05/06 (node id,
evidence key, relay), privacy 04 §5/§7 and 05 §5.1 (installation id + evidence key), authorization-consolidation 06/09 (4b directory),
Devices plan §12 + `YFex.Devices/docs/04-serial.md:102`.

## 5. Implementation log

### 2026-11-10 — Open: durable installation registry (noted by the Licensing session)

`IInstallationRegistry` has one implementation, `InMemoryInstallationRegistry`. Tests and the harness are fine with it, but a real
central loses enrollments, codes, revocations and the event cursor on restart. Needed before any production central and by
YFex.Licensing LC-P3/P4 (seat counts and the activation outbox read the registry and its events). What the durable registry must keep
from the in-memory one: `IEnrollmentPolicy` called inside the enrollment transaction under a per-tenant lock (in SQL: a transaction with
a per-tenant advisory lock or a row lock on a tenant row, so two central nodes cannot both take the last seat); atomic revocation of
`Replaces`; contiguous event sequence numbers (a cursor never skips one); `CountAsync` by tenant, role and scope prefix. Not started;
owner: the Installations session.

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

### 2026-10-19 — HTTP form of installation assertions (from authentication AN-P4, additive)

Authentication binds refresh tokens to an installation (⚑AN16) on its HTTP endpoints too, and asked for an HTTP header form of the
assertion (⚑I11). It did not exist (RPC only), so it was added:
- core `InstallationAssertionHeaders` (`yfex-installation: YFex-Installation {base64}`, shared with RPC; Installations.Rpc's constants and
  decoder now point at it), `HttpInstallationAssertionChallengeSource` (`POST {central}/installations/assertion/challenge`) and
  `InstallationAssertionHandler` (a `DelegatingHandler` adding the header; a 401 with `WWW-Authenticate: YFex-Installation` drops the
  cached assertion);
- Central.AspNetCore `AddYFexInstallationAssertionHttp()`, `UseYFexInstallationAssertion()` (verifies the header; no header = unproven;
  a bad header = 401 with `error="…"`, never treated as "no installation"; sets the `InstallationAssertionAccepted` and core
  `InstallationIdentity` features, `HttpContext.GetInstallation()`) and `MapYFexInstallationAssertionChallenge()`.
Test `AssertionHttpTests` (enrolled machine proves itself, forged header refused, revoked installation refused). Installations 170
tests green, AOT smoke passed. Scope pinning over HTTP (the RPC `InstallationScopeMiddleware`) is not mirrored: HTTP endpoints that need
it read `GetInstallation()`.

### 2026-10-19 — Offline Phase 4 asks: ⚑I25, diagnostics reports, firewall rule (Claude, task from the offline planning session; decisions by the user)

**Decisions (user, 2026-10-19):** ⚑I25 = a **software transport key** for installations that listen, evidence key in hardware; the
firewall-rule presence (an I5 item) = **measure + the named rule**; diagnostics = **signed relay item**.

**Built (additive):**

| Item | Where |
|---|---|
| `InstallationKeyStore(store, transportStore)`: new transport keys in the transport store; a transport key that already exists in the evidence store is used until a renewal moves the certificate to the next slot; `TransportStore`; `TransportKeyPlacement { Automatic, WithEvidenceKey, Software }` | `Keys/InstallationKeyStore.cs` |
| `WindowsInstallationKeys.Create(…, softwareTransport)`, `UseWindowsKeyStore(preferHardware, transport)` (Automatic = software for `SiteBroker`) | `YFex.Installations.Windows` |
| `HardwareKeyRequirement { BothKeys, EvidenceKey }`, `HardwareKeyEnrollmentPolicy(requirement, roles)`, `RequireHardwareEvidenceKeys(roles)` | `YFex.Installations.Central` |
| `FriendObservation`, `InstallationFriends.TakeObservations()` (dials, dial failures, inbound per friend since the last call), `PeerStatistics.InboundAccepted` | `Peers/` |
| `InstallationDiagnosticsContent`/`Report`/`Codec` (strict, relay item `installations.diagnostics`, domain `yfex.installation.diagnostics.v1`), `InstallationDiagnosticsReporter`, `peers.ReportDiagnostics(interval)` (off unless called; default every 15 min), `IFirewallRuleProbe`, `FirewallRuleState`, `FirewallRules.PeerRuleName` = "YFex Installations peer" | `Diagnostics/` |
| `WindowsFirewallRuleProbe` (`HNetCfg.FwPolicy2` through `[GeneratedComInterface]`, read-only, no elevation; Present / Missing / Disabled / FirewallOff / Unknown), `peers.CheckWindowsFirewall()` | `YFex.Installations.Windows` |
| Central: `InstallationDiagnosticsConsumer` (evidence key as of the report time, refused after a revocation), `IInstallationDiagnosticsStore` + in-memory (30 days), `SummarizeAsync(candidate, since)` → `ReachabilitySummary` (reporters, dials, failures, inbound, latest report), `CollectDiagnostics()` | `YFex.Installations.Central/InstallationDiagnosticsCollection.cs` |

**Verified on the dev PC (Windows 11, TPM 2.0):** a broker with its evidence key in the TPM (`Hardware`) and a software KSP transport key
(`OperatingSystem`) serves mutual TLS 1.3 to a terminal whose keys are in the TPM; the firewall probe answers `Missing` for an unknown rule
without elevation, also under NativeAOT.

**Tests:** Installations.Tests 125 → **127**, Central.Tests 26 → **28**, Rpc.Tests 13, Windows.Tests 6 → **9**; AOT smoke **0** IL warnings,
exit 0 (diagnostics, separate transport store, firewall probe). `MeshAcceptanceTests.Bundles_from_a_friend_are_verified_like_bundles_from_central`
is timing-sensitive (pulls before the friend is reachable; failed twice, then passed 3/3, and passes on the unchanged code): follow-up.

**Open:** the machine-scope TPM measurement stays optional; Linux/macOS firewall probes report `Unknown` (the measured inbound connections
decide); a durable diagnostics store and the ranking policy are Offline Phase 4; Offline's terminals and brokers turn `ReportDiagnostics()` on
in Phase 4.

### 2026-10-27 — Docs synced with the crypto deliveries (docs only, no code)

YFex.Cryptography built two things this library waited for (crypto 12 §5, 14 §9–§10):
- **Key-agreement key store** `IRecipientKeyStore`: `CngRecipientKeyStore` (`.Windows`, TPM first, measured ≈ 47 ms per agreement) and
  `SealedRecipientKeyStore` (files, every OS). **The I5 key-agreement item is no longer blocked.**
- **`YFex.Cryptography.Android`**: R7 answered on emulators (Keystore key = TLS 1.3 client credential), so handhelds keep both keys in the
  Keystore. The real-handheld run is still pending.

Updated: README status + ⚑I4; 04 §1 (Android row, key-agreement stores, the closed Linux/macOS gap); 08 §2 (dependency rows), §3 I5 and
I6 (I6 split into **I6a**, not blocked: Android package, attestation at enrollment, lost-key handling, online QR path; and **I6b**, after
I5); 09 §2 (state under `NoBackupFilesDir`, not `FilesDir`; key-agreement row; key-store gap closed), §5 status; 11 status, §1, §6, §9.
Found while checking the code for I6a: `InstallationStorageOptions` has no Android default; `IInstallationKeyStore` and the enrollment
request have no attestation challenge or chain; `AndroidKeystoreKeyLostException` would escape from `InstallationStateStore`'s ring load.
Backup: `C:\Temp\installations-backup\docs-sync-2026-10-27\`.

**Next:** I5 (SAS pairing, site issuing CA, provisional certificates + central confirmation, key-agreement key, rotation drills, chaos
suite); I6a can run alongside it; one real-handheld run of the crypto device tests settles StrongBox.

### 2026-10-27 — I5: offline pairing (SAS), site issuing CA, central confirmation, key-agreement key (Claude)

User: "Start Phase I5: SAS pairing, site issuing CA, provisional certificates with central confirmation, and the key-agreement key."
Backup: `C:\Temp\installations-backup\I5\`. The repo's earlier uncommitted work was kept; nothing committed.

**Built (main package `Pairing/`, `Keys/`; Central; Central.AspNetCore; Windows; Testing):**

| Item | Where |
|---|---|
| Key-agreement key: `InstallationKeyStore(…, keyAgreementStore)`, `IInstallationKeyStore.GetKeyAgreementKeyAsync`, `InstallationSealing.Seal/OpenAsync` (installation id + purpose bound) | `Keys/` |
| Enrollment/renewal payload tag 2 with the key-agreement public key (tag 1 bytes unchanged); registry `KeyAgreementKey`; a renewal adds it for older installations | `Enrollment/`, Central |
| `WindowsInstallationKeys.Create(…, keyAgreement: true)` → `CngRecipientKeyStore` (TPM first) | `.Windows` |
| Pairing exchange `PairingProtocol` (Hello → Commit → Nonce → Reveal → Proof → Result; SAS = 6 digits of the transcript, which includes the broker TLS certificate), `PairingClient`, `PairingValidation` | `Pairing/` |
| Broker: `InstallationPairingHost` + `IPairingApprover` (the app's screen; `installations.pair` is the app's check), on the peer port selected by SNI `pairing.yfex.invalid` / ALPN `yfex-pairing/1` (`InstallationFriends.AcceptPairing`); `peers.AcceptPairing(sp => approver)` in DI | `Pairing/`, `Peers/`, DI |
| Site issuing CA: key `installation/site-authority`; central `IssueSiteAuthorityAsync` (`POST …/site-ca`, only while `RosterSettings.PairingEnabled`), 30 days, path length 0, broker URNs; registry keeps them forever, rosters list each broker's two newest valid ones; the broker renews at ⅓ life left (`EnsureSiteAuthorityAsync`, every 6 h in DI) | Central, `Pairing/` |
| Provisional state: `InstallationProvisioning.Pairing` (`SignedPairingReceipt`), provisioning response case 4, state format 3; `LocalInstallation` publishes `InstallationProvisional`; the peers service starts friends for it | `Enrollment/`, state |
| Confirmation: `EnrollmentClient.RenewAsync` of a provisional installation → `POST …/confirm` (receipt + proof by both keys) → central checks broker signature, site CA held at pairing time, 30-day window, policies → normal certificate for the same transport key (`PairedBy`), or the installation is registered **revoked** (`PolicyRefused`, then `PairingRejected` on retry) | Central, `Enrollment/` |
| New refusals `PairingDisabled`, `PairingRejected`; domains `site-authority`, `pairing`, `pairing-receipt`, `pairing-confirmation`, `sealed` | contracts |

**Deviations from 04 §5 (recorded there):**
1. The SAS sketch had no commitment. Without one, a man in the middle can search about 2^20 nonces for matching digits. The broker
   now commits to its nonce before it sees the spare's. The broker certificate is also in the transcript, so a relay fails at the
   proof.
2. Pairing uses the same port, selected by SNI, so no second firewall rule is needed.
3. Central confirms automatically through the enrollment policies, or with an administrator's approval: both, per scope (user,
   2026-10-27; see the entry below).

**Acceptance:**
- **S7 (spare PC paired during an outage, confirmed later)** passes:
  - the same code shows on both screens;
  - the spare is a provisional `SameSite` friend;
  - central does not know the installation before confirmation;
  - a lost answer is retried idempotently;
  - the confirmed certificate comes from the installation issuing CA.
- **MITM.** A relay that presents its own certificate claiming the broker's id is refused with `InvalidProof`, and the approver is
  never called.
- **Provisional installations cannot issue approvals at central:** they have no key there until confirmed.

**Tests:**
- Installations.Tests 127 (unchanged).
- Central.Tests 28 → **38** (`PairingTests`: S7, MITM relay, pairing disabled, rejected by the person, policy rejection revokes on the
  site, revoked broker, forged receipt, codec round trips, sealing, renewal adds the key-agreement key).
- Rpc.Tests 13, Windows.Tests 9: green.
- NativeAOT smoke: published with 0 IL warnings, exit 0. The new I5 segment runs over real TCP/TLS: site CA from central, pairing,
  confirmation through the wire codecs, sealing.
- `PeerAcceptanceTests.Forged_and_replayed_beacons_are_rejected_and_counted` is timing-flaky **before** I5 too: it failed in 2 of
  3 baseline runs.

**Not built (I5 leftovers):**
- rotation drills (issuing CA yearly, root overlap);
- the chaos suite (partitions, restarts, disk-image restore);
- site revocation signed by the broker (04 §4);
- forwarding the receipt to central through the relay (`InstallationPairingHost.Paired` is the hook);
- an HTTP end-to-end test of `site-ca` / `confirm` (the codecs are exercised by the AOT smoke's wire transport);
- Linux and macOS runs.

### 2026-10-27 — I5: admin approval of offline pairings (user: "both")

The user wanted **both** confirmation modes.
- `CentralInstallationOptions.PairingConfirmationFor(scope)`: `Automatic` (the default) or `AdminApproval`.
- **With approval:** the confirmation request is kept pending (new refusal `PairingPending` = 19; the provisional installation keeps
  working and its renewal loop retries). An administrator then calls one of these (the host checks `installations.enroll`):
  - `CentralEnrollmentService.ApprovePairingAsync(id, decidedBy)`: the next confirmation goes through the policies and is enrolled.
  - `RejectPairingAsync(id, decidedBy)`: the installation is registered **revoked** at once and the revocation list is published, so
    the site stops accepting it.
- `ListPendingPairingsAsync(scopePrefix)` lists what waits.
- Registry: `PendingPairingRecord`, `PairingDecision`, `ListPendingPairingsAsync`, `DecidePairingAsync`, and
  `ConfirmPairingAsync(…, requireApproval)`.

**Tests:**
- Central.Tests 38 → **40**, three runs green: waits → approved → confirmed; rejected → revoked on the site.
- Rpc 13 and Windows 9 green.
- The AOT smoke was not re-run: the change is on central's admin path only, and a parallel session ("I5 rotation drills and chaos
  suite") owns the smoke right now.

### 2026-10-27 — I5: rotation drills and chaos suite (Claude)

Task from the I5 session, run alongside the admin-approval work above (that session's files were not touched; nothing committed).
Backup: `C:\Temp\installations-backup\I5-drills\`.

**Built:**

| Item | Where |
|---|---|
| Roster `TenantRootCertificates`: during a root rotation, the other root and the cross-certificates. Written as roster **format 2** only when present, so format 1 bytes and the goldens are unchanged. | `Roster/InstallationRoster.cs` |
| `TenantRootOverlap` (internal): a listed root counts only with a cross-certificate for its subject and key, issued **directly by a pinned root**. It counts while that certificate and its issuer are valid; a cross-certificate revoked by serial ends it. `InstallationTrust.InRootOverlap(now)`. | `Trust/` |
| Renewal re-pin: `ProvisioningValidation` accepts another `TenantRoot` only when the answer's verified roster carries a cross-certificate from the pinned root, valid now. | `Enrollment/ProvisioningValidation.cs` |
| `InstallationAuthority(…, retiringIssuing, tenantRootOverlap)`, `IssuingCertificatesAt(now)`, `TenantRootOverlap`. Rosters list the current CA, the retiring CAs until they expire, and the overlap. `CentralInstallationBundlePublisher` issues a new roster version by itself when the roster's CAs differ from the authority's. | Central |
| `InstallationTrustSource.RestoreAsync(provisioning, store, …)` and `ApplyStoredAsync(store)`: one restart path for DI and the harness. | `Peers/`, DI |
| Duplicate identity: `InstallationFriends.DuplicateIdentitySuspected` (the sender's address), `PeerStatistics.DuplicateIdentityBeacons` and `BeaconAddressConflicts`. | `Peers/` |
| `TestTenantAuthority`: `RotateIssuingAuthority()`, `RotateRoot(crossValidity)`, `EndRootOverlap()`, `RetiringIssuingAuthorities`, `IssuingAuthoritiesForRoster`, `TenantRootOverlap`, `PreviousRoot`. Also `TestInstallation.Root` (the pinned anchor), and `TestCentral` passes the rotation state. | `.Testing` |
| Harness chaos support: `HarnessDisk` (sealed state through the real `InstallationStateStore`, `HarnessBundleStorage`, `Image()` / `Restore()`), `HarnessInstallation.StopAsync` / `RestartAsync` / `IsRunning` / `Disk` / `ProvisioningFor`, `InstallationHarness.AddCopy` (a disk image booted on a second PC), `HarnessSite.IssuingAuthorities`. Every harness installation now starts from its sealed state file. | `.Testing` |

**Defects the drills exposed (fixed):**
1. **Rosters listed only the current issuing CA.** Installations enrolled after a rotation pin only the new CA, so they refused every
   installation still certified by the old one. Rosters now list retiring CAs until they expire.
2. **Central kept serving the old roster after a rotation**, because the registry saw no change. The publisher now re-issues when the
   roster's CAs (or a retiring CA that expired) differ from the authority's.
3. **A root rotation was impossible.** Renewal compared the root byte for byte, and rosters had no way to carry a second anchor. Now:
   endorsed re-pin at renewal, plus the overlap material in the roster.
4. **`InstallationTrustSource.Apply` re-verified the other current bundle.** Once the revocation list expired, no newer roster could be
   applied, and the reverse. After an outage of more than 30 days both expire, and nothing could ever be applied again. Only the
   incoming bundle is verified now.
5. **A restart started from the enrollment receipt.** `FromProvisioning` threw when the receipt's bundles had expired since the last
   renewal, even with newer valid bundles in the store, so the DI peers service failed to start. `RestoreAsync` starts from the newest
   ones.
6. **`BundleStore.GetLatestAsync` ignored the floor.** An older bundle copy put back came back `Valid`: gossip offered it to friends, and
   other kinds (licenses, snapshots) used it. It now answers `OlderThanFloor` (or `ConflictingSameVersion`).
7. **Re-delivering the floor's own version did not repair a restored older copy** in storage. It is now rewritten.
8. **Bundles applied straight to the trust got no floor.** This covers central's site-CA answer in `EnsureSiteAuthorityAsync`, a host's
   `ApplyBundle`, and harness pushes; a restart then fell back below them. `BundleGossip` now floors every trust change.
9. **A cloned disk image was invisible.** A beacon carrying the installation's own id was dropped without a trace. It is now counted and
   raised when this process did not write it (the issue times of its last 32 beacons are kept).
10. Harness: a copy signed its goodbye beacons with keys that were already disposed (teardown order).

**Acceptance:**

| I5 item | Result |
|---|---|
| Issuing CA, yearly | Real central, enrollment at day 290 of the old CA. After the rotation, the roster is re-issued listing both CAs. A new enrollment and a forced renewal get the new CA. Installations that pinned either CA accept each other. After the old CA expires, central drops it by itself; on the mesh, a roster that forgets the retiring CA strands its installations (`Untrusted`) |
| Root overlap | A renewal re-pins the new root (stored receipt). Installations pinned to either root accept each other over real TLS on the LAN, both directions. When the overlap ends, a new root without endorsement is `Untrusted`. A changed root without a cross-certificate is refused at renewal (`InvalidProvisioning`, nothing stored). These cannot add an anchor: the bundle key alone, another tenant's CA, or this tenant's own issuing CA. The overlap ends when the cross-certificate expires or is revoked by serial |
| Site issuing CA of a broker | At day 20.5 the broker renews (less than ⅓ of its life left), and the roster lists its two newest CAs. A provisional certificate of the older CA keeps working (provisional, on the broker and on a terminal) and ends with its own validity plus grace. At day ~41, the third CA brings the roster to the second and third CAs, and the second CA's provisional certificates still work |
| Partitions and heals | A terminal cut off from everyone misses a revocation the broker spread. After the heal, its friends' beacons bring the new list; the revoked spare is dropped and the floor is raised. A 30 % lossy, slow link still converges, and requests get through |
| Restarts | The identity and receipt come from the sealed state file, and the store's newer roster wins. The floor refuses v2 and the receipt's v1 again. The installation reaches the broker and the broker reaches it, with no false duplicate-identity alarm. A broker restarted during a partition comes back at its stored version and catches up after the heal |
| Disk-image restore | **Older state file:** the store's floor and list win, and the revoked spare stays refused while partitioned. **Older bundle copy:** flagged `OlderThanFloor`; the trust uses the newest copy left (v2, newer than the receipt's), the floor stays at v3, nothing below it is accepted, and after the heal the friends' v3 repairs the copy. **Whole image** (state and floors): nothing on the machine can tell. Its friends refuse its older versions, and the first friend it reaches gives it the newer list |
| Duplicate identity | A copy booted on a second PC (same id, same software keys) is accepted by trust, as expected. **Both copies** raise `DuplicateIdentitySuspected` with the other's address, and the broker counts `BeaconAddressConflicts` |
| Long outages | A fresh roster applies while the current revocation list is expired, and the reverse. A restart after the receipt's bundles expired starts from the store's newer ones |

Mutation checks: with gossip flooring, the floor-copy repair and the automatic roster re-issue disabled, 6 drills fail (both central
rotation drills and four chaos tests).

**Tests:**

| Project | Before | After |
|---|---|---|
| Installations.Tests | 127 | **141** (`RotationDrillTests` 4, `ChaosTests` 10) |
| Central.Tests | 40 | **44** (`RotationDrillTests` 4) |
| Rpc.Tests | 13 | 13 |
| Windows.Tests | 9 | 9 |

All four projects green in **three consecutive full runs** (projects one by one; the known flaky
`PeerAcceptanceTests.Forged_and_replayed_beacons_are_rejected_and_counted` passed all three times). NativeAOT smoke (not edited, but its
libraries changed): old exe deleted, published with 0 IL warnings (publish exit 0), run exit 0.

**Deviations and decisions (with reason):**
1. **The root overlap travels in the roster, but only the pinned root's key can add an anchor** (through a cross-certificate). A bundle
   key or an online issuing CA that leaks cannot add roots. The enrollment contracts are unchanged (no second anchor in provisioning).
2. **Cross-certificates are never in a chain.** Their path length is 1, because `CertificateAuthority` requires a CA below a root with path
   length 2 to have less. They only endorse the other self-signed root, which becomes a second anchor.
3. **One overlap window per trust:** the intersection of the endorsements (a rotation adds one root at a time).
4. **After a bundle-copy restore, the trust uses the newest copy left,** even below its floor (it is still newer than the receipt's). The
   floor itself never moves down.
5. **A duplicate identity is detected, not acted on.** Central revokes the id and the machines are re-enrolled. The diagnostics report
   does not carry it yet.
6. **Deployed installations must run this code before a root rotation:** older code refuses roster format 2.

**Open (for the user):**
- **Everything expired at restart.** → **decided 2026-10-28 (⚑I38): stale mode, bundles only; built.** If both the receipt and the store are older than their 30-day validity (no central or friend for a
  month), `RestoreAsync` throws and peers do not start, so the installation cannot even receive gossip. Choose: start with expired
  bundles flagged stale, or wait for central.
- **Grace for provisional certificates.** → **decided 2026-10-28 (⚑I39): configurable, built.** Provisional certificates get the 7-day LAN grace like normal ones (72 h + 7 days). Is that
  intended?
- **Duplicate identity at central.** → **decided 2026-10-28 (⚑I40): yes; built.** Add it to the diagnostics report (codec change) so central sees it.
- **No scheduled rotation (user asked whether the periods are configurable: yes).** → **decided 2026-10-28 (⚑I37): option + hosted service + optional Jobs package; not built.** A rotation is an operator action: build the authority
  with the new CA plus `retiringIssuing`, or with the new root plus `tenantRootOverlap`. "Yearly" and "10 years" are the certificates'
  lifetimes chosen when they are created, and the overlap lasts as long as the retiring CA or the cross-certificates. A scheduled
  issuing-CA rotation at central (an option such as `IssuingAuthorityLifetime` plus a job that mints the next CA before the end) is not
  built. Already options: `CertificateLifetime` (90 d), `SiteAuthorityLifetime` (30 d), `PairingOptions.SiteAuthorityRenewBefore` (⅓).
- Nothing in the admin-approval session's files was needed for the drills.

### 2026-10-27 — I6a: Android package, attestation at enrollment, lost Keystore keys, QR link with sponsor confirmation (Claude)

Task from the I5 session (I6a only; I6b — broker path, presence, revocation — later). Backup: `C:\Temp\installations-backup\I6a\`.
Nothing committed; the I5 work in the folder was kept. No new NuGet package.

**Built:**

| Item | Where |
|---|---|
| `YFex.Installations.Android` (`net11.0-android`, API 26+, trimmable), own `installations.android.slnx`: `AndroidInstallationKeyStore` (both keys in the Keystore through `AndroidKeystoreSigningKeyStore`, alias prefix `yfex.installations.{app}.`, never a software transport key), `AndroidInstallationKeys.Create / StateDirectory / IsKeyLost / WipeAsync`, `AndroidHardwareHintSource` (`ANDROID_ID`), `UseAndroidKeyStore()` | `src/YFex.Installations.Android/` |
| State under **`NoBackupFilesDir/yfex/installations/{app}`**. The portable default still refuses Android (`PlatformNotSupportedException`, now naming the Android package), because .NET maps its folders to `FilesDir`, which Auto Backup copies. New builder hooks for platform packages: `PlatformStorage(…)` (default directory; `Storage(…, path)` still wins) and `DetectLostKeys(…)` | main package, DI |
| **Attestation at key creation:** `IInstallationKeyStore.GetAttestedTransportKeyAsync / GetAttestedEvidenceKeyAsync / ReadAttestationChain` (default methods: no attestation). `EnrollmentClient` derives the challenge `EnrollmentProtocol.AttestationChallenge(token)` = SHA-256(label ‖ central's challenge token), 32 bytes (the Keystore limit is 128) | `Keys/`, `Enrollment/` |
| **Wire:** enrollment payload **tag 3** = the v1 fields · [key-agreement key] · two attestation chains (≤ 8 × 8 KiB) · [confirmation commitment] · nonce. Only written when it carries attestation or a commitment: tag 1 / 2 bytes and goldens unchanged. Response **case 6** = `EnrollmentPending(IssuerNonce, ExpiresAt)` (new case of the `ProvisioningResponse` union) | `EnrollmentProtocol` |
| Central records `KeyAttestations` + the expected challenge on `InstallationRegistryRecord` (`Attestation`, `AttestationChallenge`, `SponsoredBy`) and passes them to `IEnrollmentPolicy` (`EnrollmentCandidate.Attestation / AttestationChallenge / Sponsor`). Record only: no Google-root check | Central |
| **Lost Keystore keys:** `InstallationStateFailure.KeysLost = 4`. `InstallationStateStore(options, rings, isKeyLost)` maps a ring-load failure the classifier recognizes (Android: `AndroidKeystoreKeyLostException` anywhere in the inner chain) to `InstallationStateUnreadable(KeysLost)` in `InitializeAsync` / `LoadAsync` (and the reload path); without a classifier it still throws. `InstallationStateStore.DeleteAsync()`; `AndroidInstallationKeys.WipeAsync(app, keyDirectories)` deletes the Keystore keys, the state folder, the key-agreement key and the app's ring folders (restart the app after it) | state store, Android |
| **`EnrollmentLink` v1** codec: tag 1 · root hash (16 B) · expires · [central URL · code] · [broker id · transport key id · 1–3 endpoints · pairing code] · sponsor id · evidence key id · [endpoint]; text `YFEX:` + RFC 4648 base32 (QR alphanumeric mode). Canonical decode, codes must have 4 groups, ≤ 512 bytes. A full link is 198 bytes / 322 characters; an online-only link 112 bytes / 172 characters | `Enrollment/EnrollmentLink.cs` |
| **Online sponsor path:** `EnrollmentCodeRequest(…, Sponsor, Confirm)`; a sponsored code has 4 groups, lives 5 min (max 5 min); the sponsor must be an unrevoked installation of the tenant allowed by `CentralInstallationOptions.MaySponsor` (default: **same scope only**); `MaximumPendingPerSponsor` = 3 (checked at code creation → `TooManyPending`). `EnrollmentClient.EnrollAsync(link, name, showCode)` refuses a central not in `EnrollmentClientOptions.LinkCentrals` (`UntrustedLink`, nothing sent; DI `EnrollAt` adds its address, `AcceptLinksFrom(uri)` adds more), refuses an expired link, and refuses an answer whose root does not match the link's hash (`InvalidProvisioning`, nothing stored) | client, Central |
| **Pending + SAS:** the registry holds the first request (`PendingEnrollmentRecord`, `RegistryEnrollmentPending`), another device gets `CodeUsed`, policies run at hold time and again at confirmation. `CentralEnrollmentService.EnrollmentAwaitingConfirmation` (event, the push hook), `ListPendingEnrollmentsAsync(sponsor)`, `ConfirmEnrollmentAsync(sponsor, installation, shownCode, by)` (issues the certificate at once; the device's next poll gets it through the existing idempotent-retry path; the roster is re-published), `RejectEnrollmentAsync`, `CancelCodeAsync`. New refusals 20–24: `SponsorUnavailable`, `ConfirmationRequired`, `EnrollmentRejected`, `UntrustedLink`, `TooManyPending` | Central, contracts |
| Testing: `TestAttestingKeyStore` (attests only at creation, like the Keystore), `TestCentral.CreateSponsoredCodeAsync`, `LinkFor`, `LinkAddress` | `.Testing` |

**Deviations from 11 §4.1 (with reason):**
1. **Commit/reveal, as in I5.** The 11 §4.1 formula is kept, SHA-256(domain ‖ evidence SPKI ‖ transport SPKI ‖ id ‖ issuer nonce ‖
   handheld nonce) → `PairingProtocol.CodeOf`, so I5 and I6 share the 6-digit derivation (`EnrollmentConfirmation`). But the handheld
   first sends only a commitment to its nonce. Central then picks its nonce, and the handheld reveals its own on the next request. Without
   this, an attacker who can sit between the handheld and central (TLS broken) chooses the nonce the handheld sees after it knows its own
   digits, and can search 10^6 values until both screens agree. The sponsor's screen shows the code only after the reveal.
2. **Pending is a response case**, not a refusal like `PairingPending`, because it carries central's nonce. `EnrollmentResult` is
   unchanged: `EnrollAsync(link, …, showCode)` polls (`ConfirmationPollInterval`, 2 s) until enrolled, rejected or expired.
3. **The certificate is issued at confirmation** (inside the registry transaction), not at the device's next request.
4. **"Push to the sponsor"** is an event on the service; the host's transport to the sponsor's screen (RPC) is the app's.
5. A typed code that needs confirmation is refused with `ConfirmationRequired`: only the link path commits to a nonce.

**Acceptance (I6a part of 08 Phase I6):**

| Item | Result |
|---|---|
| QR → pending → SAS confirm → enrolled | Windows harness (`HandheldEnrollmentTests`, real `EnrollmentClient`) **and** both emulators (Keystore keys, sealed state under the Keystore root key in `NoBackupFilesDir`, `ANDROID_ID` hint): same digits on both screens; no registry record before confirmation; receipt pins the QR's root; roster lists the handheld |
| A second device using the same QR | refused `CodeUsed`; never reaches the sponsor's list; a rejection reaches the first device (`EnrollmentRejected`) |
| A QR with an unknown central host, a wrong root hash | `UntrustedLink` with zero requests sent (other host, other port); `InvalidProvisioning`, nothing stored. Wrong broker key = I6b |
| Wrong digits / another sponsor / reveal tricks | confirming other digits → `InvalidProof`, nothing enrolled; another sponsor → null; a reveal before central's nonce, or one that does not open the commitment → `InvalidProof` |
| Attestation | emulators: 3-certificate chain, the leaf's KeyDescription carries central's challenge for **both** keys; an existing key keeps its first attestation; harness: a failed attempt's keys keep their old challenge and central records it as is |
| Lost Keystore keys | emulators: deleting the root key alias → `KeysLost`; `WipeAsync` → a new identity. Windows: the same with a test root provider; without a classifier the error still propagates |
| Keystore transport key as TLS client credential | emulators: `LocalPeer.CreateAsync(receipt, androidKeys)` + TLS 1.3 to a site broker, which reads the handheld's installation id, role and scope from the certificate |
| Trim gate of the Android build | `installations.android.slnx` Release: 0 warnings, 0 errors |

**Tests:**

| Project | Before | After |
|---|---|---|
| Installations.Tests | 141 | **151** (`HandheldTests` 10: link goldens bytes + text, invalid links, root pin, payload tag 3, pending case, SAS, attestation challenge, `KeysLost` + wipe, platform storage default) |
| Central.Tests | 44 | **53** (`HandheldEnrollmentTests` 9) |
| Rpc.Tests | 13 | 13 |
| Windows.Tests | 9 | 9 |
| Android.DeviceTests (new) | — | **6/6** on Android 16 ATD (`20261027-232241-emulator-5586`) and Android 15 (`20261027-233638-emulator-5584`) |

- Central, Rpc and Windows were green in every run.
- In Installations.Tests two timing tests failed in the full suite tonight:
  - `PeerTcpTests.Handshake_and_first_request_take_at_most_50_ms_with_software_keys` (median 75–85 ms; it passes alone);
  - `ChaosTests.A_partitioned_terminal_misses_a_revocation_…` (2 of 4 runs).
- **The pre-I6a code fails the same way on this machine tonight.** It was rebuilt from the backup in `C:\Temp` and run twice: handshake
  79 / 87 ms, plus the chaos test once. So these failures are environmental, not caused by I6a. They need a look on a quiet machine.
- Every new test and every other test passed.
- NativeAOT smoke (not edited; its libraries changed): old exe deleted, publish exit 0 with 0 IL warnings, run exit 0.
- The first Android 15 attempt crashed with a start-up ANR under memory pressure, while a Release build ran in parallel. The re-run alone
  passed.

**Not built / open (I6b or user):**
- broker path + sponsor push over RPC;
- `SponsoredBy` / `PresenceWitness` in the roster, `SitePresence`;
- revocation from a site terminal and self-revocation;
- an attestation-checking policy (Google roots) — **deferred 2026-10-28 (⚑I34)**: after the real-handheld run, opt-in, central-only;
- the audit entry `Installations.EnrollmentRejected` (no audit integration yet);
- a readable enrolled state whose Keystore *signing* keys alone are gone (`GetEvidenceKeyAsync` would create a new key, and central then
  refuses the renewal with `InvalidProof`): it should read as `KeysLost` too — **built 2026-10-28 (⚑I35)**;
- the real-handheld run (StrongBox).

### 2026-10-28 — I6a follow-ups: sponsor rules, decisions ⚑I33–I36 (Claude)

User decisions on the I6a open items (README §6.5): sponsor scope → options (⚑I33, built); attestation check → deferred with a reminder
(⚑I34); signing keys lost → `KeysLost` (⚑I35, not built); enrollment without a QR or an administrator's code → open (⚑I36).

| Built | Where |
|---|---|
| `SponsorRules.SameScope` (default of `CentralInstallationOptions.MaySponsor`, behaviour unchanged), `SameSite(siteRank)` (the sponsor's rung of that rank and below; rank 0 refused; a sponsor without that rank sponsors nothing), `SameScopeOrBelow` (documented risk: a sponsor above sites) | `Central/SponsorRules.cs` |

Tests: Central.Tests 53 → **54** (`HandheldEnrollmentTests.Sponsor_rules_same_scope_same_site_or_below`: an end-to-end sponsored code for
`…/2=deposito` with `SameSite(1)`, plus each rule on store, deposit, region and a text-prefix sibling). AOT smoke: 0 IL warnings, passed.
Backup: `C:\Temp\installations-backup\i6a-decisions-2026-10-28\`.

**⚑I35 built (same day):**

| Item | Where |
|---|---|
| `IInstallationKeyStore.TryGetEvidenceKeyAsync` / `TryGetTransportKeyAsync(keyName)`: never create. Default methods call the `Get…` methods (a store that cannot tell counts its keys as present); `InstallationKeyStore`, `AndroidInstallationKeyStore` and `TestAttestingKeyStore` ask their stores | `Keys/`, Android, Testing |
| `InstallationStateStore(options, rings, isKeyLost, keys)`: when the file is read (not on cached reads) and holds a receipt, the evidence key must exist and the receipt's transport key must exist **and** match the certificate's public key; otherwise `KeysLost`. A lost platform key while asking (the `isKeyLost` classifier) counts as missing. DI passes `IInstallationKeyStore` | state store, DI |

Limits: the receipt has no evidence public key, so an evidence key **already recreated** by older code cannot be told apart locally (central still
refuses it); keys deleted while the process runs are seen at the next read of the file (restart, or the next save).
Tests: Central.Tests 54 → **55** (`An_enrolled_state_whose_signing_keys_are_gone_reads_as_KeysLost`: not enrolled → no check; missing evidence
key → `KeysLost` from `LoadAsync` and `InitializeAsync`, no key created, file kept, wipe → new identity; missing transport key and a new key
under the old name → `KeysLost`). `TestMachine` now passes its keys, so every central enrollment/renewal test runs the check, and the renewal
test asserts a reopened state still loads after two rotations. Mutation: with the check off, the new test fails.
Backup: `C:\Temp\installations-backup\i35-2026-10-28\`.

**User decisions, round 2 (README §6.5):** ⚑I24 keep as built; ⚑I38 stale-mode restart; ⚑I39 provisional grace configurable (built,
below); ⚑I40 duplicate identity in the diagnostics report; ⚑I37 scheduled CA rotation placed per ⚑H1 (hosted service + optional `.Jobs`
package; root-key signer is a sub-question); ⚑I41 visitors added at central only.

**⚑I39 built:**

| Item | Where |
|---|---|
| `CertificateGrace(Enrolled, Provisional)` (default 7 days each, behaviour unchanged). `InstallationTrust` accepts an expired chain within the longer of the two, then applies the grace of its kind once the issuer says whether it is provisional (refusal `Expired`). The `certificateGrace` parameters of `InstallationTrust` / `InstallationTrustSource` (`FromProvisioning`, `RestoreAsync`) now take it (they took a single `TimeSpan?`, never passed by any caller). `InstallationPeersBuilder.CertificateGrace` sets it for the DI peers service; negative values are refused | `Trust/`, `Peers/`, DI |

Tests: Installations.Tests 151 → **152** (`TrustTests.Enrolled_and_provisional_certificates_have_their_own_grace`: default 7 days for a provisional
certificate; a 12 h provisional grace refuses at +13 h while enrolled keep 7 days; the reverse; a negative grace refused). Mutation: without
the per-kind check the test fails. Central 55, Rpc 13, Windows 9; AOT smoke 0 IL warnings, passed; Android package compiles.
Backup: `C:\Temp\installations-backup\grace-2026-10-28\`.

**⚑I38 and ⚑I40 built (same day):**

| Item | Where |
|---|---|
| **Stale start (⚑I38).** `InstallationTrustSource(…, allowStale)`, `FromProvisioning(…, allowStale)`, `RestoreAsync(…, allowStale, …)`: when the roster or revocation list fails **only** because it expired (signature, publisher key and audience still verify), the source starts `IsStale` instead of throwing; `RestoreAsync` then also starts from expired stored copies newer than the receipt's. `InstallationTrust.Stale` shares only `bundles`: `Check` on another channel → new refusal `PeerRefusal.TrustStale = 10`, `Shares` → false, and beacons offer only `bundles`. `Apply` clears it once the other list is valid too (`Changed` fires); a source that started fresh never becomes stale (running behaviour unchanged). The DI peers service and the harness pass `allowStale: true`; the public default stays false | `Trust/`, `Peers/`, DI, Testing |
| **Duplicate identity at central (⚑I40).** `InstallationDiagnosticsContent` gains `TrustStale`, `DuplicateIdentityBeacons` (since the previous report) and `DuplicateIdentityAddresses` (≤ 8, from `InstallationFriends.TakeDuplicateIdentitySources()`). Content **tag 2** = the tag 1 fields · stale · beacons · count · (address bytes · port)*, written only when one is set, so tag 1 bytes are unchanged; a tag 2 with all three empty is refused as non-canonical. Central: `IInstallationDiagnosticsStore.ListDuplicateIdentitiesAsync(since)` → `DuplicateIdentityFinding(installation, beacons, addresses, first, last)` (a new interface member: other store implementations must add it) | `Diagnostics/`, `Peers/`, Central |

Limits: a stale installation accepts bundles only from peers in its **old** roster (a broker enrolled during its outage cannot serve it);
its own certificate must still be within grace, else only central can help. Offline Phase 4 reads the same report: the new fields are
additive.
Tests: Installations.Tests 152 → **156** (`ChaosTests`: stale start + only `bundles` + recovery + forged list still refused; harness: a
terminal restarted 35 days later (shifted clock) starts stale, its report says so, the broker's beacons bring both lists and `time` works
again. `DiagnosticsTests`: a disk copy reaches central as one finding naming both addresses; tag 1 bytes unchanged, tag 2 round trip with
IPv4/IPv6, limits, non-canonical tag 2 refused). Harness `SignRoster(site, issuedAt)`. Mutations: stale enforcement off → the stale test
fails; tag 2 never written → both diagnostics tests fail. Central 55, Rpc 13, Windows 9; AOT smoke 0 IL warnings, passed; Android compiles.
Backup: `C:\Temp\installations-backup\i38-i40-2026-10-28\`.

### 2026-10-28 — I6b step 1: revocation from a site terminal and self-revocation (Claude)

Task from the I6a session (11 §7 rows 2–4, ⚑I31). Backup: `C:\Temp\installations-backup\i6b-revocation\`. Nothing committed.

**Built:**

| Item | Where |
|---|---|
| **Site terminal → central (row 2).** `CentralEnrollmentService.RevokeFromSiteAsync(caller, installation)` → `InstallationRevocationResult` = `InstallationRevocationAccepted(record, AlreadyRevoked)` \| `InstallationRevocationRefused(reason)`. The host checks the person's `installations.revoke` and passes the installation its assertion authenticated (`InstallationCallContext.Current`, `HttpContext.GetInstallation()`); only its id is used, the registry decides. Refusals (`InstallationRevocationRefusal`): `CallerNotTrusted` (unknown to this tenant, revoked, not enrolled — a provisional installation is unknown until confirmed), `SelfRevocation` (the caller named itself), `UnknownInstallation`, `OutsideSite` (renamed `NotAllowed` 2026-10-29, ⚑I44). The effect is `RevokeAsync`'s (registry, revocation list published and pushed at once). `CentralInstallationOptions.MayRevokeFromSite(caller, target)`, default **same scope, any role** (⚑I44; since 2026-10-29 never the `SiteBroker`); a `SponsorRules` value fits through `(c, t) => rule(c, t.Scope)`. Metric `installations.central.requests`, operations `revoke-from-site` / `revoke-self` | Central |
| **Self-revocation (row 4).** `SelfRevocationRequest(installation, requestedAt: TrustedInstant, signature)`, domain `yfex.installation.self-revocation.v1`, `SelfRevocationCodec` (content tag 1 = installation · bytes(instant); request tag 1 = bytes(content) · signature; strict decoding; relay kind `installations.self-revocation`; item id = SHA-256 of the bytes; `Verify(request, InstallationKeyRecord)`). `InstallationSelfRevocation(keys, relay, state, wipe, time, identity).RevokeAsync()` → `SelfRevocationOutcome` (`Revoked`, `NotEnrolled`, `KeysLost`, `HeldByFriend`, `CentralUnreachable`, `Refused`): signs with the evidence key (never creates one), sends through `IInstallationRelay` (directly when the device reaches central, through a friend otherwise), and **only on central's acknowledgement** deletes the state (`InstallationStateStore.DeleteAsync`) and runs the app's wipe (Android: `AndroidInstallationKeys.WipeAsync`). DI: `peers.AllowSelfRevocation(sp => wipe)` | main package `Enrollment/SelfRevocation.cs`, `InstallationDomains` |
| Central: `RevokeSelfAsync(request)` against the registry's evidence keys. A request dated more than `PublicKeyBackdate` ahead of central's clock is refused. A key revoked **together with** the installation still verifies, so a repeat after the revocation is answered `AlreadyRevoked` and the device can still wipe; a key replaced earlier does not. `SelfRevocationConsumer` (relay ingress; the producer named by mutual TLS must be the installation in the request) and `central.AcceptSelfRevocations()` | Central `InstallationRevocationRequests.cs` |
| Harness: `HarnessCentral.AcceptSelfRevocations(publishAtOnce)` → `HarnessSelfRevocations` (the production `Verify`; `Revoked`; `Publish()` = central's push of the new list) | `.Testing` |

**Decisions taken here (small, reversible):**
1. **Central unreachable → refuse to wipe and tell the app** (`CentralUnreachable` / `HeldByFriend`), instead of queueing and wiping. A friend's "held" is a claim kept in memory only (24 h, lost on restart); a wiped device that central never hears of stays listed as active. The friend still delivers what it holds, and central answers a repeat idempotently.
2. **No new `InstallationRevokedBy` value.** Both paths record `Central` (central decided). `InstallationRevocations.TryDecode` refuses an unknown `By` byte, so a new value would make every older installation refuse the whole revocation list. Who asked (terminal, person) is the host's audit (no audit integration yet).
3. Self-revocation needs no challenge: it can only ask for what the signer can already end, and an id is never re-used (re-enrollment mints a new one).

**Not built — needs a decision (README §6.5):**
- **Store offline (row 3), ⚑I42** (built 2026-10-29, entry below). At the time nothing of the broker-signed site revocation existed (only `InstallationRevokedBy.SiteBroker` and the list format). It needs a new bundle kind or a second signer for the tenant list, i.e. a trust decision. Options A/B/C in README §6.5; recommended A: `installations.site-revocations`, signed by the broker's evidence key, adds only, adopted by central.
- **⚑I43 — central's push overtakes the relay's answer** (fixed 2026-10-29, entry below). Found by the harness: when central's new list reaches the broker before the broker's answer, `InstallationFriends` closes the device's connection on the trust change, so the device never hears "delivered". It is revoked at central and on the site but keeps its state, and no friend accepts it any more. `SelfRevocationTests.When_the_push_overtakes_the_relay_answer_…` pins this. Recommended fix: let in-flight requests finish before closing (a Peers change, not made here).
- **⚑I44** — whether a terminal may revoke the site broker (built: any role in the same scope; decided and changed 2026-10-29, entry below).
- An HTTP/RPC endpoint for the site path (the host's admin API calls the service, like `ConfirmEnrollmentAsync`); the "Revoke handheld" screens (app).

**Acceptance (11 §7):**

| Row | Result |
|---|---|
| Site terminal, central reachable | CAIXA-01's real installation assertion (provider → validator against the registry) → `RevokeFromSiteAsync` revokes COLETOR-03: one push, the published list carries it, central refuses the handheld's assertion (`Revoked`) and its renewal (`InstallationRevoked`); a repeat answers `AlreadyRevoked`. Refused: a terminal of another store, the caller itself, an unknown id, a claimed identity unknown to the registry, a revoked caller, another tenant's central. A device of `…/2=deposito` is outside the default rule and inside `SameSite(1)` |
| Store offline | built 2026-10-29 (⚑I42, entry below) |
| The device itself | Central.Tests: unreachable and held by a friend → nothing wiped, state still loads, not revoked; delivered → revoked, list published, state file gone, the app's wipe called, the next start mints a new identity. Forged key, another installation's key, a future date, an unknown id → refused; a request relayed by a terminal but naming the handheld → refused; repeat after revocation → `AlreadyRevoked`; strict codec. Not enrolled / evidence key gone → nothing sent. **Harness:** a Wi-Fi-only handheld with central down → `CentralUnreachable`, state kept; central back → `Revoked` through the broker's relay over mutual TLS, state gone, the site applies the list and the broker drops the handheld |

**Tests:**

| Project | Before | After |
|---|---|---|
| Installations.Tests | 156 | **158** (`SelfRevocationTests` 2) |
| Central.Tests | 55 | **61** (`RevocationTests` 6) |
| Rpc.Tests | 13 | 13 |
| Windows.Tests | 9 | 9 |

All four green in one full run (projects one by one); the new harness tests passed 3 runs in a row. Mutation: `SelfRevocationCodec.Verify` accepting any signature → `Central_checks_a_self_revocation_…` fails. AOT smoke: new I6b segment (the site path refuses the caller itself; self-revocation through the relay ingress; wipe; registry revoked; exit codes 36–39): old exe deleted, publish exit 0 with 0 IL warnings, run exit 0. Android package: `dotnet build src/YFex.Installations.Android` succeeded, 0 warnings (not run on an emulator: no Android code changed; the wipe hook is the existing `AndroidInstallationKeys.WipeAsync`).

### 2026-10-29 — ⚑I43 drain before close, ⚑I44 broker not revocable from the site (Claude; decisions by the user)

The user chose **A** for ⚑I42 (not built: its sub-questions — signing key, request channel, offline permission check — are open),
**A** for ⚑I43 and the recommendation for ⚑I44.

**Built:**

| Item | Where |
|---|---|
| **⚑I43.** `PeerConnection.DrainAndCloseAsync()`: a friend the new roster or revocations refuse is no longer trusted at once — new requests and channel openings are answered `ChannelNotShared`, its pushes dropped, this side's pending requests to it fail `Disconnected`, and `IsUsable` is false (`InstallationFriends` no longer picks it, `FriendConnection.IsOpen` is false) — but the requests it already sent finish and their answers are written; then the connection closes (at most `PeerEndpointOptions.RequestTimeout`). `InstallationFriends.OnTrustChanged` drains instead of closing | `.Installations` `Peers/PeerConnection.cs`, `InstallationFriends.cs`, `FriendSet.cs` |
| **⚑I44.** `CentralInstallationOptions.MayRevokeFromSite` default: same scope **and** the target is not a `SiteBroker`. `InstallationRevocationRefusal.OutsideSite` → `NotAllowed` (code `not-allowed`; unreleased, value 4 unchanged) | `.Central` |

**Tests:** `SelfRevocationTests.When_the_push_overtakes_the_relay_answer_the_handheld_still_hears_it_and_wipes` (was the pin of the
limit: now `Revoked`, state file gone, then the broker drops it); `RevocationTests.A_site_terminal_cannot_revoke_its_broker_by_default_but_central_and_a_wider_rule_can`.
`ChaosTests.A_partitioned_terminal_misses_a_revocation_…` was flaky before this change (2 of 12 runs, measured with the old peer files):
it read the friend set right after the trust version changed, before `Publish`; it now waits for the friend set. 12 runs of the
self-revocation, gossip, friends and chaos tests in a row green.

| Project | Before | After |
|---|---|---|
| Installations.Tests | 158 | 158 |
| Central.Tests | 61 | **62** |
| Rpc.Tests | 13 | 13 |
| Windows.Tests | 9 | 9 |

AOT smoke (win-x64 publish, no IL warnings) passes.

### 2026-10-29 — ⚑I42 offline revocation by the site broker (Claude; decisions by the user)

The user chose A for ⚑I42, then the broker's **evidence key** (not the site CA key), a **new peer channel** and an **approver seam**
whose default proof is the person's credentials re-verified at the broker (like Site approvals, offline 06 §6).

**Built:**

| Item | Where |
|---|---|
| **The list.** `SiteRevocations`: `Sign(evidenceKey, broker, list, issuedAt)`, `Verify(bundle, roster, tenantRevocations, now, out list)` (members), `Verify(bundle, broker, brokerKey, now, out list)` (central), `Add`, `None`, `IsAdditive`, `StreamFor(broker)`, `RelayKind`. Kind `installations.site-revocations`, stream = the broker id, audience = Scope(site), `ValidUntil = MaxValue`, payload = `InstallationRevocations` (entries `By = SiteBroker`, never the broker itself, dated ≤ the signature, no serials). Domain `InstallationDomains.SiteRevocations` = `yfex.installation.site-revocations.v1` (`SignedBundle.Create` gained an internal domain overload). `BundleCheck.NotAdditive = 11` | `.Installations` `Roster/SiteRevocations.cs` |
| **Trust.** `InstallationTrust(…, siteRevocations)`: `Revocations` = tenant list + site entries for **non-broker members** of the roster, only while the roster shares `PeerChannel.SiteRevocation` (two brokers naming one device: the earlier time); `TenantRevocations`, `SiteRevocations`. `InstallationTrustSource.Apply` takes the kind (verified with the roster's broker key; older → `OlderThanFloor`, same version other content → `ConflictingSameVersion`, dropping an entry → `NotAdditive`); `SiteRevocationBundles`, `SiteRevocationsOf(broker)`; a broker central revoked later keeps what it signed before; `ApplyStoredAsync` restores each roster broker's list from the store. `BundleStore.AcceptVerifiedAsync` / `GetLatestAsync(verify: false)` (internal: floors for bundles the trust source verifies) | `Peers/InstallationTrustSource.cs`, `Trust/InstallationTrust.cs`, `Bundles/BundleStore.cs` |
| **Gossip and beacons.** `BundleGossip` routes the kind to the trust, floors it, announces it, and pulls when a member's beacon advertises a newer list. Beacon **v2** (`LengthV2` = 100 bytes) appends `SiteRevocationsVersion`; written only when the announcer holds its own list, so sites without offline revocations keep v1 (92 bytes); a v2 with version 0 is refused | `Mesh/BundleGossip.cs`, `Peers/InstallationBeacons.cs` |
| **Channel.** `PeerChannel.SiteRevocation` = `installations.site-revocation`, `ChannelSharing.SiteRevocation` (SameSite); central's `RosterSettings.Default` adds it (`ChannelSharing.Defaults` and the golden roster are unchanged). Wire `SiteRevocationWire` (request = tag 1 · installation · bytes(proof ≤ 16 KiB); response = tag 1 · outcome · version) | `Roster/InstallationRoster.cs`, `Mesh/SiteRevocation.cs` |
| **Broker.** `SiteRevocationHost` (`peers.AcceptSiteRevocations(sp => approver)`, `InstallationPeersService.SiteRevocations`): requester must be a same-site, non-provisional member and not the target; target a non-broker member not yet revoked; `ISiteRevocationApprover.ApproveAsync(SiteRevocationApprovalRequest(requester, entry, proof))` → `SiteRevocationApproval` (timeout `SiteRevocationOptions.ApprovalTimeout`, 2 min); signs version + 1, applies it (connections to the device drain and close, ⚑I43), raises `Revoked` (`SiteRevocationRecord`: who asked, who approved), relays the list to central and retries every `ForwardRetryInterval` (1 min) until delivered (`ForwardedVersion`). `RevokeAsync(installation, approvedBy)` for the broker's own screen. Metric `installations.site-revocations` by outcome | `Mesh/SiteRevocation.cs`, DI |
| **Terminal.** `SiteRevocationClient.RevokeAsync(installation, proof)` (`peers.RequestSiteRevocations()`): asks the Active broker first, then Standby ones; `SiteRevocationOutcome` = `Revoked`, `AlreadyRevoked`, `NotApproved`, `NotAllowed`, `RequesterNotTrusted`, `NotEnabled`, `BrokerUnreachable`, `Failed`. Online the host still calls central's `RevokeFromSiteAsync` | `Mesh/SiteRevocation.cs` |
| **Central.** `AdoptSiteRevocationsAsync(broker, list)` → `SiteRevocationAdoption(Check, Adopted, Ignored)`: signed by one of the broker's registry evidence keys; entries of the broker's scope that are not brokers are revoked `By = SiteBroker` **at the broker's time** (clamped to now), others ignored (never refused); one publication. `SiteRevocationsConsumer` (the producer must be the stream's broker), `central.AcceptSiteRevocations()`. `IInstallationRegistry.RevokeAsync(…, revokedAt)` | `.Central` |
| **Harness.** `HarnessInstallation.AcceptSiteRevocations(approver, options)`, `.SiteRevocations`, `.SiteRevocationClient`; `HarnessCentral.AcceptSiteRevocations()` → `HarnessSiteRevocations` (`Adopted`, `Publish()`); `RevokeAtCentral(…, by, at)` | `.Testing` |

**Decisions taken here (small, reversible):** one list **per broker** (stream = its id), so a site with two brokers works; entries for anyone but the
site's non-broker members are **ignored, not refused**, so a stale entry never blocks the broker's later lists; the roster channel is the site's
switch for both serving and accepting (central's default: on, since it only narrows); lists never expire (they only grow; central adopts them);
a broker revoked by central keeps what it signed before its revocation.

**Not built:** the YFex.Offline adapter for `ISiteRevocationApprover` (re-verify the typed credentials against `SiteCredentials`, the broker's
lockout, `installations.revoke` in the signed snapshot); the apps' "Revoke handheld" screens; pruning entries central has adopted.

**Tests:** `SiteRevocationTests` (6, harness): store offline → terminal revokes COLETOR-03 through the broker → broker and CAIXA-02 drop it,
its handshake is refused, the floor is stored → central back → the broker's retry delivers, central adopts at the broker's time; refusals (wrong
PIN, the broker, another broker, the requester itself, unknown, already revoked); a roster without the channel; forged signer, another
store's broker, the bundle domain, conflicting/dropping/older versions, a non-member entry ignored; restart keeps the list; beacon v1/v2.
`RevocationTests` +2 (central adoption and refusals; default roster settings). AOT smoke segment I42 (exit codes 40–43).
`WindowsPeerTests`: the roster entry no longer pins port 7443 (a parallel run of the test projects collided on it).

| Project | Before | After |
|---|---|---|
| Installations.Tests | 158 | **164** |
| Central.Tests | 62 | **64** |
| Rpc.Tests | 13 | 13 |
| Windows.Tests | 9 | 9 |

### 2026-10-29 — I6b step 2: presence (⚑I30), `SponsoredBy` / `PresenceWitness` in the roster (Claude)

User: "commit then start next step" after the I42 work was committed (Installations `8ed2006`). This step is presence; the broker path
(offline handheld enrollment + sponsor push) is the next one.

**Built:**

| Item | Where |
|---|---|
| **Roster format 3** (written only when used, format 1/2 bytes unchanged): `RosterEntry.SponsoredBy`, `RosterEntry.PresenceWitness`, `InstallationRoster.PresenceValidity` (whole seconds ≤ 1 day; zero = `SitePresence.DefaultValidity` 15 min, `EffectivePresenceValidity`). Central fills them from the registry's `SponsoredBy` and new `RosterSettings.PresenceWitnesses` / `PresenceValidity`; `RosterSettings.Default` now also shares `ChannelSharing.Presence` | `Roster/`, Central |
| **`SitePresence`** (domain `yfex.installation.presence.v1`): statement = subject · witness · scope · issued at · challenge kind (`Central` token / `Nonce`) · challenge · binding; `Sign`, `TryDecode` (canonical), `HasValidSignature`, `MayWitness` (broker, the subject's sponsor, a listed witness), and the offline check `Verify(trust, subject, now, binding?, challenge?)` → `SitePresenceCheck` | `Presence/SitePresence.cs` |
| **Channel** `PeerChannel.Presence` = `installations.presence` (SameSite), served by **every** installation (`SitePresenceHost`, in `InstallationPeersService` and the harness); a terminal listed as witness listens (`ShouldListen`). New opt-in **`IPeerCertificateChannelHandler`**: `PeerConnection` passes the handshake certificate to such handlers (one type test per request; other handlers unchanged) | `Peers/`, `Presence/` |
| **Client** `SitePresenceClient` (`peers.RequestPresence()`, also `ISitePresenceSource`): asks the active broker, then standby brokers, then its sponsor or listed witnesses; verifies the answer against its own roster; refreshes every 5 min and on each new central challenge | `Presence/` |
| **Assertion v2**: `InstallationAssertion.Presence` (wire tag 2 = v1 fields · bytes(statement); v1 bytes unchanged; `MaximumLength` 2048 → 4096). `InstallationAssertionProvider(…, presence)` reports each challenge to the source and re-encodes the cached header when a newer statement arrives (no new evidence-key signature). `InstallationAssertionValidator(…, witnesses)` → `InstallationAssertionAccepted.Presence` (`VerifiedSitePresence(Witness, Scope, At)`); a failing statement is ignored, never refuses the call | `Assertions/` |
| **Central** `CentralPresenceWitnesses : ISitePresenceWitnesses` (registered by `AddYFexInstallationsCentral`; used by the RPC and HTTP validators): witness unrevoked, same tenant, enrolled at the statement's scope with the subject at or below it, and a site broker, the subject's `SponsoredBy` or listed in `RosterSettings.PresenceWitnesses`; looked up at every call. RPC: `InstallationCallContext.CurrentPresence` | Central, Rpc, AspNetCore |

**Deviations from 11 §5.2 (with reason):**
1. **Binding = key id of the transport key** in the handshake certificate, not the certificate's hash: central knows the installation's
   transport key, not its certificate, and the key is what TLS proved. Same guarantee (a renewal changes both).
2. **Online, a statement lasts as long as its central challenge** (5 min, must be *valid*, not just authentic: an expired token's purpose and
   audience are not checked by `MessageToken`). `VerifiedSitePresence.At` = the challenge's issue time on central's clock. The 15 min roster
   validity applies to offline checks (nonce + the witness's trusted time). The client asks again with every new challenge, so presence stays
   continuous while a witness answers.
3. **The statement is outside the installation's signature** (assertion v2 appends it): it carries the witness's signature and is bound to the
   subject's id and transport key, so a newer one costs no TPM signature.
4. **Listed witnesses are central's per-scope setting** (`RosterSettings.PresenceWitnesses`), not a registry field; standby brokers witness too.
5. `InstallationCallContext.CurrentPresence`, not `Current.Presence` (`Current` is the identity).

**Open (for the user):** a **sponsor terminal** witnesses only when it can serve TLS: it must listen (mesh `AllInstallations` or listed as
witness) **and** have a certificate with server authentication, which central issues today only to `Central` and `SiteBroker`
(`CertificateUsageFor`), and on Windows a software transport key (⚑I25). So by default the **broker** is the witness; Direct stores
(no broker) need the host to widen `CertificateUsageFor` for their witness terminals. Decide whether central should do that automatically for
listed witnesses and sponsors.

**Not built:** the consumers (Authentication `presence_at` / off-site rule ⚑AN27, Authorization `RequireSitePresence` ⚑Z20, Offline ⚑O32); the
broker path and sponsor push (next step); the real-handheld run.

**Tests:** `SitePresenceTests` (6, harness): broker witnesses the handheld, statement bound to its certificate, verified offline by CAIXA-01
(other subject / key / nonce / age refused); central challenge used while valid, `ForAssertion` drops it after expiry; Direct store: sponsor or
listed witness answer, CAIXA-02 refuses `NotAWitness`, the sponsor refuses for a member it did not sponsor; roster without the channel; forged
and non-witness statements, a 30-min site validity; roster format 3 round trips and refuses relabelled bytes. `PresenceTests` (5, central, real
sponsored enrollment): broker statement → `Presence` with `At` from central's challenge; sponsor yes, CAIXA-02 only when listed; another store's
broker, another signer, another transport key, a nonce, a junk or expired challenge, a revoked broker → ignored; the provider re-encodes
without signing again; assertion v1 bytes unchanged, v2 canonical. Mutation: the witness rule off in `SitePresenceHost` → the Direct-store test
fails. AOT smoke segment I30 (exit codes 44–47): publish 0 IL warnings, run exit 0. The Android solution was not rebuilt (no Android code changed).

| Project | Before | After |
|---|---|---|
| Installations.Tests | 164 | **170** |
| Central.Tests | 64 | **69** |
| Rpc.Tests | 13 | 13 |
| Windows.Tests | 9 | 9 |

### 2026-10-31 — I6b step 3: broker path and sponsor push (Claude)

Committed as Installations `55f725f`. This closes the I6b code; the real-handheld run (I6 acceptance) is still pending.

**Built:**

| Item | Where |
|---|---|
| **Channel** `PeerChannel.SponsoredPairing` = `installations.sponsored-pairing` (`ChannelSharing.SponsoredPairing`, SameSite; in `RosterSettings.Default`, used only while the roster enables pairing). The broker serves it (codes, decisions); a sponsor serves it for the broker's pushes | `Roster/`, `Pairing/SponsoredPairing.cs` |
| **Sponsor side** `HandheldSponsorship.StartAsync(self, keys, sponsor, central, …)`: builds the enrollment link (tenant root, sponsor id + evidence key id, central's part when reachable, a broker part asked of the broker when it answers); `BrokerOutcome` says why a link has no broker part. `ConfirmBrokerAsync` (the person confirms the digits on the sponsor; central's code is then cancelled, `CentralCancelled`), `ConfirmedOnlineAsync` (central confirmed: the broker code is cancelled), both codes cancelled when the screen closes | `Pairing/SponsoredPairing.cs` |
| **Broker side**: per-sponsor codes (`PairingOptions.SponsoredCodeLifetime` default and maximum 5 min, `MaximumCodesPerSponsor` 3, `SponsorPollInterval` 20 s); a sponsor must be an enrolled, not provisional member of the site; answers `SponsoredPairingOutcome`. DI: `AcceptPairing()` without an approver = sponsored pairing only (never confirmed on the broker's screen); `SponsorPairings()` on terminals | `InstallationPairingHost`, `InstallationPeersBuilder` |
| **Handheld** `PairingClient.PairAsync(EnrollmentLink, …)`: tries the link's broker addresses and talks only to a TLS server whose certificate names the link's broker and holds its transport key (else `UnexpectedBroker`, nothing sent); sends the link's code (Hello **v2**, v1 bytes unchanged; link codes have every group, 80 bits); the answer must chain to the link's root hash and the receipt must name the link's sponsor. New refusals: the code is unknown/expired/spent, another device already presented it | `Pairing/` |
| **Receipt v2** carries `SponsoredBy` (v1 bytes unchanged without it); central keeps it as `InstallationRegistryRecord.SponsoredBy` when it confirms the provisional handheld, and registers the handheld as revoked when the sponsor was revoked since | `EnrollmentProtocol.Pairing.cs`, Central `Registry` |
| **Sponsor push at central** (11 §4.1 step 6): `CentralEnrollmentService.PendingEnrollmentsChanged` (per sponsor; expiry raises nothing), `CancelSponsoredCodeAsync`; `CentralSponsorFeed : ISponsorFeedSource` long poll (answers at once when the version differs, else at the next change or after 25 s), registered by `AddYFexInstallationsCentral`; only enrollments whose code can be shown are listed | Central, `Enrollment/SponsorFeed.cs` |
| **RPC** `IInstallationSponsorFeed` (plain RPC calls, never compute methods; byte wire `SponsorFeedWire`; the caller's assertion is the sponsor; only the sponsor cancels its code); ActualLab proxy checked in, `ProxyGoldenTests` case added | Rpc |

**Tests:** `BrokerPairingTests` (9, central + broker + sponsor on the in-memory LAN, real TLS): the same QR offline pairs through the broker
with the digits on the sponsor and central confirms it later; a wrong broker key or id is refused before the Hello; a second device with the
same QR is refused while the first waits and after it paired; other digits decide nothing and a rejection spends the code; codes are limited
per sponsor, bound to it and need a trusted sponsor; without pairing there is no broker code and the link has central only; central
confirming the online path cancels the broker code; central rejects a broker pairing whose sponsor was revoked since; Hello and receipt keep their v1 bytes without a code or sponsor. `SponsorFeedRpcTests`
(2, real ActualLab RPC): the long poll returns the waiting handheld with its digits, another sponsor sees nothing, only the sponsor cancels; the feed wire is canonical.

| Project | Before | After |
|---|---|---|
| Installations.Tests | 170 | 170 |
| Central.Tests | 69 | **78** |
| Rpc.Tests | 13 | **16** |
| Windows.Tests | 9 | 9 |

**Not done:** no AOT smoke segment for step 3 (its types are not in the smoke app); the real-handheld run; the apps' screens (QR, digits,
confirm) belong to the apps.

### 2026-10-31 — Two machines on a real LAN: a Raspberry Pi and the development PC (Claude)

Committed as Installations `1e2a18b`. User: test on another machine. A Raspberry Pi 3 Model B (Raspbian 13 trixie, **32-bit ARM** armv7l,
1 GB, OpenSSL 3.5.7, no TPM) on the same Wi-Fi, with the .NET 11 preview.4 runtime and SDK in `~/.dotnet`; the PC reaches it with the SSH key
`~/.ssh/yfex_pi`. Builds are made on Windows and copied (the SD card is too slow to build on).

**Linux on the Pi** (`Installations.Tests` built on Windows, `dotnet vstest` on the Pi): 167/170 at first.
- 2 failures were a **harness bug**, now fixed: `HarnessDisk.Restore` wrote the copied state file with `File.WriteAllBytes` (0644 under the
  umask) and the store rightly refused it ("must have mode 0600"). It now writes 0600 in a 0700 directory on Unix. Production code was
  right; only `AddCopy` (a disk image booted on a second PC) was affected, on Linux and macOS.
- 1 failure stays: `Handshake_and_first_request_take_at_most_50_ms_with_software_keys` measures a median of 100–120 ms on a Pi 3.
  **Open:** keep the 50 ms budget for PC-class hardware only, or accept the Pi's number.
- After the fix: Pi 169/170, Windows 170/170.

**New tool `tests/YFex.Installations.LanProbe`** (in `installations.slnx`, not a test project): `broker` runs a `TestCentral`, enrolls a site
broker and listens on the real peer port (pairing and an echo on `time`); `pair <address[:port]>` is a new terminal that pairs offline (S7)
and then opens a friend channel; `firewall` prints `WindowsFirewallRuleProbe`'s answer. Exit codes 0, 2 usage, 10 pairing, 11 channel. The
broker listens on the roster's port (7443): `PeerEndpointOptions.Port` applies only when the roster entry has none.

| Run | Result |
|---|---|
| PC pairs with the Pi's broker | same SAS on both, provisional in 0.9 s, mutual TLS 1.3 channel in 0.7 s, requests 6–13 ms |
| Pi pairs with the PC's broker | same SAS, provisional in **≈ 4 s** (the Pi's key creation and signatures), channel 0.8 s, requests 5–15 ms |
| Windows **Private** profile, no allow rule (pop-up left unanswered) | the Pi gets no TCP answer → `BrokerUnreachable`; probe `Missing` ✔ |
| Only `YFex Installations peer` (TCP 7443, Private) | paired, channel OK, no pop-up and no program rule created; probe `Present` ✔ |

Windows behaviour seen on the way (for the future YFex.Installer and for support):
- The "allow access" pop-up creates program rules for the listening executable (`dotnet.exe` or the app host) on the current profile.
  **Allow** gives allow rules for every port, which hide a missing peer rule; leaving it unanswered (or Cancel) gives **block** rules for that
  program, and a block rule beats the port rule. With the port rule present no pop-up appears.
- So on a PC where someone clicked Allow, `CheckWindowsFirewall()` says `Missing` while friends do connect; the measured inbound connections
  decide, as the probe's summary already says. The `FirewallRuleState.Missing` comment ("friends cannot connect") is too strong for that case.

**Fixed: pairing connect timeout.** A broker behind a firewall that drops the connection made the client wait `PairingOptions.MessageTimeout`
(30 s) per address. New `PairingOptions.ConnectTimeout` (default 3 s, like `PeerEndpointOptions.ConnectTimeout`) bounds only the TCP connect;
the TLS handshake and the messages keep `MessageTimeout`. Test
`A_broker_behind_a_firewall_that_drops_the_connection_is_unreachable_after_the_connect_timeout_not_the_message_timeout` (a silent transport;
mutation back to `MessageTimeout` → fails after 1 min). On the LAN: 30.6 s → **3.6 s**; a normal Pi → PC pairing still works.

| Project | Before | After |
|---|---|---|
| Installations.Tests | 170 | 170 |
| Central.Tests | 78 | **79** |
| Rpc.Tests | 16 | 16 |
| Windows.Tests | 9 | 9 |

**Not covered:** Native AOT on 32-bit ARM Linux (.NET does not support it); the TPM store (no TPM on the Pi: use WSL + swtpm); macOS and
Windows 10. The AOT smoke was not re-run for the `ConnectTimeout` property.
