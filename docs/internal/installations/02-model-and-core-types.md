# 02 — Model and core types

## 1. Installation ≠ scope ≠ actor ≠ machine

| Concept | Answers | Example at Loja Centro | Owner |
|---|---|---|---|
| **Installation** | which installed copy of an app made or received this | `CAIXA-02` (id `0199a3c4-…`, Terminal, keys in its TPM) | YFex.Installations |
| **Scope** | whose data | `0=farmaviva/1=loja-centro` | YFex.Tenancy |
| **Actor** | who did it | Ana (User), or the kiosk itself (`ActorKind.Device`, subject = the installation id text) | Authorization (⚑Z3) |
| **Machine** | which hardware | the Dell in the manager's office | nobody — at most a **hint** (⚑I16) |

They are not one-to-one:

- `PC-GERENTE` is **one machine with two installations**: the "YFex Site Broker" Windows service (role `SiteBroker`, machine key
  store) and the manager's desktop app (role `Terminal`, user key store). Each has its own id, keys and certificate (offline 07 §2).
- `NOTEBOOK-REGIONAL` is **one installation in several scopes' rosters**: enrolled at `0=farmaviva/1=regiao-sul`, listed as a visitor
  in the rosters of the stores of that region.
- Central is **ten cloud instances**: each container is an installation with role `Central` (they share the tenant CA operation and
  bundle rings, not installation keys).
- A reinstalled Windows on `CAIXA-02` is a **new installation** with a new id; the old one is revoked. The hint says "looks like the
  old CAIXA-02".

**Enrollment pins an installation to a scope** (offline 06 §9, unchanged): the certificate carries `urn:yfex:scope:…`, central refuses
any envelope **or online call** (⚑I11) whose scope is not within the installation's scope.

## 2. Roles are authority levels, not form factors

```csharp
// YFex core, namespace YFex.Installations — closed set owned by the library (authority levels, not app concepts)
public enum InstallationRole : byte { Central = 1, SiteBroker = 2, Terminal = 3 }
```

| Role | Authority | Not to confuse with |
|---|---|---|
| `Central` | runs the app's server code; issues certificates, rosters, bundles (through its `.Central` packages) | Logging `NodeKind.Server` (a form factor: a Jobs worker is a `Server` node but may be a `Terminal`-role installation of another app) |
| `SiteBroker` | the more trusted machine of a site (offline 02 §3): coordinator for Offline, always runs a peer endpoint | — |
| `Terminal` | where people work; offline decisions run here | `NodeKind.Desktop/Device/Mobile` |

A kiosk, a handheld or a back-office PC are all `Terminal`s; what they may do is permissions (Authorization) and app configuration,
not role. This keeps the set closed (domain-agnostic rule, `yfex-libs-domain-agnostic`).

## 3. Lifecycle

```
 first run ──► NotEnrolled ──(enrollment code, online)──► Enrolled ──(renewal every ~60 days)──► Enrolled
     │              │                                        │  └──(revoked at central or by a manager at the broker)──► Revoked
     │              └──(SAS pairing at the broker, central unreachable, if the site allows it)──► Provisional ──(central confirms)──► Enrolled
     │                                                                                              └──(central rejects)──► Revoked
     └─ id minted here (⚑I3): Guid v7, sealed state file; logs/audit use it at once
```

| Status | Logs/audit carry the id | Signs evidence | Has a certificate | Friend of others | Central accepts its envelopes |
|---|---|---|---|---|---|
| `NotEnrolled` | yes | **no** (keys exist, nobody knows them) | no | never | no (online calls work as today: user token only) |
| `Enrolled` | yes | yes | yes (90 days) | per roster | yes |
| `Provisional` | yes | yes, flagged `ProvisionalInstallation` | site issuing CA, ≤ 72 h | same site only, no approvals | yes, as review findings until confirmed |
| `Revoked` | yes | signatures after the revocation time are refused | no | never | only envelopes signed before the revocation |

Reinstalling deletes the state file and the keys, so the installation starts again at `NotEnrolled` with a new id.

## 4. Core types (YFex core, `YFex/src/YFex/Installations/`, types only)

