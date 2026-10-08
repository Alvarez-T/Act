# 10 — Usage examples (before → after, YFex.Farma first)

`✍` marks code an app developer writes. Everything else is library code (shown as `// (sketch)` so the reader sees what happens
underneath). The rule from api-naming-and-annotation-style holds: **app code gets shorter or stays the same**. The cryptography
moves into the libraries.

## 1. Encrypting a stored value on a pharmacy terminal

Real case: `CAIXA-02` at loja-centro keeps the offline outbox (sales waiting for the internet) and a warm cache of agreement customers
("convênio": company-paid purchases) in SQLite.

```csharp
// ✍ Before — Program.cs of YFexFarma.Desktop
services.AddYFexSqliteStorage(AppPaths.Data("farma.db"));
services.AddYFexStorageEncryption(EncryptionKeySource.OsKeyStore);       // F1: new key every start · F2: no AAD · C1: shared AesGcm
// or, believing it encrypts:
services.AddYFexMessagingRpcClient(rpc => rpc.Encryption = EncryptionMode.AesGcm);   // C3: never read — outbox stays plain text
```

```csharp
// ✍ After
services.AddYFexCryptography(cryptography => cryptography
    .UseCngRootKey()                                         // TPM on the checkout if present
    .StoreKeyRingsInFiles(AppPaths.Data("keys")));
services.AddYFexSqliteStorage(AppPaths.Data("farma.db"));
services.AddYFexStorageEncryption(storage => storage.StoreName = "farma");   // ring "device/storage" by default
```

The code that stores and reads values does not change:

```csharp
// ✍ unchanged — e.g. the outbox or a cache
await store.SetAsync("cache:convenio:4711", bytes, ttl: TimeSpan.FromDays(7), ct);
var cached = await store.GetAsync("cache:convenio:4711", ct);
```

```csharp
// (sketch) what EncryptedKeyValueStore.SetAsync now does — zero allocations besides the output array the inner store needs
Span<byte> contextBuffer = stackalloc byte[SealContext.GetLength(StoreDomain, _storeName, key)];
var context = SealContext.Write(contextBuffer, StoreDomain, _storeName, key);   // "yfex.store.v1" [farma, cache:convenio:4711]
var ring = _rings.TryGetCached(_ringName, out var cached) ? cached : await _rings.GetAsync(_ringName, ct);
var sealedValue = new byte[SealedValue.GetSealedLength(value.Length)];
SealedValue.Seal(ring, value, context, sealedValue);
await _inner.SetAsync(key, sealedValue, ttl, ct);
```

**Result:** after a power cut during an ISP outage, the checkout restarts, unwraps the same storage key from the TPM (75 ms once) and
replays the queued sales. A support technician who copies `farma.db` to a laptop gets ciphertext only. A row copied from
`cache:convenio:4711` to `outbox:0042` fails as `AuthenticationFailed` instead of being replayed as a sale.

## 2. Signing an offline sale envelope

Real case: during the outage Ana finalizes a sale of dipirona and vitamin C under agreement credit on `CAIXA-02`. The offline handler
`FinalizarVendaOffline` runs on the terminal, and YFex.Offline wraps the result in a signed `OfflineEnvelope` (offline 08 §2).

```csharp
// Before (offline design draft) — each library would hold its own ECDsa and its own byte layout
using var ecdsa = deviceKeys.Open();                                       // separate key per library (offline F11)
var signature = ecdsa.SignData(JsonSerializer.SerializeToUtf8Bytes(envelope), HashAlgorithmName.SHA256);  // JSON: unstable bytes
```

```csharp
// (sketch) After — YFex.Offline on the terminal; the app writes nothing new
var payload = new ArrayPoolBufferWriter<byte>(512);
var writer = new CanonicalWriter(payload);
envelope.WriteCanonical(ref writer);          // idempotency key, installation + sequence, actor, scope, provenance, bundle versions, command digest
Span<byte> signatureBuffer = stackalloc byte[evidenceKey.SignatureLength];           // 64 for P-256; 3 309 later for ML-DSA-65
DetachedSignature signature = SignedPayload.Sign(evidenceKey, OfflineDomains.Envelope, payload.WrittenSpan, signatureBuffer);
// evidenceKey = ISigningKey "installation/evidence" from CngSigningKeyStore (TPM): ~32 ms, once per sale
```

```csharp
// (sketch) Central, at replay — "as of the moment it happened" (offline 08 §3)
var check = SignedPayload.Verify(installationKeys, OfflineDomains.Envelope, payloadBytes, envelope.Signature,
                                 signedAt: envelope.Provenance.DecidedAt.Value);
switch (check)
{
    case SignatureCheck.Valid: pipeline.Accept(envelope); break;
    case SignatureCheck.KeyRevoked: findings.Raise(OfflineFinding.SignedAfterRevocation(envelope)); break;   // CAIXA-02 was reported stolen earlier
    case SignatureCheck.KeyNotValidAt: findings.Raise(OfflineFinding.OutsideKeyValidity(envelope)); break;
    default: findings.Raise(OfflineFinding.BadSignature(envelope, check)); break;
}
```

