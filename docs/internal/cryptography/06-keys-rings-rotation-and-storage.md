# 06 — Keys, key rings, rotation and storage (⚑K6, ⚑K7, ⚑K8)

## 1. `SecretKey`: symmetric key material in pinned, zeroed memory

```csharp
namespace YFex.Cryptography;

public enum KeyPurpose : byte { Sealing = 1, MessageAuthentication = 2, Derivation = 3, Wrapping = 4,
                                Signing = 5, KeyAgreement = 6 }   // closed: the library owns it. 5/6 = P-256 private scalars (as built)

[Sensitive(DataSensitivity.Secret)]               // core Privacy attribute: logging/capture never prints it
public sealed class SecretKey : IDisposable
{
    public KeyId Id { get; }
    public KeyPurpose Purpose { get; }
    public int Length { get; }

    public static SecretKey Generate(KeyPurpose purpose, int length = 32);               // RandomNumberGenerator.Fill into pinned memory
    public static SecretKey Import(KeyId id, KeyPurpose purpose, ReadOnlySpan<byte> material);   // copies; the caller clears its span

    public KeyLease Lease();                       // keeps the material alive while an operation uses it
    public void Dispose();                         // marks for destruction; bytes are zeroed when the last lease ends
    public override string ToString();             // "SecretKey(01927c…, Sealing)", never the bytes
}

public readonly struct KeyLease : IDisposable { public SecretKey Key { get; } public void Dispose(); }
```

- **Pinned:** material is allocated with `GC.AllocateArray<byte>(length, pinned: true)` (pinned object heap). A compacting GC can never
  leave stray copies, so zeroing actually removes the key from memory.
- **Zeroed:** `CryptographicOperations.ZeroMemory` when the key is disposed and the lease count reaches zero. Crypto-shredding (Privacy) can
  destroy a key while another thread is opening a value with it: the open completes, then the bytes are wiped.
- **Not exportable:** the material is `internal`. It leaves the library only **wrapped** (§4). Privacy's `KeyLease` (privacy 05 §5.2)
  becomes this type.
- **Purpose is enforced:** `SealedValue.Seal` refuses a `MessageAuthentication` key, and `KeyDerivation` refuses anything but
  `Derivation`. Using one key for two jobs is a classic mistake. Here it is an `ArgumentException` at the first call (a programmer error,
  07 §3).

## 2. `KeyRing` and `KeyRingProvider`

```csharp
public readonly record struct KeyRingName(string Value);     // open set, chosen by the owner: "device/storage", "tenant/0=farmaviva/secrets"

public enum KeyStatus : byte
{
    Unknown = 0,     // not a key of this ring (StatusOf)
    Pending = 1,     // opens/verifies, not yet used to seal (propagation window on multi-node servers)
    Active = 2,      // the current key: seals and MACs
    OpenOnly = 3,    // a previous key: opens and verifies
    Destroyed = 4,   // material gone; opens return KeyDestroyed (crypto-shredding)
}

/// Immutable snapshot. Readers never lock: the provider swaps the whole ring with Volatile.Write.
public sealed class KeyRing
{
    public KeyRingName Name { get; }
    public KeyPurpose Purpose { get; }
    public SecretKey Current { get; }
    public bool TryGet(KeyId id, out SecretKey key);                          // Pending, Active, OpenOnly
    public KeyStatus StatusOf(KeyId id);                                      // 0 = unknown
    public ImmutableArray<KeyRingEntry> Entries { get; }
}

public readonly record struct KeyRingEntry(KeyId Id, KeyStatus Status, ValidityPeriod Validity);   // ValidityPeriod: core (⚑Z13)

public sealed class KeyRingProvider                                          // singleton, thread-safe
{
    public ValueTask<KeyRing> GetAsync(KeyRingName name, CancellationToken cancellationToken);   // first call: load + unwrap; then cached
    public bool TryGetCached(KeyRingName name, [NotNullWhen(true)] out KeyRing? ring);           // hot path
    public ValueTask<KeyRing> RotateAsync(KeyRingName name, CancellationToken cancellationToken);
    public ValueTask DestroyAsync(KeyRingName name, KeyId key, CancellationToken cancellationToken);
}
```