```csharp
namespace YFex.Installations;

/// One enrolled installed copy of an app. Guid v7 minted by the installation at first run (⚑I3).
/// Canonical form: the 16 bytes of the Guid in big-endian (RFC 9562) order. Text: "0199a3c4-…" (Guid "D").
/// Not personal data by itself (like ActorIdentity.Subject); telemetry pseudonymizes it with the privacy pseudonymizer.
public readonly record struct InstallationId(Guid Value)
    : ICanonicalValue<InstallationId>, ILogValue<InstallationId>, ISpanFormattable, ISpanParsable<InstallationId>
{
    public static int MaxCanonicalLength => 16;
    public static bool TryWriteCanonical(in InstallationId value, Span<byte> destination, out int written);   // Guid.TryWriteBytes(bigEndian: true)
    public bool IsEmpty => Value == Guid.Empty;
    // ToString/TryFormat/Parse/TryParse = Guid "D"; ILogValue writes the Guid field (search key)
}

public enum InstallationRole : byte { Central = 1, SiteBroker = 2, Terminal = 3 }

// Status = native union (user 2026-10-07: union where the cases carry data or change behaviour; §4.1)
public sealed record InstallationNotEnrolled;
public sealed record InstallationEnrolled(ValidityPeriod Certificate);                     // renewal at ⅔, grace after the end
public sealed record InstallationProvisional(InstallationId PairedBy, DateTimeOffset ExpiresAt);   // SAS pairing at a broker (04 §5)
public sealed record InstallationRevoked(DateTimeOffset RevokedAt, InstallationRevokedBy By);
public enum InstallationRevokedBy : byte { Central = 1, SiteBroker = 2 }
public union InstallationStatus(InstallationNotEnrolled, InstallationEnrolled, InstallationProvisional, InstallationRevoked);

/// Who this installation is. Scope = canonical YFex.Tenancy scope text ("0=farmaviva/1=loja-centro"); empty while not enrolled.
/// Core cannot reference YFex.Tenancy, the same reason ActorIdentity.HomeScope is text (⚑Z3).
public sealed record InstallationIdentity(
    InstallationId Id,
    InstallationRole Role,                 // configured role before enrollment, certified role after
    string Scope,
    string DisplayName,                    // "CAIXA-02" — display only, never used for trust
    InstallationStatus Status);

/// Ambient identity of THIS process' installation. Published by YFex.Installations at startup (before hosted services start)
/// and again when enrollment changes it. Logging, Audit, Privacy, Messaging read it without referencing the library.
public static class LocalInstallation
{
    public static InstallationIdentity? Current { get; }                // null = YFex.Installations not used in this app
    public static event Action<InstallationIdentity>? Changed;          // NotEnrolled → Enrolled, renewal of role/scope, revocation
    public static void Publish(InstallationIdentity identity);          // called by YFex.Installations only (documented, like ActorContext.SetSession)
}
```

Moved from offline 05 §2 (⚑I9), format unchanged except `KeyId`:

```csharp
// YFex core, namespace YFex.Installations — the header of everything central signs for installations
public readonly record struct BundleKind(string Name);                  // app-extensible ("installations.roster", "rules.data", …)
public readonly record struct BundleAudience(BundleAudienceKind Kind, string Value);   // Installation id text | scope text | tenant
public enum BundleAudienceKind : byte { Installation = 1, Scope = 2, Tenant = 3 }
public readonly record struct BundleHeader(BundleKind Kind, string Stream, long Version, BundleAudience Audience,
    DateTimeOffset IssuedAt, DateTimeOffset ValidUntil, KeyId KeyId, Hash256 PayloadHash);
```

Well-known kinds **owned here**: `installations.roster`, `installations.revocations`. Kinds owned elsewhere are declared by their
owners as static properties (`OfflineBundleKinds.Manifest`, `AuthorizationBundleKinds.Snapshot`, `RuleBundleKinds.Data`…), like
`PermissionKey` catalogs. `SignedBundle` (header + payload + signature) and `BundleCheck` are library types (05 §3), because only the
library verifies.

### 4.1 Union or enum? (user, 2026-10-07: "union only if necessary")

Rule used: a **union** when the cases carry their own data or a consumer must treat each case differently (exhaustive switch);
an **enum** when the set is closed, carries no data and travels as one byte on the wire.

