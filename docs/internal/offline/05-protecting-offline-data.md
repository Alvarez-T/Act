# 05 — Protecting offline data: signed bundles, device-bound encryption, trusted clock, offline login

## 1. Principle: three tools, three different jobs

| Tool | Protects against | Does **not** protect against |
|---|---|---|
| **Signature by central** (signed bundle) | editing or forging what central sent: permissions, rule tables, budgets, manifest | someone with admin rights making the app *ignore* the data. Only central re-validation catches that |
| **Device-bound encryption** (TPM/CNG/DPAPI) | a stolen disk, another Windows user, casual file browsing | an administrator running code as the app user on the running machine |
| **Central re-validation + evidence** (envelopes, installation chains, findings) | lies about what happened offline, stale or rolled-back data, revoked people acting | nothing is prevented *in the moment*. It is detected, flagged and attributed afterwards |

A device can always lie while it is offline. The design keeps the lie **small** (budgets, caps, short validity), **attributable**
(installation and user signatures) and **visible** (findings at sync). Anything that must not happen even once stays `CentralOnly`
([06](06-offline-authority.md) §3).

### Threats (offline-specific; T-numbers continue audit 06's style)

| # | Actor / event | Attack | Protection | Residual |
|---|---|---|---|---|
| O-T1 | cashier with file access | raise their own permissions in the cached snapshot | signed snapshot (§2) | none |
| O-T2 | same | restore an older table with a bigger discount cap | version floor + signed header (§6) | a disk-image restore is not prevented; it is detected at sync (§6) |
| O-T3 | same | set the clock back to keep an expired snapshot or approval alive | trusted clock (§4) | at most the time spent powered off with the clock set back; budgets bound the damage |
| O-T4 | dismissed employee | keep working offline | snapshot validity ≤ 72 h; revocation bundle reaches the site broker instantly when online; findings at sync | up to the validity window if the site was offline before the revocation |
| O-T5 | thief | read customer data or credentials from a stolen terminal | storage key wrapped by TPM/CNG (§3); credential verifiers only on site brokers for PINs (§5) | software-key installations: a determined attacker with the disk and the Windows account password |
| O-T6 | LAN attacker | impersonate the site broker, sniff or inject | mutual TLS pinned to the tenant CA ([03](03-trust-enrollment-pairing.md) §4) | none |
| O-T7 | dishonest manager owning the site broker | site-signed approvals for friends; fake site receipts | caps and budgets; every session, approval, slice and receipt signed by the broker's evidence key and re-validated; the broker runs no app code, so it cannot invent sales, only coordinate them; manager actions audited | bounded fraud within the site's budget, attributed to the manager |
| O-T8 | compromised terminal | fabricate envelopes | evidence signatures + installation sequence + plausibility checks at central | bounded by the terminal's budget slice; revoke |
| O-T9 | flaky link | duplicate or reordered replays | idempotency key enforced at central; installation sequence order ([08](08-sync-reconciliation-conflicts.md)) | none |

## 2. Signed bundles: one format for everything central sends for offline use (⚑O8)