Configuration (composition root):

```csharp
services.AddYFexCryptography(cryptography => cryptography
    .UseCngRootKey()                                           // YFex.Cryptography.Windows: TPM first, then software KSP (§3)
    .StoreKeyRingsInFiles(AppPaths.Data("keys"))               // FileKeyRingStore (05 §7)
    .KeyRing("device/storage", ring => ring
        .For(KeyPurpose.Sealing)
        .RotateEvery(TimeSpan.FromDays(365))
        .KeepOpenOnlyFor(TimeSpan.FromDays(730))));
```

Families of rings (one per tenant) are configured once with a pattern: `.KeyRingsMatching("tenant/*/secrets", ring => …)`. A ring of the
family is created on first use.

> **As built (2026-10-04): generic per-scope keys.** `cryptography.ScopedKeyRings("web/secrets", scoped => scoped.For(KeyPurpose.Sealing)
> .Master(ring => ring.RotateEvery(…)).DedicatedRings("tenant/{scope}/secrets", ring => …))` + the `ScopedKeyRings` service
> (`GetAsync(family, scope)`, `CreateDedicatedRingAsync`, `ReloadAsync`, hot path `TryGetCached`). Every scope gets keys derived from
> the master (nothing stored per scope); a scope that needs its own lifecycle gets a dedicated ring, and the merged `KeyRing` keeps
> opening its derived-key values (`NeedsReseal`). Any service can use it (Web.Data credentials, Privacy, per-store MAC keys…).
> `KeepOpenOnlyFor(period)` **destroys** keys superseded longer than the period (background, or `RetireExpiredAsync`).
> `TryGetExistingAsync` loads a ring without creating it.

**One concept replaces four** (01 §2): Rules' `HmacOverrideTokens` dictionary, Privacy's `KeyRing`, Offline's `IBundleSigner` ring
(asymmetric, §5) and Audit's signing-key list all become a `KeyRing` (symmetric) or a set of `PublicKeyRecord`s with validity (§5).

## 3. Root keys: the ladder (⚑K7)

```csharp
public interface IRootKeyProvider                    // moved from privacy 05 §5.2; same two methods + identity
{
    byte ProviderId { get; }                          // 05 §7 (RootKeyFormat); first byte of every wrapped blob
    KeyProtection Protection { get; }
    ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);
    ValueTask<int> UnwrapAsync(ReadOnlyMemory<byte> wrapped, Memory<byte> key, CancellationToken cancellationToken = default);
}

public enum KeyProtection : byte { Memory = 0, File = 1, OperatingSystem = 2, Hardware = 3 }   // also used by ISigningKey (04 §5)
```

