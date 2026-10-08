# 03 — Installation identity, enrollment, LAN trust (mutual TLS) and pairing

> **2026-10-05 (⚑N6):** §2–§7 of this file are superseded by the YFex.Installations design ([../installations](../installations/README.md):
> 02 core types, 04 keys/certificates/enrollment/revocation/pairing/assertion, 05 friends and peer channels). The goals (§1) and
> alternatives (§8) still hold. Changes: `Site` → `Scope` (roaming installations), `urn:yfex:site:` → `urn:yfex:scope:`, root P-256
> (cryptography decision), proof of possession by signed request instead of PKCS#10, `IInstallationKeyRegistry` → `InstallationKeyDirectory`.

## 1. Goals and attackers

| Goal | Attacker it stops |
|---|---|
| A terminal talks only to **its** genuine site broker | a laptop on the shop Wi-Fi impersonating `PC-GERENTE` (ARP spoofing, rogue DHCP, a fake "YFex" beacon) |
| The site broker accepts only **enrolled terminals of its site** | a customer's or ex-employee's device on the guest Wi-Fi; a terminal of another store |
| Nothing on the LAN is readable or injectable | passive sniffing, replay of captured traffic |
| Trust works **without internet, DNS or public CAs** | an ISP outage (the case we design for); LAN IP changes |
| Identity survives years | certificate expiry, key rotation, replaced hardware |

The **device** identity (which machine) is separate from the **user** identity (which person). mTLS proves the device. A session token
proves the user on that device (§4.4).

## 2. Installation identity and keys (⚑O5)

```csharp
// YFex core, namespace YFex.Installations (⚑O1) — read by Audit, Logging, Privacy, Authorization, Messaging (fixes F11),
// also when everything is online: it is "which installed copy of the app", not an offline concept
public readonly record struct InstallationId(Guid Value);
public sealed record InstallationIdentity(InstallationId Id, InstallationRole Role, string Site, string DisplayName);

public static class LocalInstallation                                      // ambient, set once by YFex.Offline at startup
{
    public static InstallationIdentity? Current { get; }
}
```

Each installation owns **two** ECDsa P-256 key pairs. Both are generated on the installation, and neither private key ever leaves it:

| Key | Signs | Why separate |
|---|---|---|
| **transport key** | TLS 1.3 handshakes (its public key is in the installation certificate) | rotated with the certificate (90 days) |
| **evidence key** | offline command envelopes, site receipts, device approval tokens, audit installation-chain heads, privacy offline consent | must stay verifiable for years of audit retention. Rotated only on re-enrollment. Old public keys are kept forever in the central installation registry |

The two are kept apart because their lifetimes differ. Every evidence signature also uses a domain-separated prefix
(`"yfex.envelope.v1|"`, `"yfex.approval.v1|"`…), so one signature can never be replayed as another kind.

**Where the private keys live** (`IInstallationKeyStore`):

| Platform | Implementation | BCL only? |
|---|---|---|
| Windows | `CngSigningKeyStore` of **YFex.Cryptography.Windows** (cryptography ⚑K8, Phase 2): `CngKey` persisted, `ExportPolicy = None`. Tries `CngProvider.MicrosoftPlatformCryptoProvider` (**TPM**) first, then `MicrosoftSoftwareKeyStorageProvider`. `MachineKey` for the site-broker service, user key for desktop apps. TPM signing costs ≈ 32 ms, so the evidence key signs envelopes and batch heads, never single audit entries | yes (`System.Security.Cryptography`, Windows) |
| Linux/macOS/Android (later) | `FileSigningKeyStore` of YFex.Cryptography: random key sealed under the YFex.Cryptography root key (`FileRootKeyProvider`, mode 0600; Keychain/Keystore providers later) | yes for the file provider |
| Tests | `InMemoryInstallationKeyStore` | yes |

A TPM key cannot be copied with a disk image. A software CNG key can be extracted by a local administrator. The enrollment record
says which kind the installation got (`KeyProtection.Hardware | Software`), so central can require hardware keys for site brokers if the
user wants (⚑O5).

`IInstallationKeyStore` stays in YFex.Offline as a thin, typed layer: it names two keys of the YFex.Cryptography signing-key store,
`"installation/transport"` and `"installation/evidence"`, and records their `KeyProtection` at enrollment.

**Moves:** `IDeviceKeyStore` (Rules) becomes the central-side `IInstallationKeyRegistry` in YFex.Offline: public keys by `(InstallationId, key id)`
with validity periods. Rules' device approval issuer takes an `IInstallationKeyStore` instead of a raw `ECDsa`. With ⚑Z12 these tokens move
to Authorization anyway.

## 3. Tenant private CA at central

