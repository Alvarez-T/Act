# 04 — Keys, certificates, enrollment, revocation, pairing

Every key, signature and certificate goes through **YFex.Cryptography** (its architecture test bans `ECDsa.Create`, `CngKey.Create`
and friends outside that repo, `ArchitectureTests.cs:13`). This file names **what** Installations asks the library to do; the
mechanisms are the library's (crypto 12 §5 handoff, now addressed to this library).

## 1. Installation keys (⚑I4 = ⚑O5 re-homed)

| Key name in `ISigningKeyStore` | Signs | Rotated | Public key kept |
|---|---|---|---|
| `installation/transport` | TLS 1.3 handshakes (its public key is in the installation certificate) | with the certificate (new key at each renewal, 90 days) | until the certificate expires |
| `installation/evidence` | enrollment and renewal requests, installation assertion, beacons, offline envelopes and receipts (Offline), device/site approval tokens (Authorization), audit node-chain heads (Audit), offline consent (Privacy) | only on re-enrollment | **forever** in the central registry (years of audit retention) |
| `installation/key-agreement` *(Phase I5, ⚑I4)* | nothing — ECDH P-256 so central can seal a secret **to this installation** (an A1 certificate for offline NFC-e, crypto 13 §6) | yearly | while sealed secrets reference it |

```csharp
// YFex.Installations — the thin typed layer over YFex.Cryptography (was planned in YFex.Offline, offline 03 §2)
public interface IInstallationKeyStore
{
    ValueTask<ISigningKey> GetTransportKeyAsync(CancellationToken cancellationToken);
    ValueTask<ISigningKey> GetEvidenceKeyAsync(CancellationToken cancellationToken);
    ValueTask<ISigningKey> RotateTransportKeyAsync(CancellationToken cancellationToken);   // renewal: new key, old one deleted after the new certificate is stored
    X509Certificate2 AttachTransportKey(X509Certificate2 certificate);                    // CngSigningKeyStore.CopyWithPrivateKey (CngSigningKeyStore.cs:93)
}
```

| Platform | `ISigningKeyStore` | Configured by |
|---|---|---|
| Windows | `CngSigningKeyStore(keyNamePrefix: "YFex.Installations.{app}.", machineScope, preferHardware: true)` (`CngSigningKeyStore.cs:29`) | `.UseWindowsKeyStore()` (user scope) / `.UseWindowsKeyStore(machine: true)` (broker service, kiosks) |
| macOS | Keychain / Secure Enclave store (`YFex.Cryptography.Apple` `AppleSigningKeyStore`, spike later: crypto 14 §5; 09 §2, §4 R2) for the **evidence** key in signed apps; transport key and unsigned builds: `FileSigningKeyStore` | `.UsePlatformKeyStore()` |
| Linux | v1: `FileSigningKeyStore` (crypto 14 §2): software keys sealed under the root key (`FileRootKeyProvider`, 0600) = `KeyProtection.File`; later TPM 2.0 (`.Linux` through `tpm2-openssl`, native dependency: crypto 14 §6, crypto 12 §4 question 8) | `.UsePlatformKeyStore()` / `.UseFileKeyStore(directory)` |
| **gap** | YFex.Cryptography has no persisted `ISigningKeyStore` outside Windows yet. **2026-10-07: designed in crypto 14** (`FileSigningKeyStore` approved, not coded; Apple store = spike) | — |
| Tests | `InMemorySigningKeyStore` (YFex.Cryptography.Testing) | `YFex.Installations.Testing` |

**TPM cost (≈ 22–32 ms per signature, crypto 06):** fine for enrollment, one assertion per connection, a beacon every 5 s, one envelope
per sale and one audit head per batch. A broker that must sign more than ~20 objects/s (receipts, session tokens) uses the session
subkey pattern documented in crypto 06 (software key certified by the evidence key, ≤ 12 h) — only if measured.

## 2. Certificate profile (⚑I6)

