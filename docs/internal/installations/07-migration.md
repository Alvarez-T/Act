# 07 — Migration of existing identities (⚑I15)

Rule for every step: **the consumer keeps working without YFex.Installations** (`LocalInstallation.Current == null` → today's
behaviour). Nothing is a hard break except where a parallel design already planned one (Authorization 4b).

## 1. Logging `NodeIdentity`

```csharp
// Before — the app must remember to name a file, or gets a new InstanceId per process (LoggingEngine.cs:325)
services.AddYFexLogging(log => log
    .Node(node => node.InstanceIdFile(@"%LOCALAPPDATA%\FarmaViva\instance-id").DeviceId(fingerprint.DeviceIdHash)));

// After — nothing to configure when YFex.Installations is registered
services.AddYFexInstallations(installations => installations.Terminal().UseWindowsKeyStore()
    .AdoptExistingId(@"%LOCALAPPDATA%\FarmaViva\instance-id"));     // one-time: keeps the log history's id (⚑I3)
services.AddYFexLogging(log => log.Node(node => node.Kind(NodeKind.Device)));
```

| Change | Where | Compatibility |
|---|---|---|
| `CreateNode`: `options.InstanceId ?? LocalInstallation.Current?.Id.Value ?? InstanceIdStore.LoadOrCreate(file)` | `LoggingEngine.cs:315` | explicit `InstanceId(...)` still wins |
| `NodeIdentity.InstanceId` doc: "the installation id (YFex.Installations) when present" | `NodeIdentity.cs:22` | wire unchanged (Guid) |
| `NodeIdentity.DeviceId` + `NodeBuilder.DeviceId` → `[Obsolete("use the installation id; hardware hints are recorded by YFex.Installations")]` | `NodeIdentity.cs:28`, `LoggingBuilder.cs:236` | still written for one release |
| `LocalInstallation.Changed` (NotEnrolled → Enrolled) does **not** change the id (minted at first run) → no restart needed | — | — |
| relay: `log.Shipping(s => s.ThroughFriends())` (adapter in Logging.Shipping over `IInstallationRelay`) replaces `AddYFexLoggingRelay()` on a dedicated edge node | logging 05 §6 | the broker is a friend like any other (⚑O19 superseded by ⚑O26) |

`AdoptExistingId(path)` reads the Guid in the old file once, uses it as the installation id **only if it parses and no state file
exists yet**, and leaves the file untouched.

## 2. Audit node and device key

```csharp
// Before — a "stable installation id" the app has to invent; random per process by default (AuditTrail.cs:13)
services.AddYFexAudit(audit => audit.Node(nodeId: Guid.Parse(File.ReadAllText(idPath)), deviceId: "pdv-02"));

// After
services.AddYFexAudit(audit => audit.SignNodeChainsWithInstallationKey());   // node = LocalInstallation; heads signed by the evidence key
```

| Change | Where |
|---|---|
| `AuditOptions.NodeId` default = `LocalInstallation.Current?.Id.Value ?? Guid.CreateVersion7()`; `DeviceId` `[Obsolete]` | `AuditTrail.cs:13,16`, `AuditServiceCollectionExtensions.cs:17-24` |
| node-chain head signed through `IInstallationKeyStore.GetEvidenceKeyAsync` + `SignedPayload` (domain `yfex.audit.node-head.v1`); `NodeChainHead.KeyId` = core `KeyId` | audit 06 §3 (`06-tamper-evidence.md:47,52`) |
| ingest verifies heads with `InstallationKeyDirectory` (public keys forever, `RevokedAt`) | audit 05 §6 |
| "DPAPI device key + own registry" dropped (audit 06 §3 asked for this move) | `06-tamper-evidence.md:56-59` |
| relay: `relay` channel (§1 note) | audit 05 §6 `:177-178` |

## 3. Privacy consent ledger (Tier 2, not built)

```csharp
// Before (design, privacy 04 §5)
new ConsentEntry(…, DeviceId: "pdv-02", …, DeviceSignature: ecdsaSignature, …);

// After (design amendment)
new ConsentEntry(…, Installation: LocalInstallation.Current!.Id, …, Signature: signedByEvidenceKey, …);
// central: SignatureVerifier(InstallationKeyDirectory); unknown/revoked installation → quarantine (privacy 04 §7 rule unchanged)
```

`DataSubjectKind.Device` subjects (telemetry before login) use the pseudonymized installation id (`PseudonymDomain.Telemetry`).

## 4. Rules `IDeviceKeyStore` → Authorization 4b

> **Done 2026-10-16** (authorization-consolidation 09, Phase 4b): `DeviceApprovalTokenIssuer` (YFex.Authorization.Installations) signs
> with the evidence key; `DeviceApprovalSignatures` + `AddYFexDeviceApprovalVerification()` (….Installations.Central) verify through
> `IInstallationKeyDirectory`. v1 was dropped (Authorization A9), so the `[Obsolete]` period below did not happen: `IDeviceKeyStore` is deleted.