> **2026-10-05 (⚑I9, confirmed 2026-10-07):** the format below, the verifier and the version floors move to YFex.Installations (core
> `BundleKind`/`BundleHeader`/`BundleAudience` in namespace `YFex.Installations`; `KeyId` becomes the core type). Offline keeps
> its kinds (manifest, allowances, verifiers, site authorization) and payloads (installations 05 §3).
> **2026-10-14: built** (Installations I1): `SignedBundle` (signature = core `DetachedSignature`), `BundleVerifier`, `BundleStore` (floors,
> re-verified at every load), `BundlePublishers` (each kind verifies only against its publisher's keys), `InstallationBundleKinds`
> (roster, revocations). The code blocks below are the original proposal; the source is normative. Offline adds `OfflineBundleKinds`
> (Phase 1) and publishes through central's `PullableBundles(...)` (Phase 2).
> **2026-10-15 (⚑O11, ⚑O14, ⚑O18 confirmed):** Offline's kinds verify only against **Offline's publisher keys** (a `SigningKeyRing` at
> central), bound with `BundlePublishers.Kind(...)`. Installations learn those public keys from the tenant-signed `offline.publishers`
> bundle (§2.3). Rule data (`rules.data`) is signed once per published version in the Rules.Admin publish pipeline through the
> `IRuleDataSigner` hook, which `YFex.Offline.Rules` implements; `RuleSetSync` and `KeyValueRuleDataCache` verify through
> `IRuleDataVerifier` (§2.1). Terminals sign **delivery receipts** for the versions they accepted (06 §8, ⚑O18).

```csharp
// YFex core, namespace YFex.Offline (contract) — canonical binary encoding, never JSON (same rule as audit)
public readonly record struct BundleKind(string Name)          // app-extensible, like PermissionKey; well-known values below
{
    public static BundleKind Manifest { get; }                 // "sites.manifest"
    public static BundleKind Revocations { get; }              // "installations.revocations"
    public static BundleKind PermissionSnapshot { get; }       // "authorization.snapshot"      — one user (Authorization 05)
    public static BundleKind SiteAuthorization { get; }        // "authorization.site"          — all staff of a site, for the site broker
    public static BundleKind CredentialVerifiers { get; }      // "authentication.verifiers"     — offline login (§5)
    public static BundleKind RuleData { get; }                 // "rules.data"                   — one published rule-data version (fixes F6)
    public static BundleKind Allowance { get; }                // "offline.allowance"           — offline budgets (06 §4)
}

public readonly record struct BundleHeader(
    BundleKind Kind,
    string Stream,                 // monotonic sequence name, e.g. "rules.data/Venda/descontos/0=farmaviva/1=loja-centro"
    long Version,                  // strictly increasing per stream
    BundleAudience Audience,       // one installation, one site, or the tenant
    DateTimeOffset IssuedAt,       // central time
    DateTimeOffset ValidUntil,     // central time; DateTimeOffset.MaxValue = no expiry (rule data, 05 §7)
    string KeyId,
    Hash256 PayloadHash);          // SHA-256 of the payload bytes

public sealed record SignedBundle(BundleHeader Header, ReadOnlyMemory<byte> Payload, ReadOnlyMemory<byte> Signature);
// Signature = ECDsa P-256 (IEEE P1363, 64 bytes) over "yfex.bundle.v1|" + canonical(Header)
```

```csharp
// (sketch) the one verifier every consumer uses — at receipt AND at every load from disk
public enum BundleCheck : byte
{
    Valid, BadSignature, UnknownKey, WrongAudience, NotYetValid, Expired, OlderThanFloor, ConflictingSameVersion, Malformed
}
public BundleCheck Verify(SignedBundle bundle, BundleAudience self, TrustedInstant now);
```

### 2.1 Before/after: rule data on a terminal (fixes F6)

```csharp
// Before — RuleSetSync stores whatever arrived; the cache trusts whatever is on disk
await cache.SaveAsync(ruleSet, records, ct);                                   // RuleAdminSetup.cs:87 — unsigned
var records = await cache.LoadAsync(ruleSet, ct);                              // edited or rolled-back data is applied silently

// After — central signs each published version once; the device stores the signed bytes and re-verifies at load
var bundle = await central.GetRuleDataBundleAsync(ruleSet, afterVersion, ct);  // RuleAdmin.Queries.GetDataVersions returns SignedBundle[]
switch (bundles.Verify(bundle, self, clock.GetTrustedNow()))
{
    case BundleCheck.Valid: host.Apply(RuleDataCodec.Decode(bundle.Payload)); floors.Raise(bundle.Header); break;
    case BundleCheck.OlderThanFloor: findings.Raise(OfflineFinding.RollbackAttempt(bundle.Header)); break;   // kept local, shipped at sync
    default: findings.Raise(OfflineFinding.RejectedBundle(bundle.Header, check)); break;
}
```

The developer writes nothing new: `AddYFexRuleSetSync(deviceCache: true)` keeps its signature. Signing happens in the Rules.Admin
publish pipeline at central. Verification happens in `RuleSetSync` and `KeyValueRuleDataCache`.

### 2.2 Cost

ECDsa P-256 verification takes ≈ 60–120 µs. Bundles are verified when received and when loaded from disk (startup, cache miss), never
per operation. Decoded bundles live in memory, keyed by `(stream, version)`.

### 2.3 Keys at central

`IBundleSigner` holds a key ring (current key + previous keys still valid for verification). Its private keys are wrapped by the
YFex.Cryptography `IRootKeyProvider` (cryptography 06 §3), and an HSM/KMS seam can come later. Signatures are core `DetachedSignature`s made with
YFex.Cryptography `SignedPayload` (domain `yfex.bundle.v1`). Public keys reach installations at enrollment and in every manifest. An installation
accepts a new signing key only if a bundle signed by a key it already trusts announces it. Rotation is therefore a chain, and a
stolen file cannot introduce a key.

> **2026-10-15 (Phase 2):** the chain is concrete. The tenant's bundle key (from enrollment, Installations) signs one
> **`offline.publishers`** bundle per tenant listing Offline's publisher public keys (current and previous ring keys). Installations
> verify it like a roster, keep it with floors, and read Offline's keys from it (`OfflinePublisherKeys`); manifests, allowances and
> rule data then verify only against those keys. Rotating Offline's ring = publishing a new `offline.publishers` version.

## 3. Device-bound encryption at rest (fixes F1, F2; ⚑O9) — on YFex.Cryptography since 2026-10-04

```csharp
// Before — key "derived" from a randomized DataProtection output (DataProtectionKeyProvider.cs:26-33): new key every start (F1)
var seed = protector.Protect(Encoding.UTF8.GetBytes("yfex-storage-master-key-seed-v1"));
_cachedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, seed, 32, salt: null, info: …);

// After — ✍ the terminal's composition root (YFex.Persistence.Encryption over YFex.Cryptography)
services.AddYFexSqliteStorage(AppPaths.Data("farma.db"));
services.AddYFexStorageEncryption(storage => storage.StoreName = "farma");
// equivalent explicit form, when the app configures cryptography itself:
services.AddYFexCryptography(cryptography => cryptography
    .UseCngRootKey()                                         // TPM first, software KSP otherwise (YFex.Cryptography.Windows)
    .StoreKeyRingsInFiles(AppPaths.Data("keys")));
```

What happens underneath (cryptography 06, 09 §2):

- **The storage key is a key ring**, `device/storage`: a random key generated **once**, kept only sealed in a ring file whose store
  key is wrapped by the root key provider. Every start unwraps the same key (≈ 75 ms once with a TPM): **F1 fixed**. The ring rotates
  yearly; old keys keep opening their values.
- **Root key provider on Windows:** `CngRootKeyProvider`, now in **YFex.Cryptography.Windows** (not YFex.Offline.Windows): RSA-OAEP-SHA256
  under a non-exportable persisted CNG key, TPM when available, the software KSP otherwise. No package. On Linux/macOS the default is
  a 0600 root-key file (with a startup warning) until Keychain/libsecret providers exist; servers use an environment root key.
  ASP.NET Core DataProtection is no longer used (no package).
- **Associated data (fixes F2):** every value is sealed value v3 (cryptography 05 §1): version `0x03`, algorithm, key id, nonce,
  ciphertext, tag, with associated data = header + **length-prefixed** context `"yfex.store.v1" [store, key]`. A value copied to another
  key or store fails authentication. The `'|'`-joined text of the interim layout could collide when a store name contained `'|'`; the
  length prefixes cannot.
- **Compatibility with offline Phase 0:** its `.storage-key` file (`[0x01][CNG blob]`, CNG key `YFex.Persistence.StorageKey.<app>`) is
  **imported once** as the first key of `device/storage` (fixed id `EncryptedKeyValueStore.LegacyStorageKeyId`), so values in layout
  `0x02` (associated data `"yfex.store|" + store + "|" + key`) and `0x01` (none) still open, report `OpenedNeedsReseal`, and are
  re-sealed as `0x03` on their next write. The old file is left in place. DataProtection-wrapped `.storage-key` files (interim, non-Windows)
  are not read any more: the import fails loudly instead of creating a new key.
- **Unreadable values are never "missing":** `GetAsync` throws `StorageIntegrityException` for a value that cannot be opened
  (tampered, moved, unencrypted, unknown key); `TryGetAsync` returns the status as a value; the first failure of each kind is logged as
  `StorageKeyMismatch`. A value whose key was destroyed (crypto-shredding) reads as missing (`Erased`).
- **Scope partitions** stay as today (`ScopedKeyValueStore`). One store per scope, one storage-key ring per installation. Register the
  encryption decorator before the scope decorator (11 §5 follow-ups).

## 4. Trusted clock (fixes the clock parts of F5, F7, F10; ⚑O10 → YFex.Time)

> **2026-10-05:** the trusted clock is the shared library **YFex.Time** (`../time/README.md`, ⚑T1–T12). The types below are the
> core `YFex.Time` ones; YFex.Offline no longer defines its own `TrustedInstant`/`TimeSource`/`TrustedTimeProvider`. What stays
> in YFex.Offline is the **site** part: the broker as the shop's time server, `UseSiteAnchor()`, the site time table and peer
> comparison (time README §8, tasks in 11 Phase 4).

```csharp
// core YFex.Time (types) — the library registers TrustedTimeProvider as the TimeProvider
public readonly record struct TrustedInstant(DateTimeOffset Value, TimeSpan Uncertainty, TimeSourceKind Source,
    TimeSpan SinceLastAnchor, TimeFlags Flags);

// every TimeProvider consumer is protected (⚑T10); decisions ask for the details (⚑T8)
TrustedInstant now = time.GetTrustedNow();
```

How "now" is computed (details: time README §2.4):

```
anchor      = best answer of central (TimeReply in the health call), the site broker (same, over mTLS) or NTP; offset NTP-style
now         = anchor time + monotonic time elapsed since the anchor          (synchronous, ~45 ns; changing Windows' clock has no effect)
highWater   = persisted every 60 s and at shutdown, with "an authenticated anchor was active"
start       = max(wall clock, highWater); wall clock < highWater − 2 min → RollbackDetected
check       = every 10 s: wall clock behind our clock by > 2 min → RollbackDetected (event, every envelope flagged)
```

- **The clock can move forward on its own, but never backward.** Setting Windows to Sunday when it is Monday does not bring an expired
  snapshot back. `now` keeps counting with the monotonic clock. Only an authenticated anchor (central, the broker) may step it back,
  and that raises an event (fixes an accidental "2030").
- **Unauthenticated sources (NTP, peers) never decide.** A snapshot or a device approval is usable with an authenticated anchor, or
  after an offline start whose stored high-water came from authenticated time and shows no rollback (⚑T11: a standalone PC that boots
  without internet can still sell).
- **Residual:** with the machine powered off *and* the hardware clock set back, the powered-off time is not counted. Time is a soft
  bound. Budgets (counts and amounts) are the hard bounds ([06](06-offline-authority.md) §4).
- Envelopes carry the raw wall clock and the whole `TrustedInstant` (value, uncertainty, source, since-anchor, flags). Central
  recomputes corrected times (logging 05 §5, audit 05 §8) and checks plausibility.

### 4.1 Site time: the broker serves and compares time (⚑T12)

> **2026-10-05 (⚑I19, ⚑T13, confirmed 2026-10-07):** `UseSiteAnchor()` and a new `UsePeerTime()` ride YFex.Installations' peer
> channel and live there; the site time table and its findings stay here. T13 lets anchored friends be an unauthenticated fill-in
> source (installations 06 §2.3).
> **2026-10-14: built** (Installations I4): the `time` channel carries `TimeReply` between friends (not `Sites.Queries.Health`);
> `UseSiteAnchor()` and `UsePeerTime()` are in YFex.Installations; T13 is in YFex.Time `TimeSelection`. So two bullets below changed:
> the broker's reply travels on the `time` channel, and with the broker down **anchored peers do fill in** (flagged
> `Unauthenticated`, never `IsUsableForSecurity`) instead of being evidence only. Still Offline's (Phase 4): the site time table, its
> findings and resync requests. Still open in YFex.Time: peer-raised `RollbackDetected` and "peers strictly after NTP".

