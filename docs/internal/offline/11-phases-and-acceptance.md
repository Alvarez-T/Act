# 11 — Phases, acceptance and implementation log

> **Plan update 2026-10-14:** the phases below were re-cut after YFex.Cryptography (Phases 0–3), YFex.Time (T0–T3, T13),
> YFex.Networking (Phases 0–6) and YFex.Installations (I0–I4) were built. Everything those libraries now own was removed from
> Phases 1–5 and is listed in §2 with its status. What remains here is the offline **operating model**: modes, authority,
> envelopes, custody, budgets and claims, the site feed, offline login, reconciliation and the broker services. The superseded
> plan is in git history and in `C:\Temp\offline-backup\plan-update-2026-10-14\`.

## 1. Working rules (every phase)

- `C:\Dev\YFex\YFex` is now a git repository (the docs and core live there); sibling repos (`YFex.Installations`, `YFex.Time`, …) are
  their own git roots. Commit nothing unasked. For files outside a git root, copy them to `C:\Temp\offline-backup\<phase>\` with the
  same relative path before editing.
- Check modification times before editing shared files. Owners: Authorization consolidation (Cqrs, Messaging.Rpc CallContext, Rules
  approvals, snapshots), Installations (identity, keys, friends, peer channels, relay, bundles, pairing), Time (clock rules), Logging
  and Audit (their shippers and relays), Privacy (core, `CLAUDE.md`). Coordinate through their docs' implementation logs; when no
  session is running, make additive changes only and log them in the owner's §5, as the Installations sessions did.
- Use the RoselYn MCP tools for C# exploration and `dry_run_compile` before writing non-trivial edits. Never guess an API from these
  design docs: the source of YFex.Installations, YFex.Time and YFex.Cryptography is the reference.
- No new NuGet package without asking the user. Everything here is BCL + YFex libraries (+ the ASP.NET Core shared framework only on
  central).
- Record test counts before and after in §5, and update the memory file `yfex-offline.md`.
- Native AOT smoke must stay at 0 warnings for every touched library that has one.

## 2. Dependencies on other designs

Status as of 2026-10-14 (checked in each design's implementation log).

| This design needs | From | Status | Phase here |
|---|---|---|---|
| `IRootKeyProvider`, key rings, sealed value v3, storage encryption | YFex.Cryptography Phase 0 | ✅ 2026-10-04 (offline Phase 0 moved onto it) | 0 (done) |
| core `Hash256`, `KeyId`, `DetachedSignature`, `SignatureAlgorithm`, `ICanonicalValue<T>` | YFex core (cryptography ⚑K2) | ✅ 2026-10-04 | 1 |
| `CanonicalWriter`, `MessageAuthentication`, `PasswordHashing` | YFex.Cryptography Phases 0–1 | ✅ 2026-10-04 | 1 (codecs), 3 (offline login) |
| `SignedPayload`, `SignatureVerifier`, `IPublicKeyDirectory`, `RecipientSealing` | YFex.Cryptography Phase 2 | ✅ 2026-10-05 | 1 (envelopes, receipts), 3 (sealed personal fields) |
| `HashChain` (installation sequence), `CertificateAuthority`/`CertificateTrust` | YFex.Cryptography Phase 3 | ✅ 2026-10-05 (the CA and trust are used by Installations, not here) | 1 (sequence) |
| CNG-held key-agreement key (`IRecipientKey`) | YFex.Cryptography (crypto 13 §6) | ⏳ not built | only Fiscal's offline NFC-e A1 delivery; not an Offline blocker |
| `TrustedTimeProvider`, `TrustedInstant`, `TimeReply`, `CreateReply`, central anchor | **YFex.Time** T0–T2 (⚑T1–T12, ⚑O10) | ✅ 2026-10-05 | 1 (adopt) |
| anchored peers as unauthenticated fill-in (⚑T13) | YFex.Time (built by the Installations I4 session) | ✅ 2026-10-14; "peers strictly after NTP" and peer-raised `RollbackDetected` still open (time §10) | 4 |
| `IServerConnection`, `ServerConnectionChangedEvent`, core `INetworkConditions`, `EndpointHealth`, `Hysteresis` | **YFex.Networking** Phases 0–1 (⚑N4) | ✅ 2026-10-07 | 1 (`IConnectivity`) |
| beacons, `InMemoryNetwork`, `NetworkFingerprint` | YFex.Networking Phases 2/5 | ✅ 2026-10-13 (used through Installations) | 1 (harness) |
| core `InstallationId`, `InstallationRole`, `InstallationStatus`, `InstallationIdentity`, `LocalInstallation`; core `BundleKind`/`BundleHeader`/`BundleAudience` | **YFex.Installations** I0 (⚑O1, ⚑I9) | ✅ 2026-10-07 | 1 |
| `IInstallationKeyStore` (transport + evidence keys, TPM first), `IInstallationKeyDirectory`, certificate profile, `SignedBundle`, `BundleVerifier`, `BundleStore`, `BundlePublishers`, version floors, roster, revocations, `InstallationTrust` | YFex.Installations I1 (⚑O4, ⚑O5, ⚑O8) | ✅ 2026-10-07 | 1–3 |
| tenant CA operation, enrollment codes, enrollment + renewal, central registry (in memory), installation assertion on RPC (`UseInstallationAssertion`, `WithInstallation()`) | YFex.Installations I2 (⚑I11) | ✅ 2026-10-08, reworked 2026-10-13; SQL registry not built | 2 |
| `PeerEndpoint` (mTLS 1.3), `IInstallationFriends`, `FriendSet`, discovery, `InstallationAnnouncementSigner/Verifier`, `InstallationHarness` | YFex.Installations I3 (⚑O6, ⚑O25, ⚑O30) | ✅ 2026-10-14 (TPM keys cannot be a Schannel **server** credential: ⚑I25; Linux/macOS not run) | 1 (harness), 4 |
| `time` channel + `UseSiteAnchor()`/`UsePeerTime()` (⚑I19), `relay` channel (`IInstallationRelay`, central `IRelayItemConsumer`), `bundles` gossip, sharing scopes in the roster | YFex.Installations I4 (⚑O19 → ⚑O26, ⚑O27–O29) | ✅ 2026-10-14 | 3 (envelopes over relay), 4 |
| SAS pairing, site issuing CA, provisional status, hardware-key policy, rotation drills, chaos suite | YFex.Installations I5 (⚑O7, ⚑I14) | ⏳ next in Installations | 5 |
| core `ActorContext`/`ActorIdentity` (⚑Z3) | Authorization Phases 1–3a | ✅ 2026-10-04 | 3 |
| `PermissionSnapshot` moved out of Rpc + client `UseServerPermissions()` (⚑Z9) | Authorization 3a | ✅ 2026-10-04 (payload only, online) | — |
| **signed + persisted** `PermissionSnapshot` (bundle kind `authorization.snapshot`) | Authorization 3b | ✅ 2026-10-15 (`YFex.Authorization.Installations`: offline sign-in calls `RestoreAndApplyAsync`; per-site validity from `OfflineLimitKeys.PermissionSnapshotValidity` still to be wired in Phase 3; Offline may move to the generic `PublisherKeySet`/`EndorsedPublisherKeys`/`PublisherEndorsement` of Installations — authorization 09 "Phase 3b") | 2 (signing), 3 (offline start) |
| typed permissions in Rules/approvals (⚑Z2) | Authorization 4a | ✅ 2026-10-05 | — |
| approval tokens v2 (device tokens signed by the installation evidence key, `IInstallationKeyDirectory` as the directory), `.RequireApproval` | Authorization 4b (⚑Z12, installations 07 §4) | ✅ 2026-10-16 (`DeviceApprovalTokenIssuer`, `AddYFexDeviceApprovalVerification()`; `Site` refused as `UnsupportedKind`); Offline supplies `IApprovalOriginAccessor` and the queue behind `Defer`; no as-of approver check yet (⚑O15) | 3 (as of), 4 (`ApprovalTokenKind.Site`) |
| `Shipping(s => s.ThroughFriends())`, audit relay + node-head signing | Logging / Audit sessions over `IInstallationRelay` | ⏳ their sessions | none (no longer an Offline task, ⚑O26) |
| `CallContextClientBuilder.SendHeader` (extra headers next to the token) | Messaging.Rpc (Installations I2 rework) | ✅ 2026-10-13 | 3 |

**What this means for the order (updated 2026-10-17):** Phases 1 and 2 are done; Authorization 3b and 4b are built and the Phase 3
decisions are confirmed, so Phase 3 can start. Phase 4 needs Phase 3 and the ⚑O31 decision (✅ 2026-10-19: peer channels). Phase 5 waits for Installations I5. Installations I5 does
not block Offline 1–4.

## 3. Phases

### Phase 0 — Fix existing defects — ✅ DONE 2026-10-03 (log §5)

| Task | Change | Acceptance |
|---|---|---|
| 0.1 F1 | `Persistence.Encryption`: random 32-byte storage key generated once, stored wrapped (since 2026-10-04 the key ring `device/storage` on YFex.Cryptography) | two-process test: write, restart, read back. Unreadable values logged once as `StorageKeyMismatch` |
| 0.2 F2 | AES-GCM with associated data store + key (sealed value v3 since 2026-10-04; `0x01`/`0x02` still read) | value copied to another key fails; legacy value readable and re-sealed on write |
| 0.3 F3 | `yfex-idempotency` header on every command call (online + replay); server `IIdempotencyStore` (in memory) | same command sent twice after a simulated lost reply → handler ran once, second call gets the stored result |
| 0.4 F4 (interim) | `OutboxEntry` captures `Subject`, `Scope`, sequence at enqueue; replay sends the captured scope. Full fix in Phase 3 | cross-scope replay impossible |
| 0.5 F5 | replay order by sequence; expired entries kept by default | clock set back between enqueues does not reorder |
| 0.6 F12 | single `EncryptionKeySource`; `YFex/CLAUDE.md` map corrected | builds; doc paths exist |

### Phase 1 — Offline contracts, connectivity and the YFex.Offline repo — ✅ DONE 2026-10-15 (log §5)

Removed from the earlier plan (now in other libraries): the core `YFex.Installations` types and `IInstallationKeyStore` (Installations I0–I1),
`BundleKind`/`BundleHeader`/`SignedBundle` and the bundle codec + verifier (Installations I1, core `YFex.Installations`), the
`YFex.Offline.Windows` package (keys are `YFex.Installations.Windows` over `CngSigningKeyStore`), clock types (YFex.Time) and
`IDeviceKeyStore` → directory (Authorization 4b over `IInstallationKeyDirectory`, installations 07 §4).

- Core `YFex/src/YFex/Offline/` (namespace `YFex.Offline`, types only): `SiteTopology`, `UpstreamRoute`, `ConnectivityMode`,
  `IConnectivity`, `Reachability` (on core `EndpointHealth`, 04 §1), `ConnectivityChange`, `OfflineProvenance`, `OfflineFlags`
  (incl. `ProvisionalInstallation`, `ClockRollback`, `Unanchored`), `OfflineEnvelope` header types. Envelopes and provenance carry
  core `YFex.Time.TrustedInstant` and `InstallationId`.
- Repo `C:\Dev\YFex\YFex.Offline` (`offline.slnx`, net11.0, AOT):
  - `YFex.Offline`: `OfflineEnvelope` codec (`CanonicalWriter`), signed with the installation **evidence key** from
    `IInstallationKeyStore` through `SignedPayload` (domain `yfex.offline.envelope.v1`; the TPM signs batches/heads, crypto handoff);
    installation sequence over `HashChain`; `OfflineBundleKinds` (`Manifest`, `Allowance`, `CredentialVerifiers`,
    `SiteAuthorization`) registered with Installations' bundle store and verifier (`Peers(p => p.GossipBundles(...).Publishers(...))`, store
    from YFex.Installations.Persistence); `IConnectivity` implementation composing the
    central `IServerConnection`, `INetworkConditions` and `IInstallationFriends` (is the coordinator a reachable friend, does any friend
    reach central) with `Hysteresis` (04 §4); `AddYFexOffline(o => o.Terminal())` reading identity, keys and trust from
    `AddYFexInstallations` (refuses to start without it).
  - `YFex.Offline.Testing`: harness on top of `InstallationHarness` / `InMemoryNetwork` (several installations on one simulated LAN) +
    YFex.Time's fake clock.
  - `.Tests`, `.AotSmoke`.
- **Acceptance:** golden encodings pinned for the envelope and provenance; an envelope signed on one installation verifies at central
  through `IInstallationKeyDirectory` and fails after one byte changes or under a revoked key (as of `RevokedAt`); the envelope carries a
  `TrustedInstant` and the rollback/unanchored flags; a provisional installation's envelopes carry `ProvisionalInstallation`; a
  not-enrolled installation cannot create one; `IConnectivity` switches Central → Site → Standalone and back in the harness with the
  04 §4 hysteresis (central unreachable, broker friend lost, broker friend back); AOT smoke at 0 warnings.

### Phase 2 — Offline signed data at central — ✅ DONE 2026-10-15 (log §5)

Removed: tenant CA, enrollment codes, installation certificates, revocation and roster bundles, `IBundleSigner`, installation
assertion on the WebSocket upgrade (all Installations I1–I2; the assertion is `UseInstallationAssertion()` + `WithInstallation()`).

- `YFex.Offline.Central`: `SiteManifest` (02 §5, now citing `RosterVersion`; no installation lists), `OfflineLimits`,
  `Sites.Commands.Configure` (`sites.configure`), allowance bundles, delivery receipts. Offline signs its bundles with its own
  publisher key (`SigningKeyRing` + `SignedPayload`); installations bind the Offline kinds to that publisher in `BundlePublishers`, so a
  manifest verifies only against Offline's keys. Central hands them out with the roster through `PullableBundles(...)` on
  `AddYFexInstallationsCentral`, and friends pass them on through `bundles` gossip (audience-checked).
- Rules.Admin publish pipeline signs rule-data versions (kind `rules.data`); `RuleSetSync`/`KeyValueRuleDataCache` verify with
  `BundleVerifier` and floors (fixes F6). Coordinate with the Rules owner.
- Signed `PermissionSnapshot` = **Authorization 3b** (that session's work; Offline only consumes the bundle kind).
- **Decisions confirmed 2026-10-15** (README): ⚑O11 — typed `OfflineLimit<T>` keys, built-ins in `OfflineLimitKeys`, app keys with
  defaults and hard bounds, precedence admin value (manifest) → app default → library default, out-of-bounds refused when saved, rule
  based limits through the optional `YFex.Offline.Rules` (`OfflineFacts` via `[SituationSource]`); ⚑O14 — allowances optional per
  declared `AllowanceKey`, Phase 2 builds grants + the `offline.allowance` bundle; ⚑O23 — the manifest's `SiteFeed` switch per site,
  only for commands the developer marked; ⚑O18 — Phase 2 builds delivery receipts and `MaxOfflineAge`.
- Terminal side: Offline's kinds bound to Offline's publisher (`Peers(p => p.Publishers(...).GossipBundles(...))`), the manifest
  verified with `BundleVerifier`/`BundleStore` floors and its topology applied to `OfflineConnectivity.Topology`.
- **Acceptance:** S4 (tampered table) passes; a manifest older than the stored one is never applied; a manifest version reaches a
  connected installation within one push and a LAN friend within one gossip round (Installations already proves this for revocations;
  re-run here for the Offline kinds); out-of-bounds limit values are refused; custom limits round-trip; golden encodings pinned for the
  manifest and allowance bundles.

### Phase 3 — The lone terminal, end to end — ✅ DONE 2026-10-19 (3a–3d 2026-10-17, 3e 2026-10-19; log §5)

**Slices (2026-10-17):** 3a declarations (Cqrs feature slot, `ErrorKind.Unavailable`, `.Offline(...)`, `OfflinePolicyTable`) · 3b offline
login (verifiers, lockout, signed sessions, offline profile, limit keys) · 3c terminal envelopes, replay and relay transport · 3d central
reconciliation, findings, review queue, approvals as of the decision · 3e durable stores (`YFex.Offline.Data`), acceptance scenarios,
property test, benchmarks.

Needs Authorization 3b (persisted snapshot, offline start) and 4b (device approval tokens v2) — both ✅ (2026-10-15/16).

**Decisions confirmed 2026-10-17** (README ⚑O12, O13, O15, O17, identity-provider question; log §5):
- Offline login reads verifiers from an **app-chosen credential source**; the built-in operator credential (operator code + offline
  secret, PBKDF2 verifier at central, shipped in the site verifier bundle) is optional. Capturing the secret for the terminal's cached
  verifier when the app's online login never sees it is the app's job. Limits (cached users, verifier age, lockout, session length) are
  new `OfflineLimitKeys`. The **offline profile** (signed per site in the manifest, off by default) is built here.
- `.Offline(...)` attaches through a new **domain-neutral typed feature slot** on the Cqrs `CommandConfigurationBuilder`/`CommandPolicy`
  (Cqrs owner: additive change, logged in Cqrs' docs); `ErrorKind.Unavailable` is a new additive union case with a reason.
- Approvals: Offline supplies `IApprovalOriginAccessor` (envelope installation + `Provenance.DecidedAt`); the approver re-check **as of**
  the operation time needs a new additive, optional Authorization interface (`HeldAtAsync` over a permission change history; Authorization
  owner — additive change logged in authorization 09). Site tokens and the site's daily cap (an ⚑O14 `AllowanceKey` per approval
  permission) are Phase 4.
- `UnverifiedApproverAction.Defer`: a fact is applied with a finding; a request is parked in `IOfflineReviewQueue` (Accept re-runs it,
  Reject refuses it with a receipt).
- Also from Authorization 3b: offline sign-in calls `RestoreAndApplyAsync`; the snapshot signer gets the per-site
  `OfflineLimitKeys.PermissionSnapshotValidity` (clamped 1 h–14 d); optional move of Offline's publisher endorsement onto Installations'
  generic `PublisherKeySet`/`EndorsedPublisherKeys`/`PublisherEndorsement`.

- Offline login (cached verifiers with `PasswordHashing`, sessions, lockout, credential source, offline profile); `.Offline(...)` verb (`RecordsFact`, `Consumes` +
  `.WhenExhausted(Refuse | RequireApproval | AllowAndFlag)` (⚑O14), `ClaimsUnique`, `SharedWithSite<TProjection>` (layer 1 of ⚑O23; the
  manifest's `SiteFeed` is layer 2), `.WhenRulesDiffer(RuleVersionPolicy.KeepAndReport | Reevaluate)` / `.WhenRulesDiffer<TResolver>()`
  with `IRuleVersionResolver<TCommand>` (⚑O18), `Lane`) next to the existing `.OnOffline<T>()`, compiled availability — a source
  generator should compile the `.Offline(...)` chains into the `OfflinePolicy` table —, `ErrorKind.Unavailable` (additive);
  allowance amounts `.FromRules<T>().CappedBy(...).DefaultPerSubject(...)` and per-terminal grants (⚑O14); envelopes replace the outbox
  payload; installation replay with the envelope actor (full F4 fix).
- **Transport to central:** RPC when central is reachable; otherwise the Installations **`relay`** channel (`RelayItemKind`
  `offline.envelope`, central `IRelayItemConsumer`). The envelope's idempotency key travels inside the signed bytes and reaches central's
  `IIdempotencyStore` unchanged on both paths (constraint agreed with Installations, 05 §4.1). `HeldByFriend` is not custody: the
  terminal keeps its copy until central's receipt.
- Central reconciliation pipeline (08 §3) with facts/requests, `IFactReconciler<T>`, findings, `IOfflineReviewQueue` (in-memory +
  query); approvals as of the operation time (F7); budgets granted directly to terminals.
- Durable central stores, **last step of this phase** (⚑O17, 2026-10-17): new `YFex.Offline.Data` on YFex.Data with the shared
  `IIdempotencyStore` (≥ 30 days, open since Phase 0), the review queue, site settings, publications and delivery receipts. In-memory
  versions come first; Phase 3 closes only when replays survive a central restart.
- **Acceptance:** a create whose id already exists with other content or scope is refused with a finding, never applied as an
  overwrite (⚑O21, 08 §5; moved here from Phase 1, which has no central apply). S2 (standalone column), S3, S5, S6 pass in the harness. Property test "exactly once + no overspend" under random
  partitions passes (also through the relay path, with a friend that drops items). Benchmarks meet 10 §4.

### Phase 4 — The site broker services

Removed: Kestrel mTLS host, peer validator, discovery (manifest + beacon), site/peer time transport, logging/audit relays and bundle
distribution to terminals (Installations I3–I4: `PeerEndpoint`, `InstallationTrust`, beacons, `time`/`relay`/`bundles` channels).

**Decisions for the user (open 2026-10-20; the first one before the broker's site sessions, the others before the coordination work):**

| # | Question | Today (as built) | Options / recommendation |
|---|---|---|---|
| D1 | Second factor (AN-P5) and offline sign-in | offline sign-in checks only the password (terminal) or code + PIN/secret (broker) | **✅ 2026-10-20: (a) + (b)** — allowed; the session carries "second factor not checked" and central raises one finding per session; a per-site setting limits those people to the offline profile. User asked for the verifier-theft check → D7 |
| D2 | Order of `offline.feed` entries made while the broker is off (`Mesh = AllInstallations`) | no peer-to-peer feed yet | **✅ 2026-10-20: (b) the latest change wins.** Only on sites with `Mesh = AllInstallations` and only while the broker is off (otherwise the broker orders the feed): terminals send `offline.feed` entries to each other, and every terminal keeps, per feed key (e.g., product + field), the entry with the latest trusted decision time (tie: lower installation id), whatever the arrival order, so all converge. When the broker or central returns, it re-orders those entries by the same time and its result is final; each such entry carries a flag ("decided without coordinator") and central reviews them. The user accepts the residual risk — a person who manipulates the clock could make an older change win — because the case needs broker **and** central off, and trusted time (rollback detection, peers never pulling time back, `time.*` findings) already limits it |
| D3 | Optional SNTP server on the broker | not built; YFex.Time has an SNTP **client** (`NetworkTimeSource`) and a W32Time monitor (`YFex.Time.Windows`), no server | **✅ 2026-10-20: not built.** Trusted time already reaches terminals over the mTLS `time` channel (`UseSiteAnchor()`); an SNTP server would only align the Windows taskbar clock, is unauthenticated and opens UDP 123 on the broker. The site time table shows skewed terminals instead. Revisit only if stores ask |
| D4 | **Weighted per-terminal shares** | `GrantTerminalsAsync` splits evenly only | **✅ 2026-10-20: even split stays**; add weights (set by an admin, or from the last 30 days of use) only when a store has the case. Shares only matter while the broker is off (with it, terminals reserve from its pool) |
| D5 | **`IFactReconciler<T>`** (08 §3 step 11): the app's hook after a Fact is applied with findings (stock check, collect an agreement balance) | designed, **not built** (only named in a doc comment of `OfflineFindings`); findings go to the review queue only | **✅ 2026-10-20: build it in this phase**, optional per command (Facts are never rejected, so compensation is the app's only lever) |
| D6 | **Who wins between competing Requests / unique claims** from standalone terminals | first envelope to **reach** central wins (`ClaimAsync` keeps the first holder); no global order across standalone installations; the loser's `ConflictPolicy` applies | optional arbiter (e.g., `IOfflineConflictArbiter<T>`): central holds competing Requests for a short window and an app rule picks by role, lane or corrected decision time. Requests only (Facts cannot be undone). **✅ 2026-10-20: as recommended** — keep "first to arrive" by default; build the arbiter only when an app brings a real case |
| D7 | **Theft of site verifiers** (user question 2026-10-20) | the account password never leaves central (peppered). The broker's `authentication.verifiers` bundle holds **unpeppered** PBKDF2 (600 000) verifiers of the **same PIN** central accepts (authentication 04 §4), signed but not encrypted; central keeps every published version (`SqlOfflinePublicationStore`). A terminal keeps a PBKDF2 verifier of the user's **real password** (≥ 8 chars by default), encrypted only if the app registers the encrypted store (documented, not enforced). Whoever extracts a verifier (admin on the running PC, or the store plus its root key) brute-forces a 4–6-digit PIN in seconds to minutes and can then use that PIN at central too | recommended: (1) the broker and terminals **re-wrap** every verifier on receipt with a non-exportable TPM HMAC key and drop the raw copy (a copied file is useless away from that PC; guesses must run on it, under its lockout); (2) **seal** the verifier bundle to the broker (`RecipientSealing`) and keep only the newest version at central; (3) refuse to start `KeyValueOfflineCredentialStore` over an unencrypted store; (4) raise `MinimumPasswordLength` default to 10 (05 §5 and the `Secret` kind already say 10); (5) Authentication option: PIN sign-in at central only from an enrolled installation of the person's site (a cracked PIN alone is not enough). **✅ 2026-10-20: all five.** Clarified with the user: (3) does **not** stop terminals keeping verifiers — standalone sign-in still works with broker and central down; it only makes a missing storage encryption a start-up configuration error instead of a silent plain-text file. Without a TPM (Windows 10 PCs) the re-wrap key falls back to a software key under the root key (weaker, flagged in diagnostics). **Configurable (user condition, 2026-10-20):** the existing per-site `OfflineLimitKeys` (verifier validity 7 d · 1 h–30 d, cached users 20 · 1–200, minimum password 8 → **10** · 6–64, lockout failures and duration, session length) plus new ones built with D7: a per-site boolean "require a hardware (TPM) key for verifiers" (default off: software fallback + flag), how many verifier-bundle versions central keeps (default 1), and Authentication's realm option "PIN sign-in at central only from an enrolled device of the person's site". **Fixed safety floors** (not settings): a standalone terminal never verifies a PIN, and the terminal's credential store must be encrypted (tests use the in-memory store) |
| D8 | **Step-up while offline** (user question: "password to sign in, PIN for operations") | online it exists (Authentication: `.RequireRecentAuthentication(maxAge, minimum)`, inline approvals, lock/unlock with PIN). Offline there is **no step-up**: a command with that requirement replayed at central gets `ReauthenticationRequired` → High finding (Fact) or rejection (Request) (`OfflineCentralRpc`) | recommended: the broker (PIN) or terminal (password) re-verifies and signs a **step-up record** into the offline session (method + trusted time); envelopes cite it and central checks `maxAge` against it. Without a broker a standalone terminal accepts only the password (no PIN). **✅ 2026-10-20: as recommended.** `maxAge`/`minimum` stay the command's own (Authentication's `.RequireRecentAuthentication`); offline step-ups use the same lockout settings as offline sign-in |

Note for D4–D6 (user question 2026-10-20): budgets stay optional (⚑O14). A command with `.Consumes(...)` and **no grant on the
terminal** is treated as exhausted, so with the default `Refuse` it is refused offline — until the broker reserves from the site grant
(this phase), a site-level grant alone needs per-terminal shares or `AllowAndFlag`. Budget use is never gossiped between terminals:
without the broker each one spends only its own share (06 §4.1).

> **D2 refinement — proposed 2026-10-30; the user confirmed the recommendation ("go with your recommendation"); not built.** Build it
> in 4e. Factor A waits for the site time table (4f).
> - **Ordering key:** (1) trusted decision time, compared within the trusted-time uncertainty plus a configured margin (proposed default
>   5 min); (2) trust rank of the source installation, used only inside that window; (3) installation id, lower wins. No factor can
>   override time outside the window.
> - **Who ranks:** only the coordinator, and only while the broker is live (✅ 2026-10-30: central does not rank). It signs the ranking into an
>   `offline.feed.ranking` snapshot (version, factor list, one score per installation) that every terminal keeps. A terminal never ranks
>   itself; it only compares the signed numbers.
> - **Factors, recommended order:** (A) time health: anchored to the broker or central through the `time` channel, no `ClockRollback` in
>   the last N days, closeness to the site median (the site time table). (B) installation standing: roster `Active`/`Standby`, not
>   `ProvisionalInstallation`, hardware evidence key before software. (C) history: findings and rollback events in the last N days from
>   installation diagnostics. Kept for later: (D) witnessed presence (⚑I30, handhelds). (E) the actor's business role is **not** a trust
>   factor; it belongs in an app conflict rule (D6).
> - **Mechanism options:** (1) fixed rule in code: simple, but a new factor needs a protocol change. (2) **Pluggable ordered factors,
>   compiled by the coordinator into the signed snapshot (recommended):** a new factor is a new factor id and score, and terminals need
>   no change because they only compare numbers. (3) Weighted sum: hard to explain and has cliff effects; rejected. (4) Rules-engine
>   evaluation on each terminal: rule versions diverge offline; rejected.
> - **Dependencies:** the snapshot and factor B can be built now (the broker's roster and ledger exist). Factor A needs 4f.
> - **✅ 2026-10-30 (user):** factor A looks back **7 days** and factor C **30 days**, and the margin is **5 minutes**. All three are
>   defaults that each site can change. **Only the broker ranks:** central never signs a ranking, and with the broker off, terminals keep
>   using the last ranking they hold, whether central is reachable or not. Details are in the 4e sub-spec below.

**4e sub-spec — feed ordering and the ranking snapshot (written 2026-10-30, before the build; the user accepts it with the D2 refinement)**

*Scope now:* the ordering key, the signed `offline.feed.ranking` snapshot (codec, coordinator signer, terminal verifier and holder) and factor B (standing). *Not now:* factor A (needs the site time table, 4f), factor C (history, later), the terminal-to-terminal `offline.feed` entries and their push path (they need the durable broker store of 4c and the feed's own push topic), and central's review flag on entries decided without the coordinator.

*Ordering key (per feed key, e.g. product + field):* every source installation keeps only its latest entry for that key, so the set is one entry per installation. Let **L** be the entry with the latest trusted decision time (tie: lower installation id). An entry **e** *contends* when `L.time − e.time ≤ W`, where `W = margin + bound(e.uncertainty) + bound(L.uncertainty)`, and `bound(x)` is `x`, or 0 when the uncertainty is `TrustedInstant.UnknownUncertainty` (an unknown bound never widens the window, so time decides, as in D2). The winner is the contender with the highest ranking score; tie: lower installation id. The **margin** defaults to **5 minutes** (decided here: it covers the ordinary skew of peer-synced terminals, measured in minutes, without letting rank override a real later change; it is an option of the ordering, valid 0 to 24 hours, and becomes a per-site `OfflineLimitKeys` entry once 4b's limit keys are committed). Anchoring on **L** is deliberate: a pairwise "within the window" test is not transitive, so the winner would depend on arrival order; anchored on the set's latest, the winner is a function of the set alone, and every terminal converges. With no snapshot held, every score is 0 and the id decides inside the window.

*Ranking snapshot (`offline.feed.ranking`):* signed by the coordinator with its evidence key (domain `yfex.offline.feed-ranking.v1`), content = coordinator · **version** (≥ 1; a terminal keeps only a strictly newer version, from any coordinator) · signed-at (trusted instant) · **factor list** (1 to 8 factor ids, most significant first) · **one score per installation** (a `ulong`, sorted by installation id, no duplicates). Byte *i* of a score is the value of factor *i* (0–255, most significant byte first), so a terminal compares the numbers and never interprets them. Factor ids: **1 standing (B)**, 2 time health (A, 4f), 3 history (C, later); an unknown id is kept and compared, not interpreted. Standing (B) = roster status (Active 2, Standby 1, anything else 0) × 4 + (not `Friend.Provisional` ? 2 : 0) + (hardware evidence key ? 1 : 0): the three tests in the order the refinement lists them. A hardware evidence key is not carried by the roster today (only central's enrollment policy knows the protection), so the caller supplies it; carrying it to the coordinator is an open item for Installations. Wire v1 (strict, golden-pinned): content = `tag 1 · coordinator · version · bytes(trusted instant) · factor count · factor ids · score count · (installation · score)*`; the signed form = `tag 1 · bytes(content) · signature`, like the delivery receipt. A terminal verifies against the coordinator's key record and checks that the signer is the coordinator it knows as the roster's `Active` broker. **Only the broker ranks (✅ 2026-10-30):** central never signs a ranking. With the broker off, terminals keep the last ranking they hold, and it orders their feed entries until a live broker sends a newer one.

*Per-site settings (✅ 2026-10-30, defaults the user accepted, each changeable per site; they become `OfflineLimitKeys` entries once 4b is committed):* the ordering margin, 5 min (0–24 h); factor A's look-back for clock rollbacks, 7 days (1–30); factor C's history window, 30 days (7–180). Seven days matches the verifier validity and the certificate grace; 30 days matches D4's "last 30 days of use".

*Version continuity (✅ 2026-10-30, user confirmed the proposal; built, log §5):* versions count per broker, not per site. A terminal keeps a ranking from the roster's current `Active` broker if it is newer than the last one it holds **from that same broker**, or if the ranking it holds came from another broker. The roster already says the broker changed, and the old broker's rankings are refused because it is no longer `Active`. The broker sets the version from its trusted signing time in milliseconds, at least its last version + 1, so a restart needs no durable counter and this does not wait for 4c. A broker clock set back only delays its next ranking (`NotNewer`) until time passes the last version.

*Hardware evidence key (✅ 2026-10-30, user chose (a); Installations' change, not built yet):* factor B gives +1 to installations whose evidence key is hardware-protected, but the broker that ranks cannot see this: a roster entry carries the public key, not its protection; only central's enrollment knows. The options are (a) Installations adds the evidence-key protection to each signed roster entry (an additive field central fills from enrollment; recommended), or (b) drop the hardware test from factor B and keep only the roster status and the provisional test. Until then the caller supplies it.

**Build order (cut 2026-10-21, like 3a–3e):**

| Step | Content | Closes when |
|---|---|---|
| **4a** | Broker plumbing: `YFex.Offline.Broker` (`AddYFexOfflineBroker()`), the four channels with one versioned request/reply/push codec (golden-pinned), "only the roster's `Active` `SiteBroker` answers" (Installations: roster status on `Friend`, DI-level channel registrations), a request ledger that answers a repeated request id with the first reply, pushes with catch-up on (re)connect (channel sequence + store epoch), terminal-side `OfflineBrokerLink` with retry of the same id; the coordinator is the `Active` broker only | dormant broker refuses; retry after a lost reply returns the first answer; a terminal that reconnects catches up on missed pushes; codecs golden |
| **4b** ✅ 2026-10-29 | Site sessions: the sealed `authentication.verifiers` bundle (D7.2, newest version only), broker verification of code + PIN/secret with lockout and TPM re-wrap (D7.1), broker-signed sessions, D1 flag + per-site "offline profile only", D8 step-up records, terminal sign-in through the broker in `Site` mode; D7.3/D7.4 on terminals | S2 site column (sign-in part); verifier file useless off the PC |
| **4c** ✅ 2026-10-30 | Custody: site journal, signed receipts R1, R2 relayed down, forwarding in site-sequence order, hand-over from `Standalone`, backup copy until R2, failover of the terminal between `Site` and `Standalone` | S1; broker lost mid-outage → terminals re-send to central, nothing twice |
| **4d** ✅ 2026-10-30 | Coordination: reservations from the site grant, broker-signed slices, unique claims, `ApprovalTokenKind.Site` + daily cap, `IFactReconciler<T>` (D5) | S8, S9 |
| **4e** | Site feed: broker ordering, `ISiteFeedProjection<T>`, peer-to-peer latest-wins without the broker (D2) | S11 |
| **4f** | Broker choice (ranking on diagnostics, `Active`/`Standby` in the roster, pinning, hysteresis), site time table + `time.*` findings, `YFex.Hosting` with the architecture test, 30-terminal benchmark, TLS negative tests on the channels | S10 and the Phase 4 acceptance list below |

- **⚑O31 ✅ decided 2026-10-19: Installations peer channels** `offline.session`, `offline.coordination`, `offline.custody`,
  `offline.feed`, registered by `AddYFexOfflineBroker()` with `Peers(p => p.Channel(channel, handler, FriendScope.SameSite))`, the
  roster deciding who may open them; only the roster's `Active` `SiteBroker` answers (a dormant broker refuses every `offline.*`
  request). No Kestrel/ASP.NET Core on store PCs, no second Fusion peer. Offline owns: small versioned binary codecs per message
  (golden-pinned, like the envelope/session/receipt codecs), **catch-up on (re)connect + versioned pushes** (budget changed, feed entry,
  approval granted, custody receipt) turned into local `IEventBus` events and local services that view models read with
  `[Live(Cache = Local)]`/`[Subscribe<T>]` (view models unchanged; `[Live]` with `ServerShared` still means central), a **retry with
  the same envelope id** for calls in doubt (the broker's durable ledger deduplicates reservations and custody by it) before the
  hysteresis drops the terminal to `Standalone`, and per-kind logging. Terminal-to-terminal `offline.feed` without the broker only
  where the roster says `Mesh = AllInstallations`; ordering such entries while the broker is off: latest trusted decision time wins, re-ordered by the broker/central on return (D2 above).
- `YFex.Offline.Broker`: the broker services as a **module**, `AddYFexOfflineBroker()`, composed with `AddYFexInstallations(i =>
  i.SiteBroker()…)` (machine-scope keys, peer endpoint always on) inside the **shared generic service host** (no app code, ⚑O20) — not an
  executable of its own: the store's broker PC runs one YFex service with every module it needs (hosting ⚑H1 decided 2026-10-19: new
  repo `YFex.Hosting`, built in this phase; see
  [hosting](../hosting/README.md)). Enrollment through `IInstallationEnrollment` with the code as a start-up argument until YFex.Installer
  exists.
- **Site verifier bundle (⚑O12 + authentication ⚑AN18, contract settled 2026-10-19):** central's publisher reads
  `ISiteCredentialSource.ListAsync(siteScope)` (built-in source: YFex.Authentication.Offline.Central) and signs the
  `SiteCredentialVerifier`s (operator code, subject, scope, PIN or secret, unpeppered PBKDF2 of the NFC secret, set at) into
  `authentication.verifiers` for the site; the broker verifies code + PIN/secret with its own rate limit and lockout. Terminals still
  keep their own password verifiers (`OfflineSignIn.RememberAsync`, fed by YFex.Authentication.Offline after an online sign-in).
- Broker services: failover state machine for terminals (04 §4–§6), custody + site journal + signed receipts R1/R2, coordination
  (budget reservations and slices, unique claims; the coordinator is the roster's `Active` `SiteBroker`, ⚑O27, no election),
  site feed + `ISiteFeedProjection<T>` (store price override, S11, ⚑O23) on `offline.feed`, site sessions and the verifier bundle,
  `ApprovalTokenKind.Site` (with Authorization 4b).
- **Broker choice (⚑O2, 02 §4.1):** dormant broker installations on every eligible PC; central ranking policy (opening-hours uptime,
  can listen, terminal reachability, hardware key, admin pin) writing `Active`/`Standby` into the roster while online; re-rank with
  hysteresis; `Sites.Commands.Configure(PinnedSiteBroker: …)`. Needs from Installations: terminals report friend reachability,
  firewall-rule presence (I5) and a fix for ⚑I25 on candidates — **✅ built 2026-10-19** (log §5): `peers.ReportDiagnostics()` (signed
  relay item `installations.diagnostics`), `peers.CheckWindowsFirewall()` (rule "YFex Installations peer"), central
  `CollectDiagnostics()` → `IInstallationDiagnosticsStore.SummarizeAsync(candidate, since)`; ⚑I25 = software transport key for listeners
  (`TransportKeyPlacement`, automatic for `SiteBroker`) and `RequireHardwareEvidenceKeys(InstallationRole.SiteBroker)` at central.
- **Site time (what is left of ⚑T12):** the broker's site time table + findings `time.device-skew` / `time.rollback` /
  `time.disagreement` + resync requests, fed by the `TimeReply` every friend already exchanges on the `time` channel. The transport,
  `UseSiteAnchor()` and `UsePeerTime()` are built (Installations I4). Optional SNTP server on the broker = ask first.
- **Acceptance:** S1, S2 (site column), S8, S9, S10, S11 pass. A terminal whose Windows clock is set back shows up in the site time table
  within one probe. TLS negative tests (10 §3) re-run against the Offline channels. 30 simulated terminals on one site broker. An
  architecture test proves the broker host references no app assembly. Broker choice: a site with three candidates promotes the one
  with the best opening-hours uptime; a dormant broker answers nothing on `offline.*`; an outage changes no roster status; a pinned
  machine stays `Active` while reachable; re-ranking does not flap on a one-day difference. (The site-anchor acceptance — terminals anchored to the broker
  with its uncertainty, broker off → peers flagged `Unauthenticated` — passed in Installations I4.)

### Phase 5 — Hardening and UI (after Installations I5)

Removed: SAS pairing, provisional certificates, the site issuing CA, the hardware-key policy and the chaos harness (Installations I5).

- Offline behaviour of provisional installations: envelopes flagged, approvals refused, review items when central rejects the pairing
  later; chaos and property suites for the Offline scenarios on top of the I5 chaos harness; operations checklist (10 §2) for the future
  YFex.Installer; status bar + review-queue UI with the Avalonia phase.
- **Acceptance:** S7 passes (jointly with Installations I5). Every scenario test is green. AOT smoke at 0 warnings.
- **Handhelds (⚑O32, 2026-10-20; with Installations I6):** the optional off-site rule for offline sessions and the presence record in
  envelopes (new envelope version tag if the codec output changes). **Acceptance:** a handheld enrolled by QR sells/counts in `Site`
  and `Standalone` modes like a checkout; a handheld paired offline is provisional (envelopes flagged, no approvals); off-site offline
  work is flagged at reconciliation; a handheld revoked from a site terminal during the outage is refused by the broker at once.

## 4. Docs to update when the phases land

`YFex/CLAUDE.md` (solution map row for `YFex.Offline`), `YFex/src/Messaging/docs/03-rpc-and-offline.md` (modes, envelopes, `.Offline`),
`docs/public/messaging.md`, `authorization-consolidation/05` and `06` (signed snapshot, site approvals, as-of checks), `installations/06 §3`
(what Offline actually took), `time/README §8` (site time table built), `privacy/05 §5` (sealed intake), `validation/13` (D34 refinement),
`docs/index.md` row status. Logging 05 §6 and audit 05 §6 no longer depend on Offline (relay = Installations `relay` channel).

## 5. Implementation log

### Phase 0 — DONE 2026-10-03 (user confirmed ⚑O9, ⚑O16, ⚑O22)

Coordinated live with the authorization-consolidation session (its Phases 2a/2b edited the same Messaging.Rpc/Cqrs files the
same morning; it re-read every file listed below before its own edits). Backups: `C:\Temp\offline-backup\phase0\`.

**Tasks**

| Task | Done | Where |
|---|---|---|
| 0.1 F1 storage key | Random 32-byte key generated once, stored only wrapped: `WrappedKeyProvider` (file `<KeyDirectory>\<KeyName>.storage-key`, temp-file + move so concurrent first starts agree, never regenerates silently → `StorageKeyUnavailableException`), `IKeyWrapper` + `KeyProtection {Hardware, Software, DataProtection}`, `CngKeyWrapper` (RSA-OAEP-SHA256 under a persisted non-exportable CNG key, TPM first with a verified round trip, software KSP fallback, user or machine scope), `DataProtectionKeyWrapper` (cross-platform, Protect/Unprotect of the key — not a KDF), `StorageEncryptionOptions` (`StoreName`, `KeyName` = entry assembly, `KeyDirectory`, `MachineScope`, `PreferHardwareKey`). `DataProtectionKeyProvider` (internal) deleted | `YFex/src/Persistence/YFex.Persistence.Encryption/` |
| 0.2 F2 associated data | `IValueProtector.Protect(plaintext, associatedData)` / `TryUnprotect(…) → UnprotectStatus {Opened, OpenedLegacyLayout, AuthenticationFailed, UnsupportedVersion, Malformed}` (old one-argument calls kept). Layout `0x02`; `0x01` still read (`OpenedLegacyLayout`) and re-sealed on the next write; a `0x02` blob relabelled `0x01` fails. `EncryptedKeyValueStore(inner, protector, storeName, logger)`: AAD `"yfex.store|" + store + "|" + key`, `ReadFailed` event, `UnreadableValues`/`LegacyValuesRead` counters, first failure of each kind logged once as `StorageKeyMismatch` (keys never logged) | same project |
| 0.3 F3 idempotency | Client: `yfex-idempotency` header on every command dispatch (`Guid.CreateVersion7()`), replay re-sends the entry's key (`OutboxReplayContext`). Server: `IIdempotencyStore` + `InMemoryIdempotencyStore` (24 h) + `IdempotentExecution`, run by `ValidatingHandlerInvoker` **before** validation/rules/authorization; key = business key (`.IdempotencyKey(…)`) or header, bound to message type + scope header; only successful executions are remembered; concurrent duplicates await the first | `YFex.Messaging.Rpc/Idempotency.cs`, `ValidatingHandlerInvoker.cs`, `FusionMessageBus.cs`, `RpcMessagingExtensions.cs` |
| 0.4 F4 (interim) | `OutboxEntry(…, Subject, Scope, Sequence)` captured at enqueue by `IOutboxCallerCapture` (`DefaultOutboxCallerCapture`: `IPrincipalAccessor` + `IScopeContext`) inside the outbox implementations (so `LocalDispatcher`'s enqueue is covered too); replay sends the **captured** scope; a different signed-in subject → warning + `SyncStatus.ReplaysUnderAnotherSubject` | `IOutbox.cs`, `InMemoryOutbox.cs`, `StorageBackedOutbox.cs`, `OutboxReplayer.cs`, `SyncStatus.cs` |
| 0.5 F5 | Persisted monotonic sequence (`outbox-sequence` key, outside the `outbox:` prefix); `OutboxEntry.CompareReplayOrder` (sequence, then enqueue time for legacy 0); `OutboxOptions.KeepExpiredEntries = true` (default): expired entries are kept and replayed with a one-time warning | same files |
| 0.6 F12 | Duplicate `EncryptionMode`/`EncryptionKeySource` enums and the never-read `Encryption`/`EncryptionKeySource` client options removed (F15); `StorageDirectory` `[Obsolete]`; `CLAUDE.md` map (Messaging.Rpc line, Persistence tree, Layer 5) corrected | `YFexMessagingRpcClientOptions.cs`, `CLAUDE.md` |
| **F14** (found while implementing) | The client never used the durable outbox: `OutboxPair` now picks `StorageBackedOutbox` + `StorageBackedSyncFailureLog` when `Storage != InMemory` and the registered store is not `MemoryKeyValueStore` | `OutboxPair.cs`, `RpcMessagingExtensions.cs` |

**Files touched** (for sessions re-reading before their edits): Persistence.Encryption — `IValueProtector.cs`, `AesGcmValueProtector.cs`,
`EncryptedKeyValueStore.cs`, `EncryptionServiceExtensions.cs`, `.csproj` (description), new `IKeyWrapper.cs`, `CngKeyWrapper.cs`,
`DataProtectionKeyWrapper.cs`, `WrappedKeyProvider.cs`, deleted `DataProtectionKeyProvider.cs`; new project
`YFex.Persistence.Encryption.Tests` (+ `yfex.slnx`). Messaging.Rpc — `IOutbox.cs`, `InMemoryOutbox.cs`, `StorageBackedOutbox.cs`,
`OutboxReplayer.cs`, `SyncStatus.cs`, `FusionMessageBus.cs` (HeadersAsync + 2 command call sites), `ValidatingHandlerInvoker.cs`
(public InvokeAsync → idempotency → renamed `InvokeValidated*`), `RpcMessagingExtensions.cs` (client outbox block, server idempotency
registration), `YFexMessagingRpcClientOptions.cs`, new `Idempotency.cs`, `OutboxPair.cs`. Tests — Messaging.Tests
`Rpc/OutboxTtlTests.cs` (opts out explicitly + new kept-by-default test), new `Rpc/OutboxPhase0Tests.cs`; Messaging.Rpc.Tests new
`IdempotencyTests.cs`. Docs — `src/Messaging/docs/03-rpc-and-offline.md`, `04-storage-and-backends.md` (rewritten: it described
extensions that do not exist), `docs/public/messaging.md` (client example + encryption section), `YFex/CLAUDE.md`.

**Deviations from §3 (with reason)**

1. 0.1 names: `CngKeyWrapper` + `WrappedKeyProvider` + `DataProtectionKeyWrapper` instead of one `CngStorageKeyProvider` — wrapping
   (platform) and file handling (portable) are separate concerns, and the same `WrappedKeyProvider` serves both platforms.
   `DataProtectionKeyProvider` was internal, so it was deleted rather than obsoleted.
2. 0.1 two-process test: done as a real two-process run with a file-based app (`C:\Temp\yfex-edits\twoprocess\twoprocess.cs`: process
   13692 wrote with a TPM-wrapped key, process 35956 read `Opened`), plus in-process restart tests (independent providers, two DI
   containers). An xunit test spawning a child process was not added (slow, fragile on CI).
3. 0.3: idempotency is a step inside `ValidatingHandlerInvoker`, not a separate outer decorator: `LocalDispatcher` unwraps
   `ValidatingHandlerInvoker` by type and the Wolverine adapter re-runs `Decorate` — a separate decorator would have needed edits to
   both (owned by the authorization session for Phase 2b) or run validation twice in-process. Semantics are the planned ones
   (duplicates answered before every stage).
4. 0.4: capture lives in the outbox (`IOutboxCallerCapture`) instead of the dispatchers, so `LocalDispatcher` needed no change.
5. 0.6: F15 (dead options) and F14 (durable outbox never wired) were not in the plan; both are live data-loss/false-security bugs
   in files Phase 0 already touched.

**AOT:** Messaging.Rpc (`IsAotCompatible`) builds with no IL/CA1416 warning in any touched file; Persistence.Encryption has no
warning (CNG calls are guarded by `[SupportedOSPlatform("windows")]` / `OperatingSystem.IsWindows()`). No AOT publish was run.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Persistence.Encryption.Tests (new) | — | **15** |
| YFex.Messaging.Tests | 148 + 9 failing (authorization 2a in progress) + 2 skipped | **164** + 2 skipped (+5 outbox, +1 TTL) |
| YFex.Messaging.Rpc.Tests | (authorization added tests the same day) | **55** (+6 idempotency over real RPC) |
| YFex.Rules.Admin.Tests | 22 | 22 |
| YFex.Persistence.Tenancy.Tests | 9 | 9 |
| YFex.Persistence.Smoke | 146/149 | 147/149 (the 2 remaining failures are pre-existing) |
| YFex.Persistence.Tests | does not compile (pre-existing: references `YFex.State`, `UndoContext`, `MemorySnapshotStore`) | unchanged |

**Open items**

- **Identity at replay** is still the connection's (F4 full fix = signed envelope, Phase 3). Retrying a dead letter
  (`ISyncFailureLog.RetryAsync`) loses `Subject`/`Scope` because `SyncFailure` has no such fields.
- **Shared `IIdempotencyStore`** (SQL over YFex.Data, ≥ 30 days) for farms and server restarts — needed before production use of
  replays across server restarts.
- **Decorator order with tenancy:** register `AddYFexStorageEncryption` **before** the `ScopedKeyValueStore` decorator so the
  encryption sees the scope-prefixed key in its associated data (`Scoped(Encrypted(store))`); the reverse order leaves tenants
  separable only by prefix. Document/enforce in `YFex.Persistence.Tenancy` (not changed here).
- `YFex.Persistence.Tests` does not compile (pre-existing); `docs/public/messaging.md` still lists the old
  `YFex.Messaging.Rpc.Sqlite/IndexedDb/Encryption` names in its header line and later sections.
- The parallel "shared cryptography library" evaluation (`docs/internal/cryptography`, separate session) may relocate
  `IKeyWrapper`/`CngKeyWrapper`/`WrappedKeyProvider`; the on-disk formats (`.storage-key` `0x01`, value layout `0x02`) are the
  compatibility contract to keep. → **Done 2026-10-04**, see the addendum below.

### Addendum 2026-10-04 — encryption at rest moved onto YFex.Cryptography (cryptography Phase 0)

The user approved the shared cryptography library (docs/internal/cryptography, ⚑K1–K18) and asked to update this design accordingly.
What changed in the Phase 0 code (log: cryptography 12 §5):

| Offline Phase 0 (2026-10-03) | Now |
|---|---|
| `IKeyWrapper`, `CngKeyWrapper`, `DataProtectionKeyWrapper` | `IRootKeyProvider` (YFex.Cryptography), `CngRootKeyProvider` (YFex.Cryptography.Windows, byte-compatible blobs); DataProtection dropped |
| `WrappedKeyProvider` + `{KeyName}.storage-key` | key ring `device/storage` in `{KeyDirectory}\{KeyName}\` (`store.key` + `.keyring`, HMAC-tagged); the old file is imported once as the ring's first key |
| `IValueProtector`, `AesGcmValueProtector` (layout `0x02`, `'|'` AAD, one shared `AesGcm`) | `SealedValue` v3 (`0x03`, key id + algorithm in the header, length-prefixed context, one pooled `AesGcm` per key: cryptography C1) |
| `UnprotectStatus`, unreadable value → `null` + event | `OpenResult` union; `EncryptedKeyValueStore.TryGetAsync` → `StoredValueStatus`; `GetAsync` throws `StorageIntegrityException` (cryptography C10, ⚑K15) |
| `AddYFexStorageEncryption(EncryptionKeySource, byte[]?, configure)` | `AddYFexStorageEncryption(storage => …)` (`StoreName`, `KeyRing`, `KeyName`, `KeyDirectory`, `MachineScope`, `PreferHardwareKey`, `RotateEvery`, `ImportLegacyStorageKey`, `UseKey(bytes)`); the old overload is `[Obsolete]` for one release |
| `ActivatorUtilities` to build the inner store | keyed re-registration `"yfex.storage.inner"` (no reflection helpers; cryptography C8); package marked `IsAotCompatible` |

Kept: the default key directory `%LOCALAPPDATA%\YFex\Keys` (`%PROGRAMDATA%` with `MachineScope`), the decorator-order rule above, the
`StorageKeyMismatch` log (once per kind), `ReadFailed`, `UnreadableValues`, `LegacyValuesRead`. Tests: Persistence.Encryption.Tests 15
(golden `0x02` values produced by the interim code before the move; `0x01`; import of a real interim `.storage-key`; restart; erase;
reload after another installation rotates), Messaging.Tests 167 (+2 skipped), YFex.Persistence.Smoke encryption scenario all green.

### 2026-10-14 — Plan update after the dependencies were built (docs only, no code)

Requested by the user: bring the Offline plan up to date, because parts of it moved to YFex.Installations and its dependencies
(Cryptography, Time, Networking, Installations) are now built. Sources checked: cryptography 12 §5 (incl. the 2026-10-05 "Handoff to
Offline"), time README §4/§10, networking 06, installations 07 §7 and 08 §5, authorization-consolidation 09, and the YFex.Installations
public API through RoselYn (`InstallationsBuilder`, `InstallationPeersBuilder`, `CentralInstallationsBuilder`, bundle types). Backup:
`C:\Temp\offline-backup\plan-update-2026-10-14\`.

**Changed**
- 11: header note; §1 working rules (YFex is a git repo now; owners list; additive-only rule when no owner session runs); §2 rewritten
  with a status column; §3 Phases 1–5 re-cut to Offline's own work, each with a "Removed" line naming where the work went; §4 doc list.
- README: status and "Builds on" lines, Installations/Time/Networking notes, terminal example on `AddYFexInstallations`, ⚑O1/O4–O8/O19/O20
  notes with build status, new rows ⚑O25–O30 (pointer, confirmed) and **⚑O31** (broker services on peer channels vs a second Fusion
  peer), file table row for 03.
- 02 §5: manifest without installation lists (roster), `RosterVersion`; terminal, central and broker composition on the built APIs.
- 03: "built in Installations I1–I3" note with the two build findings (⚑I25 TPM server credential, macOS TLS 1.3).
- 04 §1: `Reachability` on `EndpointHealth`, coordinator and `AnyFriendReachesCentral`; §2 built note; §3 ⚑O31 note.
- 05 §2 and §4.1: built notes (the `time` channel replaces `Sites.Queries.Health` for `TimeReply`; peers fill in per ⚑T13).
- 07: trusted clock, logging and audit rows on the `time`/`relay` channels; broker diagram without Kestrel.
- 10 §3: harness on `InstallationHarness` + YFex.Time fake clock; TLS tests against the `offline.*` channels.
- `docs/index.md`: Offline row status.

**Not changed:** 01 (historical inventory), 06, 08, 09 (their content is Offline's own and still valid; 06's pointer to 03 §6 for
provisional pairing now leads to the superseded note there).

**Open for the user:** ⚑O31 (before Phase 4); the remaining ⚑O decisions (O2–O8, O10–O15, O17–O19, O21, O23) still say
"recommendation". Phase 1 needs O2 and O3 (roles, route in `SiteTopology`/`UpstreamRoute`) and O21 (ids in envelopes); Phase 2 needs
O11 (validity defaults) and O23 (feed allow-list in `OfflineLimits`); O8's mechanism is built by Installations, so only its Offline kinds remain.

### 2026-10-14 — ⚑O21 confirmed (docs only)

The user asked whether client-generated ids are a security concern and said to proceed if not. Confirmed with five rules, now in 08 §5:
no overwrite on an existing id, ids are not clocks, ids are not secrets, ids of personal data stay internal, number series are expected.
Phase 1 acceptance gains: a create with an existing id and different content is refused with a finding (harness test). README ⚑O21 row
updated. ⚑O2 (user proposal: automatic successor election with confirmations from the others) and ⚑O3 are under discussion.

### 2026-10-15 — ⚑O2 and ⚑O3 confirmed (docs only)

The user proposed an election among the remaining PCs (confirmation by all others, never self-elected, ties fall back to Standalone,
central as a helper, manual option kept). Discussed: a safe election needs a strict majority of the fixed roster, one vote per term,
leases and fencing, and above all a state hand-over (the old broker's budget and claims), which means sharing broker memory. The user
preferred not to have people choose the broker and to have "a machine elected at least once and saved". Decision: **⚑O2 = central
chooses automatically while online and saves it in the signed roster (`Active`/`Standby`), admin may pin, nothing changes during an
outage**; elections during an outage stay a possible later opt-in. **⚑O3 = `CentralFirst`.** Changed: README ⚑O2/⚑O3 rows; 02 §4 +
new §4.1 + `Configure(PinnedSiteBroker)`; 07 §2 hosting; 10 §1 pitfall row; 11 Phase 4 task + acceptance; installations 06 §4 note.

### Phase 1 — DONE 2026-10-15 (Claude; the user said "Start phase 1")

No other session was running. Nothing outside the new repo and core `YFex/src/YFex/Offline/` was changed in code. Sources: the
current YFex.Installations, YFex.Cryptography, YFex.Time, YFex.Networking and YFex.Authorization.Public APIs (RoselYn; `CanonicalWriter`
read directly because RoselYn shows no members of the ref struct). Backups: none needed (new files only; YFex is a git repo).

**Built**

| Item | Where |
|---|---|
| core `SiteTopology`, `UpstreamRoute`, `ConnectivityMode`, `IConnectivity`, `Reachability`, `ConnectivityChange(Reason)`, `OfflineFlags`, `OfflineProvenance` | `YFex/src/YFex/Offline/` (3 files, types only) |
| repo `C:\Dev\YFex\YFex.Offline` (git init, not committed): `offline.slnx`, `Directory.Build.props` (net11.0, preview, warnings as errors), `AGENTS.md`, `README.md` | — |
| `OfflineEnvelopeContent` / `OfflineEnvelope`, `BundleReference`; `OfflineEnvelopeCodec` (strict canonical, ≤ 1 MiB, golden-pinned) | `YFex.Offline/Envelopes` |
| `OfflineEnvelopeSigner`: evidence key from `IInstallationKeyStore`, `SignedPayload` domain `yfex.offline.envelope.v1`, trusted time via `TimeProvider.GetTrustedNow()`, flags from `TimeFlags` and `InstallationStatus`, refuses not-enrolled/revoked, advances the sequence before returning | same |
| `OfflineSequenceChain` (`HashChain`, domain `yfex.offline.sequence.v1`, genesis keyed by the installation id; `IsFirst`, `Follows`, `HeadAfter`), `IOfflineSequenceStore`, `InMemoryOfflineSequenceStore` | same |
| `OfflineEnvelopeVerifier` (central): installation known, key one of its evidence keys, installation/key not revoked at the decision time, signature; result `OfflineEnvelopeCheck` (native `union`, like Installations) | same |
| `OfflineDomains`, `OfflineBundleKinds` (`sites.manifest`, `offline.allowance`, `authentication.verifiers`, `authorization.site`) | `YFex.Offline` |
| `OfflineConnectivity` (`IConnectivity`): `ICentralHealth` + `INetworkConditions` (no network = definitive) + coordinator among `IInstallationFriends`, one Networking `Hysteresis` each; `OfflineConnectivityService` (periodic + network change) | `YFex.Offline/Connectivity` |
| `AddYFexOffline(o => o.Terminal().Topology(…).UseCentralHealth(…).UseSequenceStore(…).Connectivity(…))`; clear errors without `AddYFexInstallations`, a role or a sequence store | `YFex.Offline/DependencyInjection` |
| `KeyValueOfflineSequenceStore` (`offline-sequence:{id}`, unreadable entry = error, never a silent restart), `UseKeyValueSequenceStore()` | `YFex.Offline.Persistence` |
| `ScriptedTrustedClock`, `SettableCentralHealth`, `SettableNetworkConditions`, `TestInstallationKeys` | `YFex.Offline.Testing` |
| infrastructure map row | `YFex/docs/agent/infrastructure-map.md` |

**Deviations from §3 (with reason)**

1. `OfflineEnvelope` is in the `YFex.Offline` library, not in core: it names `ActorIdentity`, which lives in `YFex.Authorization.Public`
   (core is the leaf below it). Core keeps the types several boundaries read (modes, connectivity, provenance, flags).
2. `Reachability` = `(Central, Coordinator id, CoordinatorHealth, AnyFriendReachesCentral)`: provenance needs the coordinator's id (04 §1 updated).
3. `ICentralHealth` instead of reading Messaging.Rpc's `IServerConnection`: keeps YFex.Offline free of Messaging.Rpc/Fusion. The
   adapter comes with `rpc.UseOffline()` in Phase 3; until then the app registers one.
4. The coordinator is the same-site `SiteBroker` friend (preferring connected/seen). `Friend` does not expose the roster's
   `Active`/`Standby` status, so the ⚑O2 filter waits for Phase 4 together with the Installations addition it needs.
5. `IOfflineSequenceStore` has no compare-and-set (`IKeyValueStore` has none): one signer per installation per process.
6. The ⚑O21 duplicate-id acceptance moved to Phase 3 (Phase 1 has no central apply step).
7. `Session` is optional (`Guid?`): offline sessions arrive in Phase 3; envelopes created online have none.

**Measured / found**
- Native `union` results need `.Value` in FluentAssertions (`check.Value.Should().BeOfType<…>()`); pattern matching works directly.
- `HarnessCentral` always reports `RevokedAt = null`, so revocation tests use their own `IInstallationKeyDirectory` built from the same
  `TestInstallation` (`InMemorySigningKey.ToRecord()`).
- `FriendSet` has an internal constructor: connectivity reads friends through an internal seam (`IFriendObservations`); the
  production adapter and one harness test (terminal sees the broker on the simulated LAN → `Site`) use the real `IInstallationFriends`.
- Text in envelopes is NFC-normalized by `CanonicalWriter`; with `InvariantGlobalization=true` non-ASCII command types or scopes throw.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Offline.Tests (new) | — | **16** green, three runs (golden content, strict decoding, validity, harness verify, tamper/impostor/unknown/malformed, revocation as of decision time, time and status flags, refused installations, sequence across a restart, fork after a disk restore, unreadable position, mode transitions with hysteresis, no network, direct site + topology change, simulated LAN, registration errors) |
| YFex.Offline.AotSmoke | — | `dotnet publish -c Release -r win-x64`: **0** IL warnings, exe exit 0 |
| core `YFex.csproj` | builds | builds, 0 warnings |
| YFex.Installations.Tests | builds (baseline) | not re-run: no Installations code changed |

**Open / next**
- Benchmarks of 10 §4 were not run (they gate Phase 3). The verifier builds one `SignatureVerifier` per envelope (an ECDSA key import
  each time); cache by key id if the 200 envelopes/s target needs it.
- Phase 2 next: `YFex.Offline.Central` (manifest citing `RosterVersion`, `OfflineLimits`, allowances, delivery receipts, publishing through
  `PullableBundles`), rule-data signing with the Rules owner. Needs ⚑O11 and ⚑O23.
- Linux/macOS runs not done (no machine), as for Installations.

### 2026-10-15 — ⚑O11, ⚑O14, ⚑O18 and ⚑O23 confirmed (docs)

The user confirmed four decisions before Phase 2 (relayed by the planning session):

- **⚑O11 (amended):** one mechanism for built-in and custom validity limits — typed `OfflineLimit<T>` keys (TimeSpan, int, long,
  decimal, bool, text), built-ins in `OfflineLimitKeys` (snapshot 72 h, site authorization 72 h, verifiers 7 d, allowance 24 h, rule
  data flagged after 7 d via `MaxOfflineAge`, certificate grace 7 d), app keys with a default and hard bounds; precedence per-site
  admin value (`Sites.Commands.Configure`, signed into the manifest) → app default (`AddYFexOfflineCentral(c => c.Limits(...))`) →
  library default; out-of-bounds admin values refused when saved. Rule-based limits go to YFex.Rules through the optional package
  `YFex.Offline.Rules` (`OfflineFacts` situation field via `[SituationSource]`; rule data edited in Rules.Admin and signed as
  `rules.data`); central may derive a built-in value per bundle from a rule set.
- **⚑O14 (amended):** budgets are optional (off unless an `AllowanceKey` is declared); `.Consumes(...)` reserves before the handler,
  `.WhenExhausted(Refuse | RequireApproval | AllowAndFlag)` (Phase 3); amounts code default → admin rule data → app cap query; Phase 2
  builds the grants and the `offline.allowance` bundle.
- **⚑O23 (amended):** site feed in three narrowing layers — developer marks `.SharedWithSite<TProjection>()`, admin switches it per
  site in the signed manifest (never on for an unmarked command), the command's rules run before the envelope exists. Nothing marked
  means no feed.
- **⚑O18:** central accepts the newest version delivered (delivery receipts), flags rollback or `MaxOfflineAge`, never re-prices facts;
  per-command `.WhenRulesDiffer(...)` / `IRuleVersionResolver<TCommand>`; a source generator compiles `.Offline(...)` chains into the
  `OfflinePolicy` table later (Phase 3). Phase 2 needs only delivery receipts and `MaxOfflineAge`.

Changed: README ⚑O11/O14/O18/O23 rows; 02 §5 (manifest `SiteFeed`, resolved limits, `Configure` example, app limit defaults);
05 §2 note, §2.3 (`offline.publishers` key chain), §7 (keys, precedence table, rules package); 06 §2 (`.WhenExhausted`,
`.WhenRulesDiffer`, generator note), §4.1 (optional budgets, exhaustion table, amount sources), §4.2 (`OfflineFacts`), §5 (three
layers), §8 (delivery receipts, override); 11 Phase 2 (decisions, terminal side, acceptance) and Phase 3 (verbs).

### Phase 2 — DONE 2026-10-15 (Claude; task from the planning session, decisions ⚑O11/O14/O18/O23 confirmed by the user)

No other session was running (no shared file changed today before this one). Sources: current YFex.Installations (bundles, gossip,
`InstallationPeersBuilder`, `CentralInstallationsBuilder.PullableBundles`, harness), YFex.Cryptography (`SigningKeyRing`, `SignatureVerifier`),
Rules.Admin and YFex.Rules (read directly where RoselYn shows no constructors or bodies). Nothing committed. Builds use
`--artifacts-path C:\Temp\yfex-artifacts`: the RoselYn MCP process locks the YFex generator DLLs in their `bin` folders.

**Built**

| Item | Where |
|---|---|
| `OfflineLimitValue` (TimeSpan/int/long/decimal/bool/text, no boxing), `OfflineLimitKey`/`OfflineLimit<T>` (name, default, hard bounds), `OfflineLimit.*` factories, `OfflineLimitKeys` (6 built-ins, 05 §7), `OfflineLimits` (sorted values, typed `Get`, canonical) | `YFex.Offline/Limits` |
| `SiteManifest` + `SiteManifestCodec` (topology, route, `RosterVersion`, endpoints, resolved limits, `SiteFeed`; golden-pinned) | `YFex.Offline/Sites` |
| `OfflinePublisherKeySet` (tenant-signed `offline.publishers` payload), `OfflinePublisherKeys` (terminal directory of Offline's keys), `SiteManifestTracker` (store-accepted manifests only, never older, topology → `OfflineConnectivity.Topology`), `peers.OfflineBundles()` / `BundlePublishers.AddOffline(...)`, hosted attach in `AddYFexOffline` | `YFex.Offline/Sites` |
| `AllowanceKey`, `AllowanceEntry`, `OfflineAllowanceGrant` + `OfflineAllowanceCodec` (golden-pinned) | `YFex.Offline/Allowances` |
| `DeliveryReceipt` (+ codec, `DeliveryReceiptSigner`, domain `yfex.offline.delivery.v1`, relay kind `offline.delivery`) | `YFex.Offline/Receipts` |
| `OfflineBundleKinds.Publishers`, `.RuleData`, `.PublishedByOffline`, `.Gossiped`; `OfflineCanonicalText` | `YFex.Offline` |
| **new project `YFex.Offline.Central`**: `SitePermissions.Configure` (`sites.configure`), `Sites.Commands.Configure` + `SitesConfiguration` gate + `SiteConfiguration` (refuses unknown/out-of-bounds limits, unmarked feed commands, foreign sites), `OfflineLimitCatalog` (admin → `IOfflineLimitProvider` → app default → library default), `SiteFeedCatalog`, `SiteManifestPublisher`, `AllowancePublisher` (+ `IAllowanceAmountSource`, `IAllowanceCap`), `OfflinePublisherEndorsement`, `IOfflineSigningKeys` (`KeyRingOfflineSigningKeys` ring `offline/bundles`), `IOfflinePublicationStore`/`ISiteSettingsStore`/`IDeliveryReceiptStore` (in memory), `DeliveryReceiptVerifier`/`Consumer`, `RuleVersionCheck`, `OfflineCentral`, `AddYFexOfflineCentral`, `central.PullableOfflineBundles()` | `src/YFex.Offline.Central` |
| **new project `YFex.Offline.Rules`** (references YFex.Rules only): `OfflineFacts` (`IRawValue`, one situation field), `IOfflineFactsSource`/`OfflineFactsSource`, `RuleDataBundleCodec` (golden-pinned), `RuleDataBundleSigner` (`IRuleDataSigner`), `RuleDataBundleVerifier` (`IRuleDataVerifier`, signatures + floors), `AddYFexOfflineRules()`, `AddYFexOfflineRuleSigning(...)` | `src/YFex.Offline.Rules` |
| Rules hooks (additive, logged in validation 13): contracts in YFex.Rules, `GetSignedRuleData`, signing in `RulePublisher`, verification in `RuleSetSync` and `KeyValueRuleDataCache` | `YFex/src/Validation` |
| `OfflineTestPublisher`, `OfflineHarnessMember` | `YFex.Offline.Testing` |

**Deviations (with reason)**

1. **How installations learn Offline's keys:** a tenant-signed `offline.publishers` bundle (05 §2.3 made concrete) instead of keys in the
   enrollment receipt — enrollment is Installations', and this needed no Installations change. Rotation = a new version; floors stop a rollback.
2. **Rule-data floors and effective dating:** a version below a stream's floor is still loaded from the cache when the floor version is
   cached too (an older version may be the one in force today); it is `RolledBack` only when the newer accepted version is missing (09 S4).
   Versions received from central below the floor are history and accepted.
3. **Rule-data streams** are `{set}/{kind}/{code}/{tier path}` with URI-escaped parts (bundle headers must stay ASCII); free text in Offline
   payloads (limit texts, subjects, rule-data notes) is UTF-8 bytes (`OfflineCanonicalText`) so terminals with `InvariantGlobalization=true`
   can read it (canonical text needs ICU normalization there and throws). The pinned bytes equal canonical text for NFC input.
4. **Hook contracts in YFex.Rules, not Rules.Admin:** Rules.Admin brings Messaging.Rpc → Fusion/MemoryPack, whose AOT warnings broke the
   smoke (158 third-party IL warnings); with the contracts in YFex.Rules, `YFex.Offline.Rules` publishes clean.
5. `Sites.Commands.Configure` is a `YFex.Cqrs` command with its gate (`SitesConfiguration`) and a host-agnostic handler service; the host
   routes it (no Messaging.Rpc dependency in `.Central`, like Installations.Central). `PinnedSiteBroker` waits for Phase 4.
6. `PullableBundles(...)` takes no service provider: `PullableOfflineBundles()` binds a late link when the host starts or `OfflineCentral`
   is resolved. Allowances are renewed by calling `GrantAsync` again (no timer yet); per-terminal grants and slices are Phases 3–4.
7. `RuleVersionCheck` treats data as old only when something newer existed for longer than `MaxOfflineAge` (a table unchanged for a year is
   current). The rule-derived limit seam is `IOfflineLimitProvider`; no Rules evaluation adapter for it yet.
8. Rule data is not gossiped yet (it reaches each installation through `RuleSetSync`); `OfflineBundleKinds.Gossiped` = publishers, manifest, allowance.

**Found**
- Offline kinds spread on the LAN only through announcements on **open** connections: beacons advertise roster/revocation versions only,
  and `BundleGossip` drops an announcement that arrives within `MinimumPullInterval` (1 s) of the last pull from that friend — the next
  trigger is the next announcement. Fine for one push at a time; a burst can leave a friend one version behind until the next one. Proposed
  for Installations: re-schedule instead of drop, or advertise a digest of other kinds' versions in beacons.
- `InstallationHarness` verifies every kind against the tenant key; the Offline tests serve the `bundles` channel with their own gossip
  (`OfflineHarnessMember`) instead of changing the harness.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Offline.Tests | 16 | **36** green, three runs (limits and bounds, golden manifest/allowance/rule data, strict decoding, endorsement + rotation, older manifest never applied incl. restart, other site, Configure refusals, precedence + custom limits round trip, pull through `AddYFexInstallationsCentral(...).PullableOfflineBundles()` applied on a terminal, allowance amounts, delivery receipts, rule-version standing, push + one gossip round on the harness, S4, OfflineFacts) |
| YFex.Offline.AotSmoke | 0 warnings | `dotnet publish -c Release -r win-x64`: **0** IL warnings, exit 0 (now with Phase 2, incl. a non-ASCII limit text under `InvariantGlobalization`) |
| YFex.Rules.Admin.Tests | 27 | **30** |
| YFex.Rules.Tests | 65 | 65 |

**Open / next**
- Phase 3 (needs Authorization 3b/4b): `.Offline(...)` verb with `.WhenExhausted`, `.WhenRulesDiffer`, `.SharedWithSite` (layer 1 of ⚑O23
  from code instead of `SiteFeed(...)`), the `OfflinePolicy` generator, per-terminal allowance grants.
- SQL stores (`YFex.Offline.Data`) for settings, publications and receipts before several central nodes publish.
- Delivery receipts are created and verified; sending them automatically after each accepted bundle is Phase 3 (with the relay path).

### 2026-10-17 — Phase 3 decisions confirmed (docs only, no code)

The user went through the Phase 3 decisions one at a time. Sources checked first: the Authorization repo README and authorization 09
(3b ✅ 2026-10-15, 4b ✅ 2026-10-16 and their open items for Offline), current `IPermissionDirectory`/`ApprovalTokenVerifier`
(re-check is "holds now" only), Cqrs `CommandConfigurationBuilder` (fixed verbs, no extension slot) and `ErrorKind` (ends at
`ApprovalRequired = 7`). No login code exists in YFex or YFex.Farma.

- **Identity provider:** YFex.Offline stays independent of it; it differs per project (future external provider, YFex accounts, not
  decided). The operator credential is a built-in, optional credential source.
- **⚑O12 (amended):** mechanism as recommended; numbers as `OfflineLimitKeys`; secret capture when the online login never sees it = the
  app's job; offline profile built in Phase 3, off by default.
- **⚑O13 (amended):** as recommended; `.Offline(...)` through a generic typed feature slot in Cqrs; `ErrorKind.Unavailable` additive.
- **⚑O15 (amended):** as recommended; as-of approver re-check through a new optional Authorization interface (`HeldAtAsync`); the site's
  daily approval cap reuses ⚑O14 allowances.
- **⚑O17 (amended):** as recommended; `Defer` = fact applied + finding, request parked in the review queue; durable stores
  (`YFex.Offline.Data`) inside Phase 3.
- **⚑O16:** marked ✅ in the README (confirmed with Phase 0).

Changed: README (status, identity-provider question, ⚑O12/O13/O15/O16/O17 rows); 11 §2 (4b row, order note), §3 Phase 3 (decisions
block, task wording, durable stores step). Still open for later phases: **⚑O31** (before Phase 4); Installations asks for Phase 4
(friend reachability, ⚑I25); ⚑O7 policy and Installations I5 (Phase 5).

### Phase 3a/3b — DONE 2026-10-17 (Claude; the user said "Start Phase 3 with the decisions confirmed today")

No other session was running (no shared source file changed since 2026-10-16 noon). Nothing committed. Builds use
`--artifacts-path C:\Temp\yfex-artifacts`.

**Built — 3a (declarations, ⚑O13)**

| Item | Where |
|---|---|
| `MessageFeatures` (typed feature slot, one value per type, last configuration wins); `Feature<T>()`/`GetFeature<T>()` on both command builders; `CommandRegistrationMetadata.Features`; `CommandPolicy.Features` (optional last parameter) | `YFex/src/Cqrs/YFex.Cqrs` (additive) |
| `ErrorKind.Unavailable = 8`, `UnavailableReason {CentralRequired, CoordinatorRequired, AllowanceExhausted}`, `Unavailable` case, `Error.Unavailable(reason, message?)` | `YFex.Cqrs/Error.cs` |
| Wire format **v6** (`Unavailable` + reason byte; v1–v5 still read) | Messaging.Rpc `RpcResultFormatters` |
| Offline dispatch of a central-only command returns `Unavailable(CentralRequired)` instead of `Fail("No network connectivity.")` | `FusionMessageBus`, `LocalDispatcher` |
| `server.unavailable` code in generated view models | `YFex.Validation.Generator/Emitter.Vm.cs` |
| `.Offline(o => o.RecordsFact().Consumes(...).WhenExhausted(...).ClaimsUnique(...).SharedWithSite<T>().WhenRulesDiffer(...)/<TResolver>().Lane(...))` (constraint `IQueueable` = compile-time error otherwise), `OfflineCommandPolicy`, `OfflineReplay`, `AllowanceExhaustedAction`, `RuleVersionPolicy`, `OfflineLane`, `IRuleVersionResolver<T>`, `ISiteFeedProjection<T>`, `SiteFeedEntry`, `OfflineCommandNames` | `YFex.Offline/Commands` |
| `OfflinePolicyTable` (routes `CentralOnly/LocalOnly/QueuedRequest/LocalHandler/Declared`, policies by type and name, `SharedWithSite`), registered by `AddYFexOffline` from the `CompiledMessagingRegistry` | same |
| Central: `SiteFeedCatalog.Mark(OfflinePolicyTable)`; `AddYFexOfflineCentral` marks every `.SharedWithSite` command of the registered registry (layer 1 of ⚑O23 from code) | `YFex.Offline.Central` |

**Built — 3b (offline login, ⚑O12)**

| Item | Where |
|---|---|
| `OfflineSession` (+ content, flags `OfflineProfile/SnapshotTimeUntrusted/ShortSecret`), `OfflineSessionCodec` (golden-pinned), `OfflineSessionSigner` (domain `yfex.offline.session.v1`), `OfflineSessionVerifier` (central) | `YFex.Offline/SignIn` |
| `EvidenceSignatures` (shared check of envelopes and sessions: installation known, evidence key, not revoked at the time, signature) | `Envelopes/OfflineEnvelopeVerifier.cs` |
| `OfflineCredential`, `OfflineLockout`, `IOfflineCredentialStore` (+ in-memory), `OfflineCredentialCodec`; `IOfflineSessionStore` (+ in-memory) | `YFex.Offline/SignIn` |
| `OfflineSignIn`: `RememberAsync` (app-captured secret → PBKDF2 verifier; refuses short secrets and digit-only PINs; drops the oldest beyond the limit), `SignInAsync` (lockout 5 → 5 min doubling, ≤ 24 h, persisted; verifier age; snapshot or offline profile; signed, stored session capped by the snapshot's validity), `SignOut`, `ForgetAsync`; outcome union `OfflineSignedIn/NotAvailable/LockedOut/Rejected` | same |
| `IOfflinePermissions` seam | same |
| New `OfflineLimitKeys`: `CachedCredentials` 20, `MinimumPasswordLength` 8, `LockoutFailures` 5, `LockoutDuration` 5 min, `SessionLength` 12 h, `OfflineProfile` (text, empty = off) | `Limits/OfflineLimit.cs` |
| `KeyValueOfflineCredentialStore` (an unreadable lockout never unlocks), `KeyValueOfflineSessionStore`, `UseKeyValueSignInStores()` | `YFex.Offline.Persistence` |
| **new project `YFex.Offline.Rpc`**: `ServerConnectionCentralHealth` (`UseRpcCentralHealth()`), `SignedSnapshotOfflinePermissions` (`RestoreAndApplyAsync`, profile = version-0 snapshot; `UseSignedSnapshots()`) | `src/YFex.Offline.Rpc` |

**Deviations (with reason)**

1. `.Offline(...)` on a non-`IQueueable` command is a **compile-time** error (generic constraint) instead of a startup error; the table
   still refuses a hand-built registration.
2. The `OfflinePolicy` **source generator** is not built: the table is compiled once at startup from delegates captured where the command
   type is generic (AOT-safe, no reflection). The generator stays a later optimisation (⚑O18 said "should").
3. Wire **v6 is not readable by v5 readers** (they refuse the whole payload, as with every earlier bump); the claim in the 2026-10-17
   decision discussion that old clients map unknown kinds to `Failed` was wrong.
4. A later configuration (server/client overrides) **replaces** a command's `.Offline(...)` declaration; a second call in the same
   configuration refines it.
5. The **offline profile** is a limit key (`offline.profile.permissions`, comma-separated ids, empty = off) instead of a separate manifest
   field: it reuses the ⚑O11 precedence and needs no manifest codec change.
6. Until Phase 4 the **terminal verifies in `Site` mode too** (no broker services yet). Digit-only secrets count as PINs and are never kept
   on a terminal; the minimum length is a limit key.
7. `IOfflinePermissions` is a seam in YFex.Offline; the adapter over `SignedPermissionSnapshots` lives in the new **YFex.Offline.Rpc**,
   because `YFex.Authorization.Installations` references Messaging.Rpc (Fusion/MemoryPack) and YFex.Offline's AOT smoke must stay clean.
8. The central **operator-credential store** (the built-in optional source, and the `authentication.verifiers` bundle) moves to Phase 4
   with the broker that consumes it; terminals in Phase 3 keep verifiers the app captured.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Offline.Tests | 36 | **54** (+6 declarations, +12 sign-in) |
| YFex.Messaging.Tests | 176 + 2 skipped | **179** + 2 skipped (+3 feature slot) |
| YFex.Messaging.Rpc.Tests | 79 | **80** (+1 wire v6) |
| YFex.Offline.AotSmoke | 0 warnings | **0** IL warnings, exit 0 (after the Cqrs reference) |

### Phase 3c/3d — DONE 2026-10-17 (Claude, same session)

Terminal envelopes, replay and relay transport (3c) and central reconciliation (3d) were built together: the transport ends in the intake.
Nothing committed by Claude (the user committed pending work at 20:38 — `1b86ed2`, `e2187ec` in YFex — which includes the 3a Cqrs
feature-slot change; everything after is uncommitted).

**Built — shared code (additive)**

| Item | Where |
|---|---|
| `IOfflineAdmission` (before an offline handler; `Abandon` when it throws) in `FusionMessageBus` and `LocalDispatcher`; `IOutboxEnqueueObserver` + internal `ObservedOutbox` (the client's outbox, only when observers exist); `IOutboxReplayRoute` asked first by `OutboxReplayer`; `ReplayOutcome.Rejected(policy, error)` | Messaging.Rpc `OfflineHooks.cs`, dispatchers, `RpcMessagingExtensions`, `OutboxReplayer`, `OutboxCommandRegistry.cs` |
| `IPermissionHistory.HeldAtAsync` (⚑O15 as-of approver check; used for tokens whose `ApprovalOrigin.DecidedAt` is known; default: the directory when it implements it) | Authorization.Public `Approvals.cs`, `ApprovalTokenVerifier`, `AddYFexApprovals` |
| `PermissionSnapshotSigner.VerifyAsync(wire, audience, at)` (central checks a snapshot it signed, at the decision time, zero skew) | Authorization.Installations.Central |
| `OfflineFlags.OverAllowance`, `OfflineFlags.ProvisionalClaim` | core `YFex/Offline` |

**Built — YFex.Offline**

| Item | Where |
|---|---|
| `OfflineAdmissions` (reserve from this terminal's OWN grant; `.WhenExhausted` Refuse / RequireApproval (needs an approval token on the command; central verifies it) / AllowAndFlag; provisional claims; `Take`/`CommitAsync`/`Abandon`), `IOfflineAllowanceGrants` + `BundleStoreAllowanceGrants` (stream `key/site/installation`), `IOfflineAllowanceLedger`, `IOfflineClaimStore`, in-memory stores | `Admission/` |
| `OfflineEnvelopes` + `QueuedCommand` (actor and session of the offline sign-in, else the subject at enqueue; mode, coordinator, snapshot version, cited grant and manifest versions, flags; custody written before the reservation is spent) | `Replay/OfflineEnvelopes.cs` |
| `OfflineReceipt`/`OfflineOutcome` (Applied, AppliedWithFindings, Rejected, Held, Refused), `OfflineSessionUpload`, `OfflineSubmission`, `OfflineReceiptBatch`, `OfflineSubmissionCodec` | `Replay/OfflineSubmission.cs` |
| `CustodyEntry`, `IOfflineCustodyStore` (+ in-memory), `CustodyOrder` (deadline lane first, background last, then sequence) | `Replay/OfflineCustody.cs` |
| Sessions keep the signed snapshot they started with (`IOfflineSessionStore` stores `OfflineSessionUpload`; `IOfflinePermissions.ExportSnapshotAsync`) | `SignIn/` |
| `KeyValueOfflineCustodyStore` (unreadable = error), `KeyValueOfflineAdmissionStores` (ledger + claims), `UseKeyValueCustodyStore()` | `YFex.Offline.Persistence` |

**Built — YFex.Offline.Rpc (terminal)**: `IOfflineIntake : IRpcService` (`Task<byte[]> SubmitAsync(byte[])`; proxy checked in from
ActualLab.Generators 13.0.12 + `ProxyGoldenTests`), `OfflineMessagingAdmission`, `OfflineEnvelopeObserver`, `OfflineUploader` (sessions
first; call-context headers with the installation assertion), `OfflineReplayRoute` (receipt → `ReplayOutcome`; a rejection uses the
command's `ConflictPolicy`), `OfflineCustodyRelease` (custody ends when the outbox drains the entry), `OfflineRelayForwarder`
(`offline.session` / `offline.envelope` through a friend while central is unreachable; custody kept), `UseRpc()`.

**Built — YFex.Offline.Central (host-agnostic)**: `OfflineReconciliation` (sessions: signature, sender, snapshot through
`IOfflineSnapshotVerifier` with subject/scope/version match, the offline profile from the site's resolved limit; envelopes: steps 1–5 and 9,
the session or `IOfflineActorPermissions`, the executor, claims and allowance accounting through an inspection callback, receipts stored
except refused requests, findings to the review queue, `ResolveParkedAsync`), `OfflineSessionPendingException` (an envelope that overtook
its session through a friend: transient), `IOfflineCommandExecutor`, `FindingKinds` (22 kinds), `FindingSeverity`, `OfflineFinding`,
`IOfflineReviewQueue` + in-memory (grouped per site/kind/actor/UTC day; parked requests; resolutions), `IOfflineReconciliationStore` +
in-memory (receipts, sessions, sequences, claims, spending).

**Built — new YFex.Offline.Central.Rpc**: `MessagingOfflineCommandExecutor` (the generated `OutboxCommandRegistry` materializes the
command into a replay `IDispatcher`; `CallContext` from the cited snapshot and the envelope's scope; `ValidatingHandlerInvoker` runs the
stages; a FACT whose stage fails runs on the undecorated invoker with a finding; "revoked since" through `IPermissionDirectory`),
`OfflineReplayScope`, `OfflineApprovalOrigin` (4b's open item), `OfflineUnverifiedApproverHandler` (⚑O17 Defer: a fact is flagged with a
finding, a request is parked), `OfflineIntakeService`, `OfflineRelayConsumer`, `AuthorizationOfflineSnapshotVerifier`, `AddYFexOfflineIntake()`.

**Deviations (with reason)**

1. **Facts and failed checks:** the first failing stage of a fact becomes the finding (the pipeline stops at the first failure); later
   stages of that fact are not evaluated. Requests behave exactly as online.
2. **Sequence forks** are detected when the next expected number does not chain; a number at or below the highest seen is treated as a
   late arrival (no per-number hash history yet).
3. **Over-grant spending** at central is counted per cited grant version; checking it against the grant needs `GrantLookup` (the
   publication store keeps only the newest grant of a stream). Terminal-flagged overspending (`OverAllowance`) is always a finding.
4. **Rule-data standing (step 9)** reports rollbacks (cited < acknowledged); `Stale` needs the superseded time of each version and waits
   for the rules session's history.
5. **Central receipts are not signed**: they return on the authenticated RPC call; signed receipts (R1/R2) come with the broker (Phase 4).
6. **Without an offline session** an envelope runs with `IOfflineActorPermissions` (the app's user store) or anonymously; the offline
   profile at central is the site's CURRENT profile limit, not the one in force at the decision.
7. **One envelope per RPC call** from the outbox replay route (simple; batching later).
8. **ActualLab.Generators 13.0.12 and Microsoft.CodeAnalysis.CSharp 4.11.0** were added to `YFex.Offline.Tests` only (test-only, the
   same pins as the Authorization and Installations.Rpc tests) for the proxy golden test.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Offline.Tests | 54 | **69** (+14 replay: admission, reconciliation through the real pipeline, facts vs requests, forged/foreign/out-of-scope, claims, sequence, parked requests, approval origin, route, rejection mapping, relay with an overtaking envelope, codecs, durable stores; +1 proxy golden) |
| YFex.Messaging.Tests | 179 + 2 skipped | **184** + 2 skipped (+5 hooks) |
| YFex.Messaging.Rpc.Tests | 80 | 80 |
| YFex.Authorization.Installations.Tests | 42 | **44** (+1 as-of approver, +1 snapshot verification) |
| YFex.Authorization.Tests / AspNetCore / Generator | 88 / 8 / 10 | = |
| Cqrs.Approvals / Compensation / Tenancy | 12 / 17 / 11 | = |
| Validation / Validation.Generator / Rules / Rules.Admin | 62 / 50 / 65 / 29 | = (Rules.Admin is 29 since 4b) |
| YFex.Offline.AotSmoke | 0 warnings | **0** IL warnings, exit 0 — now also the verb, sign-in, sessions, custody, the submission codec and reconciliation |

**Open / next — 3e**
- `YFex.Offline.Data` on YFex.Data: `IOfflineReconciliationStore`, `IOfflineReviewQueue`, site settings, publications, delivery receipts,
  the shared `IIdempotencyStore` (≥ 30 days); receipts kept ≥ 30 days.
- Acceptance scenarios S2 (standalone column), S3, S5, S6 in the harness; property test "exactly once + no overspend" under random
  partitions (also through the relay); benchmarks of 10 §4 (verifier key cache if needed).
- Per-terminal allowance grants at central (`AllowancePublisher.GrantAsync` for a holder) and their pull; delivery receipts sent
  automatically after each accepted bundle.
- Docs of §4 (Messaging 03, public messaging, authorization 05/06, installations 06 §3) and the `YFex/CLAUDE.md` map rows for the new projects.

### 2026-10-19 — Note from authentication AN-P4 (additive, not an Offline phase)

The authentication session (user: "you can change those other libraries … align the design plan") settled ⚑AN18 with this library:
- **New contract** `ISiteCredentialSource` + `SiteCredentialVerifier` + `SiteCredentialKind` (`SignIn/SiteCredentials.cs`): where Phase
  4's verifier-bundle publisher gets a site's staff verifiers. Implemented by YFex.Authentication.Offline.Central.
- **`OfflineSignIn.CheckAsync(SignInRequest)` → `OfflineCredentialCheck`**: checks a secret against the kept verifier without signing in
  (unlock of a running session while central is unreachable), same lockout as `SignInAsync`.
- **Secrets are NFC-normalised** before PBKDF2 in `RememberAsync`/`SignInAsync`/`CheckAsync`, like YFex.Authentication's verifiers
  (an ASCII secret is unchanged, so existing verifiers keep working).
- Phase 4 text: the broker becomes a module of the shared service host ([hosting](../hosting/README.md), ⚑H1 open), and the verifier
  bundle reads `ISiteCredentialSource`. 69 Offline tests green, Offline AOT smoke OK.
- **⚑H1 decided the same day:** the shared host is a new repo `YFex.Hosting`, built together with this library's Phase 4.

### 2026-10-19 — ⚑O31 decided, Phase 3e DONE, Installations asks built (Claude; task from the planning session, decisions by the user)

**Decisions (user, 2026-10-19, through AskUserQuestion):**
- **⚑O31 → Installations peer channels** (`offline.session`, `offline.coordination`, `offline.custody`, `offline.feed`), after the user asked
  for scenarios of what is lost and gained (live updates, pushes in both directions, broker reboot mid-request, broker off, view models).
  Recorded in README, 02, 04 §3, 11 §3 Phase 4. App view models do not change in either option: broker values reach them as local
  `IEventBus` events and local Offline services (`[Live(Cache = Local)]`, `[Subscribe<T>]`); `[Live]` with `ServerShared` still means central.
- **⚑I25 → software transport key for installations that listen**, evidence key in hardware (installations README).
- **Firewall-rule presence → measure + the named rule** ("YFex Installations peer"); **diagnostics travel as a signed relay item.**

**Built — Phase 3e**

| Item | Where |
|---|---|
| New project **`YFex.Offline.Data`** (YFex.Data, hand-written row readers, AOT-safe): `SqlOfflineReconciliationStore` (receipts, sessions with the verified snapshot, sequences, claims, spending per envelope), `SqlOfflineReviewQueue` (grouping, history generations, findings, parked requests), `SqlSiteSettingsStore`, `SqlOfflinePublicationStore` (every version kept; PK refuses a second writer of a version, rebuild ≤ 3 tries), `SqlDeliveryReceiptStore`, `SqlIdempotencyRecords` (claim/complete/abandon, 2-minute lease takeover); `OfflineSqlSchema.Statements(SqlEngine)` for SQLite/PostgreSQL/SQL Server; `AddYFexOfflineData(connections, o => o.Retention)` (≥ 30 days, default 35) + `OfflineDataPurge` (hourly) | `src/YFex.Offline.Data` |
| `SqlIdempotencyStore : IIdempotencyStore` (Messaging.Rpc) over the records, `IdempotencyResultCodecs` (built-in Guid/string/bool/int/long/decimal/DateTimeOffset/bytes; `Add<T>` for app results), `UseSqlIdempotency()` | `YFex.Offline.Central.Rpc/SqlIdempotencyStore.cs` |
| Per-terminal grants: `AllowancePublisher.GrantTerminalsAsync(key, site, holders, subjects)` (even split rounded down, remainder counts to the first holders), `GetGrantsAsync(site, holder)`; `OfflineCentral.PullAsync` adds the caller's own shares | `YFex.Offline.Central` |
| Over-grant check: `IOfflinePublicationStore.GetVersionAsync` (in-memory keeps 64 versions), `AllowancePublisher.IsOverGrantAsync`, wired as `GrantLookup` by `AddYFexOfflineIntake` | same + `.Central.Rpc` |
| Spending recorded **per envelope** (`SpendAsync(…, idempotencyKey, …)` replaces, `GetSpendingAsync`): a refused request that runs again is no longer counted twice | `IOfflineReconciliationStore` |
| Automatic delivery receipts: `DeliveryReceiptSender` (on `BundleStore.Accepted` for manifest, allowance, rule data; through the relay; pending newest-per-stream, retried every 30 s) + hosted `DeliveryReceiptService` in `AddYFexOffline` | `YFex.Offline/Receipts` |
| Acceptance: 09 S2 standalone column (PIN refused on the terminal, password checked, device approval token, judged as of the sale), S3 (37 sales of a dismissed cashier = one Low item), S5 (clock rollback → High finding, session ends on trusted time), S6 (post-revocation envelopes refused + Critical, earlier ones applied + Low), replays surviving a central restart on SQLite | `tests/…/ReplayTests.Scenarios.cs` |
| Property test "exactly once + no overspend": 8 fixed seeds × 80 random steps (sales, RPC replays with lost replies, relay through a friend that drops a third of the items, partitions, central restarts over SQLite), then heal; invariants: every decided sale applied once, none twice, each checkout within its share, each subject within the site budget, central's spending equals the checkouts' | `ReplayTests.Properties.cs` |
| Benchmarks of 10 §4 | new `benchmarks/YFex.Offline.Benchmarks` |
| Docs of §4: Messaging 03 (offline-capable commands section), public messaging (pointer), authorization 05 + 06 ("as used/built with Offline"), installations 05 §4.1 / 06 §3 (what Offline took), 06 §4.1 here, `docs/index.md` row, `docs/agent/infrastructure-map.md` row (YFex/CLAUDE.md has no per-repo rows; the map is the agent routing file). Time README §8, privacy 05 §5 and validation 13 have nothing new from Phase 3 (site time table and sealed intake are Phase 4/5) | — |

**Benchmarks** (dev PC, in-process BenchmarkDotNet 0.15.8, ShortRun):

| Path | Budget | Measured |
|---|---|---|
| Route decision (`OfflinePolicyTable.RouteOf`) | ≤ 50 ns, 0 B | 2.0 ns, 0 B |
| Create + sign an envelope (ECDsa P-256, software key) | ≤ 150 µs, 1 allocation | 126 µs, 1.2 KB (content, envelope, signature, async state — see deviation 6) |
| Verify a bundle (manifest) | ≤ 150 µs | 119 µs |
| Trusted "now" (YFex.Time benchmark) | ≤ 30 ns, 0 B | `GetUtcNow` 27 ns, `GetTrustedNow` 32 ns, 0 B |
| Reconciliation at central (one site, no-op handler) | ≥ 200 envelopes/s | 574 µs/envelope in memory ≈ 1 740/s; 807 µs on SQLite ≈ 1 240/s |
| Site broker with 30 terminals | Phase 4 | — |

**Deviations (with reason)**
1. `SqlEngine` is declared again in `YFex.Offline.Data` (YFex.Authentication.Data has the same enum): YFex.Data has no engine type; a
   composition root using both must qualify it. Follow-up: one enum in YFex.Data.
2. **Microsoft.Data.Sqlite 9.0.1** (tests + benchmarks) and **BenchmarkDotNet 0.15.8** (benchmarks) — the same pins as YFex.Authentication.Tests
   and the other YFex benchmark projects; nothing new in shipped packages.
3. Interface changes inside Offline (not shipped yet): `SpendAsync` takes the envelope key, `GetSpendingAsync`, `GetVersionAsync`; a resolved
   review group stays as history and a later finding opens `…#2` (both queues).
