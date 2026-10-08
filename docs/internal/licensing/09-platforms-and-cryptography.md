# 09 — Platforms and cryptography

## 1. Windows, Linux, macOS (⚑LC20, `yfex-cross-platform`)

Licensing has **no OS-specific code**, so there is no `.Windows`/`.MacOS`/`.Linux` package. Everything OS-dependent sits behind
libraries that already split per OS:

| Need | Windows | Linux | macOS | Provided by |
|---|---|---|---|---|
| verify vendor signatures (ECDSA P-256) | BCL (CNG) | BCL (OpenSSL) | BCL (Apple Security) | YFex.Cryptography `SignatureVerifier` |
| installation identity, evidence key, tenant root | TPM/CNG | software key under root key (0600), TPM later | Keychain/Secure Enclave (after the I1 spike) | YFex.Installations (installations 09) |
| central's `licensing/licensee` ring (software key under the root key) | CNG/TPM-wrapped root | 0600 root-key file (`FileRootKeyProvider`) | 0600 root-key file, Keychain later | YFex.Cryptography key rings |
| vendor's `licensing/vendor` active key | TPM via `CngSigningKeyStore` today | an HSM/KMS-backed `ISigningKey` (**not built**, optional request §4) or a software ring under a TPM2/KMS-wrapped root | — (vendor servers are not Macs) | YFex.Cryptography |
| licensing local state + running-time counter + meters | app `IKeyValueStore` + `AddYFexStorageEncryption` | same | same | YFex.Persistence(.Encryption) |
| monotonic running time | `Stopwatch` includes sleep | excludes sleep | excludes sleep | BCL — both are lower bounds (04 §2) |
| file check-in (disconnected) | files chosen by the admin through the app's file dialog | same | same | the app's UI |

Licensing stores **no files of its own** (everything goes to the app's encrypted `IKeyValueStore`), so there are no per-OS directories
to choose. The evaluator loads its state in `IHostedLifecycleService.StartingAsync`, before other hosted services, so the first
`PermissionSet` of the process is already masked.

Tests run on Windows and Linux in CI (macOS when a machine is available), with Installations' and Time's fakes.

## 2. Every cryptographic use, mapped

No `ECDsa.Create`, `HMACSHA256`, `AesGcm` or `IDataProtector` in this repo: YFex.Cryptography's architecture test applies with **no new
allow-list entry**.

| Use | API | Domain / ring |
|---|---|---|
| vendor signs License, ActivationList, VendorKeyList | Installations' `SignedBundle` codec over `SignedPayload` | `yfex.bundle.v1`, ring `licensing/vendor` |
| installations verify them | Installations' `IBundleVerifier`, publisher `vendor` with an `IPublicKeyDirectory` built from the pinned keys + the current key list (§3) | — |
| activation request, check-in request | `SignedPayload.Sign` with the licensee ring | `yfex.licensing.activation.v1`, `yfex.licensing.check-in.v1` |
| check-in response (binds the nonce) | `SignedPayload.Sign` with the vendor ring | `yfex.licensing.check-in-response.v1` |
| usage report from an installation | Installations' evidence key (`IInstallationKeyStore`) + `SignedPayload` | `yfex.licensing.usage.v1` |
| `TenantRoot`, `CatalogHash` | `Hashing.Sha256` → core `Hash256` | — |
| canonical payloads | `CanonicalWriter` / `CanonicalReader` | — |
| local state at rest | `SealedValue` v3 through `AddYFexStorageEncryption` (ring `device/storage`) | — |
| nonces, activation codes | the BCL `RandomNumberGenerator` (to confirm it is outside the banned list; otherwise a crypto helper) | — |

## 3. Vendor keys: pinning, rotation, compromise (⚑LC17)

```csharp
// YFex.Licensing — pinned in the app binary
public sealed class VendorKeys
{
    public static VendorKeys Pin(string active, string standby);     // Base64 SubjectPublicKeyInfo, P-256
}

// payload of "licensing.vendor-keys" (publisher vendor, stream "vendor", audience every tenant)
public sealed record VendorKeyList(long Version, ImmutableArray<VendorKeyEntry> Keys);
public sealed record VendorKeyEntry(KeyId Id, ReadOnlyMemory<byte> PublicKey, VendorKeyRole Role, DateTimeOffset Since);
public enum VendorKeyRole : byte
{
    Active = 1,     // signs new documents
    Standby = 2,    // recovery key, offline; signs only key lists
    Retired = 3,    // normal rotation: still verifies the documents it signed, signs nothing new
    Revoked = 4,    // compromise: verifies nothing from the moment the list arrives
}
```

| Event | Who signs the new key list | Rule that makes it safe |
|---|---|---|
| yearly rotation | the current **active** key | the active key may **add** an active key and mark **itself** `Retired`; it can never change or remove the standby. Retired keys keep verifying what they signed, so a disconnected site that checks in every 90 days is not affected |
| active key stolen | the **standby** key, taken from the safe | the standby may mark any key `Revoked`, become active and name a new standby; a list signed by the standby **wins** over any list signed by an active key of the same or lower version |
| standby lost (not stolen) | the active key **cannot** replace it | ship an app update with a new pinned standby (rare by design) |
| both stolen | — | app update with new pinned keys |

- Bundles are accepted when signed by an `Active` or `Retired` key of the current list. After a compromise, the vendor re-issues every
  current License and ActivationList with the new active key (higher sequences); documents signed by a `Revoked` key stop verifying the
  moment the new list arrives. The vendor therefore delivers the new list **together with** the re-issued documents (one check-in
  response, one push), so an installation switches in one step and never falls to `LicenseMissing`.
- The standby private key lives **offline** (a hardware token in a safe). Using it is a two-person procedure
  (`RotateVendorKey` requires an approval, 05 §5).
- New app versions pin the current active + standby, so fresh installs do not depend on receiving the list first.
- No certificate chain: two keys and a signed list give the same recovery property with less machinery.

## 4. Requests to YFex.Cryptography

| Request | Priority | Status |
|---|---|---|
| `FileSigningKeyStore` / file root key for Linux/macOS (already requested by Installations, installations 09 §5) | needed for Linux on-premises centrals | open |
| confirm `RandomNumberGenerator` is allowed outside the crypto repo (or name the helper) | small | open |
| an `ISigningKey` over a cloud KMS / PKCS#11 HSM for the vendor's signing key | optional (the vendor can use a TPM-backed Windows server first) | not requested yet |
