# 09 — Platforms: Windows, Linux, macOS (and later mobile/browser)

User requirement (2026-10-07): the core and the library must be **generic and work on every OS**, each with its own restrictions —
storage and everything else. This file says what is OS-free, what differs per OS, which package holds it, and what must be verified.

## 1. Rule: three layers

| Layer | OS-specific code? | Contents |
|---|---|---|
| **YFex core** (`YFex.Installations` namespace) | **never** | types only (02 §4). No paths, no keys, no `OperatingSystem.Is…` |
| **`YFex.Installations`** (main package) | only behind `OperatingSystem.Is…()` and only **file reads / BCL calls** (like Networking ⚑N11) | everything that the BCL provides on all three OSes: TLS (`SslStream`), certificates, framing, roster, bundles, friends, state file with Unix permissions, per-OS directories, the software key fallback, Linux/macOS hardware hint sources |
| **`.Windows`** / **`.MacOS`** / **`.Linux`** | yes (`[SupportedOSPlatform]`) | hardware-backed keys and OS service integration that need native APIs: CNG/TPM (Windows), Keychain/Secure Enclave (macOS, P/Invoke Security.framework), TPM 2.0 (Linux, needs a native library → approval) |

An app picks its key store in the composition root; everything else is identical:

```csharp
services.AddYFexInstallations(installations => installations
    .Terminal()
    .UsePlatformKeyStore());   // Windows: CNG/TPM; macOS: Keychain/Secure Enclave (when .MacOS is referenced); else: software keys under the root key
```

`UsePlatformKeyStore()` lives in the main package and picks the best **registered** provider; `.Windows`/`.MacOS`/`.Linux` register
theirs when referenced. Without any, it uses the software store and records `KeyProtection.File` (central can refuse it for brokers,
⚑O5 policy).

## 2. Matrix

