# YFex.Offline — offline operating model: topologies, site broker, trust and sync — Design

> **Status:** PROPOSAL (2026-10-02, revised the same day: name `YFex.Offline`, identity namespace `YFex.Installations`, site
> **broker** instead of a local application server, store-shared changes). **Phase 0 DONE 2026-10-03**; **Phase 1 DONE 2026-10-15** (repo `C:\Dev\YFex\YFex.Offline`); **Phase 2 DONE 2026-10-15**; **Phase 3 DONE 2026-10-19** (3a–3d 2026-10-17: declarations, offline login, envelopes and replay, central reconciliation; 3e 2026-10-19: durable stores `YFex.Offline.Data`, acceptance S2/S3/S5/S6, property test, benchmarks, per-terminal grants, automatic delivery receipts; log in 11 §5); Phases 4–5 not started; **⚑O31 decided 2026-10-19** (broker services on Installations peer channels). **Phase 3 decisions ⚑O12, O13, O15, O17 and the
> identity-provider question confirmed 2026-10-17**; Authorization 3b/4b are built, so Phase 3 is unblocked.
> **Plan updated 2026-10-14** after the libraries this design depends on were built: the phases in
> [11](11-phases-and-acceptance.md) now hold only Offline's own work, and 11 §2 lists every dependency with its status. Decisions
> marked **⚑O#** need user confirmation before any code change (table at the end). No NuGet package is added: cryptography comes from
> YFex.Cryptography, time from YFex.Time, network conditions from YFex.Networking, and identity, keys, trust, peer channels and signed
> bundles from YFex.Installations, all BCL-only.
> **Audience:** the user first, then the sessions that implement the phases. Read this file, then the file your phase needs
> ([11-phases-and-acceptance.md](11-phases-and-acceptance.md)).
> **Normative vs sketch:** names in code blocks are normative unless marked `// (sketch)`. Where a type now lives in another library,
> the source of that library wins over the code blocks here.
> **Builds on:** YFex.Installations ([../installations](../installations/README.md), I0–I4 built), YFex.Cryptography
> ([../cryptography](../cryptography/README.md), Phases 0–3 built), YFex.Time ([../time](../time/README.md), T0–T3 built), YFex.Networking
> ([../networking](../networking/README.md), Phases 0–6 built), authorization consolidation (⚑Z3 actor ✅, ⚑Z9 snapshot — signed
> version in 3b, ⚑Z12 approvals — 4b), validation D29 (CallContext) and D34 (offline approvals), audit 05–06 (device journal, node
> chains), logging 05 (clocks), privacy 05 (root keys, sealed values, offline intake).

## Why

"Offline" is two different situations (user, 2026-10-02):