| Field | Value |
|---|---|
| Issuer | the tenant's **installation issuing CA** (P-256, 1 year, online at central), under the tenant root (P-256, 10 years — crypto decided P-256, IF6) |
| Subject | `CN=CAIXA-02` (display only) |
| SAN URIs | `urn:yfex:installation:0199a3c4-…`, `urn:yfex:scope:0%3Dfarmaviva%2F1%3Dloja-centro` (percent-encoded canonical scope), `urn:yfex:role:terminal` |
| Usage | `ClientAuthentication` for every installation; `SiteBroker` and mesh-enabled terminals also get `ServerAuthentication` (`CertificateUsage`, `CertificateAuthority.cs:10-25`) |
| Validity | 90 days, renewed at ⅔ (≈ day 60) |
| No IP/DNS names | trust is by installation id, scope and role |

`InstallationCertificate.Read(trustedLeaf)` parses the three URNs **only after** `CertificateTrust.Validate` returned
`CertificateTrusted` (`CertificateTrust.cs:20,35`) — X.509 name constraints do not cover URN SANs, so scope/role checks are ours
(offline 03 §4.2 note, kept).

Hierarchy (built by `CertificateAuthority`, tested in crypto: root → installation issuing CA → site issuing CA → terminal):

```
Tenant root (P-256, 10 y) — a SigningKeyRing key on central servers or a CNG/TPM key; signs only intermediates
 ├─ Installation issuing CA (P-256, 1 y, path 1) — enrollment and renewal
 │    └─ Site issuing CA (P-256, ≤ 30 d, path 0) — on the site broker, only when offline pairing is enabled (§5)
 └─ (bundle signing keys are NOT certificates: SigningKeyRing "installations/bundles", public keys in the state file)
```

## 3. Enrollment protocol (⚑I5)

> **As built (2026-10-13, 08 §5):**
> - **Challenge.** Step 1 is a POST of `EnrollmentChallengeRequest { code, installation id }`.
>   - The challenge is a stateless `MessageToken` (`InstallationChallenges`; shared ring `installations/challenges` on farms).
>   - The tenant hint key comes back only for a valid unused code, and the installation computes the hint (§7) before step 2.
>   - Failed codes are limited per tenant.
> - **Certificate.** Central issues it inside the registry transaction, after the code, identity, policy and replacement checks.
> - **Display name.** The name on the code wins over the installation's suggestion.
> - **Idempotency.** A retry after a lost answer is answered idempotently, recognized by installation id + code digest, also
>   after the code expired.
> - **Client checks.** The installation validates the answer (chain to the pinned root, its own key/id/role/scope, bundles)
>   before storing it.
> - **Endpoints.** `HttpInstallationEnrollmentTransport` (client) and `MapYFexInstallationsEnrollment` (package
>   `YFex.Installations.Central.AspNetCore`), at `/installations/enrollment/{challenge,enroll,renew}`.

```
 admin (installations.enroll) ──► CreateEnrollmentCode(scope, role, display name) ──► "7Q4K-M2XD" (one use, 15 min)

 new installation (state NotEnrolled, id minted at first run, keys created on first use)
   1. GET  /installations/enroll/challenge                        ◄── { central nonce (32 B), central time (TimeReply) }
   2. POST /installations/enroll  (public TLS to central)
        EnrollmentRequest (canonical bytes, domain "yfex.installation.enrollment.v1"):
          code, installation id, configured role, display name, transport SPKI, evidence SPKI,
          key protection of each key, keyed hardware hint (§7), app name + version, central nonce
        + signature by the EVIDENCE key + signature by the TRANSPORT key   (SignedPayload; proves both private keys)
   3. central: code valid + unused + role matches → id unused (or same id + same evidence key = idempotent retry)
        → CertificateIssueRequest(subject "CN=CAIXA-02", transport SPKI, URNs, usage, 90 d)     (CertificateAuthority.cs:35)
        → registry: id, scope, role, status Enrolled, both public keys (PublicKeyRecord with ValidityPeriod), protection, hint
   4. ◄── EnrollmentResponse: certificate + chain, tenant root (trust anchor), bundle-signing public keys,
          current roster + revocation bundle for its scope, central TimeReply
   5. installation pins the root, stores everything in installation.state (sealed), publishes LocalInstallation (Enrolled)
```