| Provider | Package | Protection | Where | Notes |
|---|---|---|---|---|
| `CngRootKeyProvider` | `.Windows` | `Hardware` (TPM via `MicrosoftPlatformCryptoProvider`) else `OperatingSystem` (`MicrosoftSoftwareKeyStorageProvider`) | Windows terminals, site brokers, Windows servers | persisted RSA-2048, `ExportPolicy = None`, OAEP-SHA256 (measured: wrap 2.6 ms, unwrap 75 ms on the TPM). `MachineKey` for services (site broker), user key for desktop apps. Fallback ladder: TPM OAEP-SHA256 → TPM OAEP-SHA1 (older TPM firmware; still sound for key transport) → software KSP. Package-free (BCL `CngKey`/`RSACng`) |
| `EnvironmentRootKeyProvider` | library | `Memory` | containers, cloud servers (the orchestrator's secret store injects `YFEX_ROOT_KEY`) | AES-256-GCM wrap with the 32-byte key |
| `ProvidedRootKeyProvider` | library | `Memory` | tests, adapters for a KMS client the app already has | |
| `FileRootKeyProvider` | library | `File` | Linux/macOS until Keychain/libsecret providers exist; dev machines | key file created with `UnixCreateMode = UserRead \| UserWrite` (0600; fixes the C7 pattern) and a startup warning "root key protected by file permissions only" |
| `DataProtectionRootKeyProvider` | optional adapter (ASP.NET Core shared framework) | `OperatingSystem`/whatever DataProtection is configured with | ASP.NET servers that already run DataProtection | wraps the **store key** with `Protect`/`Unprotect`. Randomized output is fine **for wrapping**. F1 came from using it as key **material** |
| DPAPI | — | — | — | **not needed**: CNG covers Windows without the `System.Security.Cryptography.ProtectedData` package. If ever wanted, `[LibraryImport("crypt32.dll")] CryptProtectData` in `.Windows` is package-free and AOT-safe |
| Cloud KMS (AWS KMS, Azure Key Vault, GCP KMS) | — | `Hardware` | servers | seam only. Each SDK is a NuGet package (approval) |

What each level protects against (same as offline 05 §1): a TPM or CNG root stops a **stolen disk** and **another Windows user**. Nothing
stops an administrator running code as the app on the running machine. That is why offline data is also signed and re-validated
centrally.

## 4. Key-ring stores

```csharp
public interface IKeyRingStore
{
    ValueTask<StoredKeyRing?> LoadAsync(KeyRingName name, CancellationToken cancellationToken);
    ValueTask SaveAsync(StoredKeyRing ring, long expectedRevision, CancellationToken cancellationToken);   // optimistic concurrency
}
// StoredKeyRing = name, purpose, revision, wrapped store key, entries with sealed material (05 §7)
```

| Store | Package | For |
|---|---|---|
| `InMemoryKeyRingStore` | `.Testing` | tests |
| `FileKeyRingStore` | library | devices, single servers (05 §7 file, atomic replace via temp file + `File.Move(overwrite: true)`). **Finding F-P1 (2026-10-07):** `store.key`, ring files and the directory are written without Unix modes (0644/0755 under the default umask); fix designed in 14 §3 (0600/0700), not built |
| `KeyValueKeyRingStore` | `YFex.Persistence.Encryption` | apps that keep everything in their `IKeyValueStore` (the raw inner store, never the encrypted decorator, to avoid a chicken-and-egg loop) |
| SQL vault | `YFex.Privacy.Data` (privacy Phase 4) | multi-node servers, tenant rings, subject keys |

## 5. Signing keys (asymmetric) and public-key directories (⚑K8)

Private signing keys are **not** `SecretKey`s: the best ones cannot be exported at all.

| Store | Package | Keys |
|---|---|---|
| `CngSigningKeyStore` | `.Windows` | persisted ECDsa P-256, `ExportPolicy = None`, TPM first (`KeyProtection.Hardware`) then software KSP. Measured TPM sign: **31.8 ms** |
| `FileSigningKeyStore` (**designed 2026-10-07, not built: 14 §2**) | library | **named** P-256 keys on Linux/macOS (installation keys where no hardware store exists): PKCS#8 sealed with the store key under the root provider, Unix 0600/0700, atomic first-writer-wins creation, always `KeyProtection.File`, `CopyWithPrivateKeyAsync` for `SslStream` (PKCS#12 round trip on Windows) |
| `AppleSigningKeyStore` (**spike later: 14 §5**) | `.Apple` | Secure Enclave → data-protection Keychain (`Hardware`/`OperatingSystem`); needs a signed app with `keychain-access-groups` |
| Linux TPM 2.0 (**noted only: 14 §6**, native dependency needs approval) | `.Linux` | TPM key through the `tpm2-openssl` provider + `SafeEvpPKeyHandle.OpenKeyFromProvider` |
| `InMemorySigningKeyStore` | `.Testing` | tests |
| **`SigningKeyRing`** (as built 2026-10-04; replaces `FileSigningKeyStore` **for rotating server keys only**) | library | rotating **software** keys of servers (snapshots, bundles, anchors): a key ring `For(KeyPurpose.Signing)`, so rotation, propagation, root wrapping and retirement are the ring's; it is its own `IPublicKeyDirectory`. Not for named installation keys (one key per name, never shared) |

**Offline's installation keys** (offline 03 §2) become two names in this store: `"installation/transport"` (rotated with its 90-day
certificate) and `"installation/evidence"` (rotated on re-enrollment). `IInstallationKeyStore` stays in YFex.Offline as a thin, typed
wrapper. Rules' `DeviceOverrideTokenIssuer(deviceId, ECDsa privateKey, …)` stops taking a raw `ECDsa` (⚑Z12 move) and takes the evidence
`ISigningKey`, so **one device identity** serves approvals, envelopes, audit heads and consent (offline F11).