1. **One computer alone**, no connection at all: a checkout whose cable was cut.
2. **A shop LAN with a more trusted machine** (the manager's PC) that the other terminals use while the central server is
   unreachable. This must be configurable, and the local communication must be secure. The user's refinement: central's server code
   must **not** be installed on a store PC. The local machine keeps keys safely and handles auth, tokens and permissions. It is a
   **broker**, not a second application server ([02](02-topologies-and-configuration.md) §3).

YFex already queues commands (`IQueueable`) and serves cached queries (`ICacheable`), but it knows only one server and one boolean
"connected". The inventory ([01](01-current-state-and-inventory.md)) also found live defects in today's offline code:

- **F1:** the "OS key store" storage key changes on every restart (randomized DataProtection output used as key material). After a
  restart during an outage, **queued sales silently disappear**.
- **F3:** the outbox idempotency key never reaches the server, so a lost reply plus a replay gives a **duplicate sale**.
- **F4:** replays run as **whoever is logged in at replay time, in the scope of replay time**. Ana's sales become Bruno's, and a
  manager's laptop can replay store 7's sales into store 8.
- **F6/F10:** cached rule tables and the designed permission snapshot are **unsigned**. The validity checks use the device clock.
- **F7:** offline approvals are checked against the **replay** time. A Friday approval replayed on Monday fails.
- **F8:** there is **no offline login**, so nobody can open the shop without internet.
- **F14/F15** (found in Phase 0): the client never used the durable outbox, and the client's `Encryption` options were never
  read.

> **Phase 0 DONE (2026-10-03):** F1, F2, F3, F5, F12, F14, F15 and the scope half of F4 are fixed in code — log in
> [11 §5](11-phases-and-acceptance.md).
>
> **Cryptography change (2026-10-04):** every cryptographic mechanism this design uses now comes from the shared library
> **YFex.Cryptography** ([../cryptography](../cryptography/README.md), approved ⚑K1–K18). Encryption at rest already runs on it
> (cryptography Phase 0): the storage key is the key ring `device/storage` wrapped by `CngRootKeyProvider` (TPM first, now in
> `YFex.Cryptography.Windows`), values are sealed value v3 (`0x03`), and the `0x01`/`0x02` values and `.storage-key` file of
> offline Phase 0 stay readable ([05 §3](05-protecting-offline-data.md), [11 §5 addendum](11-phases-and-acceptance.md)).
> Envelopes and offline verifiers use the library's `SignedPayload`, `HashChain` and `PasswordHashing` (cryptography Phases 2–3,
> ✅ 2026-10-05). The core types `Hash256`, `KeyId`, `DetachedSignature` exist.
>
> **Installations change (2026-10-05, ⚑N6; confirmed 2026-10-07; I0–I4 built by 2026-10-14):** installation identity, keys,
> enrollment, the tenant CA operation, peer validation ("friends"), the signed-bundle format with version floors, offline pairing and
> the site-mesh substrate (peer endpoint, channels, relay, bundle gossip, site/peer time transport) moved to **YFex.Installations** —
> design [../installations](../installations/README.md) (⚑I1–I20, mesh decisions ⚑O25–O30 and ⚑T13, all confirmed). Only pairing
> (Installations I5) is not built yet. YFex.Offline keeps modes, authority, budgets, claims, envelopes, custody, the site feed,
> offline login, reconciliation, the site time table and the broker services ([installations 06 §3](../installations/06-site-mesh.md)).
>
> **Time and networking (2026-10-05 / 2026-10-07):** the trusted clock is **YFex.Time** (`TrustedTimeProvider`, `TrustedInstant`,
> built T0–T3); the per-server link state is Messaging.Rpc `IServerConnection` and the machine's conditions are core
> `INetworkConditions` / `EndpointHealth` (YFex.Networking ⚑N4). Offline defines no clock or link types of its own.
>
> **Where the files stand (2026-10-14):** 03 is superseded by installations 02/04/05 (kept for goals and alternatives); 04 §2, 05 §2
> and 05 §4.1 describe mechanisms now built elsewhere and carry pointer notes; 01 is the 2026-10-02 inventory and stays historical.

## The mental model

```
              ┌────────────────────────────── CENTRAL (the only place app server code runs) ──────────────────────────────┐
              │ tenant CA · signs bundles (manifest, revocations, snapshots, rule data, budgets, verifiers) ·             │
              │ reconciles envelopes "as of" when they happened · review queue                                           │
              └───────────────▲───────────────────────────────────────────────────────────────────▲────────────────────────┘
       envelopes, evidence    │  signed bundles, receipts                                          │ direct (no broker)
          ┌───────────────────┴──────────────────────────┐                                         │
          │ SITE BROKER (PC-GERENTE, generic service,     │◄──── mTLS (tenant CA) ────┐             │
          │ no app code, keys in the TPM)                 │                           │             │
          │ sessions · permission answers · approvals ·   │                           │             │
          │ budgets · unique claims · custody · site feed │                           │             │
          │ · time · log/audit relays                     │                           │             │
          └───────────────────────────────────────────────┘                           │             │
   TERMINALS (where offline business decisions run, with the app's OnOffline<T> handlers):
        mode Central ──(central unreachable)──► Site ──(broker unreachable)──► Standalone
        every decision = signed envelope (installation, sequence, actor, scope, versions, trusted time)
        alone: signed snapshot ≤ 72 h · own budget slice · device-signed approvals (capped, flagged)
```

Four rules make it safe:

1. **Signatures for integrity, device keys for confidentiality, central re-validation for honesty** ([05](05-protecting-offline-data.md) §1).
2. **Time is a soft bound, budgets and claims are the hard bound.** A trusted clock that never goes backward, plus pre-granted,
   partitioned budgets, lets several terminals decide at once without double spending ([05](05-protecting-offline-data.md) §4,
   [06](06-offline-authority.md) §4).