| Type | Growth | Data per case | Behaviour per case | Choice |
|---|---|---|---|---|
| `InstallationStatus` | low | **yes**: certificate validity, who paired and until when, when and by whom revoked | **yes**: Offline flags provisional envelopes and refuses approvals; verifiers accept signatures made before `RevokedAt`; renewal reads the validity | **union** — otherwise these become nullable properties valid only in some states |
| `InstallationRole` | very low (authority levels; a new one is a deliberate library change: URN, EKU, roster rules) | none | yes, but inside the library (broker always listens, gets server EKU, default coordinator) | **enum** — written as one byte in beacons, roster and URN text |
| `FriendRelation`, `FriendScope`, `MeshMode`, `RosterEntryStatus`, `PeerRefusal`, `InstallationRevokedBy` | low | none | read in one place | **enum** |
| `PeerCheck`, `EnrollmentResult` | — | yes | yes | **union** (already, cold path) |

**As implemented in I0:** the native union also admits an unset/default case (`null` in an exhaustive switch). Consumers must
handle that case explicitly and must not interpret it as enrolled. A metadata value is never proof of enrollment.

Cost: the native union stores the case as `object` (`csharp-union-usage`). `LocalInstallation.Current` changes a few times in an
installation's life and is read when a library starts or an envelope is signed, so the allocation does not matter here.

```csharp
// ✍ Offline, before signing an envelope
OfflineFlags flags = LocalInstallation.Current!.Status switch
{
    InstallationEnrolled     => OfflineFlags.None,
    InstallationProvisional  => OfflineFlags.ProvisionalInstallation,
    InstallationNotEnrolled  => throw new InvalidOperationException("not enrolled: cannot decide offline"),
    InstallationRevoked      => throw new InvalidOperationException("revoked"),
};
```

**Deliberately not in core:** the URN format, the state file, keys, certificates, the roster, friends, channels — they need logic,
crypto or I/O (⚑K2 rule "types only").

### 4.1 What other libraries read

| Reader | Reads | Instead of |
|---|---|---|
| Logging `CreateNode` | `LocalInstallation.Current?.Id` → `NodeIdentity.InstanceId`; `DisplayName` → `Host` stays the machine name | its own file (07 §1) |
| Audit engine | `Current.Id` → `NodeId`; signs node-chain heads with the evidence key (through the library) | random per process |
| Privacy consent | `Current.Id` → `ConsentEntry.Installation` | free string |
| Authorization.Public (leaf) | core `InstallationId` in `ApprovalToken` v2 body for device/site tokens | string `KeyId` |
| Offline | `LocalInstallation` + the library's keys, trust and channels | its own core types (offline Phase 1 shrinks) |
| Messaging.Rpc server | `CallContext.Installation` (filled by `YFex.Installations.Rpc`) | nothing (⚑I11) |

## 5. Local state on disk

Directories per OS and scope are in [09](09-platforms.md) §2 (Windows shown here). The core never knows a path (⚑I21).

```
%LOCALAPPDATA%\YFex\Installations\{app}\      (user scope: desktop app)
%PROGRAMDATA%\YFex\Installations\{app}\       (machine scope: broker service, kiosks)
  installation.state     sealed value v3 with the ring "device/storage" (crypto, AddYFexStorageEncryption's ring):
                         id, configured role, status, scope, display name, certificate (DER) + chain, pinned tenant root,
                         bundle-signing public keys, roster version floor, hint key id
  CNG keys               "YFex.Installations.{app}.installation/transport" and ".../evidence" (TPM first; not files)
                         (macOS: Keychain/Secure Enclave; Linux v1: software keys sealed under the root key — 09 §2)
received bundles + floors → the app's IKeyValueStore with AddYFexStorageEncryption (⚑I4), not this directory
```

- **Tamper**: a modified state file fails to open (`OpenResult` not `Opened`) → the installation reports `StateUnreadable` and asks
  for re-enrollment; it never silently trusts a replaced root.
- **Disk image copied to another PC**: hardware-bound keys can prevent the clone from opening state or signing. Software key
  stores do not provide that guarantee if the private keys and their root protection are copied together. The enrollment record's
  actual `KeyProtection` informs central policy (⚑O5); software protection must not be described as clone-proof.
- `{app}` = entry assembly name by default (`farma-pdv`, `yfex-site-broker`), so two apps on one PC are two installations.