**The 32 ms TPM cost decides what gets signed:**

| Signed object | Rate on a busy checkout | Signed with |
|---|---|---|
| offline sale envelope | ~1/min | evidence key (TPM): 32 ms is invisible next to printing a receipt |
| device approval token | a few per day | evidence key |
| audit node-chain **batch head** | every batch (seconds) | evidence key, **once per batch**, never per entry (entries are hash-chained) |
| site broker session tokens, slices, receipts | tens per minute per site | broker evidence key: TPM for receipts. If a broker exceeds ~20 signatures/s, a **session subkey** (software ECDsa, certified by the TPM key, ≤ 12 h) signs the high-rate ones. Pattern documented here, used only if measurements require it |

**Public keys** are looked up through `IPublicKeyDirectory` (04 §5): Offline implements it from the installation registry and the
manifest. Audit implements it from `audit.signing_keys`. Records carry a `ValidityPeriod` and an optional revocation time, so "valid if
signed before revocation" is one rule in `SignatureVerifier`.

## 6. Rotation

| Ring / key | Purpose | Rotate | Keep previous | Notes |
|---|---|---|---|---|
| `device/storage` | Sealing | yearly, or at 2³¹ seals | until no value references it (re-seal on write + background sweep), max 2 years | a POS seals ~10⁶ values per year, far below the nonce bound |
| `tenant/{scope}/secrets` (Web.Data) | Derivation | yearly (derived keys follow) | while rows reference it | per-tenant keys are **derived** (C2 fix), no per-tenant storage |
| approval HMAC (`approvals/server`) | MessageAuthentication | 90 days | 1 day (tokens live 15 min) | |
| event audiences (`messaging/event-audience`) | MessageAuthentication | 30 days | the token lifetime (default 12 h) | shared by all nodes through the SQL or file store (C4) |
| Privacy pseudonym secret | Derivation | per domain (`analytics` yearly; `log`/`identifier:*` never) | per privacy 05 §5.4 | |
| Privacy field / subject keys | Sealing | privacy 05 §5.4 | until re-encrypted / destroyed | |
| bundle signing, audit anchors | signature | yearly | **public keys forever** (old bundles/anchors verify for years) | |
| installation transport / evidence | signature | 90 days / re-enrollment | public keys forever in the registry | |

**Nonce bound:** `KeyRingProvider` counts seals per key in memory and persists the count every 1 000 seals and at shutdown. At 2³¹ (half
the NIST bound, which leaves margin for the counts a crash loses) it rotates. Only busy servers will ever get near it.

**Multi-node rotation (two-phase):** the new key is written as `Pending` and every node loads it within the propagation window (default
10 minutes: ring cache TTL plus a change signal). Only then does it become `Active`. A node that meets an **unknown** key id reloads the
ring once (rate-limited) before it reports `KeyUnknown`. Nodes therefore never fail to open values sealed by a peer that rotated first.

**Time:** rotation and validity use the `TimeProvider` from DI. On devices that is Offline's `TrustedTimeProvider`, so setting the clock
back does not resurrect an expired key or token.