Authorization 4b already moves the token family (⚑Z12, `09-phases-and-acceptance.md:113-122`) and still needs "an app-supplied
`IPublicKeyDirectory`" (`README.md:154`). That directory is this library's:

```csharp
// Before — YFex.Rules (OverrideTokens.cs:146-208)
var issuer = new DeviceOverrideTokenIssuer("pos-7", ecdsaPrivateKey, TimeSpan.FromMinutes(15));
var verifier = new DeviceOverrideTokenVerifier(new MyDeviceKeyDictionary(), caps);   // IDeviceKeyStore by string id

// After — Authorization 4b (sketch; names are Authorization's)
var issuer = ApprovalTokens.Device(await installationKeys.GetEvidenceKeyAsync(ct), LocalInstallation.Current!.Id);  // ApprovalToken v2
services.AddYFexAuthorization(a => a.VerifyDeviceApprovals(caps));       // directory = InstallationKeyDirectory from DI (.Central)
```

- v2 body names the **`InstallationId`** (core type, so `Authorization.Public` stays a leaf) and the evidence `KeyId`.
- v1 tokens keep verifying through the golden codec until the cut-off; `IDeviceKeyStore` stays `[Obsolete]` in Rules for that period
  (`ApprovalTokenTests.cs:58` keeps its dictionary implementation).
- Site tokens (`ApprovalTokenKind.Site`, ⚑O15) are signed by the **broker's** evidence key and verified the same way.

## 5. Devices remoting (plan §12)

```csharp
// Before (plan) — PSK in a config file + IP allow-list
services.AddDeviceShareHost(share => share.Port(2217).PreSharedKey(config["Devices:Psk"]).Allow("192.168.0.0/24"));

// After — installation identity; the allow-list names installations
services.AddDeviceShareHost(share => share
    .Port(2217)
    .Secure(installationTls, DevicePeerChannels.Serial)          // mTLS with installation certificates + InstallationTrust
    .Allow("COM3", friend => friend.Relation == FriendRelation.SameSite && friend.Role != InstallationRole.Central));
```

- Discovery stays `_yfex-device._tcp` mDNS (YFex.Networking, ⚑N2); identity and trust from here. The plan line already says so
  (`tender-sauteeing-frog.md:228`); `YFex.Devices/docs/04-serial.md:102` changes "TLS, PSK" to "TLS with installation certificates".
- Option for later: run the share as a peer channel (`devices.serial`) on the installation's peer endpoint instead of its own port.

## 6. Profiling, Jobs, Authorization actor

| Library | Change |
|---|---|
| YFex.Profiling | none in code. `HardwareFingerprint` stays telemetry; the privacy pseudonymizer fix (privacy 05 §3) is independent. Installations never reads it (⚑I16) |
| YFex.Jobs | none (⚑I18, ⚑Z16). Docs: `ProcessIdentity.NodeId` may be set to `LocalInstallation.Current?.Id.ToString()` by the app; Jobs' `InstanceId` = process (not installation) noted in its docs |
| Authorization `ActorKind.Device` | none. A kiosk acting alone is an actor whose subject is the installation id text; the installation itself is in `CallContext.Installation` (⚑I11) |

## 7. YFex.Offline (design) — what changes in its docs

| Offline item | Change |
|---|---|
| ⚑O1 | core namespace `YFex.Installations` stays; the types and keys now come from the **YFex.Installations repo**, not the Offline repo |
| ⚑O4, ⚑O5 | mechanism re-homed here (CA operation, keys, certificate profile, validator); the decisions stand |
| ⚑O6 | discovery = YFex.Networking beacons + kind `yfex.installation` (⚑I12) |
| ⚑O7 | pairing implemented here (⚑I14); policy unchanged |
| ⚑O8 | `SignedBundle` format, verifier and floors here (⚑I9); Offline keeps its kinds |
| ⚑O19 | relays = the `relay` channel through any friend (⚑O26); the broker is one of them |
| ⚑O20 | the generic broker host composes Installations (enrollment, peer endpoint) + Offline services |
| 02 §5 `SiteManifest` | `Installations`, `RevokedInstallations` → roster (⚑I8); manifest cites `RosterVersion` |
| 03 | §2–§7 superseded by this design (pointer note at the top) |
| 04 §2 | discovery by Networking + Installations |
| 05 §2 | format moved (pointer) |
| 05 §4.1 / time §8.1 | `UseSiteAnchor()` → Installations (⚑I19); site time table stays |
| 11 | Phase 1 core identity types → Installations I0; Phase 2 central trust → I2; Phase 4 mTLS/discovery/time transport → I3–I4; Phase 5 pairing → I5 |
</content>
</invoke>
