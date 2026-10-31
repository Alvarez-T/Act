# 11 — Mobile handhelds: QR enrollment, offline work, presence and revocation

> **Status:** DESIGN, decisions ⚑I26–I32 **confirmed by the user on 2026-10-20** (README §6.4). Installations: I6a built (below), I6b started (revocation, 2026-10-28).
> **2026-10-27:** the crypto part is built (`YFex.Cryptography.Android`, crypto 14 §9; emulators only, real handheld pending). Phase I6
> is split into I6a (Android package, attestation, online QR path; not blocked) and I6b (broker path, presence, revocation; after I5), 08 §3.
> **2026-10-27 (I6a built, 08 §5):** `YFex.Installations.Android`, attestation at enrollment (§6), lost-key → `KeysLost` + wipe (§6),
> `EnrollmentLink` v1 (§3) and the online sponsor path (§4.1, with a commit/reveal before the SAS). I6b (§4.2, §5, §7): revocation from a site terminal through central and self-revocation are built (2026-10-28, §7); the offline site
> revocation, the broker path and presence are not.
> **Why:** a pharmacy needs Android handhelds (stock counting, sales on the floor) that are authorized once by scanning a QR code
> on a trusted terminal. They must keep working during an internet outage. They should not need a new scan while they stay
> authorized. A configurable rule (e.g. "re-authenticate after N hours outside the store") applies when they leave the store.
> **Owners:** Installations (device, QR link, confirmation, presence, revocation), YFex.Cryptography (`YFex.Cryptography.Android`,
> ⚑K19), Authentication (session profile and off-site rule, ⚑AN26–AN28), Authorization (`RequireSitePresence`, ⚑Z20), Offline
> (handhelds as offline terminals, ⚑O32). Networking only gives hints (§5.4).
> **Normative vs sketch:** names in code blocks are sketches until the phase that builds them (08 Phase I6).

## 1. What a handheld is (⚑I26)

- A handheld is an **installation with role `Terminal`** (02 §2: kiosks and handhelds are terminals, the role set stays closed). Its
  application name (e.g. `farma.coletor`) tells the app's rules which device type it is.
- It is a **full friend of its site** after enrollment: it chains to the tenant root, is listed in the roster, and gets the
  `SameSite` relation. So it can work in Offline `Site` mode (through the broker) and in `Standalone` mode, like any checkout.
- It is a **peer-channel client only**. It never runs a peer endpoint (Android limits background listeners, and there is no
  inbound firewall to manage). It connects to the broker and, when needed, to its sponsor terminal (§2).
- Keys: `installation/evidence` and `installation/transport` in the Android Keystore through **`YFex.Cryptography.Android`** (⚑K19,
  `AndroidKeystoreSigningKeyStore`). `installation/key-agreement` (I5) is a sealed file (`StoreRecipientKeysInFiles`), because there is
  no Keystore recipient store yet.
  The package `YFex.Installations.Android` adds `UseAndroidKeyStore()`, the state directory under `NoBackupFilesDir` (09 §2: never a
  backed-up folder) and an Android hardware hint source (04 §7: only a hint, never trust).

```csharp
// (sketch) the handheld app's composition root
services.AddYFexInstallations(installations => installations
    .Terminal()
    .UseAndroidKeyStore());                 // StrongBox → TEE → software Keystore; records the real KeyProtection (§6)
```

## 2. Who may create the QR: the sponsor

- The **sponsor** is the trusted installation whose screen shows the QR. It can be **any installation of the same site** that is
  `Enrolled` (not `Provisional`, not revoked) and runs a UI (a checkout, the manager's desktop app). A visitor (e.g. the regional
  laptop) cannot sponsor for a store unless the site roster lists it for that.
- The **person** at the sponsor must hold `installations.enroll` (central reachable) or `installations.pair` (offline path, §4).
  Online, the app's command also requires a recent step-up (`RequireRecentAuthentication(5 min, MultiFactor)`, built). Offline, the
  same person re-enters their PIN (Offline re-authentication).
