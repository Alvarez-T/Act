# 01 — Current state and inventory

Every identity, pairing, trust store and relay concept found in code and designs (2026-10-05), and where it goes. Paths are relative to
`C:\Dev\YFex`. "Design" rows are documents only (nothing built).

## 1. Identities ("which machine / which copy?")

| # | Concept | Where (file:line) | What it is today | Defect / gap | Goes to |
|---|---|---|---|---|---|
| D1 | `NodeIdentity.InstanceId` | `YFex/src/YFex/Diagnostics/NodeIdentity.cs:22,33` ("Stable per installation (persisted)") | Guid filled by `LoggingEngine.CreateNode` (`YFex.Logging/src/YFex.Logging/Engine/LoggingEngine.cs:315`) from `InstanceIdStore.LoadOrCreate` (`:321-341`): a plain text file whose path the app passes (`LoggingBuilder.cs:233` `InstanceIdFile`) or an explicit id (`:234`) | **No file configured → `Guid.NewGuid()` per process** (`:325`); on a write error also a new id (`:339`). Unprotected text file, anyone can copy it to another machine | = `InstallationId.Value` (⚑I3, ⚑I15); the existing file is adopted once (07 §1) |
| D2 | `NodeIdentity.DeviceId` | `NodeIdentity.cs:28,39`; `LoggingBuilder.cs:236`; logging 05 §3 (`logging/05-correlation-and-distribution.md:39`: "= Profiling hardware fingerprint hash") | optional string | a hardware hash used as an identity: unkeyed, links the same PC across tenants (privacy F-list) | deprecated; Installations records a keyed hint (⚑I16) |
| D3 | `NodeKind` | `NodeIdentity.cs:4-14` (`Server, Desktop, Mobile, Browser, Worker, Device`) | machine kind for log filters | none — it is **form factor**, not authority | stays in Logging; unrelated to `InstallationRole` (02 §2) |
| D4 | Audit `AuditOptions.NodeId` | `YFex.Audit/src/YFex.Audit/AuditTrail.cs:13` (`Guid.CreateVersion7()`); doc `:9-12` "random per process until the durable journal persists it" | producer id of the node chain | **a new node every restart** → every restart looks like a new device and chain continuity (audit 06 §3) cannot hold | = installation id (07 §2) |
| D5 | Audit `AuditOptions.DeviceId` | `AuditTrail.cs:16` | optional string | free text, no binding to a key | = installation id; field removed later |
| D6 | Audit entry `NodeId` | design `audit/02-entry-model.md:36` ("NodeIdentity.InstanceId of the producer"); `audit/07-storage-and-server.md:22,68-71` (`node_id uuid`, `audit.node_heads`, `audit.gaps`) | column type uuid | none (shape fits `InstallationId`) | value = installation id; no schema change |
| D7 | Audit `NodeChainHead.KeyId` | design `audit/06-tamper-evidence.md:47,52` (`string KeyId`) | id of the device key | string, not the core `KeyId` | core `KeyId` of the evidence key |
| D8 | Privacy `ConsentEntry.DeviceId` | design `privacy/04-consent-and-purposes.md:201` | free string | — | `InstallationId` |
| D9 | Privacy `DataSubjectKind.Device` | design `privacy/04-consent-and-purposes.md:183,186` ("Device = an installation (telemetry before login)") | a data subject that is a machine | none | the installation id, pseudonymized in telemetry (privacy 05 §3) |
| D10 | Rules device id | `YFex/src/Validation/YFex.Rules/Approvals/OverrideTokens.cs:146-150` (`IDeviceKeyStore.GetPublicKey(string deviceId)`), `:158` (`DeviceOverrideTokenIssuer(string deviceId, …)`) | free string (`"pos-7"` in `YFex.Rules.Tests/HandWritten/ApprovalTokenTests.cs:41`) | not bound to any enrollment; revoke = delete from an app dictionary | v2 tokens name the `InstallationId` + evidence `KeyId` (Authorization 4b, 07 §4) |
| D11 | Authorization `ActorKind.Device` | `YFex.Authorization/src/YFex.Authorization.Public/Actor.cs:10-11` ("a device acting without a signed-in person") | an **actor** kind | none — a kiosk acting alone is an actor whose subject may be the installation id; still "who", not "which copy" | unchanged; doc note (02 §1) |
| D12 | Profiling `HardwareFingerprint` | `YFex.Profiling/YFex.Profiling/Data/HardwareFingerprint.cs:3`; `YFex.Profiling.Windows/WindowsHardwareFingerprint.cs:23-28` (`SHA256(MachineGuid + salt)`, salt = `TransportSaltKey` option `:15`) | telemetry device hash | unkeyed hash of a low-entropy value (privacy 05 §3 already plans the pseudonymizer) | Profiling keeps it for telemetry; **never** an installation identity (⚑I16) |
| D13 | Jobs `ProcessIdentity` | `YFex.Jobs/src/YFex.Jobs.Hub.Contracts/Identity/ProcessIdentity.cs:17-18,35-38` (`InstanceId` = **ephemeral per launch**, `NodeId` = host); `WorkerOptions.cs:31` and `SchedulerHost.cs:96` default `Environment.MachineName` | job governance tuple | the word `InstanceId` means **process** here and **installation** in logging (name clash, not a defect) | untouched (⚑I18, ⚑Z16); the app may set `NodeId` to the installation id text |
| D14 | Jobs CLI "Installation" | `YFex.Jobs/docs/09-cli-reference.md:16-20` | how to install the `yfexjobs` dotnet tool | word clash only | nothing |
| D15 | Offline `InstallationId`/`InstallationIdentity`/`LocalInstallation`/`InstallationRole` | design `YFex/docs/internal/offline/03-trust-enrollment-pairing.md:18-28`, `02-topologies-and-configuration.md:23,43` | planned core types (Phase 1, never built) | `Site` property is wrong for roaming installations (02 §2) | core `YFex.Installations` (⚑I2), `Site` → `Scope` |
| D16 | Messaging.Rpc connection identity | offline F4/F9: `RpcMessagingExtensions.cs:40` (one endpoint), outbox `Subject/Scope/Sequence` (offline 11 §5) | the server knows the **user**, never the installation | a terminal of store A with a user token can call as store B | installation assertion + scope pinning (⚑I11) |