`installationKeys` is the `SignatureVerifier` over Offline's installation registry (`IPublicKeyDirectory`). The public key is imported
once (111 µs per verify instead of 513 µs). A signature from a key revoked **before** the sale fails, and one made **before** the
revocation still verifies (offline 03 §5).

## 3. Issuing and verifying a supervisor approval token

Real case: Ana gives 12 % off a customer's monthly purchase. The rule set allows 10 %, so the decision says `ApprovalRequired`. Carla,
the shift supervisor, approves on the checkout with her PIN.

```csharp
// ✍ unchanged for the developer (authorization 06 §1.2)
d.RequireApproval(SalesPermissions.DiscountOverride, path, "discount.above.limit");
```

```csharp
// Before — YFex.Rules, composition root at central: a raw dictionary of string-named keys
var tokens = new HmacOverrideTokens(new Dictionary<string, byte[]> { ["k2"] = key2, ["k1"] = key1 }, currentKeyId: "k2",
                                    lifetime: TimeSpan.FromMinutes(15));
// the token signs (kind, approver, permission, XXH64 fingerprint, times, "k2") — no domain, non-crypto binding (C5)
```

```csharp
// ✍ After — central composition root
services.AddYFexCryptography(cryptography => cryptography
    .UseEnvironmentRootKey()                                              // container secret YFEX_ROOT_KEY
    .StoreKeyRings<SqlKeyRingStore>()                                     // shared by every node
    .KeyRing("approvals/server", ring => ring.For(KeyPurpose.MessageAuthentication)
        .RotateEvery(TimeSpan.FromDays(90)).KeepOpenOnlyFor(TimeSpan.FromDays(1))));
services.AddYFexAuthorization(authorization => authorization
    .Approvals(approvals => approvals.ServerTokens("approvals/server", lifetime: TimeSpan.FromMinutes(15))));
```

```csharp
// (sketch) YFex.Authorization — issue (online)
Hash256 digest = decision.ComputeDigest();                                // Rules: BCL SHA-256 of the canonical outcome into the core Hash256
var body = new ApprovalTokenBody(ApprovalTokenKind.Server, approver.Subject, permission.Id, digest,
                                 new ValidityPeriod(now, now + lifetime), ring.Current.Id);
Span<byte> payload = stackalloc byte[ApprovalTokenBody.MaxCanonicalLength];
body.TryWriteCanonical(payload, out int length);
Span<byte> tag = stackalloc byte[MessageAuthentication.TagLength];
MessageAuthentication.ComputeTag(ring.Current, ApprovalDomains.Version2, payload[..length], tag);

// (sketch) verify (when Ana resends the command with the token)
if (!MessageAuthentication.VerifyTag(ring, token.Key, ApprovalDomains.Version2, payload[..length], token.Tag.Span))
    return ApprovalCheck.InvalidSignature;
if (!token.Decision.FixedTimeEquals(decision.ComputeDigest()))
    return ApprovalCheck.DecisionMismatch;                                // Ana changed 12 % to 15 % after Carla approved
```

Offline (central unreachable, `PC-GERENTE` off), the same body is signed with the checkout's evidence key through `SignedPayload` as
`ApprovalTokenKind.Device`. Central verifies it as of the sale time, capped and flagged (D34, ⚑O15). Tokens issued by the old code
during the upgrade window still verify through `ApprovalTokenVersion1Codec` (golden-tested) for one `MaxAge`.

## 4. Sealing a customer's CPF for the server while offline

Real case: a new customer, Maria, registers at the counter during the outage to join the loyalty program. Her CPF must reach central,
but the checkout must not be able to read it back from its outbox (privacy 05 §5.5).

```csharp
// Before — the CPF travels in the queued command in clear text (protected only by storage encryption, which F1 broke)
await Clientes.Commands.Cadastrar(new CadastrarCliente(nome, cpf, telefone)).SendAsync(ct);
```

```csharp
// ✍ After — the developer writes the same line; Cpf is [Sensitive(PersonalIdentifier)] once on the type,
// and Privacy's offline intake seals classified identifiers of queued commands
await Clientes.Commands.Cadastrar(new CadastrarCliente(nome, cpf, telefone)).SendAsync(ct);
```

```csharp
// (sketch) YFex.Privacy intake on the terminal
Span<byte> cpfBytes = stackalloc byte[Cpf.MaxCanonicalLength];                        // 11 digits as 11 bytes (ICanonicalValue<Cpf>)
Cpf.TryWriteCanonical(in command.Cpf, cpfBytes, out int length);
RecipientPublicKey intakeKey = manifest.IntakeKey;                                    // tenant's public key from the signed manifest
Span<byte> contextBuffer = stackalloc byte[128];
var context = SealContext.Write(contextBuffer, PrivacyDomains.Intake, tenant, "cliente.cpf");
var sealedCpf = new byte[RecipientSealing.GetSealedLength(intakeKey.Algorithm, length)];   // 11 + 99 = 110 bytes
RecipientSealing.Seal(in intakeKey, cpfBytes[..length], context, sealedCpf);
CryptographicOperations.ZeroMemory(cpfBytes);

// (sketch) central, at sync: open with the intake private key, re-seal with Maria's subject key (crypto-shreddable)
var opened = RecipientSealing.Open(intakeKeyPrivate, sealedCpf, context, plaintext);
if (opened.TryGetValue(out Opened o))
    SealedValue.Seal(subjectKeyRing, plaintext[..o.Written], subjectContext, cell, keyClass: (byte)KeyKind.SubjectData);
```