- **As built (⚑I33, 2026-10-28):** "same site" is a choice of `CentralInstallationOptions.MaySponsor`: `SponsorRules.SameScope`
  (default, own scope only), `SponsorRules.SameSite(siteRank)` (the store and its sub-scopes such as `…/2=deposito`),
  `SponsorRules.SameScopeOrBelow`, or a custom function. Roster-listed visitors as sponsors come with `SponsoredBy` in I6b.
- The sponsor is **recorded** in the registry and the roster entry of the handheld (`SponsoredBy`). It is shown in the admin
  UI ("handhelds added by CAIXA-01"), it confirms the enrollment (§3) and it can be a presence witness (§5).

## 3. The QR code: an enrollment link (⚑I27)

The QR carries everything a new handheld needs to find and **pin** the right central, broker and sponsor. It never carries a
long-lived secret.

```
 EnrollmentLink v1 (canonical binary, base32 in the QR's alphanumeric mode; ≈ 250 bytes → QR version ≤ 13)
   version = 1
   tenant root hash        SHA-256 of the tenant root certificate (16-byte prefix)        ← pins the trust anchor
   expires at              trusted time of the code issuer (2–5 min, default 5)
   central                 base URL + online enrollment code (80 bits, 4 groups)         ← present when the sponsor reached central
   broker                  installation id + transport-key hash + 1–3 addresses (from the roster)
                           + offline pairing code (80 bits)                               ← present when the site allows offline pairing (⚑O7)
   sponsor                 installation id + evidence-key id (+ address when it runs a peer endpoint)
```

- **Not in the QR:** passwords, tokens, the tenant hint key, any signing key, personal data.
- Both codes are **single use**, short-lived, and bound to the sponsor that asked for them. When one path is confirmed, the
  sponsor cancels the other code at once.
- The handheld app **refuses a link whose central host is not in its own configuration** (protection against a printed QR that
  points to a fake server).
- Codes live 2–5 minutes and nobody types them, so they use **4 groups (80 bits)**, not the typed default of 2 groups
  (`CentralInstallationOptions.CodeGroups`).

## 4. Enrollment, step by step (⚑I28, ⚑I29)

### 4.1 Online path (the handheld reaches central)

```
 MANAGER on CAIXA-01 (sponsor)            CENTRAL                                   HANDHELD (NotEnrolled, keys in Keystore)
 1. "Add handheld COLETOR-03"
    (installations.enroll + step-up) ──► CreateEnrollmentCode(scope = store or …/2=deposito,
                                           Terminal, "farma.coletor", "COLETOR-03",
                                           5 min, Sponsor = CAIXA-01, Confirm = true)
 2. shows the QR  ◄────────────────────  code (80 bits)
                                                                                   3. scans; creates evidence + transport keys
                                                                                   4. challenge → EnrollmentRequest signed by
                                         ◄──────────────────────────────────────────    BOTH keys (proof of possession, built)
                                         5. code valid → state PENDING (no certificate yet)
 6. "COLETOR-03 (Samsung XCover 7)        ──► push to the sponsor
    wants to join — 482 913"                                                       6. shows the same "482 913"
 7. person compares the digits, confirms ──► Confirm(code, SAS seen)
                                         8. issues the certificate (90 days), roster updated
                                                                                   9. checks the chain against the root hash in
                                                                                      the QR, stores the sealed state: Enrolled
```

- **SAS** (short authentication string) = 6 digits of SHA-256(domain ‖ evidence SPKI ‖ transport SPKI ‖ handheld id ‖ issuer nonce ‖
  handheld nonce). This is the same computation as offline pairing (04 §5, ⚑I14), so I5 and I6 share the code.
- **Pending** is a central registry state, not an `InstallationStatus`: the handheld stays `NotEnrolled` until step 9. A pending
  request expires with the code. A rejected one is refused and audited (`Installations.EnrollmentRejected`).
- Rate limit: at most N pending requests per sponsor/site (default 3), so a stolen QR cannot flood the manager's screen.

### 4.2 Offline path (the store has no internet)

The same QR, with the broker part. The handheld pairs with the broker (⚑I14, policy ⚑O7: **the site must enable offline pairing**):