## 2. Keys and trust stores ("do I trust that machine?")

| # | Concept | Where | Today | Goes to |
|---|---|---|---|---|
| K1 | Rules device private key | `OverrideTokens.cs:158-167` | raw `ECDsa` handed in by the app; signs with `SignData` (no domain separation, crypto C5) | evidence key via `ISigningKey` (Authorization 4b) |
| K2 | Rules device public-key registry | `OverrideTokens.cs:146-150, 174-208` (`ECDsa.Create()` at `:191` is on the crypto architecture-test allow-list) | app-implemented `IDeviceKeyStore` | `InstallationKeyDirectory : IPublicKeyDirectory` (`.Central`), offline 03 §2's `IInstallationKeyRegistry` |
| K3 | Audit device key | design `audit/06-tamper-evidence.md:56-59` ("move that device identity … to a shared place"), `:176` | DPAPI-protected P-256 per device, own registry | evidence key; audit asked for exactly this move |
| K4 | Privacy device key | design `privacy/05-redaction-and-cryptography.md:177`, `privacy/04-consent-and-purposes.md:283-285` | P-256 per installation, "same infrastructure as offline rule approvals" | evidence key |
| K5 | Offline installation keys | design `offline/03-trust-enrollment-pairing.md:30-56` (transport + evidence, CNG/TPM; `IInstallationKeyStore` "stays in YFex.Offline", `:52-53`) | design | `IInstallationKeyStore` in **Installations** (⚑I4) |
| K6 | Crypto key store | `YFex.Cryptography/src/YFex.Cryptography/Signing/SigningContracts.cs:9-27` (`ISigningKey`), `:33-44` (`ISigningKeyStore`), `:47-51` (`IPublicKeyDirectory`), `:58-71` (`PublicKeyRecord`, `KeyIdOf` = SHA-256(SPKI)[0..16)); `YFex.Cryptography.Windows/CngSigningKeyStore.cs:19,29` (prefix, machine scope, prefer hardware), `:93` (`CopyWithPrivateKey`) | **built** (crypto Phases 2–3) | used as is |
| K7 | Crypto certificates | `YFex.Cryptography/src/YFex.Cryptography/Certificates/CertificateAuthority.cs:10-25` (`CertificateUsage`), `:35-56` (`CertificateIssueRequest`, `FromSigningRequest`), `:74` (`CertificateAuthority`); `CertificateTrust.cs:20` (`CertificateCheck` union), `:35` (`CertificateTrust`, custom roots only, caller's revocation list) | **built**; Windows test: TPM CA + mutual TLS 1.3 (crypto 12 §5) | used as is; Installations adds the **policy** (URN SANs, roles, roster) |
| K8 | Crypto handoff to Offline | `YFex/docs/internal/cryptography/12-phases-and-acceptance.md:684-697`; `06-keys-rings-rotation-and-storage.md:158-162` | addressed to "Offline" | re-addressed to Installations (keys, CA, peer validation, assertion); bundles/envelopes split (⚑I9) |
| K9 | Offline tenant CA, mTLS, validator | design `offline/03-trust-enrollment-pairing.md:59-114` (`SitePeerValidator`, `PeerValidation`) | design | `InstallationTrust` + friends (05 §1) |
| K10 | Offline pairing (SAS) | design `offline/03-trust-enrollment-pairing.md:158-180` | design | Installations (⚑I14) |
| K11 | Offline revocation/rotation | design `offline/03-trust-enrollment-pairing.md:182-189` | design | Installations (04 §4) |
| K12 | Devices remoting trust | plan `C:\Users\thiag\.claude\plans\tender-sauteeing-frog.md:228` (§12: `SslStream`, "a PSK or certificate", per-port allow-list; already amended: "certificates come from YFex.Installations instead of a PSK"); `YFex.Devices/docs/04-serial.md:102` ("TLS, PSK, and allow-lists come with Remoting") | planned | Installations TLS helpers + friends; allow-list by `InstallationId` (07 §5) |
| K13 | Authorization device tokens | `YFex/docs/internal/authorization-consolidation/README.md:154` (4b open: "offline device tokens on `ISigningKey` + an app-supplied `IPublicKeyDirectory`"); `06-approvals-grants-impersonation.md:50` | pending 4b | the app-supplied directory **is** `InstallationKeyDirectory` (07 §4) |

## 3. Relays and LAN endpoints

| # | Concept | Where | Today | Goes to |
|---|---|---|---|---|
| R1 | Logging relay | design `logging/05-correlation-and-distribution.md:83-85` (`AddYFexLoggingRelay()`, "store-level edge node"); batch identity `(InstanceId, firstSeq, lastSeq)` `:78` | design, no identity rule for the edge | relay channel through any friend (⚑O26) — batches are end-to-end identified by installation id |
| R2 | Audit relay | design `audit/05-write-path-and-durability.md:163` (`Submit(Guid NodeId, …)`), `:177-178` ("forwards batches unchanged … needs no trust") | design | relay channel (same) |
| R3 | Offline broker relays | design `offline/README.md:159` (⚑O19 "the broker is the relay"), `07-what-runs-where.md:31-46` | design | relay channel; the broker is one relay among friends (06 §2) |
| R4 | Offline broker endpoint | design `offline/03-trust-enrollment-pairing.md:83-94` (Kestrel 7443, client certificates), `02-topologies-and-configuration.md:166-173` | design | peer endpoint + TLS helpers (⚑I10); broker **services** stay Offline |
| R5 | Devices remoting endpoint | plan §12 (`DeviceShareHost`, RFC 2217, framed TCP) | planned | its own port, Installations certificate + validator (or a peer channel, 07 §5) |
| R6 | Offline LAN beacon | design `offline/04-connectivity-and-failover.md:36-44` (120-byte datagram, evidence signature, HMAC site hash) | design | Networking wire + Installations kind `yfex.installation` (⚑I12) |
| R7 | Networking discovery hook | design `YFex/docs/internal/networking/03-discovery.md:3-15` (`IAnnouncementSigner/Verifier`), `:119` (consumer row) | proposal | used by Installations (README §5) |
| R8 | Networking fingerprint | design `networking/02-api-and-examples.md:321-330` (`NetworkFingerprint`, HMAC with a consumer key, "only orders candidates") | proposal | key supplied by Installations; hint only |
| R9 | Time from the broker / peers | design `YFex/docs/internal/time/README.md:311-343` (§8.1 `UseSiteAnchor`), `:367-379` (§8.3 peers evidence only); offline `05-protecting-offline-data.md:177-197` | design (T12 approved) | transport in Installations (⚑I19), rule amended by ⚑T13 |
| R10 | Central time anchor | `YFex/src/Messaging/YFex.Messaging.Rpc/Time/` (`ICentralTime`, `CentralTimeWire`), time README §10 T2 | **built** | unchanged; the site/peer layer sits next to it |

## 4. Bundles and rosters

| # | Concept | Where | Goes to |
|---|---|---|---|
| B1 | `SignedBundle`, `BundleHeader`, `BundleKind`, `BundleCheck`, version floors | design `offline/05-protecting-offline-data.md:29-66`, `:233-240` | format + verifier + floors → Installations (⚑I9); Offline keeps its kinds |
| B2 | `SiteManifest.Installations`, `RevokedInstallations` | design `offline/02-topologies-and-configuration.md:128-136` | signed **roster** + revocation bundle (⚑I8) |
| B3 | Bundle signing at central | design `offline/05-protecting-offline-data.md` §2.3; crypto 12 §5 (`SigningKeyRing`, domain `yfex.bundle.v1`) | Installations.Central signs rosters/revocations; Offline.Central signs its kinds with the same ring |

## 5. Tenancy

| Concept | Where | Use here |
|---|---|---|
| `ScopeIdentity` (`IsWithin`, `Parse`, canonical text) | `YFex.Tenancy/src/YFex.Tenancy/ScopeIdentity.cs:9,53,65,74` | the scope an installation is pinned to; core stores it as canonical text (like `ActorIdentity.HomeScope`, `Actor.cs` ⚑Z3) |
| Offline tenancy rules | design `offline/06-offline-authority.md:298-305` (pinned at enrollment, roaming installation at a higher scope, broker serves only its site) | 02 §4 (unchanged rules, now enforced by Installations' trust + Rpc scope pinning) |

## 6. Reconciling the word "installation"

| Used in | Means | Same concept? |
|---|---|---|
| Offline, Crypto, Audit 08 (`audit/08-privacy-retention-and-access.md:138` "Device ids are pseudonymous installation ids"), Privacy 04 §5 | one enrolled installed copy | **yes** — this library |
| Logging `InstanceId` | "stable per installation" | **yes** (becomes the installation id) |
| Jobs `InstanceId` | one running process | **no** — a process of an installation |
| Jobs CLI "Installation" | installing a dotnet tool | **no** |
| Profiling "device" | the physical machine (hardware) | **no** — one machine may host two installations (a manager PC runs the broker service *and* the desktop app: two installations, offline 07 §2) |

## 7. Findings

| # | Finding | Severity | Fix |
|---|---|---|---|
| **IF1** | Audit `NodeId` is new per process (`AuditTrail.cs:13`), so audit 06 §3 node chains restart at every launch and gap detection reports a "new node" per restart | high once device journals ship | Phase I0/I1 + audit adoption (07 §2) |
| **IF2** | Logging without `InstanceIdFile` gets a new `InstanceId` per process (`LoggingEngine.cs:325`); batch idempotency `(InstanceId, firstSeq, lastSeq)` (logging 05 §6) is then per process, not per installation | medium | adopt `LocalInstallation` (07 §1) |
| **IF3** | Rules device tokens sign raw payloads with an app-held `ECDsa` and a free-text device id; revocation has no time ("valid if signed before revocation" impossible) | medium (already crypto C5 / ⚑Z12) | Authorization 4b on the evidence key + `InstallationKeyDirectory` (`PublicKeyRecord.RevokedAt`) |
| **IF4** | Online, central never knows the installation: a terminal enrolled for loja-centro, with a valid user token of a regional manager, can act for any store | medium (design gap) | ⚑I11 |
| **IF5** | `NodeIdentity.DeviceId` = unkeyed hardware hash links one PC across tenants | low (privacy) | ⚑I16; privacy pseudonymizer |
| **IF6** | Offline 03 §3 still names a P-384 root and `urn:yfex:site:` | doc | crypto decided P-256; ⚑I6 renames to `scope` |
</content>
</invoke>