4. `SqlIdempotencyStore` keeps results only for registered types: after a restart a duplicate of a command whose result type has no codec is
   still not executed again, but its caller gets the default result. A call in progress on another node is polled (200 ms) until done,
   abandoned (the waiter fails) or past the 2-minute lease (`TimeoutException`).
5. `YFex.Offline.Data` is in the AOT smoke only with `-p:SmokeData=true`: YFex.Data pulls MemoryPack, whose own warnings are third party
   (as in YFex.Authentication.Data); with it, only MemoryPack warnings and exit 0; the default smoke stays at 0 warnings.
6. Envelope creation allocates ~1.2 KB in several objects, not one buffer: the time budget is met with margin; pooling the records is not
   worth it at ≥ 1 s of human time per sale. `GetTrustedNow` is 2 ns over its 30 ns budget (YFex.Time's; reported, not changed here).
7. Delivery receipts waiting for a way out live in memory: after a restart the next accepted version is receipted again (central keeps the
   newest it saw).
8. A site grant and terminal shares of the same key add up: an app grants one or the other (documented on `GrantTerminalsAsync`).
9. Not covered here (Phase 4/5): S2 site column, the S3 broker variant, S6 "the thief cannot read the stores" (storage encryption is
   tested in YFex.Persistence/Cryptography), the 30-terminal broker benchmark.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Offline.Tests | 69 | **84** (+7 SQL stores, +2 grants/receipts, +5 scenarios incl. restart, +1 property test over 8 seeds) |
| YFex.Installations.Tests / .Central.Tests / .Rpc.Tests / .Windows.Tests | 125 / 26 / 13 / 6 | **127 / 28 / 13 / 9** (diagnostics, listening keys, I25 on the TPM, firewall) |
| YFex.Offline.AotSmoke | 0 warnings | **0** IL warnings, exit 0 (+ shares, over-grant, receipt sender); with `SmokeData=true`: MemoryPack only, exit 0 |
| YFex.Installations.AotSmoke | 0 warnings | **0** IL warnings, exit 0 (+ diagnostics, separate transport store, Windows firewall COM probe) |

Note: `MeshAcceptanceTests.Bundles_from_a_friend_are_verified_like_bundles_from_central` failed twice and then passed 3/3, and passed on the
unchanged code too: it pulls before the friend is reachable (timing). Follow-up for Installations: wait for `Seen` first.

**Open question for the user (raised 2026-10-19, no code): second factors and offline sign-in.** AN-P5 lets an app require an
authenticator code for some roles (`SecondFactor(m => m.RequireFor(Manager, Administrator))`). Offline sign-in checks only the kept password
verifier on a terminal (and, in Phase 4, code + PIN/secret at the broker), so such a manager could sign in offline with the password alone.
Options: (a) **allow and flag** — the offline session carries a "second factor not checked" flag and central raises one finding per session
(recommended, with b as a per-site limit); (b) **offline profile only** — those accounts get the site's offline profile, not their own
permissions, until central is back; (c) **refuse** offline sign-in for those roles (a store may lose its only approver during an outage);
(d) **verify the TOTP offline** — needs the TOTP seed on store PCs (the broker would hold every manager's seed: rejected unless the user wants
it). Decide before Phase 4 builds the broker's site sessions.

**Next:** Phase 4 (broker services on the four peer channels, `YFex.Hosting` with `AddYFexOfflineBroker()`, ranking on
`ReachabilitySummary`), after the user's answer on the question above.

### 2026-10-20 — Phase 4 decision list (docs only, no code)

The user asked how budgets behave (optional? shared between terminals with central and broker off? who wins at sync?) and asked to
record the gaps as decisions. Added a decision table at the top of §3 Phase 4: D1 second factor (the question above), D2 feed order
without the broker, D3 SNTP, and three new ones — **D4** weighted per-terminal shares, **D5** build `IFactReconciler<T>` (designed in
08 §3, not built), **D6** an optional arbiter for competing Requests/claims (today: first to reach central wins). Also noted there: a
`.Consumes` command with no grant on the terminal is refused offline by default, and budget use is never gossiped between terminals.
Also observed: the Windows firewall check is Windows-only; Linux/macOS report `FirewallRuleState.Unknown` and ranking relies on measured
inbound connections (installations 09; Linux/macOS still not run).

### 2026-10-20 — D1, D3, D5, D6 decided; D7, D8 added (docs only)

User answers: **D1** (a) + (b); **D3** no SNTP server (YFex.Time checked: SNTP client and W32Time monitor only); **D5** build
`IFactReconciler<T>` in Phase 4; **D6** keep "first to arrive", arbiter only for a real case. **D2** and **D4** explained again, still open.
New from the user's questions: **D7** verifier theft at the broker/terminal (same PIN as central, unpeppered copies, unencrypted bundle
history) with five hardening steps; **D8** no offline step-up today (`RequireRecentAuthentication` commands fail at replay). Android
device authorization (QR enrollment, network/timer re-authorization) is analysed in a separate session; ownership is mostly Installations
and Authentication.