**Re-seal on touch:** `OpenResult.Opened.NeedsReseal` is true for a non-current key or a legacy layout. Stores re-seal on the next
write. A background job in the owning library sweeps cold data when a key must be retired by a deadline.

## 7. Crypto-shredding interplay (Privacy)

The vault owns **which** key belongs to which data subject and the tombstones. The library supplies the mechanism:

```csharp
// (sketch) Privacy's reader: route by header, resolve from the vault, open with the library
if (!SealedValue.TryReadHeader(cell, out var header)) return SubjectValue.Corrupt;
var lookup = await vault.TryGetKeyAsync(scope, header.Key, cancellationToken);   // Privacy: Found(SecretKey) | Destroyed | Unknown
OpenResult result = lookup switch
{
    VaultKeyFound found => SealedValue.Open(found.Key, cell, context, plaintext),
    VaultKeyDestroyed   => new KeyDestroyed(header.Key),                          // normal after erasure: shown as <erased>
    VaultKeyUnknown     => new KeyUnknown(header.Key),                            // case types convert implicitly to OpenResult (07 §3)
};
```

`DestroyAsync` on a ring disposes the `SecretKey` (zeroed once the last lease ends), marks the entry `Destroyed` and saves. The Privacy
vault does the same for subject keys and adds the tombstone and cross-node signal (privacy 05 §8).

## 8. Large streams and key-ring escrow (CR1 for databases-and-backups ⚑DB18, built 2026-11-11)

**`SealedStream`** (format 0x82, `Sealing/SealedStream.cs`): any-length content sealed to 1–8 recipients. A random 32-byte data
key per stream is recipient-sealed (0x81/0x11) to each recipient in the header; content is cut into chunks (default 64 KiB, 4 KiB–16
MiB), each AES-256-GCM under HKDF(data key, SHA-256(header) ‖ context) with nonce = chunk index ‖ final flag (STREAM construction):
reorder, drop, truncation and header edits fail. Opening writes plaintext as chunks authenticate, so on a failed result the caller
discards the output. The input must end where the sealed stream ends (one stream per file/object). Golden tests pin both layouts
(`SealedStreamTests`, `KeyRingEscrowTests`).

**`KeyRingEscrow`** (`Keys/KeyRingEscrow.cs`): `Export(ring, escrowPublicKey)` → one recipient-sealed blob with every entry (status,
validity) and the material of non-destroyed keys, context bound to the ring name; `Import(escrowKeys, blob, name, now)` → `KeyRing`.

**Procedure** (backup rings; the same for any ring whose loss would make data unreadable):

1. **Create the escrow (recovery) key away from the server**: on an offline machine or HSM, a `KeyAgreement` ring of its own. Only
   its **public key** goes to the server (configuration or signed manifest). The private part is kept by the customer (on-prem) or
   the vendor (opt-in recovery, ⚑DB18), in a safe / second site / HSM, never next to the backups.
2. **Seal every backup set to two recipients**: the backup ring (daily use) and the escrow key. Losing the server then never loses
   the sets, even without step 3.
3. **Escrow the backup ring itself**: `KeyRingEscrow.Export` after creation, after every rotation and after every destruction;
   store the blob with the backups (it is useless without the escrow private key). Destructions must be re-exported, or an import
   would bring a shredded key back.
4. **Recover**: on the new machine, open the escrow ring from its safe copy, `KeyRingEscrow.Import`, then
   `KeyRingProvider.RestoreAsync(ring)` (stores it wrapped by the new machine's root key; refuses when a ring of that name already
   exists, so it never overwrites a live ring), restore. Then rotate the backup ring if the old machine may be compromised.
5. **Drill** with the restore drills (⚑DB19): at least yearly, import the escrow into a throwaway process and open a recent set.

Tooling to create/hold the escrow key (an offline CLI command) belongs to YFex.Backup DB-P1.