- **No PKCS#10:** `CertificateIssueRequest` already takes the SubjectPublicKeyInfo; possession is proven by the two signatures over
  a request bound to the central nonce. This avoids a CSR builder over TPM keys in YFex.Cryptography (alternative in ⚑I5).
- **Idempotent:** a lost response + retry with the same id and evidence key returns the same certificate; a different evidence key for
  an existing id is refused (`EnrollmentRefusal.IdentityConflict`) — nobody can take over an id.
- **Policy at central:** `RequireHardwareKeys(InstallationRole.SiteBroker)` refuses a broker whose keys are not `KeyProtection.Hardware`
  (⚑O5 option).
- Each step is an operation in Logging and an audit entry (`Installations.Enrolled`, actor = the admin who created the code, installation
  = the new one).

## 4. Renewal, revocation, rotation

| Event | Online | Central unreachable |
|---|---|---|
| renewal (day ~60 of 90) | `RenewalRequest` signed with the evidence key **and the current transport key**, carrying the **new** transport SPKI (signed by it too); central issues a new certificate; the hint is re-sent — a change raises a finding, never a block | retried with back-off; at expiry the certificate is accepted on the LAN for a **grace period** (default 7 days, counted with the trusted clock) and flagged `CertificateGrace` |
| terminal stolen | admin revokes at central (`installations.revoke`) → revocation bundle pushed to the site at once; central refuses evidence signatures made after the revocation time (`PublicKeyRecord.RevokedAt`, `SigningContracts.cs:58-59`) | a manager revokes at the broker → **site revocation** (signed by the broker's evidence key), effective on the site's friends at once, forwarded to central for confirmation |
| broker compromised or replaced | revoke the broker + its site issuing CA; enroll the new one; new roster | terminals see no valid broker → Offline `Standalone` |
| re-enrollment (reinstall, replaced disk) | new id, new keys; the old id is revoked when the admin confirms (hint helps, §7) | — |
| tenant root rotation (10 years) | new root signs the old root's successor intermediates; rosters carry both anchors during the overlap | — |

Revocation lists are **signed bundles** (`installations.revocations`) with version floors (05 §3), never CRL/OCSP downloads — devices
are often offline and the private CA publishes nothing (`CertificateTrust` takes the caller's `isRevoked`, `CertificateTrust.cs:22-34`).

## 5. Pairing while central is unreachable (⚑I14, policy ⚑O7 unchanged)

Saturday outage, `CAIXA-03` dies, the manager brings the spare PC from the stockroom.

```
 spare PC (NotEnrolled, keys created)                    PC-GERENTE (SiteBroker, site issuing CA present because the roster enables pairing)
   ── discover (verified yfex.installation beacon, role SiteBroker) ──►
   ── TLS 1.3 server-auth only + PairingRequest {id, transport SPKI, evidence SPKI, nonce A} ──►
   ◄── {nonce B}
   both screens: SAS = 6 digits of SHA-256(domain ‖ both SPKIs ‖ id ‖ nonce A ‖ nonce B)  →  "Confirme: 482 913"
   manager signs in on PC-GERENTE (Offline session, holds installations.pair), sees 482 913, confirms
   ◄── provisional certificate from the site issuing CA: ≤ 72 h, role Terminal, same scope; status Provisional
```

- Hashing for the SAS uses `Hashing.Sha256` of the library (no keyed operation needed).
- A provisional installation is a `SameSite` friend for time, relay and facts, **cannot issue approvals**, and every envelope carries
  `ProvisionalInstallation` (Offline). At reconnect central confirms (normal certificate, same id) or rejects (revoked, its operations
  become review findings).
- The site issuing CA exists only when the roster enables pairing; ≤ 30 days, auto-renewed online; our validator limits it to its own
  scope and the Terminal role. Default: **disabled**.

## 6. Installation assertion to central (⚑I11)

> **As built (2026-10-13, 08 §5):**
> - **Assertion.** The payload is `{ installation id, central challenge token, evidence key id }`, signed with the evidence key.
>   - Freshness comes only from the challenge, fetched over RPC (`IInstallationAssertionChallenges`) and timed by central's clock.
>   - The client signs once per challenge (default 5 minutes) and sends the result in the `yfex-installation` header on regular
>     calls (`rpc.UseInstallationAssertion()` + `AddYFexInstallationRpcClient()`, through `SendHeader`, not the token slot).
>   - Fusion compute calls carry no headers and run unasserted.
> - **Server context.** The caller's installation is `InstallationCallContext.Current` (next to CallContext), not
>   `callContext.Installation`.
> - **Refusals.** Scope pinning refuses with `Forbidden(installation.scope)`.
> - **Package.** The format, validator and provider are in the main package, so any transport can use them.

Cloud load balancers terminate TLS, so client certificates are awkward toward central. Instead, every RPC connection carries a short
signed assertion:

```
 InstallationAssertion (canonical, domain "yfex.installation-assertion.v1"):
   installation id, central nonce (from the WebSocket upgrade challenge or the last connection), central time, evidence KeyId
   + evidence-key signature   (one TPM signature per connection: ≈ 30 ms)
```

```csharp
// ✍ server side (YFex.Installations.Rpc) — inside any handler, as with the actor
InstallationIdentity? caller = callContext.Installation;   // CAIXA-02 · Terminal · 0=farmaviva/1=loja-centro (from the registry, not the client)
```

- `ctx.WithInstallation()` validates the assertion against `InstallationKeyDirectory` and fills `CallContext.Installation`.
- **Scope pinning:** a call whose scope header is not within the installation's enrolled scope is refused with `Forbidden`
  (`installation.scope`) — online too. The regional laptop (scope `regiao-sul`) may call for any store of the region.
- `RequireInstallation(role)` is available to central handlers that must only be called by, e.g., site brokers
  (`Sites.Queries.Health` from Offline).
- Installations without an assertion (`NotEnrolled`, browsers) keep working as today, unless the app sets
  `RequireEnrolledInstallations()`.

## 7. Hardware hint (⚑I16)

```
 hint = MessageAuthentication.ComputeTag(tenant hint key, domain "yfex.installation.hint.v1", MachineGuid ‖ SMBIOS UUID ‖ boot disk serial)
```

- Computed on the installation with a **per-tenant key** delivered at enrollment (derived from a central ring, never the same across
  tenants), so the same PC enrolled by two tenants gives unrelated hints and the value cannot be reversed by guessing MachineGuids.
- Used only for: "CAIXA-02 (new) looks like CAIXA-02 (old)" at enrollment, and "the hardware under this installation changed" at
  renewal (finding: disk image cloned to another PC?). Never for trust, never a block.
- YFex.Profiling's `HardwareFingerprint` (`WindowsHardwareFingerprint.cs:23-28`) is unrelated telemetry and stays Profiling's.

## 8. Permissions (⚑I17)

```csharp
// YFex.Installations.Central
[PermissionCatalog] public static partial class InstallationPermissions
{
    [Permission("installations.enroll", Name = "Enroll installations")]         public static partial Permission Enroll { get; }
    [Permission("installations.revoke", Name = "Revoke installations")]         public static partial Permission Revoke { get; }
    [Permission("installations.pair",   Name = "Pair installations offline")]   public static partial Permission Pair { get; }
    [Permission("installations.view",   Name = "View installations")]           public static partial Permission View { get; }
}
```
</content>
</invoke>