```
Tenant root CA (ECDsa P-384, 10 years) ── private key in the YFex.Cryptography signing-key store (root key provider), used only to sign intermediates through `CertificateAuthority`
  └─ Installation issuing CA (P-256, 1 year, online at central) ── signs installation certificates at enrollment and renewal
       └─ Site issuing CA (P-256, ≤ 30 days, held by the site broker, only if offline pairing is enabled; §6)
Bundle signing keys (P-256, yearly, several active by key id) ── sign manifests, snapshots, rule data, allowances (05 §2)
```

Installation certificate (BCL `CertificateRequest`):

| Field | Value |
|---|---|
| Subject | `CN=CAIXA-02` (display only, never used for trust) |
| SAN URIs | `urn:yfex:installation:7f3a…`, `urn:yfex:site:0%3Dfarmaviva%2F1%3Dloja-centro`, `urn:yfex:role:terminal` |
| EKU | client auth. The site broker also gets server auth |
| Validity | 90 days. Renewed online at ⅔ of its life (new CSR signed with the current transport key) |
| No IP/DNS SANs | LAN addresses change. Trust is by installation id and site, not by name |

**Why not a public CA:** LAN names and IPs cannot get public certificates, issuance needs internet, and a public certificate cannot
say "terminal of loja-centro".

## 4. Mutual TLS on the LAN

### 4.1 Site broker (Kestrel, Microsoft.AspNetCore.App, already referenced by Messaging.Rpc)

```csharp
// (sketch) what AddYFexOfflineBroker(broker => broker.Listen(7443)) configures in the generic broker host
kestrel.ListenAnyIP(7443, listen => listen.UseHttps(https =>
{
    https.ServerCertificate = installation.TransportCertificate;              // from IInstallationKeyStore
    https.SslProtocols = SslProtocols.Tls13;
    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
    https.ClientCertificateValidation = (certificate, chain, errors) => peers.ValidateTerminal(certificate);
}));
```

### 4.2 The validator (one class, both directions)

```csharp
// (sketch) YFex.Offline — SitePeerValidator
public PeerValidation ValidateTerminal(X509Certificate2 certificate)
{
    using var chain = new X509Chain { ChainPolicy = {
        TrustMode = X509ChainTrustMode.CustomRootTrust,                // pinned tenant root; the OS store is ignored
        RevocationMode = X509RevocationMode.NoCheck } };               // revocation comes from the signed manifest (§7)
    chain.ChainPolicy.CustomTrustStore.Add(trust.TenantRoot);
    if (!chain.Build(certificate)) return PeerValidation.UntrustedChain;

    var claims = InstallationCertificateClaims.Read(certificate);              // installation, site, role from the URN SANs
    if (claims.Site != manifest.Site) return PeerValidation.OtherSite;
    if (claims.Role is not InstallationRole.Terminal and not InstallationRole.SiteBroker) return PeerValidation.WrongRole;
    if (manifest.IsRevoked(claims.Installation)) return PeerValidation.Revoked;
    if (IssuedBySiteIssuingCa(chain) && !pairing.IsProvisionalAllowed(claims.Installation)) return PeerValidation.ProvisionalNotAllowed;
    return PeerValidation.Accepted(claims);   // X.509 name constraints don't cover URN SANs, so site/role checks are ours
}
```

### 4.3 Terminal side (through Fusion, no fork)

```csharp
// Before — one endpoint, OS certificate validation (RpcMessagingExtensions.cs:40)
fusion.Rpc.AddWebSocketClient(options.WebSocketEndpoint);

// After (sketch; exact Fusion 13 option shapes to verify in C:\Dev\OpenSource\Fusion) — two peers; the site peer is pinned
rpc.AddWebSocketClient(_ => RpcWebSocketClientOptions.Default with
{
    ConnectionUriResolver = peer => routes.UriFor(peer),               // central list or the site broker's current LAN address (04)
    WebSocketOwnerFactory = peer => routes.CreateWebSocket(peer),     // adds the installation certificate and the pinned validator for the site peer
});
```

### 4.4 The user on top of the device

- **Site mode:** after offline login ([05](05-protecting-offline-data.md) §5) the site broker issues a **site session token** with
  user, installation id, scope, the snapshot version used and an expiry. It is signed with the site broker's evidence key. The site broker
  accepts the token only on the mTLS connection of the installation it was issued for, so a token copied to another terminal is useless.
- **Central:** the user's identity-provider token as today (D29). In addition, the installation proves itself with a short **installation assertion**
  in the WebSocket upgrade: installation id, central-issued nonce and time, signed by the evidence key. Cloud load balancers terminate TLS,
  so client certificates are awkward there. A signed assertion works through any proxy. Central then knows "Ana, on CAIXA-02 of
  loja-centro". It refuses envelopes whose installation doesn't match the assertion ([08](08-sync-reconciliation-conflicts.md) §3).