1. The sponsor asks the **broker** (over its peer channel) for a pairing code. The person holds `installations.pair`.
2. The handheld connects to a broker address from the QR (TLS server authentication). It checks that the broker's certificate
   chains to the root hash **and** has the broker id and key hash from the QR. Then it sends `PairingRequest` with the code.
3. The broker holds the request as **pending** and pushes it to the sponsor. Both screens show the SAS, and the person confirms
   on the sponsor.
4. The broker's site issuing CA issues a **provisional certificate** (≤ 72 h, `Terminal`, same scope). The handheld is
   `Provisional`: a `SameSite` friend that **cannot issue approvals**, and its envelopes carry `ProvisionalInstallation` (Offline).
5. At reconnect, central confirms (normal certificate, same id, same sponsor) or rejects (revoked; its offline work becomes review
   findings, offline 11 Phase 5).

### 4.3 After enrollment

- **No new QR** while the handheld stays enrolled. The certificate renews itself at ⅔ of its 90 days (04 §4) whenever central is
  reachable, directly or through any friend's relay.
- A new QR is needed only after a reinstall, app data wipe, factory reset, revocation, or a provisional pairing that central
  rejected.

## 5. Presence: "this handheld is in the store now" (⚑I30)

### 5.1 Why not the network

"Same Wi-Fi" is a **weak** signal. Staff share the Wi-Fi password, often with customers too. The SSID and the gateway MAC can be
copied by a fake access point. Every store uses the same private ranges (192.168.0.x). The public IP is shared (CGNAT) and changes.
`NetworkFingerprint` and signed beacons stay **hints** (⚑I7, ⚑O30). A beacon is broadcast to everyone, is not bound to the
handheld, and can be relayed live. Presence therefore comes from a **signed statement of a trusted machine of the store**.

### 5.2 The presence statement

```
 SitePresence (canonical, domain "yfex.installation.presence.v1")
   subject       handheld installation id
   witness       installation id of the signer
   scope         the witness's site scope
   issued at     witness trusted time
   challenge     central's assertion challenge when the handheld has one, else a handheld nonce (offline)
   binding       SHA-256 of the handheld certificate seen in THIS mTLS handshake (the witness signs only for the peer it authenticated)
 + witness evidence-key signature (≈ 30 ms on a TPM)
```

- **Witnesses** (user, 2026-10-20): the site's **active broker** and the handheld's **sponsor terminal**. The roster can add more
  terminals (`PresenceWitness` switch per installation).
- A witness must run a peer endpoint. The broker always does. A terminal does when the site mesh is `AllInstallations` or its roster
  entry is a presence witness (that terminal then needs the inbound firewall rule, ⚑O25). Stores without a broker (Direct sites)
  use the sponsor as their witness.
- The handheld asks a witness through a peer channel `installations.presence` (request/response) every 5 minutes while it is on the
  LAN. Default validity: **15 minutes** (configurable per site in the roster).
- **Verified by:** central, when the handheld attaches the latest statement to its installation assertion. Central checks the witness
  key in `InstallationKeyDirectory`, that the witness is the active broker or the subject's sponsor (or a listed witness), the same
  scope, the challenge and the age. The result is in `InstallationCallContext.Current.Presence`. Offline, the broker and the handheld's
  own Offline/Authorization checks read the same statement.

### 5.3 What consumes it

| Consumer | Uses presence for | Decision |
|---|---|---|
| Authentication | the optional **off-site rule** of a session profile: re-authenticate after N hours without fresh presence; `presence_at` claim in the access token | ⚑AN27 |
| Authorization | `RequireSitePresence(maxAge)` on a command (e.g. stock adjustment only inside the store) | ⚑Z20 |
| Offline | the same off-site rule for offline sessions; envelopes record the presence used (witness, time), so reconciliation can flag off-site work | ⚑O32 |

All rules are **optional and configurable** (user, 2026-10-20). Without configuration nothing changes.

### 5.4 What Networking still does

On Android the app supplies `NetworkFingerprintEvidence` (SSID needs the location permission) and the `IMulticastLock` for beacons.
Their only job is to say "you are probably in store 12, try its broker now". They never count as presence.