- The **broker** runs YFex.Time anchored to central (+ NTP) and answers `TimeReply` inside `Sites.Queries.Health` (04 §4) through
  `TrustedTimeProvider.CreateReply(...)`: its trusted time, its own uncertainty, flags and stratum + 1. No new port or protocol.
- **Terminals** add `UseSiteAnchor()` (YFex.Offline): an authenticated `ITimeSource` (mTLS). They inherit the broker's uncertainty and
  flags (a broker that never reached central cannot make a terminal "anchored"), prefer central when both answer, and use the broker
  when the internet is down. The broker never anchors to a terminal (stratum rule).
- Every terminal **reports** its clock in the health probe (`InstallationTimeReport`: trusted instant, device clock, round trip). The
  broker keeps the **site time table** and raises site findings: `time.device-skew` (Windows clock off by > 2 min), `time.rollback`,
  `time.disagreement` (trusted clocks disagree beyond their uncertainty); it tells lagging terminals to resync. Findings go through
  the review queue and the audit relay to central.
- With the broker down, terminals compare **peers** through LAN beacons. Peers are evidence only: they raise `SourcesDisagree`, may
  raise `RollbackDetected` when ≥ 2 trustworthy peers agree this device is behind, and never move the clock.
- The broker does **not** set Windows clocks. Serving plain SNTP so Windows Time on terminals follows the broker is optional and
  needs a decision (time README §8.1).