3. **Facts are accepted and flagged, requests can be refused.** A sale that happened offline cannot be undone by rejecting it at sync
   ([06](06-offline-authority.md) §3, [08](08-sync-reconciliation-conflicts.md)).
4. **The broker coordinates, terminals decide, central rules.** No app code and no central secret ever sits on a store PC
   ([02](02-topologies-and-configuration.md) §3).

## One example, end to end

```csharp
// ✍ Dev writes — aggregate configuration (the record keeps IQueueable; OnOffline<T> already exists)
b.Command<FinalizarVenda, Recibo>()
    .RequirePermission(VendaPermissions.Values.Finalizar)
    .OnOffline<FinalizarVendaOffline>()                                  // runs on the checkout
    .Offline(offline => offline
        .RecordsFact()
        .Consumes(FarmaAllowances.ConvenioCredito, subject: c => c.ConvenioId, amount: c => c.TotalConvenio));

// ✍ Composition root of a terminal (identity, keys and friends come from YFex.Installations)
services.AddYFexInstallations(installations => installations
    .Application("farma-pdv", version).Terminal().UseWindowsKeyStore().EnrollAt(centralUri).Peers());
services.AddYFexOffline(offline => offline.Terminal());
services.AddYFexMessagingRpcClient(rpc => rpc.UseInstallationAssertion().UseOffline());
// Site broker: no code — the generic YFex.Offline.Broker service, enrolled once with a code (02 §5.1)
```

**Result:** online, the sale goes to central as today, now with an idempotency key. During an ISP outage, the checkout switches to
`Site` mode after about 15 s. Ana signs in with her operator code and PIN, which `PC-GERENTE` verifies. The checkout runs
`FinalizarVendaOffline` itself. The broker first reserves the agreement credit from the store's budget, then takes custody of the
signed envelope. A manager approval is verified by the broker and signed by it. A store price change made by the manager reaches the
other checkouts through the site feed within seconds. If `PC-GERENTE` is switched off, the checkout keeps selling alone within its own
budget slice and with device-signed approvals. When the internet returns, revocations come down first, evidence goes up, then the
envelopes in sequence order. Central re-checks each one **as of the moment it happened** (who Ana was, which price table, which
approval), applies every sale once, and puts anything odd in one grouped review item.

## Files

| # | File | Content |
|---|---|---|
| 01 | [01-current-state-and-inventory.md](01-current-state-and-inventory.md) | what exists (outbox, caches, CallContext, rule sync, approvals, encryption, designs), defects F1–F13, vocabulary alignment |
| 02 | [02-topologies-and-configuration.md](02-topologies-and-configuration.md) | situations, installation vs tenancy vs actor, roles and modes, **why a broker and not a local server**, upstream route, signed site manifest, composition roots, pharmacy deployments |
| 03 | [03-trust-enrollment-pairing.md](03-trust-enrollment-pairing.md) | **superseded by [../installations](../installations/README.md) 02/04/05** except goals (§1) and alternatives (§8): installation keys, tenant CA, certificates, mutual TLS, assertion, enrollment, pairing, revocation |
| 04 | [04-connectivity-and-failover.md](04-connectivity-and-failover.md) | `IConnectivity`, discovery, routing per mode, hysteresis, in-doubt calls, custody chain, scenarios, split brain |
| 05 | [05-protecting-offline-data.md](05-protecting-offline-data.md) | threat table, signed bundles, encryption fix (F1/F2), trusted clock, offline login, replay/rollback defenses, `OfflineProvenance`, validity defaults |
| 06 | [06-offline-authority.md](06-offline-authority.md) | authority ladder, `.OnOffline<T>()` + `.Offline(...)`, facts vs requests, budgets + unique claims, **site feed (store price override)**, approvals per level, authorization/rules/tenancy offline |
| 07 | [07-what-runs-where.md](07-what-runs-where.md) | matrix per component × mode, the broker process, external services (central down, internet up), what the broker keeps and never keeps |
| 08 | [08-sync-reconciliation-conflicts.md](08-sync-reconciliation-conflicts.md) | envelope and receipts, central reconciliation pipeline, sync order, conflict catalog, priorities, findings and review queue |
| 09 | [09-pharmacy-scenarios.md](09-pharmacy-scenarios.md) | eleven end-to-end scenarios in YFex.Farma (outage, approvals, dismissal, tampering, clock, theft, pairing, agreement credit, prescription, government program, store price override) |
| 10 | [10-pitfalls-and-testing.md](10-pitfalls-and-testing.md) | pitfalls, shop-LAN operations checklist, multi-installation test harness, budgets |
| 11 | [11-phases-and-acceptance.md](11-phases-and-acceptance.md) | working rules, cross-design dependencies, phases 0–5, acceptance, doc updates, implementation log |