## 6. Key protection (⚑I32)

- **Hardware when the device has it, never required by default** (user, 2026-10-20: "only if recommended"). The order is StrongBox,
  then TEE-backed Keystore, then the software Keystore.
- The handheld reports the real `KeyProtection`. When the device offers **key attestation**, the chain is sent with the
  enrollment request and recorded. Central shows "hardware key: yes / no / not attested" in the admin UI.
  *As built in crypto:* the Keystore attests a key **only when it is created**, over a challenge given then
  (`AndroidKeystoreSigningKeyStore.GetOrCreateAsync(name, challenge)`, `ReadAttestationChain(name)`). So the handheld creates its keys
  after central's enrollment challenge (the order `EnrollmentClient` already uses) and passes that challenge. Today's
  `IInstallationKeyStore` and enrollment request have no place for it (I6a, 08 §3).
- `IEnrollmentPolicy` can still **require** hardware or attestation for an application (opt-in per tenant). The library default is to
  allow and record.
- **Recorded is not verified (⚑I34, deferred 2026-10-28):** nothing checks the chain against Google's attestation roots or revocation
  list yet, so a recorded chain is only a claim and the admin UI shows "not verified". The checking policy comes after the real-handheld
  run, opt-in and central-only (README §6.5).
- Transport key and TLS (09 §4 R7): **both keys stay in the Keystore.** `CopyWithPrivateKeyAsync` gives a TLS 1.3 client credential
  from a Keystore key (emulators, crypto 14 §9.5). Every key allows `DIGEST_NONE`, because Conscrypt signs the handshake hash with
  `NONEwithECDSA`. The software transport key (⚑I22/R2, ⚑I25 pattern) is kept only as a fallback, in case the real handheld disagrees.
- Signing takes 4–6 ms on the emulator (software KeyMint). StrongBox can be much slower. Measure it on the real handheld. If an offline
  sale or count must sign faster than the device allows, use the session subkey pattern (crypto 06, 04 §1).
- Lost keys: Keystore keys disappear on uninstall, "clear data", factory reset and restore onto another device. The root provider then
  throws `AndroidKeystoreKeyLostException`. Installations turns that into "re-enroll" (§4.3), never into a crash.
  ⚑I35 (built 2026-10-28): a state that still opens while only the signing keys are gone (or replaced) also reads as `KeysLost`.

## 7. Revocation (⚑I31)