**Example:** Ana's permission snapshot is valid until Monday 14:00 (central time). At 13:30 she sets Windows to Sunday 09:00. Within
10 s the terminal flags `RollbackDetected` and keeps `now = Monday 13:30 + elapsed`; its next health probe reports it, and the
manager's screen shows "PDV-02: relógio do Windows voltou 1 dia". At 14:00 the snapshot expires anyway. Her 13:30–14:00 sales are
flagged `ClockRollback`, which is a high-severity finding at sync.

## 5. Offline login (fixes F8; ⚑O12)

```csharp
// Before — the login screen calls the identity provider; with no internet the shop cannot open
var principal = await identityProvider.SignInAsync(user, password, ct);       // throws → nobody can sell

// After — one call; the route follows the connectivity mode
var outcome = await signIn.SignInAsync(new SignInRequest(OperatorCode: "0142", Secret: password), ct);
outcome.Switch(
    signedIn => shell.Start(signedIn.Session),                                // Session carries user, installation, scope, snapshot version, provenance
    notAvailableOffline => dialog.Show("Primeiro acesso neste caixa precisa de conexão"),
    lockedOut => dialog.Show($"Bloqueado por {lockedOut.Remaining:mm\\:ss}"),
    rejected => dialog.Show("Código ou senha inválidos"));
```