## 5. Enrollment (online, once per installation)

```
 admin at central (installations.enroll) ──► enrollment code "7Q4K-M2XD" (one use, 15 min, bound to site + role)
 new installation: first-run screen or `yfex-install enroll --code 7Q4K-M2XD`
   1. generates transport + evidence keys in IInstallationKeyStore
   2. HTTPS (public TLS) to central: code + CSR + evidence public key + key protection + hardware fingerprint hash (Profiling)
   3. central checks the code, issues the installation certificate, records the installation, returns:
        installation certificate, tenant root (trust anchor), bundle-signing public keys, current site manifest, central time
   4. the installation pins the trust anchor (stored with the installation keys) and is ready
```

- The hardware fingerprint hash is recorded but never used for trust. A change at renewal raises a **finding** ("disk image cloned
  to new hardware?"), not a block.
- Re-enrollment (reinstalled Windows, replaced disk) creates a **new** installation id. The old one is revoked. Its outstanding envelopes are
  still accepted if they were signed before the revocation.

## 6. Pairing while central is unreachable (⚑O7)

Real case: `CAIXA-03` dies during a Saturday outage, and the manager brings the spare PC from the stockroom.

```
 spare PC (keys generated)                         site broker PC-GERENTE
   ── discover (04 §2: beacon or typed address) ──►
   ── TLS (server-auth only) + pairing request {transport key, evidence key, nonce} ──►
   ◄── {site broker nonce}
   both screens show SAS = 6 digits of SHA-256(both public keys ‖ both nonces)  →  "Confirme: 482 913"
   manager logs in on PC-GERENTE (holds installations.pair), sees the same 6 digits, confirms
   ◄── provisional certificate signed by the Site issuing CA: valid ≤ 72 h, role Terminal, same site
```

- The short authentication string (SAS) is what makes this safe. A rogue machine in the middle would produce different digits, and
  it costs one human comparison.
- **Provisional installations:** every envelope carries `ProvisionalInstallation`. Approvals cannot be issued on them. At reconnect, central confirms
  the installation (issues a normal certificate) or rejects it. A rejection revokes the installation and turns all of its operations into review
  findings.
- **The site issuing CA** exists only when the manifest enables offline pairing. Its certificate is short-lived (≤ 30 days,
  auto-renewed online) and limited by our validator to its own site and the Terminal role. A compromised site broker could mint
  terminals for its own site, but it can already do everything at that site, so the added risk is small. Recommended default:
  **disabled**, enabled per site.

## 7. Revocation and rotation

| Event | Online | Central unreachable |
|---|---|---|
| Terminal stolen | admin revokes at central → small signed **revocation bundle** pushed to the site broker and terminals at once; central refuses envelopes signed by that installation after the revocation time | a manager revokes at the site broker (`installations.revoke`) → site-level revocation, forwarded to central later |
| Site broker compromised or replaced | revoke installation + its site issuing CA; enroll the new site broker; new manifest | terminals keep `Standalone` until a valid site broker appears |
| Certificate expiry | auto-renew at ⅔ of life | an expired certificate is accepted on the LAN for a grace period (default 7 days), counted with the trusted clock ([05](05-protecting-offline-data.md) §4) and flagged; after that, standalone only |
| Key rotation | installation certs 90 days; installation issuing CA yearly; bundle keys yearly (overlapping key ids); root CA 10 years (re-signs intermediates) | — |

## 8. Alternatives considered

| Option | Why not (as the default) |
|---|---|
| Pre-shared key on every terminal (TLS-PSK) | one stolen terminal exposes every terminal's traffic. There is no per-device revocation. .NET has no TLS-PSK API |
| Trust on first use (pin whatever answers first) | the first contact is exactly when a rogue machine wins. There is no site/role binding |
| Plain TLS with a self-signed server cert + bearer tokens | the server side is solved, but terminals stay anonymous to the site broker. Tokens are copyable |
| Windows domain (AD CS, Kerberos) | most small pharmacies have no domain. It could be an *optional* key source later |
| Public CA (Let's Encrypt) for LAN | impossible for LAN names and IPs. Needs internet. Cannot express site/role |

No NuGet package: `CertificateRequest`, `X509Chain` custom trust, `CngKey`/TPM, `SslStream`, `ClientWebSocketOptions.ClientCertificates`
and Kestrel HTTPS are all BCL or the ASP.NET Core shared framework.

**Alignment:** YFex.Devices plans its own TLS/PSK for "device sharing over IP" (phase 10). The recommendation is that Devices reuses
these installation certificates and the validator, so a terminal has one identity on the LAN (⚑O6).