| Who | From where | Effect |
|---|---|---|
| a person holding `installations.revoke` | central admin UI | built: revocation bundle pushed at once; central refuses the handheld's assertion; refresh tokens bound to it fail; Authentication also ends its sessions at once (⚑AN28) |
| the same permission | **any trusted terminal of the site** (the sponsor or another), central reachable | the terminal calls central's revoke; same effect. **Built 2026-10-28:** `CentralEnrollmentService.RevokeFromSiteAsync(caller, installation)`; the host checks the person's permission and passes the installation its assertion authenticated; refused for an unknown/revoked/provisional caller, the caller itself, an unknown id, or another site (`CentralInstallationOptions.MayRevokeFromSite`, default same scope) |
| the same permission | any trusted terminal, **store offline** | site revocation signed by the broker (04 §4), effective on the site's friends at once, forwarded to central when it is back. **Built 2026-10-29 (⚑I42):** the terminal asks the broker over `installations.site-revocation` (`SiteRevocationClient`); the broker checks the person through the app's `ISiteRevocationApprover`, signs its own `installations.site-revocations` list with its evidence key, the site drops the device within a beacon interval, and central adopts the entry (`By = SiteBroker`, the broker's time) when the broker reaches it |
| a person holding `installations.revoke` | **the handheld itself** ("remove this device") | a revocation request signed by the handheld's evidence key, then local state wipe; useful when a device is handed back or reassigned. **Built 2026-10-28:** `InstallationSelfRevocation` (`peers.AllowSelfRevocation(wipe)`) sends a `SelfRevocationRequest` as relay item `installations.self-revocation` (directly or through a friend); central (`AcceptSelfRevocations()`) checks it against the registry's evidence key and revokes; the device wipes only after central's acknowledgement (held by a friend or unreachable → nothing wiped, the app is told; open ⚑I43) |

- A lost handheld that stays **offline** is limited by: its offline session lifetime (≤ 12 h, ⚑O12), its budget slices (⚑O14), the
  provisional limit (≤ 72 h) if it was paired offline, and the screen lock. Its envelopes are checked at reconciliation against the
  revocation time.

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| **QR photographed / shoulder-surfed** | single use, 2–5 min life, cancel button, **pending + SAS confirmation** on the sponsor, code created only after the person's step-up |
| **Replay of the enrollment** | request bound to central's (or the broker's) challenge, code consumed in the registry transaction, retry only with the same id + evidence key (built) |
| **Rogue device scans the QR first** | the person confirms the **specific** device (name, model, SAS) before any certificate; rejected requests are audited; pending requests are rate-limited |
| **Fake central or fake broker** (attacker's QR) | the app accepts only central hosts from its configuration; the answer must chain to the root hash in the QR; the broker is pinned by id + key hash |
| **Key copied off the handheld** | Keystore keys are non-exportable; hardware when available; attestation recorded; policy can require it per app |
| **Rooted device or emulator** | attestation + the app's own integrity check (e.g. Play Integrity) through `IEnrollmentPolicy`; not a library rule |
| **Lost or stolen handheld** | revocation from central, any site terminal or the device itself (§7); sessions end at once (⚑AN28); idle lock + PIN; short session profile when the app configures one; offline limits (§7) |
| **"Same network" taken as proof** | never: presence only from a witness signature bound to the handheld's own TLS session (§5) |
| **Presence relayed by an accomplice inside the store** | bound to the handheld certificate in the handshake, central's challenge and 15 min validity; needs a live insider on the LAN — accepted residual risk |
| **Person confirms without looking** | SAS on both screens, device model and time shown, audit "confirmed by Ana on CAIXA-01" |
| **Shared handheld, many people** | one person per session, quick switch, never "remember me" on shared devices (`SharedTerminal()`, Authentication 10 §2) |
| **Slow hardware signatures offline** | measured in the spike; session subkey only if needed (§6) |

## 9. What each library adds (smallest set)

| Library | Additions |
|---|---|
| YFex.Cryptography | ✅ **built 2026-10-27** — `YFex.Cryptography.Android` (⚑K19): `AndroidKeystoreSigningKeyStore` (`ICertificateKeyStore`, attestation on the store: `GetOrCreateAsync(name, challenge)` + `ReadAttestationChain`, record only), `AndroidKeystoreRootKeyProvider`, `AndroidKeyPaths` (`NoBackupFilesDir`), `AndroidKeystoreKeyLostException`; key-agreement keys through the file-based `IRecipientKeyStore` (crypto 14 §10) |
| YFex.Installations | `.Android` package; attestation challenge at key creation + chains in the enrollment request; lost-key → re-enroll; `EnrollmentLink` v1 codec; sponsor + pending/confirm at central and at the broker (shares the SAS of I5); `SitePresence` + `installations.presence` channel + `PresenceWitness` roster switch; presence in the assertion (**built 2026-10-29**, 08 §5 "I6b step 2"; binding = the transport key id, online validity = central's challenge); revoke from any trusted terminal and self-revocation (**built 2026-10-28**; offline site revocation ⚑I42 built 2026-10-29) |
| YFex.Authentication | optional per-application session profile (⚑AN26); optional off-site rule + `presence_at` claim (⚑AN27); end the sessions of a revoked installation (⚑AN28); Android token storage over the crypto package |
| YFex.Authorization | `RequireSitePresence(maxAge)` + `DenialKind.SitePresenceRequired` (⚑Z20) |
| YFex.Offline | nothing new for handhelds as such (they are terminals); off-site rule for offline sessions and the presence record in envelopes (⚑O32) |
| YFex.Networking | nothing; Android evidence and multicast lock stay app-supplied hints |
| App | "Add handheld" / "Revoke handheld" screens, QR draw/scan, its session profile and off-site settings |