| Mode | Who verifies | Verifier source |
|---|---|---|
| `Central` | identity provider (as today). Afterwards the terminal refreshes the user's signed snapshot and its **cached verifier** | — |
| `Site` | the site broker, over mTLS | `CredentialVerifiers` bundle for the **site's staff**, signed by central and stored only on site brokers |
| `Standalone` | the terminal | its **cached verifier**, created at the user's last online login on this terminal. Max 20 users, ≤ 7 days old (trusted clock) |

- **Verifier:** `PBKDF2-SHA256(secret, random 16-byte salt, 600 000 iterations)` via BCL `Rfc2898DeriveBytes.Pbkdf2`, compared with
  `CryptographicOperations.FixedTimeEquals`. Stored encrypted (§3).
- **Site-wide verifiers need a secret central knows the verifier of.** When the identity provider is external, central never sees the
  password. Retail already solves this with an operator code + offline password set in YFex. Central keeps only its PBKDF2 verifier
  and ships it in the site bundle.
- **Short PINs** (4–6 digits) fall to offline brute force if a verifier file is stolen. They are therefore accepted only when the
  **site broker** verifies them (online rate limit + lockout, verifiers wrapped by the TPM-backed storage key). Standalone terminals
  require the full password (⚑O12).
- **Lockout:** 5 failures → 5 min, doubling. Counters are kept by the verifying installation and shipped as findings.
- **No snapshot, no offline login.** A user without a valid signed snapshot (terminal) or site authorization entry (site broker) gets
  `notAvailableOffline`. An app can define a minimal **offline profile** (e.g., "cashier basics") for that case. It is off by default.
- **The session** (`OfflineSession`: user, installation, scope, snapshot version, trusted start, max 12 h) is signed by the verifying installation.
  Every envelope created under it references it, which is how central attributes offline actions to people (F4 fix, [08](08-sync-reconciliation-conflicts.md) §2).

## 6. Replay, rollback and provenance

| Object | Replay / rollback defense | Detection when the defense is bypassed |
|---|---|---|
| Bundles | per-stream **version floor** persisted on the installation: a lower version is rejected. The same version with a different hash is rejected as a security finding | central knows what it delivered to each installation (delivery receipts). An envelope citing a version older than one the installation already acknowledged is a **rollback finding** |
| Envelopes | idempotency key (deduplicated at central), gap-free **installation sequence**, evidence signature | sequence gaps or forks (two different envelopes with the same sequence) mean an installation state rollback, such as a restored disk image. High-severity finding, installation marked for review |
| Approval tokens | single use per fingerprint on the verifying installation; central deduplicates token ids across installations | the same token used on two operations → finding |
| Sessions | bound to one installation (mTLS + installation id in the token), max 12 h | a session used from another installation → rejected |

Every offline decision carries one shared value. Audit, Logging and Rules read it instead of each inventing a flag (alignment, [01](01-current-state-and-inventory.md) §3):