## Open questions (not blocking Phase 0–1)

- ~~**Identity provider:** does YFex.Farma log in through an external identity provider, or through YFex accounts?~~ **Answered
  2026-10-17:** YFex.Offline stays independent of the identity provider, which differs per project (a future external provider,
  YFex accounts, or not decided yet). Offline login reads verifiers from a credential source chosen by the app; the built-in **operator
  credential** (operator code + offline secret, PBKDF2 verifier kept at central) is optional. When an app's online login never sees the
  secret (browser sign-in with an external provider), capturing it for the terminal's cached verifier is the app's job
  ([05](05-protecting-offline-data.md) §5).
- **Which store data must terminals keep warm** in their own caches for offline handlers (catalog, prices, stock, customers,
  agreements)? This is app configuration, not library ([07](07-what-runs-where.md) §4).
- **Fiscal contingency ownership:** confirm that the fiscal module (YFex.NFe) owns contingency emission and its deadlines, and only
  consumes YFex.Offline's mode, lanes and trusted clock.
- **Mobile/browser installations:** **Android handhelds decided 2026-10-20** (⚑O32, installations ⚑I26–I32): full offline terminals.
  **Blazor WASM** stays a `Central`-only client without site mode.

## Decisions to confirm (⚑O)

Confirmed by the user on 2026-10-02: the name **`YFex.Offline`**, a separate core namespace **`YFex.Installations`** for identity,
**"Installation"** instead of "Node", and the **broker model** (no app code on the site machine). They are recorded in O1, O20 and O24.