1.2 ms on the checkout (once per registration). A stolen checkout reveals no CPF from its outbox. After Phase 5 the same call uses the
hybrid ML-KEM algorithm when `intakeKey.Algorithm` says so, with no app change (08 §3).

## 5. Hashing an audit chain

Real case: the server sequencer seals the `fiscal` retention class chain of tenant farmaviva for 2026-10-03 (audit 06 §4–§5).

```csharp
// Before (audit design draft) — HashChain in core with a timestamp parameter and an uppercase domain;
// today's audit code hashes digests with SHA256.HashData directly (AuditValueEncoding.cs:231)
var next = HashChain.Next("YFEX-AUDIT-CHAIN-v1"u8, previous, index, recordedTicks, entryBytes);
```

```csharp
// (sketch) After — YFex.Audit sequencer
Hash256 hash = HashChain.Genesis(AuditDomains.Chain, chainKeyBytes, previousDayFinal);
foreach (ref readonly var entry in batch.AsSpan())
    hash = HashChain.Next(AuditDomains.Chain, in hash, entry.Index, entry.CanonicalBytes);    // recorded time is inside the canonical entry

// periodic anchor (every 1 000 entries / 10 min): one signature, not one per entry
Span<byte> signatureBuffer = stackalloc byte[anchorKey.SignatureLength];
DetachedSignature anchorSignature = SignedPayload.Sign(anchorKey, AuditDomains.Anchor, anchorPayload, signatureBuffer);

// day seal: Merkle root for inclusion proofs of one customer's history without revealing others
Hash256 dayRoot = MerkleTree.ComputeRoot(leafHashes);
```

Cost: ~0.4 µs per 1 KiB entry (SHA-256), 0 B allocated. A batch of 1 000 entries hashes in about 0.5 ms.

## 6. Rotating a tenant key

Real case: an employee of the farmaviva network who had database access leaves on bad terms. The admin wants farmaviva's stored
integration secrets (PBM portal password, payment provider API key) under a new key now, without touching other tenants.

```csharp
// Before — Web.Data: one AES key for everybody (TenantSalt never applied, C2), no key id in the ciphertext:
// changing SecretProtectionOptions.Key makes EVERY stored secret of EVERY tenant unreadable at once.
services.AddSingleton<ISecretProtector>(new AesSecretProtector(new SecretProtectionOptions { Key = newKey }));   // = outage
```

```csharp
// ✍ After — once, when the tenant asks for its own key lifecycle (default tenants use keys derived from "web/secrets")
services.AddYFexCryptography(cryptography => cryptography
    .KeyRing("web/secrets", ring => ring.For(KeyPurpose.Derivation))
    .KeyRingsMatching("tenant/*/secrets", ring => ring.For(KeyPurpose.Sealing).KeepOpenOnlyFor(TimeSpan.FromDays(30))));

// ✍ the admin action (a command handler at central, permission settings.keys.rotate, audited)
await rings.RotateAsync(new KeyRingName("tenant/0=farmaviva/secrets"), ct);        // Pending → Active after the propagation window
await secrets.ResealAsync(tenant: "0=farmaviva", ct);                               // background: re-seal rows whose header key ≠ current
```

How readers route: the sealed value's `key class` byte says "derived from `web/secrets`" (1) or "tenant ring" (2). The key id says which
version. Old rows keep opening during the re-seal, and other tenants are untouched. Once `ResealAsync` reports zero rows on the old key,
`DestroyAsync` removes it.

## 7. Offline login verifier (bonus: the PBKDF2 one-shot)

```csharp
// (sketch) YFex.Offline — after Ana's online login on CAIXA-02, cache a verifier for offline use (≤ 7 days, offline 05 §5)
Span<byte> verifier = stackalloc byte[PasswordHashing.VerifierLength];
PasswordHashing.CreateVerifier(password, verifier);                                   // PBKDF2-SHA256 600 000: ~55 ms
await verifierStore.SaveAsync(ana.Subject, verifier, ct);                             // sealed at rest, context [user]

// during the outage
switch (PasswordHashing.Verify(typedPassword, storedVerifier))
{
    case PasswordCheck.Match: return SignedIn(ana);
    case PasswordCheck.MatchNeedsUpgrade: await RecreateVerifierAsync(ana, typedPassword); return SignedIn(ana);
    default: lockout.RecordFailure(ana); return Rejected();
}
```