```csharp
// YFex core, namespace YFex.Offline
public sealed record OfflineProvenance(
    ConnectivityMode Mode,             // Site or Standalone
    InstallationId Installation,                       // where the operation was created
    InstallationId? CoordinatedBy,             // the site broker that coordinated it (session, budget, claims), when Site
    long SnapshotVersion,              // permission data the decision used
    TrustedInstant DecidedAt,
    OfflineFlags Flags);               // ClockRollback, ProvisionalInstallation, StaleRuleData, CertificateGrace, DeviceApproval, SiteApproval, OfflineProfile…
```

## 7. Validity defaults (⚑O11 ✅ 2026-10-15, per site in the manifest's `OfflineLimits`)

| Data | Default | After it |
|---|---|---|
| Permission snapshot (per user) | 72 h (= ⚑Z9) | user cannot log in offline; open sessions end at their 12 h limit |
| Site authorization bundle (site broker) | 72 h | site broker stops verifying logins; terminals fall back to their own snapshots |
| Credential verifiers | 7 days since last online login (terminal) / bundle `ValidUntil` (site) | offline login refused for that user |
| Allowances (budgets) | 24 h, renewed while online | the budget is gone; budgeted operations need central |
| Rule data | **no hard expiry** (a pharmacy must keep selling with yesterday's prices); `StaleRuleData` flag after 7 days; per rule set `MaxOfflineAge` can block. Example: a price table capped by the regulated maximum consumer price (PMC) published by CMED | flag, or block that rule set when `MaxOfflineAge` is set |
| Manifest, revocations | no expiry; staleness flag after 7 days | flag |
| Installation certificate on the LAN | its validity + 7-day grace | standalone only |

**Confirmed 2026-10-15 (⚑O11, amended by the user): one mechanism for built-in and custom limits.**

```csharp
// YFex.Offline — built-in keys (default · hard bounds); the source is normative
public static class OfflineLimitKeys
{
    public static OfflineLimit<TimeSpan> PermissionSnapshotValidity { get; }  // "offline.snapshot.validity"     72 h · 1 h–14 d
    public static OfflineLimit<TimeSpan> SiteAuthorizationValidity { get; }   // "offline.site-authorization.validity" 72 h · 1 h–14 d
    public static OfflineLimit<TimeSpan> CredentialVerifierValidity { get; }  // "offline.verifiers.validity"      7 d · 1 h–30 d
    public static OfflineLimit<TimeSpan> AllowanceValidity { get; }           // "offline.allowance.validity"     24 h · 1 h–7 d
    public static OfflineLimit<TimeSpan> MaxOfflineAge { get; }               // "offline.rules.max-age"           7 d · 1 h–90 d (flag)
    public static OfflineLimit<TimeSpan> CertificateGrace { get; }            // "offline.certificate.grace"       7 d · 0–30 d
}

// ✍ App — its own key, same mechanism (values: TimeSpan, int, long, decimal, bool, text)
public static class FarmaLimits
{
    public static readonly OfflineLimit<decimal> MaxOfflineDiscount =
        OfflineLimit.Decimal("farma.desconto.offline.maximo", defaultValue: 50m, minimum: 0m, maximum: 500m);
}
```

| Source (first one that has a value wins) | Where it is set | Checked |
|---|---|---|
| 1. per-site admin value | `Sites.Commands.Configure` at central (`sites.configure`), signed into the manifest | bounds checked **when saved**; out of bounds → `Error.Validation`, nothing published |
| 2. app default | `AddYFexOfflineCentral(c => c.Limits(l => l.Default(key, value)))` | bounds checked at start-up |
| 3. library default | the key's declaration | — |

The manifest carries the **resolved** values, so terminals read `manifest.Limits.Get(OfflineLimitKeys.MaxOfflineAge)` and never apply
precedence themselves. A key the terminal does not know is kept and ignored (an older app version). Limits that depend on the
situation (hours offline, mode, snapshot age) are **rules**, not limits: the optional package `YFex.Offline.Rules` fills an
`OfflineFacts` situation field via `[SituationSource]`, admins edit the rule's parameters and tables in Rules.Admin, and they reach
terminals as signed `rules.data` bundles. Central may also derive a built-in value per bundle from a rule set
(`IOfflineLimitProvider`, consulted between the admin value and the app default). YFex.Offline never references YFex.Rules.