| ⚑ | Question | Recommendation | Alternative (cost) | Details |
|---|---|---|---|---|
| **O1** | Where does this live? | ✅ **`YFex.Offline`**: core namespaces **`YFex.Installations`** (`InstallationId`, `InstallationIdentity`, `LocalInstallation`, `InstallationRole`; used online too) and **`YFex.Offline`** (modes, trusted time, bundles, provenance, envelope) + sibling repo **`C:\Dev\YFex\YFex.Offline`** (`YFex.Offline`, `.Broker`, `.Central`, `.Windows`, `.Testing`, `.Tests`, `.AotSmoke`; `.Data` later) | inside Messaging.Rpc (Audit/Logging/Privacy could not use installation identity or the trusted clock without the transport); inside Authorization (trust of machines ≠ permissions of people) | 02 §2, 11 §3 **2026-10-05 (⚑N6):** the core namespace `YFex.Installations` stays, but the identity types, keys and trust come from the new sibling repo **`YFex.Installations`** (⚑I1), not from the YFex.Offline repo — [../installations](../installations/README.md). **2026-10-14:** built (I0); the core `YFex.Offline` namespace keeps modes, provenance and envelope types only (bundles are core `YFex.Installations`, time is core `YFex.Time`), and the repo has no `.Windows` package (keys = `YFex.Installations.Windows`) — 11 §3 Phase 1. |
| **O2** | Roles and brokers per site? | ✅ **confirmed 2026-10-15 (amended by the user).** Closed role set `Central/SiteBroker/Terminal`; **one active broker per site, chosen automatically by central** while online and **saved in the signed roster** (`RosterEntryStatus.Active`, the others `Standby`). Central ranks the dormant broker installations of the site (02 §4.1) and changes the choice only while online, so no broker state ever has to be shared between machines. An admin may **pin** a machine. During an outage nothing changes: no election, no promotion; without the saved broker, terminals are `Standalone` | elections during an outage (consensus, leases, terms, fencing, state hand-over: possible later per-site opt-in on top of the `Standby` list, 02 §4.1); continuous replication between machines (Raft-like, too heavy); a person chooses the broker (users forget, the broker ends up on a PC that is off) | 02 §2, §4, §4.1 |
| **O3** | Who do terminals talk to while central is up? | ✅ **confirmed 2026-10-15: `CentralFirst`** by default (broker = warm standby); `ThroughSiteBroker` per site for bad uplinks. Since ⚑O26 logs, audit and envelopes reach central through any friend in both routes | always through the broker (manager PC off = whole store standalone) | 02 §4 |
| **O4** | LAN trust? | **Tenant private CA** at central, installation certificates with URN SANs (installation, site, role), **mutual TLS 1.3** pinned to the tenant root, no hostname checks; to central: public TLS + signed **installation assertion** | PSK (one stolen terminal exposes all); trust on first use; plain TLS + bearer tokens; AD CS (most pharmacies have no domain) | 03 §3–§4, §8 **2026-10-05:** mechanism (CA operation, certificate profile with `urn:yfex:scope:` instead of `…:site:`, validator) implemented by YFex.Installations (⚑I6, ⚑I7); assertion to central in `YFex.Installations.Rpc`, online too (⚑I11). **Built I1–I3 (2026-10-07…14).** |
| **O5** | Installation keys? | Two ECDsa P-256 keys per installation (transport, evidence), **CNG persisted non-exportable, TPM first**; one identity shared by approvals, audit, logging, privacy (fixes F11); `IDeviceKeyStore` (Rules) → `IInstallationKeyRegistry`; central may require hardware keys for brokers | one key for everything (lifetimes clash); keep per-library keys (three enrollments per terminal) | 03 §2. Mechanism: YFex.Cryptography `ISigningKeyStore` + `CngSigningKeyStore` (cryptography ⚑K8, Phase 2) **2026-10-05:** decision stands; `IInstallationKeyStore` and the central `InstallationKeyDirectory` (was `IInstallationKeyRegistry`) live in **YFex.Installations** (⚑I4, ⚑I15) — [../installations](../installations/README.md) 04 §1. **Built I1:** `IInstallationKeyStore` (`installation/transport`, `installation/evidence`), `IInstallationKeyDirectory`; the Rules `IDeviceKeyStore` move is Authorization 4b. |
| **O6** | Discovery? | Manifest addresses → signed UDP **beacon** → typed address; discovery never implies trust; YFex.Devices remoting reuses the certificates and the validator | manual IP only (DHCP changes break stores); full mDNS (more code, same trust result) | 04 §2, 03 §8 **2026-10-05:** discovery mechanics in YFex.Networking; one signed beacon kind `yfex.installation` owned by YFex.Installations (⚑I12, agreed with the Networking session). **Built:** Networking Phase 5 + Installations I3 (2026-10-14). |
| **O7** | Pairing while central is unreachable? | Allowed via **SAS code comparison** + manager permission; provisional certificate ≤ 72 h from a short-lived site issuing CA held by the broker; **disabled by default**, enabled per site | never (a dead terminal waits for the internet); always on (more attack surface) | 03 §6 **2026-10-05:** implemented by YFex.Installations (⚑I14); this policy unchanged. **Not built yet:** Installations I5; Offline Phase 5 follows it. |
| **O8** | Format for offline data from central? | One **`SignedBundle`** (ECDsa P-256 over canonical binary header + payload hash), per-stream monotonic versions, audience, validity, **version floors**; `BundleKind` app-extensible | per-feature formats | 05 §2. Header uses core `KeyId`, `Hash256`; signature = core `DetachedSignature` made by YFex.Cryptography `SignedPayload` (cryptography ⚑K8) **2026-10-05:** format, verifier and version floors move to YFex.Installations (core `BundleHeader`/`BundleKind`, ⚑I9) because the roster is a bundle and the mesh gossips bundles; Offline keeps its kinds and payloads. **Built I1/I4:** `SignedBundle`, `BundleVerifier`, `BundleStore`, `BundlePublishers` (kinds bound to a publisher), `bundles` gossip. |
| **O9** | Encryption at rest fix? | Random storage key generated once, **wrapped** by `IRootKeyProvider`; package-free **`CngRootKeyProvider`** (also proposed for Privacy instead of the `ProtectedData` package); envelope v2 **AAD** = store + key | keep DataProtection (F1 stays); DPAPI via the `System.Security.Cryptography.ProtectedData` package (needs approval) | 05 §3. ✅ Implemented 2026-10-04 through YFex.Cryptography (cryptography Phase 0): `CngRootKeyProvider` in `YFex.Cryptography.Windows`, ring `device/storage`, sealed value v3, no DataProtection |
| **O10** | Device time? | **`TrustedTimeProvider : TimeProvider`**: max(wall + anchor offset, high-water + monotonic); rollback detected and flagged; anchors from central and broker. **2026-10-05: implemented by the shared YFex.Time library (`../time`, ⚑T1–T12); the broker serves and compares site time (05 §4.1)** | trust the wall clock (expiry and order become editable) | 05 §4 |
| **O11** | Offline validity defaults? | ✅ **confirmed 2026-10-15 (amended by the user): one mechanism for built-in and custom limits.** Typed keys **`OfflineLimit<T>`** (values: TimeSpan, int, long, decimal, bool, text); built-in keys in **`OfflineLimitKeys`**: permission snapshot 72 h (= ⚑Z9), site authorization 72 h, verifiers 7 d, allowance 24 h, rule data flagged after 7 d (`MaxOfflineAge`), certificate grace 7 d; apps declare their own keys with a default and hard bounds. **Precedence:** per-site admin value saved at central (`Sites.Commands.Configure`, signed into the manifest) → app default in code (`AddYFexOfflineCentral(c => c.Limits(...))`) → library default; an admin value outside the bounds is **refused when saved**. Limits based on a rule go to YFex.Rules through the optional package **`YFex.Offline.Rules`** (it fills an `OfflineFacts` situation field — mode, hours without central, snapshot age, coordinator present, flags — via `[SituationSource]`, so YFex.Offline never references YFex.Rules); admins edit rule parameters and tables in Rules.Admin, signed as `rules.data` bundles; central may also derive a built-in value per bundle from a rule set | fixed defaults per site only (no app keys: every app invents its own config file); limits as rules only (a rule engine on the critical path of every bundle) | 05 §7, 02 §5 |
| **O12** | Offline login? | ✅ **confirmed 2026-10-17 (amended by the user).** Verifiers come from an app-chosen credential source (built-in operator credential optional; identity provider per project); the numbers (20 cached users, verifier age, lockout 5 tries / 5 min doubling, session 12 h) are **`OfflineLimitKeys`** (⚑O11 precedence and bounds); capturing the secret when the app's online login never sees it is the app's job; the **offline profile** is built in Phase 3, off by default. *Original recommendation:* Terminal: **cached verifier** from the last online login (PBKDF2-SHA256 600 000, ≤ 20 users, ≤ 7 d, passwords only); broker: **site verifier bundle** (operator code + offline password set in YFex); **short PINs only when the broker verifies** them; lockout | no offline login (F8 stays); PINs everywhere (offline brute force of a stolen verifier) | 05 §5 |
| **O13** | How do messages declare offline behaviour? | ✅ **confirmed 2026-10-17.** As recommended, plus: `.Offline(...)` is an extension method of YFex.Offline stored through a new **domain-neutral typed feature slot** on the Cqrs `CommandConfigurationBuilder`/`CommandPolicy` (Cqrs never names Offline); `Unavailable` is a new additive `ErrorKind` case (after `ApprovalRequired = 7`) with a reason. Existing **`.OnOffline<T>()`** = the terminal's handler; new **`.Offline(o => o.RecordsFact().Consumes(…).ClaimsUnique(…).SharedWithSite<TProjection>().Lane(…))`** in aggregate configuration; defaults: non-`IQueueable` = central-only, `IQueueable` = queued request; new additive **`ErrorType.Unavailable`** (wire v2) | attributes on records (violates "configuration over attributes") | 06 §2, 04 §3 |
| **O14** | How do terminals decide without double spending? | ✅ **confirmed 2026-10-15 (amended by the user): budgets are optional, off unless the app declares an `AllowanceKey`.** Central grants per site; the broker cuts signed slices per terminal (Phase 4); **`.Consumes(key, subject, amount)`** reserves before the handler and **`.WhenExhausted(Refuse \| RequireApproval \| AllowAndFlag)`** decides what happens when the budget runs out (Phase 3). Amounts: code default → admin-edited rule data in Rules.Admin per tenant or store → an app query capping it to the real balance (`.FromRules<T>().CappedBy(...).DefaultPerSubject(...)`). Phase 2 builds the grants and the `offline.allowance` bundle. Unique claims at the broker and caps in the rules engine (`OfflineFacts`) stay as recommended | caps only (exposure = cap × installations); no offline credit | 06 §4 |
| **O15** | Approvals at the site? | ✅ **confirmed 2026-10-17.** As recommended (device tokens Phase 3, site tokens Phase 4), plus: the approver is re-checked **as of** the operation time through a new additive, optional Authorization interface (`HeldAtAsync` over a permission change history; without it the verifier keeps today's "holds now"); the site's daily cumulative approval cap **reuses ⚑O14 allowances** (an `AllowanceKey` per approval permission, consumed by the broker). New **`ApprovalTokenKind.Site`** signed by the broker's evidence key, approver verified by the broker; site daily budget; all tokens verified **as of the operation time** (fixes F7); device approvals need the approver's cached snapshot on that terminal | device tokens only (weaker, smaller caps, even when the manager is in the room) | 06 §6 |
| **O16** | Replay envelope and custody? | ✅ (confirmed with Phase 0, 2026-10-03; envelope built in Phase 1) **`OfflineEnvelope`** (idempotency key, installation + sequence, actor, session, scope, provenance, used bundle versions, trace, signature); replays authenticated as the **installation** with the actor from the envelope (fixes F4); custody moves only on signed receipts; terminal keeps a backup until central's receipt; server **`IIdempotencyStore`** (fixes F3) | keep `OutboxEntry` + connection identity (F3/F4 stay) | 08 §2, 04 §5 |
| **O17** | Facts vs requests and review? | ✅ **confirmed 2026-10-17.** As recommended, plus: Authorization's `UnverifiedApproverAction.Defer` = a fact is applied with a finding, a request is **parked** in the review queue (reviewer Accept re-runs it, Reject refuses it with a receipt); the durable central stores (`IIdempotencyStore` ≥ 30 days, review queue, site settings, publications, delivery receipts) are built on YFex.Data **inside Phase 3** (`YFex.Offline.Data`). **`.RecordsFact()`**: facts always applied, checks become **findings** (severity, grouped), optional `IFactReconciler<T>`, never expire in the outbox; **`IOfflineReviewQueue`** at central (UI with the Avalonia phase) | treat everything as requests (rejecting a sale cannot undo it) | 06 §3, 08 §7 |
| **O18** | Rule versions used offline? | ✅ **confirmed 2026-10-15.** Terminal cites the version (incl. store overrides); central accepts the newest version **delivered** to that installation (signed **delivery receipts** prove "delivered"), flags a rollback or anything older than `MaxOfflineAge`; **facts are never re-priced** (difference = finding). Per-command override: `.WhenRulesDiffer(RuleVersionPolicy.KeepAndReport \| Reevaluate)` or `.WhenRulesDiffer<TResolver>()` with `IRuleVersionResolver<TCommand>`; a source generator should later compile `.Offline(...)` chains into the `OfflinePolicy` table (Phase 3). Phase 2 needs only delivery receipts and `MaxOfflineAge` | re-evaluate facts with today's tables (re-prices history) | 06 §8 |
| **O19** | Logging/audit relays? | **The broker is the relay** for both (signed end to end, relay needs no trust); one LAN endpoint, one identity | separate relays per library (three pairings, F11) | 07 §1–§2 **2026-10-05:** superseded by the `relay` channel of YFex.Installations: signed items travel to central through **any friend** that reaches it, the broker being one of them (⚑O26, confirmed 2026-10-07, built I4) — [../installations](../installations/README.md) 06 §2.1. |
| **O20** | What is the site machine? | ✅ **Generic broker service**, no app code, no central secrets, no business data; Windows service under its own account, keys in the TPM; updated with the YFex.Offline protocol, not with app releases; terminals keep their own caches | local application server with site handlers and a business database (app code and data on a store PC; rejected by the user) | 02 §3, 07 §2, §4 **2026-10-05:** the generic broker host composes YFex.Installations (enrollment, peer endpoint, TLS) + the Offline broker services; recommendation to run those services on peer channels instead of a second Fusion peer (installations 05 §4.1, Offline's decision in Phase 4 → **⚑O31**). |
| **O21** | Identifiers created offline? | ✅ **confirmed 2026-10-14.** Client-generated **`Guid.CreateVersion7()`**; human numbers from per-installation series. Security rules (08 §5): an id is neither a secret nor a clock; central never overwrites on a create whose id already exists | server ids + temporary-id remapping table (complex, error-prone; signed envelopes cannot be rewritten) | 08 §5 |
| **O22** | Fix F1–F5/F12 now? | **Yes, Phase 0**, independently of the other decisions (live data-loss and duplicate-sale bugs) | wait for the whole design (bugs stay live) | 11 §3 |
| **O23** | Which changes may be shared through the site feed? | ✅ **confirmed 2026-10-15 (amended by the user): three layers, each only narrowing the one before.** (1) The developer marks a command **`.SharedWithSite<TProjection>()`**; (2) an admin switches it on or off **per site** in the signed manifest (`SiteManifest.SiteFeed`), but never on for an unmarked command; (3) the command's normal rules and validation run before the envelope exists, so a change they refuse is never shared. Payloads are signed and readable by the site's terminals; personal fields stay sealed for central. **Nothing marked means no feed.** *Original recommendation:* only messages on the manifest's **allow-list** (`OfflineLimits.SharedWithSite`). Start with store-tier price within central's guardrails (e.g. never above PMC), blocking a product/lot for sale, customers registered at the counter. Payloads signed and readable by the store's terminals; personal fields already sealed for central | any queueable message shared (more conflicts, wider blast radius); nothing shared (checkouts disagree on prices during an outage); payloads encrypted end to end to central (then other terminals cannot project them) | 06 §5, 09 S11 |
| **O24** | Name of the identity concept? | ✅ **Installation** (`InstallationId`, `InstallationIdentity`, `InstallationRole`, `IInstallationKeyStore`, permissions `installations.enroll/revoke/pair`). It is distinct from a Tenancy scope (whose data) and from the actor (who) | "Node" (too vague), "Device" (clashes with YFex.Devices), "Machine" (wrong for cloud instances) | 02 §2 |
| **O25–O30** | Site mesh (who listens, what any friend may carry, scarce resources, no shared caches, sharing per channel, discovery vs trust) | ✅ confirmed 2026-10-07, recorded in [installations README §7](../installations/README.md); built in Installations I3–I4 | — | installations 05, 06 |
| **O31** | How do terminals reach the broker services (sessions, coordination, custody, site feed)? | ✅ **confirmed 2026-10-19.** **Installations peer channels** `offline.session`, `offline.coordination`, `offline.custody`, `offline.feed` on the broker's existing peer endpoint: one port, one framing, no ASP.NET Core or Kestrel on the store PC, the same path terminal-to-terminal traffic already uses. Offline keeps the message contracts and handlers; the envelope's signed bytes (with the idempotency key) travel unchanged | a second Fusion RPC peer over its own TLS host on the broker (04 §3 as first written: two ports, Kestrel on the store PC, a second trust check to keep in line with `InstallationTrust`) | 04 §3, 07 §2, installations 05 §4.1. **Decided by the user 2026-10-19** after a scenario walk-through: app view models do not change (`[Live]` still means central; broker values reach them as local `IEventBus` events and local Offline services read with `[Live(Cache = Local)]`/`[Subscribe<T>]`); Offline builds catch-up on (re)connect + versioned pushes in both directions and its own retry keyed by the envelope id (safety comes from that key either way); terminal-to-terminal `offline.feed` only when the roster says `Mesh = AllInstallations`. Listening installations use a software transport key (⚑I25, same day) |
| **O32** | Android handhelds offline? (added 2026-10-20, installations [11](../installations/11-mobile-handhelds.md)) | ✅ **confirmed by the user 2026-10-20** ("it must handle offline scenarios"). Handhelds are ordinary **terminals** here: `Site` mode through the broker and `Standalone`, offline sign-in (⚑O12), budget slices (⚑O14), envelopes signed by their Keystore evidence key, custody and receipts. No new Offline concept. Two **optional, configurable** additions: (1) the off-site rule for **offline sessions** (re-authenticate after N hours without a fresh witness-signed presence, the same rule as authentication ⚑AN27); (2) each envelope **records the presence it relied on** (witness id + time, or none), so reconciliation can flag off-site offline work. The exact field is decided when built (envelope version tag rule) | handhelds online-only (refused by the user) | installations 11 §4.2, §5.3, §7 |
