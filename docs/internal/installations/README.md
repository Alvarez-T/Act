# YFex.Installations — installation identity, enrollment, friends and the site mesh — Design

> **Status:** DESIGN (2026-10-05). **2026-10-07: the user confirmed ⚑I1–I3 and ⚑I5–I20** (I2 with the evaluation "union only where
> needed" → `InstallationStatus` is a union, `InstallationRole` stays an enum, 02 §4.1). **⚑I4 confirmed 2026-10-07** after the storage
> explanation (§6.1). **Platforms (user, 2026-10-07):** core and library must work on Windows, Linux and macOS with each OS's
> restrictions → [09](09-platforms.md), ⚑I21–I22. **Licensing** is out of scope with two hooks → [10](10-licensing-boundary.md), ⚑I23. **The installer tool is deferred** to a
> future **YFex.Installer** library, after everything else is done (no `.Tool` package here).
> **Implementation started 2026-10-07:** I21–I23 approved as recommended (user wrote “L21 to L23”). **Phase I0 complete:**
> core contracts, 25 new tests and Windows NativeAOT smoke. **Phase I1 complete (Claude took over from Codex on 2026-10-07; review findings and deviations in 08 §5):** keys, certificate profile, signed bundles with publishers and floors, roster, revocations, trust; 46 tests; NativeAOT 0 warnings. **I2 done 2026-10-08, reworked 2026-10-13. I3 done 2026-10-14** (peer endpoint, friends, beacons, harness; open ⚑I24–I25, §6.3). **I4 done 2026-10-14** (mesh channels time, relay, bundle gossip; central relay ingress and RPC link; ⚑T13 in YFex.Time). Runtime phase I5 remains; see [08 §5](08-phases-and-acceptance.md). **2026-10-20:** Android handhelds designed
> ([11](11-mobile-handhelds.md), ⚑I26–I32 confirmed, Phase I6 after I5). **2026-10-27:** YFex.Cryptography built the key-agreement
> key store (I5 is no longer blocked) and `YFex.Cryptography.Android` (Keystore keys work as TLS client credentials on emulators); see
> 08 §2. **I5 pairing + key agreement built 2026-10-27** (SAS with commit/reveal, site issuing CA, provisional + central confirmation; 08 §5).
> **I6b:** revocation from the site (2026-10-28), offline site revocation ⚑I42, **presence ⚑I30** (2026-10-29) and the **broker path and
> sponsor push** (2026-10-31) built; I6 acceptance still needs the real-handheld run (08 §5 "I6b step 2" also lists an open point on sponsor
> terminals as witnesses). **2026-10-31:** first run on two real machines (development PC and a Raspberry Pi 3, 32-bit Linux), 08 §5. The
> site-mesh decisions **⚑O25–O30** and **⚑T13** (§7) were confirmed by the user on 2026-10-07 and are now part of the design.
> **Why now:** user decision ⚑N6 (2026-10-05, time/networking sessions): installation identity and "friends" are their own library,
> not part of YFex.Offline. Networking only finds candidate machines; Installations decides who is a friend, **only from enrollment
> certificates, never from the network**. This also fixes offline finding **F11** (three relays, four device identities, each with
> its own pairing and trust store).
> **Repo:** `C:\Dev\YFex\YFex.Installations` (sibling, like YFex.Time, YFex.Cryptography, YFex.Networking). **Core types:** namespace
> `YFex.Installations` in YFex core (`YFex/src/YFex/Installations`, types only, like ⚑K2).
> **No NuGet package** is added: BCL (`SslStream`, `X509Certificate2`), YFex.Cryptography for every key, signature and certificate,
> YFex.Networking for discovery, YFex.Tenancy for scopes.
> **Audience:** the user first, then the sessions that implement the phases ([08](08-phases-and-acceptance.md)).
> **Normative vs sketch:** names in code blocks are normative unless marked `// (sketch)`.

## 1. Why one installation library

Today the ecosystem has (or plans) **five** answers to "which machine is this?" and **four** ways to trust another machine on the LAN.
None knows about the others ([01](01-current-state-and-inventory.md) has file:line for each):

| Where | "Which machine?" | "Do I trust that machine?" |
|---|---|---|
| Logging `NodeIdentity.InstanceId` | random GUID in a file the app names (`LoggingEngine.cs:321-341`); `Guid.NewGuid()` per process when no file is set | logging relay: "edge node" with no identity rule (logging 05 §6) |
| Logging `NodeIdentity.DeviceId` | YFex.Profiling `SHA256(MachineGuid + salt)` (`WindowsHardwareFingerprint.cs:23-28`) | — |
| Audit `AuditOptions.NodeId` | **`Guid.CreateVersion7()` per process** (`AuditTrail.cs:13`) — a new "node" every restart | device key in DPAPI + own registry (audit 06 §3) |
| Privacy `ConsentEntry.DeviceId` | free string (privacy 04 §5 `:201`) | device key pair "per installation" (privacy 05 §5.1 `:177`) |
| Rules `IDeviceKeyStore` | free string `deviceId` (`OverrideTokens.cs:146-150`) | raw `ECDsa` private key handed in by the app (`:158`) |
| Devices remoting (plan §12) | — | `SslStream` + **PSK** + allow-list |
| Offline design 03 | `InstallationId` + CNG/TPM keys | tenant CA + mTLS (never built) |

The result would be three pairings per terminal, three trust stores and four key stores, and no answer to the question the user
actually asked: *"which other machines are my friends?"*

**This library gives one answer:** an **installation** is one enrolled, installed copy of an app (central instance, site broker,
terminal, the regional manager's laptop). It has one id, two keys in the TPM, one certificate from the tenant's private CA, and a
place in a **signed roster**. A **friend** is a peer whose certificate chains to the pinned tenant root, is not revoked, and is
listed in the roster with a relation that the channel allows. Everything that needs "which machine" or "is that machine one of
ours" — Offline, Logging, Audit, Privacy, Authorization approvals, Devices remoting, Time — reads it from here.

## 2. The mental model

```
                 CENTRAL (YFex.Installations.Central)
                 tenant CA · enrollment codes · installation registry (public keys forever) · signed ROSTER + REVOCATIONS
                       ▲ HTTPS enrollment / renewal (public TLS)        ▲ installation assertion on every RPC connection
                       │                                                │ (YFex.Installations.Rpc: "Ana, on CAIXA-02 of loja-centro")
   ┌───────────────────┴────────────────────────────────────────────────┴────────────────────────────────────────┐
   │ LOJA CENTRO (scope 0=farmaviva/1=loja-centro) — every box below is an INSTALLATION with transport+evidence keys │
   │                                                                                                              │
   │   PC-GERENTE (SiteBroker) ◄──mTLS peer channel──► CAIXA-01 (Terminal) ◄──mTLS──► CAIXA-02 (Terminal)        │
   │        ▲   beacon "yfex.installation" (YFex.Networking carries it; Installations signs and verifies it)      │
   │        └──mTLS── NOTEBOOK-REGIONAL (Terminal, enrolled at 0=farmaviva/1=regiao-sul, listed as visitor)       │
   │                                                                                                              │
   │   ✘ laptop of a customer on the shop Wi-Fi: hears beacons, has no certificate → handshake refused, never a friend│
   └──────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
   YFex.Networking: "something at 192.168.0.10:7443 says it is PC-GERENTE"   (candidates, reachability)
   YFex.Installations: "it proved it with a tenant certificate, it is in the roster, it is a SiteBroker of my site" (friend)
   YFex.Offline: "so I may ask it for a session, a budget slice, custody of my envelope" (authority)
```

Three questions, three owners (unchanged from offline 02 §2, `02-topologies-and-configuration.md:25-35`):

| Question | Owner |
|---|---|
| **Who** did it? (Ana) | Authorization `ActorContext` (⚑Z3) |
| **Whose data?** (`0=farmaviva/1=loja-centro`) | Tenancy `ScopeIdentity` |
| **On which installed copy?** (CAIXA-02) — and **is that other copy a friend?** | **YFex.Installations** |

## 3. Shape and packages

```
 YFex core (types only)                     YFex.Installations (repo, net11.0, AOT)                          consumers
 namespace YFex.Installations               ┌───────────────────────────────────────────────────────┐   ┌──────────────────────┐
 ┌─────────────────────────────┐            │ LocalInstallationState (id minted at first run, sealed) │──►│ Offline (modes,      │
 │ InstallationId               │◄───────────│ IInstallationKeyStore (transport, evidence) ─► Crypto  │   │  authority, broker)  │
 │ InstallationRole             │            │ EnrollmentClient · renewal · InstallationCertificate    │   │ Logging / Audit      │
 │ InstallationStatus           │            │ InstallationTrust (CertificateTrust + roster + revoked) │   │  (identity, relays)  │
 │ InstallationIdentity         │            │ SignedBundle codec/verifier + version floors (⚑I9)      │   │ Privacy (consent sig)│
 │ LocalInstallation (ambient)  │            │ Friends · FriendRelation · roster                       │   │ Authorization (device│
 │ BundleKind · BundleHeader    │            │ PeerEndpoint (mTLS 1.3) · channels · sharing scopes     │   │  /site approvals)    │
 └─────────────────────────────┘            │ announcement signer/verifier ─► YFex.Networking beacons  │   │ Time (site/peer)     │
                                            │ mesh channels: time, relay, bundles (⚑O25–O30)          │   │ Devices remoting     │
                                            └───────────────────────────────────────────────────────┘   └──────────────────────┘
```

| Package | Contents | Depends on |
|---|---|---|
| `YFex.Installations` | everything an installation runs: local state, key store contract, enrollment client, certificate profile, trust and friends, roster, signed bundles + floors, peer endpoint + channels, beacon signer/verifier, mesh channels, `ITimeSource`s for site/peer time | YFex core, YFex.Cryptography, YFex.Networking, YFex.Tenancy, YFex.Time (builder extension only), M.E.DI/Hosting abstractions |
| `YFex.Installations.MacOS` *(after the I1 spike)* | Keychain / Secure Enclave key store (or YFex.Cryptography's, 09 §5), `IOPlatformUUID` hint, launchd notes | the main package (+ a crypto Apple store if built there) |
| `YFex.Installations.Linux` *(later, needs approval of a native dependency)* | TPM 2.0 key store (tpm2-tss / PKCS#11), systemd notes. Linux v1 needs **no** extra package: software keys under the root key, `/etc/machine-id` hint, 0600/0700 files — all in the main package | the main package |
| `YFex.Installations.Android` *(Phase I6, ⚑I26)* | `UseAndroidKeyStore()` over `YFex.Cryptography.Android` (⚑K19), per-app state directory, Android hardware hint; handhelds are peer-channel clients only ([11](11-mobile-handhelds.md)) | the main package + YFex.Cryptography.Android |
| `YFex.Installations.Windows` | `UseWindowsKeyStore()` (CNG/TPM through `YFex.Cryptography.Windows.CngSigningKeyStore`, machine scope for services), service-account notes | the above + YFex.Cryptography.Windows |
| `YFex.Installations.Central` | enrollment codes, issuing CA operation, installation registry, roster and revocation publishing, renewal, `InstallationPermissions` catalog, `InstallationKeyDirectory` for verifiers at central | the above + YFex.Authorization, YFex.Cqrs (admin messages) |
| `YFex.Installations.Rpc` | installation assertion on the Messaging.Rpc connection (client) + `CallContext` installation and scope pinning (server) — online apps too (⚑I11) | the above + YFex.Messaging.Rpc |
| `YFex.Installations.Testing` | in-memory key store, test CA, `InstallationHarness` (N installations on YFex.Networking's `InMemoryNetwork`) | the above + YFex.Networking.Testing, YFex.Cryptography.Testing |
| ~~`YFex.Installations.Tool`~~ | **deferred (user, 2026-10-07):** command-line enrollment, firewall rules and service install belong to a future **YFex.Installer** library, built after everything else. Until then hosts call `IInstallationEnrollment` themselves (first-run screen, broker host argument) | — |
| tests, AotSmoke, Benchmarks | `.Tests`, `.Windows.Tests` (TPM + mTLS), `.AotSmoke` (publish, 0 warnings), BenchmarkDotNet (`InProcessEmitToolchain`) | |

## 4. What goes where

| Concept | Where | Was planned in |
|---|---|---|
| `InstallationId`, `InstallationRole`, `InstallationStatus`, `InstallationIdentity`, `LocalInstallation` | **YFex core** `YFex.Installations` (types only) | offline ⚑O1 (core namespace, unchanged) |
| `BundleKind`, `BundleHeader`, `BundleAudience` (format types) | **YFex core** `YFex.Installations` (⚑I9) | core `YFex.Offline` (offline 05 §2) |
| installation keys, `IInstallationKeyStore`, enrollment, renewal, revocation, certificate profile (URN SANs), tenant CA operation | **YFex.Installations(.Central/.Windows)** | YFex.Offline + `.Central` (offline 03, ⚑O4/O5) |
| peer validation, friends, signed roster, revocation bundle | **YFex.Installations** | Offline `SitePeerValidator`, `SiteManifest.Installations` (offline 03 §4.2, 02 §5) |
| `SignedBundle` codec, verifier, version floors, bundle gossip | **YFex.Installations** (⚑I9) | YFex.Offline (⚑O8) |
| discovery mechanics (beacon wire, multicast, mDNS, fingerprint, hysteresis) | **YFex.Networking** (agreed with that session, §5) | Offline 04 §2 |
| beacon kind `yfex.installation`, its payload and signature | **YFex.Installations** | Offline (`yfex.site-broker`) |
| mTLS peer endpoint, channels, relay, time transport, site mesh substrate | **YFex.Installations** (⚑I13, ⚑O25) | Offline broker (O19/O20) |
| offline pairing with SAS, provisional certificates, site issuing CA | **YFex.Installations** (⚑I14, policy = ⚑O7) | Offline 03 §6 |
| installation assertion to central | **YFex.Installations.Rpc** (⚑I11) | Offline 03 §4.4 |
| modes (`IConnectivity`), authority ladder, `.Offline(...)`, budgets, unique claims, envelopes, custody + receipts, site feed, offline login, reconciliation, review queue, broker services, `SiteTopology`, `UpstreamRoute`, `OfflineLimits`, site time table + findings | **stays in YFex.Offline** | — |
| meaning of bundle payloads (snapshot, rule data, allowances, verifiers, manifest) | **stays with the owner** (Offline, Authorization, Rules.Admin) | — |
| hardware fingerprint | YFex.Profiling keeps its telemetry; Installations records a **keyed hint** at enrollment, never trust (⚑I16, ⚑O30) | offline 03 §5 |

## 5. Coordination with YFex.Networking (agreed 2026-10-05; Networking approved by the user 2026-10-07, Phases 0–3 built)

The Networking session ("Design and build YFex.Networking library") accepted this split:

1. **Networking owns** beacon wire v1 (`YFXB`), groups/ports/abuse limits (⚑N19), mDNS, SSDP, `IAnnouncementSigner`/
   `IAnnouncementVerifier`, `BeaconCandidate`, `NetworkFingerprint`, `Hysteresis`, `EndpointHealth`, `InMemoryNetwork`. It never
   references Cryptography or Installations and never decides trust or tenancy.
2. **One beacon kind `yfex.installation`** for every enrolled installation (role inside the signed payload), owned by Installations;
   networking 03 §2, §6, 05 §7 and README N19 were changed accordingly.
3. The verifier receives `AnnouncementToVerify` (kind, source, service port, sequence, payload, proof, signed part, adapter id,
   adapter category, received at). Replay protection is Installations' (issued-at trusted time inside the signed payload, ≤ 30 s).
4. Windows often classifies a shop LAN as **Public**. Networking added a per-kind `AllowPublicNetworkCategory()` opt-in, allowed only
   for kinds with a verifier; Installations enables it for `yfex.installation` (unverified datagrams are still dropped).
   **Confirmed on a real PC (Networking Phase 3, 2026-10-07):** Windows NLM classified a home LAN as Public (`NetworkAdapter.Category`,
   `WindowsNetworkConnection.Category`), so the opt-in is required; it ships with the beacons in Networking Phase 5.
5. Installations does not use mDNS for itself; `_yfex-device._tcp` stays Devices'. Devices remoting takes certificates from here.
6. The `NetworkFingerprint` key comes from Installations (derived per tenant); fingerprints only order candidates. **As built
   (Networking Phase 3):** Networking writes only canonical evidence bytes (no HMAC there, crypto rule); Installations supplies the
   keyed hash — `NetworkFingerprint.Compute(evidence, bytes => MessageAuthentication tag with the per-tenant key)` (05 §6). QUIC: reported by
   `NetworkingSupport.Quic`; Installations' peer channel v1 is TLS over TCP (⚑I10).
7. `Installations.Testing` builds the multi-installation harness on `InMemoryNetwork` (per-host adapter category, partitions).

## 6. Decisions to confirm (⚑I)

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **I1** ✅ | Where does it live? | Sibling repo **`C:\Dev\YFex\YFex.Installations`** (packages §3) + core namespace **`YFex.Installations`** with types only. Supersedes the "repo YFex.Offline" half of ⚑O1 | inside YFex.Offline (online-only apps, Devices remoting, Logging and Audit would need the offline library to know who they are) | §3, 02 |
| **I2** ✅ | Core types? | `InstallationId` (Guid v7; `ICanonicalValue` 16 bytes; `ILogValue`; not personal data by itself, like `ActorIdentity.Subject`), closed **enum** `InstallationRole {Central, SiteBroker, Terminal}`, **union** `InstallationStatus(InstallationNotEnrolled, InstallationEnrolled(certificate validity), InstallationProvisional(paired by, expires at), InstallationRevoked(at, by))` (user 2026-10-07: union only where cases carry data or behaviour, 02 §4.1), `InstallationIdentity(Id, Role, Scope, DisplayName, Status)` with **scope as canonical text** (core cannot reference YFex.Tenancy), ambient `LocalInstallation` | more roles (Worker, Kiosk: those are app concepts; authority levels are the library's); `ScopeIdentity` in core (moves Tenancy into core) | 02 §2 |
| **I3** ✅ | Who mints the id? | **The installation, at first run** (`Guid.CreateVersion7()`, sealed state file), status `NotEnrolled`; enrollment **binds** it to keys, role and scope. Logs and audit have a stable id before enrollment; the existing logging instance-id file is **adopted** so log history continues. Reinstall = new id (as offline 03 §5) | central mints at enrollment (logs before enrollment get another id; two ids per machine in history) | 02 §3, 07 §1 |
| **I4** ✅ | Keys? (storage confirmed 2026-10-07, §6.1) | ⚑O5 as confirmed, re-homed: `IInstallationKeyStore` in this library names **transport** and **evidence** in any `ISigningKeyStore` (CNG/TPM first). A third **key-agreement** key (ECDH P-256, for delivering an A1 certificate to one installation, crypto 13 §6) is added in Phase I5 when YFex.Cryptography has a CNG `IRecipientKey` (**built 2026-10-27**: `IRecipientKeyStore`, crypto 14 §10) | keep `IInstallationKeyStore` in Offline (Logging/Audit/Privacy would reference Offline to sign) | 04 §1 |
| **I5** ✅ | Enrollment proof of possession? | An **enrollment request** in canonical bytes signed with **both** keys (`SignedPayload`, domain `yfex.installation.enrollment.v1`); central issues from the SubjectPublicKeyInfo (`CertificateIssueRequest` already takes it). No PKCS#10 needed, so no new crypto API | add a CSR builder over `ISigningKey` to YFex.Cryptography (only for PKCS#10 tooling) | 04 §3 |
| **I6** ✅ | Certificate profile? | URN SANs `urn:yfex:installation:{id}`, **`urn:yfex:scope:{canonical scope}`** (was `…:site:` — a roaming laptop is pinned to a region, not a site), `urn:yfex:role:{role}`; 90 days, renewal at ⅔, no IP/DNS names; P-256 root (crypto, decided) | keep `site` in the URN (wrong for roaming installations) | 04 §2 |
| **I7** ✅ | What makes a friend? | **Only** a valid chain to the pinned tenant root + not revoked + listed in the current signed roster (or an allowed provisional pairing). The relation (`SameSite`, `Visitor`, `OtherSite`, `Central`) comes from the certificate scope + roster, never from subnet, SSID, beacon or fingerprint | trust on first use; same subnet = friend (the shop Wi-Fi laptop wins) | 05 §1 |
| **I8** ✅ | Who owns the list of a site's installations? | A signed **roster** per scope owned by Installations (installations, roles, key ids, addresses, visitors, mesh switches, channel sharing levels). Offline's `SiteManifest` keeps topology, route and limits and **cites the roster version**; its `Installations`/`RevokedInstallations` fields move here | one Offline manifest (Installations would depend on Offline to know its friends) | 05 §2 |
| **I9** ✅ | Who owns the signed bundle format? | **Installations**: core `BundleKind`/`BundleHeader`/`BundleAudience`, library codec + `BundleVerifier` + persisted **version floors**. The roster itself is a bundle, and the mesh gossips bundles it must verify. Offline, Authorization (3b snapshot) and Rules.Admin keep their **kinds and payloads**. Format unchanged from offline 05 §2 (header `KeyId` becomes core `KeyId`) | keep in Offline (the roster needs its own second format; the mesh cannot verify gossip without Offline) | 05 §3 |
| **I10** ✅ | Peer channel transport? | **TLS 1.3 over TCP with `SslStream`** on both sides, ALPN `yfex-peer/1`, one port per installation (default 7443), length-prefixed frames with channel ids (request/response + push). QUIC later as an optional transport. TLS helpers for Kestrel/`ClientWebSocket` so Offline's broker can keep Fusion RPC if it wants (same certificate, same validator) | Kestrel + Fusion RPC on every terminal (ASP.NET Core on each checkout, heavy); QUIC now (Linux needs libmsquic) | 05 §4 |
| **I11** ✅ | Should central know the installation **online** too? | **Yes**: `YFex.Installations.Rpc` adds a signed **installation assertion** (id, central nonce, time; evidence key) to the RPC connection and puts the installation in the server `CallContext`; calls whose scope is outside the installation's enrolled scope are refused — CAIXA-02 of loja-centro can never act for loja-norte, online or offline | only for offline replays (offline 03 §4.4): online calls stay "any device with a valid user token" | 04 §6 |
| **I12** ✅ | Beacon kind? | One kind **`yfex.installation`**, payload v1 = id, role, site tag (HMAC of the scope with a roster key), peer port, offered channels, `TimeReply`, roster version, issued-at; proof = evidence-key signature (≈ 30 ms every 5 s on a TPM). Agreed with Networking in draft | per-role kinds (`yfex.site-broker`): terminals need to announce too for the mesh and peer time | 05 §5 |
| **I13** ✅ | Where is the site mesh substrate? | **Installations**: peer endpoint, channel registry, sharing scopes, friend selection, relay-to-central, bundle gossip, time transport. **Offline keeps** modes, authority, budgets, claims, envelopes, custody/receipts, site feed meaning, reconciliation, broker services | the mesh in Offline (Logging/Audit relays and Devices would need Offline) | 06 |
| **I14** ✅ | Offline pairing? | Moves here (SAS, provisional certificate ≤ 72 h, site issuing CA on the broker); the **policy** stays ⚑O7 (disabled by default, per site, manager permission) | stays in Offline | 04 §5 |
| **I15** ✅ | Existing identities? | Migrate to the one installation: `NodeIdentity.InstanceId` = installation id (`DeviceId` deprecated), Audit `NodeId` + device key = installation id + evidence key, Privacy consent `DeviceId` + signature, Rules `IDeviceKeyStore` → Authorization 4b over `InstallationKeyDirectory`, Devices PSK dropped | keep per-library ids (F11 stays) | 07 |
| **I16** ✅ | Hardware fingerprint? | A **keyed hint** recorded at enrollment and renewal (`MessageAuthentication` with a tenant key, not `SHA256(MachineGuid + salt)`); used only to suggest "this looks like the old CAIXA-02 — replace it?" in the admin UI. Never trust, never a block. Profiling keeps its own telemetry field | reuse Profiling's hash (unkeyed, linkable across tenants) | 04 §7 |
| **I17** ✅ | Permissions? | `InstallationPermissions` catalog in `.Central`: `installations.enroll`, `installations.revoke`, `installations.pair`, `installations.view` (as ⚑O24 named them) | in Offline's catalog | 04 §4 |
| **I18** ✅ | YFex.Jobs? | **Untouched** (⚑Z16, frozen wire): `ProcessIdentity.NodeId` stays a string (default `MachineName`); an app may pass the installation id text. Jobs' "Installation" doc section is about installing the CLI tool — a word clash, no concept clash | change Jobs' identity tuple | 01 §6 |
| **I19** ✅ | Site/peer time transport? | `UseSiteAnchor()` and `UsePeerTime()` move from Offline to **Installations** (they ride the peer channel); the broker's **site time table and findings stay in Offline** (time README §8.2) | keep in Offline (Direct sites with a mesh but no broker get no peer time) | 06 §3 |
| **I20** ✅ | Package names? | `YFex.Installations`, `.Windows`, `.Central`, `.Rpc`, `.Testing` (the tool moves to a future **YFex.Installer**) | `.Server` instead of `.Central` (central is the role's name everywhere else) | §3 |

### 6.1 ⚑I4 — where keys and state live (confirmed by the user 2026-10-07; per-OS details in [09](09-platforms.md) §2)

| What | Where (recommended) | Why |
|---|---|---|
| **private keys** (transport, evidence) | YFex.Cryptography owns the configured `ISigningKeyStore`: Windows CNG/TPM; Apple Keychain/Secure Enclave when available; sealed software files for Linux/macOS fallback (09 §2/§5). Installations stores references and public certificates, not private-key bytes in its state or application database | protection is provider-specific: a TPM-held key cannot be copied in a disk image; software keys and their root key can be copied by a sufficiently privileged operator. Record actual `KeyProtection` and apply central policy |
| **local identity** (id, status, certificate, pinned tenant root, bundle-signing public keys) | a small **sealed file owned by the library** (`installation.state`, `SealedValue` v3 with the `device/storage` ring) | read synchronously at start-up before any host or store exists; independent of which `IKeyValueStore` the app chose; not erased when an app clears its cache |
| **received bundles + version floors** (roster, revocations, other owners' kinds) | **YFex.Persistence `IKeyValueStore`** registered by the app, with `AddYFexStorageEncryption` | many, growing items; the app's durable store already exists (SQLite, file system, IndexedDB) |
| **central registry** (installations, public keys forever, enrollment codes, rosters) | **YFex.Data** (SQL) in a later `YFex.Installations.Data` package; in-memory for tests and the first phases | the same split as every other server store in the ecosystem |

### 6.2 Added 2026-10-07 (platforms, licensing) — confirmed 2026-10-07

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **I21** ✅ | How is the code split across operating systems? | **Core: no OS code at all. Main package: everything the BCL gives on Windows/Linux/macOS** (TLS, certificates, files with Unix modes, per-OS directories, software key fallback, Linux/macOS hint reads behind `OperatingSystem.Is…`). **`.Windows` / `.MacOS` / `.Linux`** only for hardware keys and OS service integration. `UsePlatformKeyStore()` picks the best registered store and records `KeyProtection`; YFex.Cryptography is asked for a `FileSigningKeyStore` and an Apple key store (09 §5) | one package per OS for everything (three copies of the trust logic); Windows-only v1 (Linux central/servers and Mac terminals left out) | 09 §1–§3 |
| **I22** ✅ | TLS version where TLS 1.3 is missing (Windows 10 Schannel; macOS `SslStream` to verify in the I1 spike) | **TLS 1.3 everywhere it exists; on those peers TLS 1.2 with only ECDHE-ECDSA-AES-GCM suites**, mutual authentication unchanged, flagged in diagnostics. Decided by measurement in the spike; if macOS cannot use a Keychain key for TLS, macOS transport keys are software (rotated every 90 days) and the Secure Enclave keeps only the evidence key | TLS 1.3 only (Windows 10 checkouts and Macs cannot join the LAN mesh) | 09 §4 R1–R2 |
| **I23** ✅ | Licensing? | **Out of scope** — a future **`YFex.Licensing`** library on top of this one. Add two hooks now: **bundle publishers** (a kind verifies only against its publisher's keys, so a **vendor-signed** license can travel as a bundle that an on-premises tenant cannot forge) and **`IEnrollmentPolicy`** at central (seat limits, hardware-key policy) | licensing inside Installations (products and prices in a domain-agnostic identity library); no hooks (licensing would need its own second bundle format) | 10 |

### 6.3 Added 2026-10-14 by Phase I3 — to confirm

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **I24** ✅ | Where does the beacon **site-tag key** come from? Central names it (`InstallationRoster.SiteTagKey`, a key id) but never delivered key material | **Decided 2026-10-28 (user): keep as built.** **As built in I3:** derived per tenant from the tenant key every installation already receives at enrollment (the hint key, 04 §7) with its own label `installations/site-tag` (`InstallationSiteTag.DeriveKey`). Members and visitors of the tenant recompute any store's tag; passers-by cannot. No protocol change. Without a hint key, beacons are untagged and verifiers skip the tag (the signature still decides) | a separate tenant "discovery key" in the enrollment provisioning (state format v3, enrollment wire change; same security, cleaner separation from the hint purpose) | 05 §6, 08 §5 I3 |
| **I25** | Windows: a **TPM** transport key cannot be a TLS **server** credential in user scope (09 §4 R6, measured). Which keys do listening installations use, and what does `HardwareKeyEnrollmentPolicy` require of brokers? | ✅ **decided 2026-10-19 (user): option 2, a software transport key for listeners**, built the same day: `InstallationKeyStore(evidenceStore, transportStore)`, `WindowsInstallationKeys.Create(…, softwareTransport)`, `UseWindowsKeyStore(transport: TransportKeyPlacement.Automatic)` (software for `SiteBroker`, give `Software` to terminals of full-mesh sites), central `RequireHardwareEvidenceKeys(InstallationRole.SiteBroker)`; verified on the dev PC (broker evidence key in the TPM, software transport key, TLS 1.3 server). The machine-scope measurement stays optional. *Original options:* 1) Run `Machine_scope_TPM_key_as_TLS_server_measurement` (Windows.Tests) once **elevated** to measure machine-scope TPM keys (brokers as services). 2) If machine scope also fails: listening installations keep the **evidence** key in the TPM and use a non-exportable **software KSP transport** key (the macOS recommendation of ⚑I22/R2 applied to Windows listeners); `InstallationKeyStore` takes two stores; the broker policy requires hardware for the **evidence** key only | keep both keys in the TPM: brokers cannot listen (no mesh), or only as machine-scope services if (1) passes | 09 §4 R6, 08 §5 I3 |

### 6.4 Added 2026-10-20 — mobile handhelds ([11](11-mobile-handhelds.md)) — confirmed by the user 2026-10-20

| ⚑ | Question | Decision | Alternative (cost) | Details |
|---|---|---|---|---|
| **I26** ✅ | What is an Android handheld? | A **`Terminal`** installation (role set unchanged), a **full `SameSite` friend** that works online, in Offline `Site` and in `Standalone` (user: "it must handle offline scenarios"). Peer-channel **client only**, never a listener. Keys through `YFex.Cryptography.Android` (⚑K19), package `YFex.Installations.Android` | `NotEnrolled` central-only client (no offline work: refused by the user) | 11 §1 |
| **I27** ✅ | What does the QR contain? | An **`EnrollmentLink` v1**: tenant root hash, expiry, **central** (URL + 80-bit online code), **broker** (id + key hash + addresses + 80-bit pairing code when the site allows pairing), **sponsor** (the terminal that generated it). No long-lived secret. The app accepts only central hosts from its configuration | a bare enrollment code (no pins, no offline path) | 11 §3 |
| **I28** ✅ | Who may generate it and confirm? | **Any enrolled (not provisional) installation of the same site** with a UI is a **sponsor**; the person holds `installations.enroll` (+ step-up) or `installations.pair` offline. Enrollment stays **pending** until the person compares a 6-digit SAS on the sponsor and the handheld and confirms | first device presenting the code wins (a photographed QR enrolls a rogue device) | 11 §2, §4.1 |
| **I29** ✅ | Enrollment while the store is offline? | The same QR's broker part: **SAS pairing at the broker** (⚑I14, policy ⚑O7 per site), confirmed on the sponsor, provisional certificate ≤ 72 h, confirmed or rejected by central later | handhelds only enrolled online | 11 §4.2 |
| **I30** ✅ | How is "in the store" proven? | A **`SitePresence` statement** signed by a **witness**: the active broker **or the handheld's sponsor terminal** (user, 2026-10-20), more by a roster switch; bound to the handheld's own mTLS session and central's challenge; 15 min. **Never** from SSID, IP, `NetworkFingerprint` or beacons (hints only) | Wi-Fi/fingerprint as a factor (copied SSID, shared password) | 11 §5 |
| **I31** ✅ | Who revokes a handheld? | A person holding `installations.revoke`, from central, from **any trusted terminal of the site** (site revocation when offline), or from the **handheld itself**; Authentication ends the bound sessions at once (⚑AN28) | central only (a store cannot react during an outage) | 11 §7 |
| **I32** ✅ | Hardware keys on handhelds? | **Hardware when the device has it** (StrongBox → TEE → software Keystore), **never required by default**; real `KeyProtection` and any attestation recorded; `IEnrollmentPolicy` may require it per application | require StrongBox (excludes cheap handhelds) | 11 §6 |

### 6.5 Added 2026-10-28 — follow-ups of I6a (decided by the user 2026-10-28 unless marked open)

| ⚑ | Question | Decision | Alternative (cost) | Details |
|---|---|---|---|---|
| **I33** ✅ | Which installation may sponsor (show the QR for) which scope? | **Options, default unchanged** (user: "ok, but create options"). `CentralInstallationOptions.MaySponsor` takes a `SponsorRules` value: `SameScope` (**default**: own scope only, the regional laptop never sponsors a store), `SameSite(siteRank)` (the sponsor's rung of that rank and everything under it: a checkout of the store sponsors `…/2=deposito`; a sponsor without that rank sponsors nothing), `SameScopeOrBelow` (everything below the sponsor; unsafe when a sponsor sits above sites), or any custom function. Built 2026-10-28 | `SameSite` as default (the library cannot know which rank is a site in an app's hierarchy) | 11 §2, 08 §5 |
| **I34** ⏸ | **Attestation check** (reminder for later) | **Deferred, decided 2026-10-28.** Today chains are only recorded, so "hardware key" is a *claim*: anyone with a rooted device or an emulator can forge a chain. A real check means: verify the chain up to **Google's hardware attestation roots**, check Google's **revocation list** for leaked factory keys, match the challenge, then read security level (TEE/StrongBox) and boot state, and let `IEnrollmentPolicy` require it per application. Not built because the root list and revocation list need upkeep and emulators cannot test it (their chains are software-rooted). **Do it:** after the real-handheld run, as an **opt-in central-only policy** (central can fetch the revocation list); or earlier if a customer or regulator requires hardware-backed devices. **Until then** the admin UI must say "not verified", never "hardware: yes" | build it now (untestable without a real device; upkeep for a feature no tenant asked for yet) | 11 §6 |
| **I35** ✅ | Enrolled state opens but the Keystore **signing** keys are gone | Treat it as **`KeysLost`** too (wipe → scan a new QR), instead of creating a new evidence key that central refuses at renewal (`InvalidProof`). **Built 2026-10-28:** `InstallationStateStore(…, keys)` checks the evidence key and the receipt's transport key (present, and still the certificate's key) when it reads the file; DI passes the key store. Also covers a cleared TPM on Windows | create a new key and fail at the next renewal | 11 §6, 08 §5 |
| **I37** ✅ | Scheduled rotation of central's issuing CA (I5 open item) | **Yes, as an option** (user 2026-10-28): an option for the CA lifetime and how early the next CA is minted, a method central calls to rotate when due, and a ready-made **YFex.Jobs** scheduled job for hosts that use Jobs (in an adapter package, so `.Central` does not depend on Jobs; Jobs itself is unchanged, ⚑I18). The manual rotation stays. Fits ⚑H1 (hosting README §3–§4): `.Central` ships the hosted service through its `Add…` (no Jobs dependency) and an optional `YFex.Installations.Central.Jobs` package adds a `[Job]` class; central runs it in its own ASP.NET Core process (with Jobs when the host adds it), not as a separate executable. **Root key (decided 2026-10-28, the recommendation):** central holds only the root *certificate*, so the host may supply a signer for the root key (online root in an HSM/TPM) and rotation is automatic; without one the job only creates the next issuing key and warns the operator ahead of time to sign it with the offline root. **Not built** | operator-only rotation (forgotten → the CA expires and new enrollments fail) | 08 §5 (I5 drills) |
| **I36** ✅ | Enrollment **without a QR and without an administrator's code**, e.g. an app whose users sign in with Google | **Yes, configurable (user 2026-10-28), off by default; not built.** The QR is already optional: it is one way to deliver a code (handhelds); the typed code from central is another, and an app can also stay `NotEnrolled` (user tokens only, like browsers). Missing: a **user-authorized** enrollment where a signed-in person holding `installations.enroll` for a scope enrolls *this* device in one step (the host creates the code for the person's own scope and the device redeems it at once). Rules to keep: the scope comes from the person's permission, never from the device or the network; the user's sign-in proves the person, not the device | always an administrator's code (a self-service app needs a second person) | §9 |
| **I38** ✅ | Restart when the receipt **and** the stored roster/revocations have expired (I5 open item) | **Start with the expired lists marked stale** (user 2026-10-28): peers start but the installation only pulls fresh bundles (they verify by central's signature) and treats nobody as a trusted friend until a valid roster and revocation list are applied. **Built 2026-10-28:** `InstallationTrustSource(…, allowStale)` / `FromProvisioning` / `RestoreAsync(…, allowStale: true)` (DI and harness pass it); `IsStale`, `InstallationTrust.Stale`, refusal `TrustStale`; only expiry is forgiven (signature, key and audience still verify) | wait for central (a spare checkout without its own internet stays dead inside a working store) | 08 §5 (I5 drills) |
| **I39** ✅ | Grace for provisional certificates (I5 open item) | **Configurable** (user 2026-10-28): `CertificateGrace(Enrolled, Provisional)`, default 7 days each (unchanged), set with `InstallationPeersBuilder.CertificateGrace` or passed to `InstallationTrust` / `InstallationTrustSource`. Built 2026-10-28 | one grace for all certificates | 08 §5 |
| **I40** ✅ | Duplicate identity seen on the LAN (I5 open item) | **Add it to the diagnostics report** (user 2026-10-28), so central and the admin UI show "CAIXA-02 seen at two addresses — revoke and re-enroll?". Report format change. **Built 2026-10-28:** report content tag 2 (only when used) with `DuplicateIdentityBeacons`, `DuplicateIdentityAddresses` and `TrustStale`; central `IInstallationDiagnosticsStore.ListDuplicateIdentitiesAsync(since)` → `DuplicateIdentityFinding` | local event only (nobody at central learns of a cloned disk image) | 08 §5 (I5 drills) |
| **I41** ✅ | Who adds a visitor to a site roster (§9) | **Central only** (user 2026-10-28): a site may narrow trust offline (site revocation) but never widen it; the visitor's owner (e.g. the regional manager) lists it ahead of time with a validity; the broker shows "unknown visitor" findings | the broker too, with `installations.pair` (a second authority that admits devices; new broker-signed bundle kind) | §9, 05 §2 |
| **I42** ✅ | **Revocation while the store is offline** (11 §7 row 3, I6b step 1 found nothing built) | **A, built 2026-10-29 (user decisions the same day: evidence key, new channel, approver seam).** Bundle kind `installations.site-revocations` (stream = the broker id, audience = the site scope, no expiry, payload = an `InstallationRevocations` of `SiteBroker` entries), signed by the broker's **evidence key** in domain `yfex.installation.site-revocations.v1` (`SiteRevocations.Sign`/`Verify`). Members accept it only from a `SiteBroker` member of their roster while the roster shares channel `installations.site-revocation` (central's `RosterSettings.Default` does), only adding (`BundleCheck.NotAdditive`); its entries count only for non-broker members of the site (`InstallationTrust.Revocations` = tenant + site entries; `TenantRevocations`, `SiteRevocations`). Terminals ask with `SiteRevocationClient` (`peers.RequestSiteRevocations()`); the broker's `SiteRevocationHost` (`peers.AcceptSiteRevocations(approver)`) asks the app's `ISiteRevocationApprover` (opaque proof: by default the person's credentials re-verified at the broker, like Site approvals), signs, applies, gossips (beacon v2 carries the version only once a list exists) and relays it to central until acknowledged; central's `AcceptSiteRevocations()` adopts the entries with `By = SiteBroker` at the broker's time (`AdoptSiteRevocationsAsync`). Log: 08 §5 2026-10-29 | B: the broker co-signs the tenant `installations.revocations` stream (two signers on one floor; a broker list that drops entries would widen trust). C: no offline revocation (the rest of the site keeps trusting the device until central answers) | 11 §7, 04 §4, 08 §5 |
| **I43** ✅ | Self-revocation through a friend: **central's push can overtake the relay's answer** | **A (user 2026-10-29), built the same day:** `PeerConnection.DrainAndCloseAsync` — a friend the new trust refuses is no longer trusted at once (no new requests, channels or pushes, `ChannelNotShared`; this side's requests to it fail) but the requests it already sent finish and are answered before the close (at most `RequestTimeout`), so the handheld hears "revoked" and wipes. Before: the device wipes only after central's acknowledgement (held by a friend or unreachable → nothing wiped, the app is told and may retry). But when the new list reaches the broker before its answer, the broker closes the device's connection first: the device is revoked at central and on the site, never hears it, keeps its state, and no friend accepts it any more (harness test). Chosen: **A** — `InstallationFriends` lets in-flight requests finish before it closes a peer the new list refuses | B: central publishes a self-revocation only after answering (ordering, not a guarantee). C: wipe as soon as a friend took the request (a lost request leaves the id active at central; its keys are gone, so only the seat and roster are wrong) | 11 §7, 08 §5 |
| **I44** ✅ | May a site terminal revoke **any role** of its site through central (the broker too)? | **Never the broker by default (user 2026-10-29, the recommendation), built the same day.** Default `MayRevokeFromSite` = same scope **and** target not `SiteBroker` (the broker serves the whole site; an administrator revokes it at central); refusal renamed `OutsideSite` → `NotAllowed` (`not-allowed`). Previously built as "any role in the same scope". `CentralInstallationOptions.MayRevokeFromSite(caller, target)` can add a role check (e.g. never the `SiteBroker`), or widen to the site's sub-scopes with `SponsorRules.SameSite` | default that refuses the broker (an administrator revokes it at central) | 11 §7 |

## 7. Site mesh decisions from the time session (⚑O25–O30, ⚑T13) — confirmed 2026-10-07

These were proposed in chat during the time session (2026-10-05) and never written down. They are stated here in full; the analysis
is in [06](06-site-mesh.md).

| ⚑ | Question | Recommendation | Alternative (cost) | Lands in |
|---|---|---|---|---|
| **O25** ✅ | Who runs a peer endpoint? | **Any enrolled installation of a site may** run a small mTLS peer endpoint. Always on for `SiteBroker`; **opt-in per site** for terminals through the roster (`BrokerOnly` or `AllInstallations`), because it needs an inbound firewall rule on every checkout. Default `BrokerOnly`; the installer adds the rule when the site turns the mesh on | every installation always listens (firewall prompts on every checkout); only the broker (Direct sites without a broker lose time/relay/bundle sharing) | Installations |
| **O26** ✅ | What may travel through any friend? | Only **signed or idempotent** traffic: time (NTP-style stratum), **relay to central** of signed envelopes, receipts, log and audit batches, **central-signed bundles gossiped** with version floors, offline **facts** as append-only signed envelopes, **signed store decisions** (site approvals, price overrides). A relaying friend can drop or delay, never forge or alter | anything (an unsigned answer from a peer becomes a decision) | Installations (transport), Offline/Logging/Audit (meaning) |
| **O27** ✅ | Scarce resources (budgets, agreement credit, stock reservations, unique claims)? | **Never gossiped.** Pre-partitioned slices (⚑O14) + an **optional coordinator** chosen by the roster (the broker, or a named terminal), **no automatic election** (⚑O2). Exclusive claims need the coordinator; without it, **accept and flag** at reconciliation | peer consensus (a consensus protocol in a pharmacy, failure modes worse than the outage) | Offline |
| **O28** ✅ | Share query caches between terminals? | **Never.** Unsigned `ICacheable` results are one terminal's view; a peer could serve anything. Data other terminals must see travels as signed bundles or signed facts | share caches (a compromised terminal poisons every checkout's prices) | Offline (no channel offered in Installations) |
| **O29** ✅ | How wide does each channel share? | **Per channel, by tenancy level, in the signed roster.** Defaults: time and relay **tenant-wide** (any friend of the tenant may relay my signed batch to central), bundles and facts **site-wide**, personal data **minimal** (sealed to central with `RecipientSealing`, never readable by peers). A visitor (regional laptop) gets only tenant-wide channels unless the site roster lists it for more | one level for everything (either over-shares facts or under-uses relays) | Installations (enforcement), roster (data) |
| **O30** ✅ | Discovery and trust? | Discovery **automatic** (manifest addresses → signed `yfex.installation` beacons → typed address); trust and tenancy **only from certificates + roster**. The hardware fingerprint is only a hint ("looks like the old CAIXA-02") | manual IPs only (DHCP breaks stores); trust from subnet/SSID | Installations + Networking |
| **T13** ✅ | May a peer anchor time? | **Yes, like NTP:** a friendly peer that is itself anchored (not `Unanchored`, stratum < 16) is an **unauthenticated** candidate (`Authenticated = false` even over mTLS). Central and the broker always win. Peers **vote together with NTP** (Marzullo); "strictly after NTP" needs a small per-kind priority in YFex.Time, which is a sub-option. Peer-raised `RollbackDetected` (time §8.3) is not built yet (06 §2.3). It **never** makes the clock usable for security decisions and never pulls time back (T3/T10/T11 unchanged). Amends T12 "peers are evidence only" | peers as evidence only (a terminal alone on a LAN without internet has no estimate at all) | YFex.Time (rule) + Installations (`UsePeerTime`) |

## 8. Files

| # | File | Content |
|---|---|---|
| 01 | [01-current-state-and-inventory.md](01-current-state-and-inventory.md) | every identity, pairing, trust store and relay concept in code and designs (file:line) and where each goes |
| 02 | [02-model-and-core-types.md](02-model-and-core-types.md) | installation ≠ scope ≠ actor, lifecycle, roles, core types, `LocalInstallation`, enrollment pins a scope |
| 03 | [03-api-and-examples.md](03-api-and-examples.md) | **dev-side code first**: Loja Centro, PC-GERENTE, CAIXA-01/02, the regional laptop, the customer's laptop, replacing a dead checkout |
| 04 | [04-keys-enrollment-certificates.md](04-keys-enrollment-certificates.md) | keys over YFex.Cryptography, certificate profile, enrollment protocol, renewal, revocation, SAS pairing, assertion to central, fingerprint hint |
| 05 | [05-friends-and-peer-channels.md](05-friends-and-peer-channels.md) | friend recognition, roster, signed bundles + floors, peer endpoint, channels, sharing scopes, beacons, TLS helpers |
| 06 | [06-site-mesh.md](06-site-mesh.md) | evaluation of the mesh proposal, channel by channel; what stays in Offline; scenarios |
| 07 | [07-migration.md](07-migration.md) | before/after per existing identity (Logging, Audit, Privacy, Rules/Authorization, Devices, Profiling, Jobs, Offline) |
| 08 | [08-phases-and-acceptance.md](08-phases-and-acceptance.md) | working rules, phases I0–I5 with acceptance, docs to update, implementation log |
| 09 | [09-platforms.md](09-platforms.md) | Windows / Linux / macOS (and later mobile/browser): what is OS-free, per-OS keys, directories, permissions, TLS, firewall/Local Network, hints, services; risks for the I1 spike; requests to YFex.Cryptography |
| 10 | [10-licensing-boundary.md](10-licensing-boundary.md) | why licensing is a separate library, what it would get from Installations, vendor vs tenant signer, the two hooks |
| 11 | [11-mobile-handhelds.md](11-mobile-handhelds.md) | Android handhelds: QR enrollment link, sponsor + SAS confirmation, offline pairing path, presence witnesses, off-site rules, revocation, risks (⚑I26–I32) |

## 9. Open questions (not blocking Phase I0–I1)

- ~~**Mobile/browser installations**~~ **Android handhelds: decided 2026-10-20** (⚑I26–I32, [11](11-mobile-handhelds.md), Phase I6):
  full `Terminal` friends with offline work. **Browsers (Blazor WASM)** stay `NotEnrolled` clients (central-only), never friends.
  iOS would follow the same design over `YFex.Cryptography.Apple` when needed.
- **Windows firewall rule**: created by the future **YFex.Installer** (user, 2026-10-07) with administrator consent; until then by the
  site's technician. Installations only **checks** presence (YFex.Networking firewall-rule read) and reports it.
- ~~**Visitors at a site**~~ **decided 2026-10-28: central only (⚑I41).** (regional laptop): who adds a visitor to a site roster — the regional manager at central, or a store manager
  at the broker (needs `installations.pair`)? Recommendation: central only; the broker can show "unknown visitor" findings.