| Concern | Windows 10/11, Server | Linux (desktop, server, containers) | macOS 13+ |
|---|---|---|---|
| **Private keys (⚑I4)** | `CngSigningKeyStore` (built): TPM (Platform Crypto Provider) → software KSP; user or machine scope | **v1:** software P-256 key sealed under the YFex.Cryptography root key (`FileRootKeyProvider`, file mode 0600; `KeyProtection.File`). **Later `.Linux`:** TPM 2.0 through `/dev/tpmrm0` (tpm2-tss or a PKCS#11 module = native dependency, needs approval); libsecret for desktop users (crypto Phase 5) | **`.MacOS`:** Secure Enclave key (`SecKeyCreateRandomKey`, `kSecAttrTokenIDSecureEnclave`; P-256 only, which is ours) → data-protection Keychain → software fallback. Needs a **signed app with a keychain-access-group entitlement**; unsigned CLI/dev builds fall back to software keys |
| **Key-store gap** | none | YFex.Cryptography has **no persisted `ISigningKeyStore` outside Windows** today (only `CngSigningKeyStore`, `InMemorySigningKeyStore`, `SigningKeyRing` for rotating server keys). Needs a `FileSigningKeyStore` (named, root-wrapped) — a crypto-library task (§5) | same gap, plus a Keychain/Secure Enclave store (`.MacOS`, crypto or here — §5) |
| **Local identity file** (user scope) | `%LOCALAPPDATA%\YFex\Installations\{app}\` | `$XDG_DATA_HOME/yfex/installations/{app}/` (default `~/.local/share/…`) | `~/Library/Application Support/YFex/Installations/{app}/` |
| **Local identity file** (machine scope: broker service, kiosk) | `%PROGRAMDATA%\YFex\Installations\{app}\` (ACL: service account + Administrators) | `/var/lib/yfex/installations/{app}/` (owner = service user, mode 0700/0600) | `/Library/Application Support/YFex/Installations/{app}/` (root or the daemon's user) |
| **File permissions** | NTFS ACL set on create | `UnixCreateMode` 0600 files / 0700 directories (BCL, as `FileRootKeyProvider` already does) | same as Linux |
| **Atomic write** | temp file + `File.Move(overwrite)` | temp + `FileStream.Flush(flushToDisk: true)` + rename (+ directory fsync where available) | same as Linux |
| **Bundles + floors** | app `IKeyValueStore` (SQLite/file) + `AddYFexStorageEncryption` | same | same |
| **Storage key root** | `CngRootKeyProvider` (TPM) | `FileRootKeyProvider` (0600) — weaker: anyone who is root or the same user reads it | `FileRootKeyProvider` until a Keychain root provider exists (crypto Phase 5) |
| **TLS 1.3 peer channel** | Schannel: TLS 1.3 on Windows 11 / Server 2022+ (Windows 10: **TLS 1.2 only**) | OpenSSL 1.1.1/3.x: TLS 1.3 | **.NET 11 (crypto 14 §5.3, desk research):** client TLS 1.3 only with the opt-in switch `System.Net.Security.UseNetworkFramework`; a **listener** gets TLS 1.2 (SecureTransport; server-side Network.framework is milestone .NET 12) |
| **Client certificate with a non-exportable key** | `CngSigningKeyStore.CopyWithPrivateKey` (tested, crypto) | software key: `FileSigningKeyStore.CopyWithPrivateKeyAsync` (crypto 14 §2.5); TPM key later through the `tpm2-openssl` provider (crypto 14 §6) | software key: same; Keychain identity (`SecIdentity`) to verify with `SslStream` (§4 R2, crypto 14 §5.4) |
| **Listening on 7443** | inbound firewall rule needed (future YFex.Installer); NLM category Public/Private matters for beacons | firewalld/ufw may block; ports < 1024 need root (7443 does not) | **Local Network privacy permission** (macOS 15+) for multicast **and** LAN connections; the app shows the system prompt once; application firewall prompt for incoming connections |
| **Multicast beacons** | YFex.Networking (Private/Domain, or `AllowPublicNetworkCategory`) | YFex.Networking; works in containers only with host networking | YFex.Networking; needs the Local Network permission (and the multicast entitlement for some app types) |
| **Hardware hint sources (⚑I16)** | `MachineGuid` registry value + SMBIOS UUID | `/etc/machine-id` (any user); `/sys/class/dmi/id/product_uuid` only as root (optional) | `IOPlatformUUID` (IOKit, P/Invoke in `.MacOS`; the main package reads nothing and sends a weaker hint) |
| **Service host (broker)** | Windows service, machine keys | systemd unit with its own user, `StateDirectory=yfex` | launchd daemon |
| **Time** | YFex.Time (same on all) | same | same |

Mobile and browser (later, README §9): Android Keystore (StrongBox) and iOS Secure Enclave would be `.Android`/`.iOS` key stores;
Blazor WASM has no sockets, no persistent non-exportable keys and no TPM → **never** a friend; at most a `NotEnrolled` client of central.

## 3. What this changes in the design

| Place | Change |
|---|---|
| README §3 packages | add `.MacOS` (Keychain/Secure Enclave, `IOPlatformUUID`) and `.Linux` (TPM 2.0, later, after approval of a native dependency); main package is cross-platform |
| ⚑I4 storage table (README §6.1) | per-OS columns above; "OS key store" means CNG on Windows, Keychain on macOS, software-under-root-key on Linux v1 |
| 02 §5 | per-OS directories; bundles are in the app's `IKeyValueStore` (⚑I4), not in the state directory |
| 04 §1 | key store per OS; `KeyProtection` recorded at enrollment shows `Hardware` / `OperatingSystem` / `File` |
| 05 §4 | TLS version policy per peer pair (§4 R1, ⚑I22) |
| 08 | I1 runs its tests on Windows **and** Linux (container) **and** macOS (when a machine is available); platform spike first |

## 4. Risks to verify in the Phase I1 spike (before writing the peer channel)

| # | Question | If the answer is bad |
|---|---|---|
| R1 | Does .NET 11 `SslStream` negotiate **TLS 1.3** on macOS (and on Windows 10)? | ⚑I22: allow **TLS 1.2** on those peers with only `ECDHE-ECDSA-AES128/256-GCM` suites (mutual auth and forward secrecy kept), flagged in diagnostics; Windows 11 and Linux stay 1.3-only |
| R2 | Can `SslStream` on macOS use a Keychain/Secure Enclave key as client/server certificate key? | macOS v1 uses software keys for **transport** (rotated every 90 days anyway) and the Secure Enclave only for **evidence** signatures |
| R3 | Can a Linux TPM key sign TLS handshakes from .NET without a native OpenSSL provider? | Linux v1 keeps software transport keys; TPM only for evidence (through the `.Linux` store) |
| R4 | macOS Local Network permission for a non-UI daemon (broker) | macOS brokers are not supported in v1 (pharmacy brokers are Windows PCs); macOS terminals are clients |
| R5 | `UnixCreateMode` + rename atomicity on network/home directories | state directory must be local; diagnostics refuse a network path |

**Answers from the crypto session (2026-10-07, crypto 14 §5.3, §6; desk research, not run):** R1: macOS clients can use TLS 1.3 with
the `UseNetworkFramework` switch (Installations sets it on macOS); macOS **listeners** stay TLS 1.2 in .NET 11, so ⚑I22's fallback
applies to them (Macs are terminals in v1). Windows 10 stays TLS 1.2 (unchanged). R2: unverified; keep the recommendation
(transport = software key, Secure Enclave for evidence). R3: probably yes through the `tpm2-openssl` provider +
`SafeEvpPKeyHandle.OpenKeyFromProvider` (the runtime has TLS tests with TPM keys); needs the native dependency (crypto 12 §4 question 8).

## 5. Requests to YFex.Cryptography (its repo, its decision)

1. **`FileSigningKeyStore : ISigningKeyStore`** — named P-256 keys, PKCS#8 sealed with the store key under the configured
   `IRootKeyProvider` (`KeyProtection.File`), Unix 0600, for Linux/macOS v1 (crypto 06 already lists it as planned; it was replaced
   by `SigningKeyRing` for servers, which does not fit named installation keys).
2. **macOS Keychain / Secure Enclave `ISigningKeyStore`** (`YFex.Cryptography.MacOS` or `.Apple`) — or Installations hosts it in
   `.MacOS` if the crypto session prefers; the architecture test must then allow-list it with a reason.
3. **Linux TPM 2.0 store** — later, needs the user's approval of a native dependency (tpm2-tss / PKCS#11).
4. A Keychain-backed `IRootKeyProvider` for macOS (already crypto Phase 5).

**Status (crypto session, 2026-10-07; crypto 14 §7):** 1 designed and approved, **not coded** (user: design only for now); 2 =
`YFex.Cryptography.Apple`, a spike when a Mac is available (no allow-list entry needed); 3 = route A (`tpm2-openssl`), waiting for approval
of the native dependency; 4 unchanged (Phase 5, same `.Apple` package).
</content>
</invoke>