### 2026-10-20 — D2 and D4 decided (docs only)

**D2:** (b) the latest change wins (per feed key, latest trusted decision time, tie by installation id) while the broker is off on
`Mesh = AllInstallations` sites; the broker or central re-orders by that time on return and decides; entries are flagged for review. The
user accepts the clock-manipulation risk for this rare case (broker and central both off). **D4:** even split stays; weights only for a
real case. Still open: **D7** (verifier-theft hardening) and **D8** (offline step-up).

### 2026-10-20 — D7 decided (docs only)

**D7:** all five hardening steps. The user asked how a terminal signs in with broker and central down if passwords are "not stored":
they still are (a verifier of the password from the last online login, ≤ 7 days, ≤ 20 users), now wrapped with the PC's TPM key; step 3
only refuses to start when that store is not encrypted. Still open: **D8** (offline step-up).

### 2026-10-20 — D7 settings and D8 decided; Phase 4 decisions complete (docs only)

**D7** agreed on condition that it is configurable: existing per-site `OfflineLimitKeys` plus three new settings (require a TPM key for
verifiers, verifier-bundle versions kept at central, Authentication's "PIN at central only from an enrolled device"); two fixed safety
floors (no PIN on a standalone terminal, encrypted credential store). **D8** as recommended: a signed step-up record in the offline
session (broker: PIN; standalone terminal: password), cited by envelopes and checked by central against the command's `maxAge`.
**All Phase 4 decisions (D1–D8) are now made.** Later: ⚑O7 (Phase 5, with Installations I5); Android handheld online-only or offline
(analysis session, question 1).

### Phase 4a — DONE 2026-10-21 (Claude; the user said "you can start it" after D1–D8)

**Built**

| Item | Where |
|---|---|
| `OfflineChannels` (`offline.session/coordination/custody/feed`, `Sharing` = the four site-wide, for central's `RosterSettings.Channels`) and wire v1 of the channels (`BrokerProtocol`: request, reply with status `Answered/Dormant/Refused/NotServed/Malformed`, refusal = error kind + text, push, live push = epoch + previous sequence + push, catch-up request/answer); golden-pinned; texts as UTF-8 bytes (`OfflineCanonicalText`) so pt-BR refusals work under `InvariantGlobalization` | `YFex.Offline/Broker/BrokerProtocol.cs` |
| Terminal side: `OfflineBrokerLink` (calls the coordinator, retries Busy/timeout/lost connection with the **same id**, `InDoubt` after the attempts; serves the channel to receive pushes, delivers each push once and in order, catches up on a gap, a new connection or a new epoch — outside the push handler, which holds the connection's frame loop), `OfflineBrokerLinks` (four), hosted catch-up on coordinator change and every 30 s; registered by `AddYFexOffline(o => o.Terminal())`, options `BrokerLinks(...)` | `YFex.Offline/Broker/OfflineBrokerLink.cs`, DI |
| New project **`YFex.Offline.Broker`**: `OfflineBrokerChannel` (answers only while `IOfflineBrokerRole.IsActive` — `RosterBrokerRole`: self is `SiteBroker` and `Active` in the signed roster; same-site friends only; `Map(operation, handler)`; one run per (terminal, call id), a repeat during the run waits for it, a decided reply — answer or refusal — is kept in `IBrokerRequestLedger`), `OfflineBrokerPushes` (`PublishAsync` keeps the push in `IBrokerPushStore` and sends it live to subscribers; catch-up subscribes; "append + pick targets" and "read + subscribe" are serialized so nothing falls between them), in-memory ledger and push store (latest state per topic + stream + audience, new epoch per store), `AddYFexOfflineBroker()` | `src/YFex.Offline.Broker` |
| Coordinator = the `Active` broker only (`FriendObservations` skips `Standby`) | `OfflineConnectivity.cs` |
| Installations (additive): `Friend.RosterStatus` (members' roster status), `PeerChannelRegistration` + `services.AddPeerChannel(...)` (module channels next to `Peers(...)`, so the host composes modules independently), harness `HarnessInstallation.RosterStatus`; the flaky `MeshAcceptanceTests.Bundles_from_a_friend…` now waits for the friend's beacon (3/3) | YFex.Installations |

**Deviations**
1. Ledger and push store are in memory in 4a: a broker restart forgets decided calls and pushes (terminals notice the new epoch and
   catch up). The durable broker store comes with custody (4c); operations that must survive a restart keep their own durable state.
2. Terminals push nothing to the broker yet (`OnPushAsync` ignores); the feed's terminal pushes and peer-to-peer entries are 4e.
3. Pushes and catch-up are per channel, not per feature: a feature picks a topic number and a stream key. A push replaces the kept
   push of the same topic, stream and audience (pushes describe state, not a log); the custody journal is not a push stream.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Offline.Tests | 84 | **91** (+7 `BrokerTests`: once per id and repeat = first reply, refusals repeat, dormant broker + promotion, lost reply retried with the same id runs once, live pushes in order + targeted + catch-up after a partition, push store epochs, golden wire) |
| YFex.Installations.Tests / .Central / .Rpc / .Windows | 127 / 28 / 13 / 9 | unchanged, all pass |
| YFex.Offline.AotSmoke | 0 warnings | **0** IL warnings, exit 0 (+ broker wire, ledger, push store, `AddYFexOfflineBroker` resolved from a real container) |
| YFex.Installations.AotSmoke | 0 warnings | 0, exit 0 |

**Found for 4b (no decision needed, design consequences):** D7.2 "seal the verifier bundle to the broker" needs an ECDH-capable
**sealing key** per installation (`RecipientSealing` is ECIES; installation keys are ECDSA signing keys, and TPM signing keys do no
ECDH) — an additive Installations key + its public key in the key record/roster. D8 needs central to evaluate
`RequireRecentAuthentication` at the **decision time** (Authorization's `PolicyEvaluator` already takes a clock).

### Phase 4b — DONE 2026-10-29 (Claude; the user said "continue the phase 4" after committing 3 + 4a as `4f022c7`)

**Built**

| Item | Where |
|---|---|
| Sealed verifier bundle (D7.2): `SiteVerifierBundleCodec` (payload = site + one part per broker, each `InstallationSealing`-sealed to that broker's key-agreement key, purpose `offline.site-verifiers`; sealed plaintext = the canonical verifier list), `SiteVerifierSet`; `SiteCredentialVerifier.SecondFactor` (D1, additive, defaults false) | `YFex.Offline/SignIn/SiteVerifierBundle.cs`, `SiteCredentials.cs` |
| Central: `SiteVerifierPublisher` (verifiers from `ISiteCredentialSource`, sealed to every non-revoked `SiteBroker` of the site with a key-agreement key — dormant ones too, so a promotion needs no new bundle — valid for `CredentialVerifierValidity`, then `IOfflinePublicationStore.PruneAsync` keeps the newest `versionsKept`, default 1); `OfflineCentral.Verifiers` + pull for site brokers only; `OfflineCentralBuilder.SiteVerifiers(versionsKept, brokers)`; built only when an `ISiteCredentialSource` is registered | `YFex.Offline.Central/SiteVerifierPublisher.cs`, `OfflineCentral.cs`, `OfflinePublications.cs`, SQL prune in `YFex.Offline.Data` |
| Re-wrap (D7.1): `OfflineVerifierWrap` — per check `Q = h(d)·G`, `ECDH(key, Q)`, HMAC tag; the key is an `IRecipientKey` named `offline/verifier-wrap` (TPM first through `CngRecipientKeyStore`); raw verifier bytes are cleared after the re-wrap; `ISiteVerifierStore` (+ in-memory, `KeyValueSiteVerifierStore`) keeps only `WrappedSiteVerifier`s and lockouts | `YFex.Offline/SignIn/OfflineVerifierWrap.cs`, `SiteVerifierStore.cs`, `YFex.Offline.Persistence` |
| `offline.session` wire v1 (golden): `Verify` (code + secret → ticket, subject, scope, kind, second factor, profile-only, or rejected / locked out / not available), `Sign` (ticket + snapshot version + terminal flags + latest end → broker-signed `OfflineSession`), `StepUp` | `YFex.Offline/SignIn/SiteSessionProtocol.cs` |
| Broker: `SiteSessionService` (lockout shared with terminals through `OfflineLockout.AfterFailure`; single-use tickets bound to the terminal, 2 min; sessions with `ShortSecret` for a PIN and `SecondFactorNotChecked` (D1); per-site `SecondFactorProfileOnly` enforced at `Sign`; `RequireHardwareVerifierKey` refuses a non-hardware wrap key, otherwise a warning), `SiteVerifierTracker` (follows the `BundleStore`, opens its sealed part), `AddSiteSessions()` with the new `IOfflineBrokerModule` hook (modules map their operations when the channels are created) | `YFex.Offline.Broker/SiteSessions.cs`, DI |
| Step-ups (D8): `OfflineStepUp` (domain `yfex.offline.step-up.v1`, golden-style strict codec), signer and verifier; `OfflineSignIn.StepUpAsync` (broker PIN/secret when the broker signed the session, else the terminal's password); kept in `IOfflineSessionStore` and sent in `OfflineSubmission` tag 2 (tag 1 unchanged without step-ups); central `AcceptStepUpAsync` (session's verifier, installation, person, within the session), stored (`offline_step_ups`, schema version 2), and `OfflineExecutionRequest.AuthenticatedAt` = newest step-up at or before the decision, else the session start; the executor adds `auth_time`/`acr` (single factor) and `OfflineDecisionClock` makes the policy evaluator judge `RequireRecentAuthentication` (and site presence) at the decision time | `YFex.Offline/SignIn/OfflineStepUp.cs`, `Replay/OfflineSubmission.cs`, `YFex.Offline.Central/Reconciliation`, `YFex.Offline.Central.Rpc` |
| Terminal in `Site` mode: `OfflineSignIn` verifies through the broker (`site` link, `siteMode` = connectivity `Site`) and falls back to its own verifier when the broker is unreachable or has none for the code; D1 on terminals too (`OfflineCredentialCapture.SecondFactor`, credential codec tag 2) | `YFex.Offline/SignIn/OfflineSignIn.cs`, `OfflineCredentials.cs`, DI |
| D1 at central: one `session.second-factor-not-checked` finding per session (not per envelope) | `OfflineReconciliation.AcceptSessionAsync` |
| D7.3: `KeyValueOfflineCredentialStore` and `KeyValueSiteVerifierStore` refuse any store but `EncryptedKeyValueStore` (or the in-memory one of tests). D7.4: `MinimumPasswordLength` default 10. New limit keys `RequireHardwareVerifierKey`, `SecondFactorProfileOnly` | `YFex.Offline.Persistence`, `Limits/OfflineLimit.cs` |

**Deviations**
1. Envelopes do not cite their step-up: central takes the newest step-up of the envelope's session at or before the decision time. Same
   result, and the signed, chained envelope codec keeps its version.
2. Step-ups go up in direct submissions only; the relay path (`offline.session` relay item through a friend) does not carry them yet.
3. Broker tickets and "sessions I signed" are in memory: after a broker restart a step-up of an older session answers `NotAvailable` and
   the terminal proves the person with its password. The durable broker store comes with custody (4c).
4. The wrap key comes from an `IRecipientKeyStore` the host registers (Installations creates its key-agreement store internally); the
   host module must register `CngRecipientKeyStore` (machine scope, hardware preferred) next to `AddSiteSessions()`.
5. Publishing a new verifier bundle is the app's call (`OfflineCentral.Verifiers.PublishAsync(site)` after a PIN change, an account
   disabled, a broker enrolled); no automatic trigger yet.

**For other repos (not Offline's code):** YFex.Authentication — `AuthenticationSiteCredentialSource` should set `SecondFactor` (D1) and
the realm option "PIN sign-in at central only from an enrolled installation of the person's site" (D7.5) is still to build.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Offline.Tests | 97 | **110** (+11 `SiteSessionTests`: wire golden, submission tags, sealed bundle opens only on its brokers + newest only, re-wrap useless off the PC + raw cleared, hardware-key floor, broker PIN session + single-use tickets, lockout doubling, D1 flag + profile-only, expired bundle, terminal ↔ broker on the harness LAN with PIN step-up and password fallback, D7.3/D7.4/D1 on terminals; +2 `ReplayTests`: `RequireRecentAuthentication` judged as of the decision against the session or its step-up, one D1 finding per session + step-up refusals; schema pin test now pins version 2) |
| YFex.Offline.AotSmoke | 0 warnings | **0** IL warnings, exit 0 (+ sealed bundle, re-wrap, broker verify/sign/step-up, submission tag 2, `AddSiteSessions` resolved); `-p:SmokeData=true` exit 0 (schema applies 2 versions) |

### 2026-10-30 — D2 refinement flagged (docs only, not built)

The user confirmed the recommendation for D2: the coordinator ranks the sources while it is live and signs the ranking, and the rank only
breaks ties inside the trusted-time window. The design and the options are in §3 Phase 4, under the D2 table. Build in 4e; factor A
waits for 4f. The O7 pairing policy is still open (user decision pending).

### Phase 4e (part) — D2 refinement: ranking snapshot and ordering key, 2026-10-30 (Claude; started from the Phase 4 session, ahead of 4c/4d)

The 4e sub-spec was written first (§3 Phase 4, under the D2 refinement). Only the parts with no dependency on 4c/4d/4f were built; 4b is
still uncommitted in the checkout and was not touched.

**Built**

| Item | Where |
|---|---|
| Ordering key: `SiteFeedDecision`, `SiteFeedOrdering.Contends` / `TryWinner` (anchor = the latest entry, tie lower id; contenders within margin + both uncertainties, an unknown uncertainty counts 0; highest score, then lower id), `DefaultMargin` **5 min**, range 0–24 h | `YFex.Offline/Feed/SiteFeedOrdering.cs` |
| Factor B: `SiteFeedFactor` (ids 1 standing, 2 time health, 3 history), `SiteFeedCandidate` (+ `From(Friend, hardwareEvidenceKey)`), `SiteFeedStanding.Of` (status × 4 + confirmed × 2 + hardware key, max 11) | same file |
| Snapshot `offline.feed.ranking`: `SiteFeedRankingContent` (+ `Compile`, factor B only for now), `SiteFeedRanking` (+ `ScoreOf`, binary search, 0 when not listed), `SiteFeedRankingCodec` wire v1 (golden-pinned, strict), domain `yfex.offline.feed-ranking.v1`, `SiteFeedRankingSigner` (coordinator's evidence key; the caller keeps the version), `SiteFeedRankingHolder` (`Accept` = verify the coordinator, key, revocation and signature, then keep only a strictly newer version; lock-free) | `YFex.Offline/Feed/SiteFeedRanking.cs` |

**Deviations**
1. The signature domain is `SiteFeedRankingCodec.Domain`, not an `OfflineDomains` entry, and nothing is registered in DI: `OfflineDomains.cs`
   and the DI extensions carry 4b's uncommitted changes. Move the domain and register the holder/signer once 4b is committed.
2. The margin is a parameter (default `SiteFeedOrdering.DefaultMargin`), not yet a per-site `OfflineLimitKeys` entry (`OfflineLimit.cs` also
   has 4b changes).
3. The AOT smoke's `Program.cs` (also 4b-modified) does not call the new types yet. They pass the library's AOT/trim analyzers
   (`IsAotCompatible`, 0 warnings in Release), and the smoke itself still publishes clean.

**Open items**
- **4c:** the coordinator's durable version counter, the terminal's persisted copy of the held ranking (in memory now), and the durable
  store of feed entries.
- **Rest of 4e (after 4c/4d):** pushing the snapshot on `offline.feed` (topic number + stream `offline.feed.ranking`), terminal-to-terminal
  entries on `Mesh = AllInstallations` (one latest entry per installation and feed key, then `TryWinner`), `ISiteFeedProjection<T>`, the
  "decided without coordinator" flag and central's review, and the re-order by the broker/central on return with the same `TryWinner`.
- **4f:** factor A (time health from the site time table) as factor id 2. The N days for A and C are still open.
- **Later:** factor C (history).
- **Installations:** the evidence-key protection (hardware/software) is not carried to the coordinator. Only central's enrollment policy
  knows it, so the caller supplies it; it needs a roster or diagnostics field.
- **Decided later that day (user):** the N days default to 7 (factor A) and 30 (factor C), and the margin to 5 min, all configurable per
  site; only the broker ranks (no central-only ranking); the version-continuity proposal (versions per broker, set from its trusted time)
  waits for the user's confirmation, and so does the hardware-evidence-key question. Both are in the 4e sub-spec.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Offline.Tests | 110 | **123** (+13 `SiteFeedRankingTests`: golden content + signed form, round trip + strict decoding, invalid contents, the standing table, compile + `ScoreOf`, later wins outside the window, rank wins inside it even when older, equal rank → lower id (and no ranking), window edge to the tick, an unknown uncertainty does not widen the window, the same winner across all 24 arrival orders (a pairwise fold would give two different winners), margin range, signer → holder: newer only, tampered, wrong coordinator, forged by a terminal, signed after revocation) |
| YFex.Offline.AotSmoke | 0 warnings | **0** IL warnings (`IlcTreatWarningsAsErrors`), exit 0; the new types are not called yet (deviation 3) |

### 2026-10-30 — 4b and the 4e part committed; the deferred 4e moves; D2 version continuity and the evidence key decided (Claude)

The user said yes to committing first: **`96d2f50`** (4b, site sessions) and **`d34ddc3`** (4e part, ordering key + ranking snapshot), both in
YFex.Offline. The user then confirmed the **version-continuity proposal** and chose **(a)** for the hardware evidence key (Installations adds
the evidence-key protection to each signed roster entry; additive, owned by YFex.Installations, not built here: until then the caller
still supplies it to `SiteFeedCandidate`).

**Built (the four moves deferred in "Phase 4e (part)", plus continuity)**

| Item | Where |
|---|---|
| Domain moved: `OfflineDomains.FeedRanking` (`yfex.offline.feed-ranking.v1`, same label, so the golden bytes and signatures do not change); `SiteFeedRankingCodec.Domain` removed | `YFex.Offline/OfflineDomains.cs`, `Feed/SiteFeedRanking.cs` |
| DI: `SiteFeedRankingHolder` on terminals (`AddYFexOffline`), `SiteFeedRankingSigner` on the broker (`AddYFexOfflineBroker`, needs the installation keys when resolved) | DI extensions |
| Per-site limit keys: `offline.feed.ordering.margin` 5 min · 0–24 h, `offline.feed.ranking.time-health.look-back` 7 d · 1–30 d, `offline.feed.ranking.history.window` 30 d · 7–180 d (in `OfflineLimitKeys.All`); `SiteFeedOrdering.DefaultMargin`/`MaximumMargin` now read the margin key | `Limits/OfflineLimit.cs`, `Feed/SiteFeedOrdering.cs` |
| Version continuity: `SiteFeedRankingSigner.CreateAsync(candidates)` sets the version to the trusted signing time in Unix ms, at least the last version + 1 (`LastVersion`; no durable counter); `SiteFeedRankingHolder.Accept` keeps a ranking newer than the held one **from the same coordinator**, or any verified one when the held one came from another coordinator | `Feed/SiteFeedRanking.cs` |
| AOT smoke calls the signer, codec, holder, `TryWinner` with the margin key and both registrations | `tests/YFex.Offline.AotSmoke/Program.cs` |

**Tests:** YFex.Offline.Tests 123 → **125** (+2 `SiteFeedRankingTests`: versions follow the broker's clock, +1 within the same ms, a restart
with the clock set back waits, a promoted broker with an older clock replaces the old broker's ranking, the old broker's later ranking is
refused; the three limit keys and the domain). AOT smoke 0 warnings, exit 0.

### Phase 4c — DONE 2026-10-30 (Claude; task from the Phase 4 session, after the user's yes to commit 4b + 4e part)

**Built**

| Item | Where |
|---|---|
| **R1** `SiteReceipt` (broker · installation · key · installation sequence · SHA-256 of the envelope bytes · site sequence · trusted instant), signed by the broker's evidence key (domain `yfex.offline.site-receipt.v1`), wire v1 golden-pinned; `SiteReceiptSigner`; `SiteReceiptVerifier` (central, against the installation registry, revocation as of the custody time) | `YFex.Offline/Custody/SiteReceipts.cs` |
| **R2** `CentralReceipt` (installation · key · digest · broker · site sequence · outcome · error kind · detail · findings · duplicate · central time), signed by Offline's key ring at central (domain `yfex.offline.central-receipt.v1`), verified with `OfflinePublisherKeys` (`CentralReceiptCodec.Verify`), wire v1 golden-pinned; `CentralReceiptSigner` | same file; `YFex.Offline.Central/Reconciliation/OfflineReconciliation.Site.cs` |
| `offline.custody` wire v1 (golden): `HandOver` (request = `OfflineSubmission`: sessions, envelopes, step-ups; answer = per envelope `Taken` + R1 / `Refused` / `Conflict`), `Status` (per key `Unknown` / `Held` / `Settled` + R2) | `YFex.Offline/Custody/SiteCustodyProtocol.cs` |
| Site forward: `OfflineSubmission.Custody` (one R1 per envelope) = submission **tag 3**; `OfflineReceiptBatch.Signed` (one R2 per receipt) = batch **tag 3**; tags 1 and 2 unchanged (golden tests still pass) | `YFex.Offline/Replay/OfflineSubmission.cs` |
| Broker: `ISiteJournal` (+ `InMemorySiteJournal`), `SiteCustodyService` on `offline.custody` (the caller's own envelopes only, signature checked against its evidence key in the signed roster — `OfflineEnvelopeVerifier.VerifyWithKey`; appends serialized, so the site sequence is the arrival order; a repeat gets the first R1, other bytes under the same key are a `Conflict`; sessions and step-ups kept for central), `SiteJournalForwarder` (sessions, step-ups, then envelopes in site-sequence order, batches of 256, keeps each R2, prunes settled entries after 7 days; runs whatever the roster status), hosted forwarder woken after each hand-over; `AddSiteCustody()` | `YFex.Offline.Broker/SiteCustody.cs`, DI |
| Uplink: `ISiteJournalUplink` (core) and `RpcSiteJournalUplink` + `AddSiteJournalUplink()` over the existing `IOfflineIntake.SubmitAsync` with the broker's installation assertion (no new RPC method, so the checked-in proxy is unchanged) | `YFex.Offline/Custody/SiteJournalUplink.cs`, `YFex.Offline.Rpc/OfflineTerminal.cs` |
| Central: `OfflineReconciliation.ProcessSiteAsync` (asserted sender must be a `SiteBroker`; sessions/step-ups of its site's installations; per envelope R1 must be the sender's, valid, for these bytes, of an installation of the broker's site — else `Refused`; then the usual pipeline with deduplication; every envelope gets a signed R2; an envelope whose session has not arrived ends the batch there, order kept); the intake routes tag 3 there; `AddYFexOfflineIntake` sets `Receipts` from `IOfflineSigningKeys` | `OfflineReconciliation.Site.cs`, `OfflineCentralRpc.cs` |
| Terminal: `CustodyEntry.HandedOver` (R1) / `.Settled` (R2), `IOfflineCustodyStore.MarkHandedOverAsync/MarkSettledAsync`, durable entry **tag 2** (tag 1 bytes unchanged without receipts); `OfflineHandOver` (in `Site` mode with a coordinator: hands over what the coordinator does not hold — `Standalone` envelopes included — with the undelivered sessions and step-ups; asks `Status`; keeps R2 only when it verifies against the publisher keys; `Unknown` forgets R1 so the next pass hands it over again; an R1 of another broker is handed over again to the current one) + hosted service (5 s, and at once when the mode becomes `Site`); `AddYFexOffline` registers it when a custody store and friends exist; options `HandOver(...)`; `IOfflineBrokerRequests` (implemented by `OfflineBrokerLink`) as its seam | `YFex.Offline/Custody/OfflineHandOver.cs`, `Replay/OfflineCustody.cs`, `YFex.Offline.Persistence`, DI |
| Replay: `OfflineUploader` answers a settled entry from R2 without calling central; `OfflineRelayForwarder` skips entries the broker holds | `YFex.Offline.Rpc/OfflineTerminal.cs` |
| **Durable broker store** (the 4a/4b wait): `KeyValueSiteJournal` (index rebuilt on first use; the last site sequence survives pruning), `KeyValueBrokerRequestLedger` (7-day TTL), `KeyValueBrokerPushStore` (epoch kept, so terminals catch up from their position after a broker restart), `KeyValueSiteSessionRegistry` (`ISiteSessionRegistry`, new: the sessions the broker signed, for step-ups; `SiteSessionService` takes it); `AddKeyValueBrokerStores()` | `YFex.Offline.Persistence/KeyValueBrokerStores.cs`, `YFex.Offline.Broker/SiteSessions.cs` |

**Closes:** S1 (outage → custody to the broker on R1 → central decides each envelope once, in site order, 0 findings → R2 reaches the
terminals → the outbox drains without calling central) and "broker lost mid-outage → terminals re-send to central, nothing twice" (a
replacement broker answers `Unknown`; the terminal re-sends straight to central, the envelope the old broker had forwarded is a duplicate;
the new broker forwarding the same envelopes gets duplicates).

**Deviations**
1. Verification **tickets** stay in memory (single use, 2 min): a broker restart between `Verify` and `Sign` asks for the PIN again. Signed
   sessions, the ledger, pushes and the journal are durable.
2. The broker checks an envelope's signature against the roster key but not revocation (central's step 1), and the terminal checks only
   R1's fields, not its signature (central verifies R1; the terminal keeps its copy until R2 anyway).
3. R2 travels **down by pull** (`Status`, every 5 s in `Site` mode), not as a custody push; a push topic can come with 4f's benchmark if
   30 terminals polling turns out costly.
4. Sessions and step-ups the broker forwarded stay "undelivered" on the terminal until its next direct submission (central deduplicates
   them by id).
5. Central does not keep a per-broker site-sequence continuity record (no gap finding); order is kept because the broker forwards in order
   and central stops a batch at a pending session.
6. Forwarding follows the site sequence only; the lane priority of 08 §4 (deadline facts first) applies to direct submissions, not yet to
   the site path. The journal cap with an alert at 80 % (10 §2) is not built.
7. R1 carries no coordination results yet (reservations, claims): 4d adds them (a new R1 tag).
8. `YFex.Offline.Persistence` now references `YFex.Offline.Broker` (the durable broker stores live there, next to the terminal ones).

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Offline.Tests | 125 | **130** (+5 `ReplayTests` custody: S1 end to end; broker lost mid-outage; broker refusals (other terminal, bad signature, conflict) and central refusals (not a site broker, R1 for other bytes, forged R1) + R2 verification; durable broker stores across a restart (journal, sequence never reused, ledger, push epoch and replace, signed sessions, custody tag 1/2); golden custody wire (R1, R2, hand-over answer, status, forward tag 3, batch tag 3) + strict decoding) |
| YFex.Offline.AotSmoke | 0 warnings | **0** IL warnings, exit 0 (+ hand-over and status over `offline.custody`, R1 codec, `KeyValueSiteJournal`, forward tag 3, `AddKeyValueBrokerStores` + `AddSiteCustody` resolved); `-p:SmokeData=true` exit 0 |

**Next:** 4d coordination (reservations from the site grant, broker-signed slices, unique claims, `ApprovalTokenKind.Site` + daily cap,
`IFactReconciler<T>`); waits for the user's go.

### Phase 4d — DONE 2026-10-30 (Claude; task from the Phase 4 session, on top of the uncommitted 4c and 4e moves)

**Decisions asked first (user, 2026-10-30):** (1) the additive Authorization change for site tokens may be made in YFex.Authorization; (2) a
permission allowed offline without a declared approval allowance gets a **count cap, default 20 per rolling 24 h**, per site (0 = no site
approvals without an allowance); (3) slices are split **evenly** by default, and the **mode is configurable per site** (`fixed` per terminal, or
any policy the broker host registers).

**Built**

| Item | Where |
|---|---|
| `offline.coordination` wire v1 (golden): `Reserve` (ticket · key · subject · amount · allow-over → `Reserved/ReservedOver/Exhausted/NoGrant` + the site grant version), `Claim` (→ `Claimed/AlreadyClaimed`), `Release`, `Bind` (ticket → envelope key), `Slice`, `Approve` (code · secret · permission · digest · amount → `Approved` + site token / `Rejected/LockedOut/NotAvailable/CapReached`) | `YFex.Offline/Coordination/SiteCoordinationProtocol.cs` |
| Broker-signed **slices**: `AllowanceSliceContent` (broker · grant with holder = terminal and parent = SHA-256 of the site grant payload · parent version · signed-at · valid until = the grant's), domain `yfex.offline.allowance-slice.v1`, wire v1 golden; `AllowanceSliceSigner`, `AllowanceSliceCodec.Verify` (the roster's evidence key); cited as kind `offline.allowance.slice` (stream `key/site/terminal`) **plus** the parent site grant; `IAllowanceSlicePolicy` with `EvenAllowanceSlices` / `FixedAllowanceSlices`; `IAllowanceSliceStore` (+ in-memory, `KeyValueAllowanceSliceStore`) | `YFex.Offline/Coordination/AllowanceSlices.cs`, `YFex.Offline.Persistence` |
| Broker: `SiteCoordinationService` on `offline.coordination` (one lock: reservations per subject from the site grant minus slices; a slice of a `*` entry leaves room for the largest per-subject reservation; claims per (namespace, value) kept 7 days; tickets bind reservations and claims to the envelope; slices once per terminal and grant version, capped by what is left; site approvals: code + PIN through `SiteSessionService.VerifyApproverAsync` (the sign-in lockout), count cap per permission in a rolling 24 h, the permission's allowance when declared with `Cap(permission, key)`, `ApprovalTokenKind.Site` signed by the evidence key, 15 min); `ISiteCoordinationStore` (+ in-memory, `KeyValueSiteCoordinationStore` in `AddKeyValueBrokerStores()`); `BundleStoreSiteAllowanceGrants`; `AddSiteCoordination()` | `YFex.Offline.Broker/SiteCoordination.cs`, DI |
| **R1 v2**: `SiteReceiptContent.Coordination` (reservation / reservation-over / claim, with the grant version); wire **tag 2** only with results, tag 1 unchanged (golden); `SiteCustodyService` asks `CoordinationOfAsync` at hand-over | `YFex.Offline/Custody/SiteReceipts.cs`, `SiteCustody.cs` |
| Terminal: `ISiteCoordinator` / `OfflineSiteCoordinator` (null on no answer → decide alone); `OfflineAdmissions` reserves and claims at the broker in `Site` mode (no `ProvisionalClaim` flag then), falls back to its own grant, then to its slice; `CommitAsync(ticket, envelope)` binds, `Abandon` releases at the broker; `SiteApproval` vs `DeviceApproval` flag by token kind; `OfflineSiteApprovals` (terminal client); hosted slice refresh (5 min and on entering `Site`) for `OfflinePolicyTable.ConsumedAllowances`; `AddYFexOffline` registers them, `UseRpc` passes the coordinator | `YFex.Offline/Coordination/SiteCoordinator.cs`, `Admission/OfflineAdmissions.cs`, DI, `YFex.Offline.Rpc` |
| Central: slice citations are counted like grant citations; R1 v2 is checked: spending more than (or for another subject than) the broker reserved = High `allowance.over-reservation`; `SiteApprovalSignatures` (signer is a registered `SiteBroker`, evidence key, not revoked at issue; issuer check = same site as the deciding installation), registered by `AddYFexOfflineIntake` | `OfflineReconciliation*.cs`, `YFex.Offline.Central.Rpc` |
| **D5** `IFactReconciler<TCommand>` + `.ReconciledBy<T>()` (facts only); runs after a fact applied **with findings**, can add app findings (`OfflineFactFindings`); a throwing reconciler = High `fact.reconciler-failed`, the fact stays applied; `FindingKind`, `FindingSeverity`, `OfflineFindingDraft` moved to the core assembly (namespace `YFex.Offline`) so commands can declare it | `YFex.Offline/Replay/OfflineFactReconciliation.cs`, `Commands/` |
| Per-site limit keys: `offline.approvals.site.daily-count` 20 · 0–1000, `offline.allowance.slices.mode` `even` (`even`/`fixed`/a registered policy), `offline.allowance.slices.share` 33 % · 0–100 | `Limits/OfflineLimit.cs` |
| Authorization (additive, uncommitted there): `IApprovalIssuerCheck`; `ApprovalTokenVerifier` no longer drops `Site` — a site token needs a registered verifier of the kind that implements the issuer check, otherwise `OtherInstallation`; all other offline-token checks unchanged | `YFex.Authorization/Approvals/` (logged in authorization 09) |

**Closes:** S8 (R$ 600 site grant: slices R$ 99 + R$ 99, R$ 120 reserved in `Site` mode, R$ 450 refused, exactly the rest fits, standalone
spends only its slice — 120 + 282 + 99 + 99 = 600) and S9 (the broker refuses the second counter; standalone the claim is provisional and
central raises the High duplicate; R1 v2 names the claim).

**Deviations**
1. The S8 default share is 33 %, so the slices are R$ 99 (the scenario says R$ 100 = one third). Set the share to 33.3333 for the exact third.
2. Claims and tickets at the broker are kept for 7 days from the claim (`SiteCoordinationOptions.Retention`), not "until R2": a longer outage
   drops older claims and central's duplicate check takes over.
3. Binding a ticket to its envelope is a separate best-effort call (`Bind`, when the envelope is created); if it is lost, R1 has no
   coordination results for that envelope (no finding at central). Releases are best effort too (the broker keeps a forgotten ticket for the
   retention, which only under-spends).
4. Central checks slice spending against the **site grant** it cites with it (the slice amount itself is not published to central); a terminal
   that overspends its slice is caught only once the site total is exceeded.
5. The broker does not check that the approver holds the permission (it has no snapshots of other people); central re-checks it as of the
   decision, as for device tokens. The site daily cap is not recounted at central. The per-token cap (`OfflineApprovalCap`) is the rules' and
   is the same for site and device tokens; separate caps per kind would be a Rules change.
6. Standalone claims handed over later are not re-claimed at the broker (06 §4 "the broker then accounts their claims"): central decides them.
7. `SiteApprovalSignatures` lives in YFex.Offline.Central.Rpc, which the AOT smoke does not reference; the smoke verifies the site token's
   signature directly.

**Tests**

| Suite | Before | After |
|---|---|---|
| YFex.Offline.Tests | 130 | **139** (+9 `ReplayTests` coordination: S8, S9, R1 v2 over-reservation at central, site approvals (PIN, lockout, rolling count cap, flags, central accepts only its site's broker), allowance cap, D5 reconciler (runs with findings only, adds a finding, a failure is a finding, facts only), slice modes/share/custom policy/`*` room, durable coordination + slice stores across a restart, golden coordination wire + slice + R1 v2 and strict decoding) |
| YFex.Authorization.Tests | 104 | **105** (+1 site tokens through the issuer check) |
| YFex.Authorization.Installations.Tests | 44 | 44, all pass |
| YFex.Offline.AotSmoke | 0 warnings | **0** IL warnings, exit 0 (+ reserve/claim/bind/slice/approve over `offline.coordination`, R1 v2, slice verify, `OfflineFactFindings`, `AddSiteCoordination` + `KeyValueSiteCoordinationStore` resolved); `-p:SmokeData=true` exit 0 (its 14 IL warnings are all inside MemoryPack, pulled by YFex.Data) |

**Next:** the rest of 4e (feed), then 4f; both in their own sessions.
