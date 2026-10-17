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
decisions are confirmed, so Phase 3 can start. Phase 4 needs Phase 3 and the ⚑O31 decision. Phase 5 waits for Installations I5. Installations I5 does
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

### Phase 3 — The lone terminal, end to end

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

- **Decision first: ⚑O31** (README) — broker services on Installations peer channels (`offline.session`, `offline.coordination`,
  `offline.custody`, `offline.feed`; recommended — registered with `Peers(p => p.Channel(channel, handler, FriendScope...))`, the
  roster deciding who may open them) or a second Fusion peer over its own TLS host (04 §3).
- `YFex.Offline.Broker`: the generic host (no app code, ⚑O20) composing `AddYFexInstallations(i => i.SiteBroker()…)` (machine-scope
  keys, peer endpoint always on) + the Offline broker services; enrollment through `IInstallationEnrollment` with the code as a start-up
  argument until YFex.Installer exists.
- Broker services: failover state machine for terminals (04 §4–§6), custody + site journal + signed receipts R1/R2, coordination
  (budget reservations and slices, unique claims; the coordinator is the roster's `Active` `SiteBroker`, ⚑O27, no election),
  site feed + `ISiteFeedProjection<T>` (store price override, S11, ⚑O23) on `offline.feed`, site sessions and the verifier bundle,
  `ApprovalTokenKind.Site` (with Authorization 4b).
- **Broker choice (⚑O2, 02 §4.1):** dormant broker installations on every eligible PC; central ranking policy (opening-hours uptime,
  can listen, terminal reachability, hardware key, admin pin) writing `Active`/`Standby` into the roster while online; re-rank with
  hysteresis; `Sites.Commands.Configure(PinnedSiteBroker: …)`. Needs from Installations: terminals report friend reachability,
  firewall-rule presence (I5) and a fix for ⚑I25 on candidates.
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
