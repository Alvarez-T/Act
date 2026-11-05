# 06 — Tamper evidence

Audit cannot prevent a privileged attacker from changing bytes on a disk. It **detects** any change after the fact, shows exactly where,
and lets a third party (auditor, fiscal inspection, customer) check the evidence without trusting the operator's database.

## 1. Threat model

| # | Who | Does what | Detected by |
|---|---|---|---|
| T1 | DBA / attacker with SQL access | edits a field of a sealed entry (`UPDATE`) | chain hash mismatch at that index (§6); blocking triggers + insert-only role make it noisy first (07 §2) |
| T2 | same | deletes an entry | index gap in the chain; Merkle root mismatch (§5) |
| T3 | same | inserts a fabricated entry in the middle | every later hash changes → mismatch vs signed anchor |
| T4 | same | truncates the tail of a day (removes the last entries) | last anchor/day seal covers a higher index than what exists |
| T5 | same | deletes a whole day | next day's genesis hash links to the deleted day's final hash (§4); no tombstone exists |
| T6 | same, with the anchor signing key | rewrites a day and re-signs it | external witness copy of the day-seal hash (§9) and previous WORM exports no longer match |
| T7 | network / server before sealing | drops or alters entries sent by a device | node chain + device signature: the server cannot forge a device's chain (§3) |
| T8 | modified POS client | omits entries locally | node sequence gap (§7); signed head on every batch |
| T9 | compromised device key | fabricates device entries | not prevented: key revocation, server-side plausibility (e.g. approvals capped, logging anomalies), review queue for offline approvals |
| T10 | the application itself (bug or malicious code) | writes a false fact at the source | not detectable cryptographically; mitigated by code review of `AuditSchema.verified.txt`, trigger mode for out-of-band writes (04 §4) |

## 2. Primitives (core, ⚑A31)

```csharp
namespace YFex.Auditing;

/// <summary>Canonical binary writer (02 §9 rules) over a pooled IBufferWriter&lt;byte&gt;. Shared with the Privacy consent ledger.</summary>
public ref struct CanonicalWriter { /* WriteTag, WriteVarUInt, WriteString (NFC UTF-8), WriteDecimal (normalized), WriteGuid, … */ }

/// <summary>SHA-256 chaining with domain separation: next = SHA256(domain ‖ previous ‖ index ‖ payload). Stack buffers, no allocation.</summary>
public readonly struct Hash256 : IEquatable<Hash256> { /* 32 bytes inline (4 × ulong), FormatHex, TryParseHex */ }

public static class HashChain
{
    public static Hash256 Genesis(ReadOnlySpan<byte> domain, ReadOnlySpan<byte> chainKey, in Hash256 previousChainFinal);
    public static Hash256 Next(ReadOnlySpan<byte> domain, in Hash256 previous, ulong index, long timestampTicks, ReadOnlySpan<byte> payload);
}
```

Domains: `"YFEX-AUDIT-NODE-v1"`, `"YFEX-AUDIT-CHAIN-v1"`, `"YFEX-AUDIT-ANCHOR-v1"`, `"YFEX-AUDIT-TOMBSTONE-v1"` (the Privacy ledger uses
its own domain strings). Domain separation guarantees a hash from one structure can never be replayed as another.

## 3. Node chains and device signatures (⚑A17)

```
NodeHash(0)   = SHA256("YFEX-AUDIT-NODE-v1" ‖ nodeId ‖ 32 zero bytes)
NodeHash(n)   = HashChain.Next(node domain, NodeHash(n-1), n, occurredTicks(n), entryBytes(n))
batch head    = NodeChainHead(NodeId, LastSequence, LastHash, SignedAtTicks, KeyId)
signature     = ECDsa P-256 (IEEE P1363, 64 bytes) over canonical(batch head) with the device's private key
```

```csharp
public readonly record struct NodeChainHead(Guid NodeId, ulong LastSequence, Hash256 LastHash, long SignedAtTicks, string KeyId);
```

- The device computes `NodeHash` at append time (inside the journal writer loop) and stores the head in `audit:head`.
- **Device keys** are the same kind the ecosystem already uses for offline approvals (YFex.Rules `DeviceOverrideTokenIssuer`,
  `IDeviceKeyStore`). This design asks to **move** that device identity (key generation, enrollment, `IDeviceKeyStore` server registry,
  revocation) to a shared place so Rules approvals, audit batches and the Privacy offline consent ledger use one device key per installation
  (10 §3). Private keys are protected by the platform (DPAPI on Windows through the existing platform packages, Keychain/Keystore on mobile).
- Servers without a device key (they write transactionally) still have a node chain for their **spill journal**; their transactional
  entries are protected by the server chain only.
- **Ingest checks** (05 §6): (1) the batch's entries re-hash to `Head.LastHash` starting from the stored head of that node; (2) the signature
  verifies with the node's registered, non-revoked public key; (3) sequences continue the stored head. (1) or (2) failing rejects the batch
  and opens a Critical `Audit.NodeChainRejected` entry; (3) failing opens a **gap** (§7) but stores the entries.

## 4. Server chains and the sequencer (⚑A16)

**One chain per (top-level tenant, retention class, UTC day of arrival).**

- *Per tenant*: verification, export and erasure requests are per tenant; tenants never see each other's hashes.
- *Per retention class*: retention can drop a whole chain without breaking another (08 §5). Mixing a 1-year and a 5-year entry in one chain
  would make the 1-year purge impossible without breaking the chain.
- *Per UTC day of arrival*: bounded chains (cheap verification, natural partitions), and late offline entries never reopen sealed days.

```
Genesis(t, c, d) = HashChain.Genesis(chain domain, canonical(t, c, d), FinalHash(t, c, previous non-empty day) or 32 zero bytes)
Hash(i)          = HashChain.Next(chain domain, Hash(i-1), i, recordedTicks(i), entryBytes(i))
```

Linking each day's genesis to the previous day's final hash makes **deleting a whole day** (T5) detectable unless a signed tombstone
(08 §5) explains it.

**The sequencer** is the only writer of a chain:

- One logical sequencer per active chain, with **exclusive ownership** through `pg_try_advisory_lock(chain key hash)` (session lock held while the
  sequencer runs; released on disconnect, so a crashed owner frees the chain). Decided 2026-11-04: not a YFex.Jobs lease. Jobs' `IJobStore.TryAcquireLeaseAsync` exists, but no
  store implements it across servers (in-memory and EF grant it always; KeyValue locks within one process), and Jobs does not build one for
  a single user; its run claim covers "run once", not long-held ownership. Many chains seal in parallel across server instances.
- Loop: read unsealed inbox rows for the chain in `(received_at, entry_id)` order → compute hashes in memory → **one** insert of up to 1 000
  rows into `audit.seals` (insert-only, PK = chain key + index, unique entry id) → maybe write an anchor (§5).
- Crash-safe: on restart it reads the last seal of the chain and continues. A partially written batch never exists (single statement).
- Throughput: hashing 1 000 × 500 B entries ≈ 1 ms; sealing is never the bottleneck.
- Latency: entries are sealed within ~1 s of arriving (configurable `SealInterval`); until then they show as **pending** (inbox rows are
  insert-only too, so the pending window is still protected by T1/T2 blocking roles).

## 5. Anchors, day seals and Merkle proofs (⚑A18)

```csharp
public readonly record struct AuditAnchor(string Tenant, string RetentionClass, DateOnly Day, ulong Index, Hash256 Hash,
    long SignedAtTicks, string KeyId, ReadOnlyMemory<byte> Signature, AnchorKind Kind, Hash256? MerkleRoot);
public enum AnchorKind : byte { Periodic = 0, DaySeal = 1, Tombstone = 2 }
```

- **Periodic anchors** every **1 000 entries or 10 minutes** of activity per chain: ECDsa P-256 signature over the canonical anchor payload.
  They bound the damage window and make tail truncation (T4) visible.
- **Day seal** after the UTC day ends and the sequencer drained the day's arrivals (default: 00:10 UTC): the final index/hash **plus a Merkle
  root** over the day's entries (RFC 6962 hashing: leaf = `SHA256(0x00 ‖ entryBytes)`, node = `SHA256(0x01 ‖ left ‖ right)`).
- **Why both a chain and a Merkle tree:** the chain gives continuity and order for the operator's full verification; the Merkle root gives
  **inclusion proofs** (log₂ n hashes, ~20 for a million entries) so an export of *one entity's history* can prove its 12 entries belong to the
  sealed days **without revealing any other customer's entries** (privacy-preserving audit for a data subject or a single fiscal document).
- Anchors live in `audit.anchors` (insert-only) and in every export.

## 6. Verification

```csharp
public enum ChainVerificationStatus : byte
{
    Pending = 0,      // day not sealed yet (or entries waiting in the inbox)
    Verified = 1,     // all hashes, anchors, day seal and Merkle root check out
    Broken = 2,       // first mismatching index recorded
    Gap = 3,          // chain intact but a node gap is open for entries that should be in it
    Purged = 4,       // removed by retention; a valid signed tombstone exists
    Unverifiable = 5, // signing key unknown/revoked without a valid historical record
}

// (sketch) cold path, native union is fine here
public sealed record ChainVerification(string Tenant, string RetentionClass, DateOnly Day, ChainVerificationStatus Status,
    ulong Entries, ulong? FirstBadIndex, string? Detail, DateTimeOffset VerifiedAt);
```

`AuditVerifier` (YFex.Audit.Server; the CLI reuses the same code over an export package):

1. Recompute genesis from the previous day's final hash (or tombstone).
2. Recompute every `Hash(i)` from the stored canonical bytes; compare with `audit.seals`.
3. Check every anchor's signature (key valid at `SignedAt`) and that its `(index, hash)` matches.
4. Check the day seal's final index and Merkle root.
5. **Projection check:** decode each entry and compare with the projected columns/targets/field-change rows (07 §2), so a tampered
   *projection* (the thing UIs read) is caught even when the bytes are intact.
6. Check node chains of the day's device entries (continuity with the stored node heads).

Schedule (YFex.Jobs): yesterday's chains every day; a random 2 % sample of older days every week; full range on demand
(`AuditCommands.Verify`). Results go to `audit.verifications` (insert-only) and feed the badges in the UI (09 §5). `Broken` creates a
Critical `Audit.VerificationFailed` entry in the tenant's `security` class chain and an alert hook (logging, when present).

## 7. Gap detection

A gap is a missing node sequence range: `(nodeId, from, to, detectedAt, status)` in `audit.gaps`.

- Opened by ingest (§3) when a batch skips sequences, or by the daily node audit when a device's last shipped head is far behind its last
  seen activity (logging heartbeat or RPC connection) — "device online for 3 days, no audit shipped".
- Closed automatically when the missing entries arrive; otherwise an operator closes it with a reason (itself audited) after investigation
  (lost device, reinstalled app).
- Open gaps older than 24 h are Critical incidents and show on the tenant's verification status.

## 8. WORM export and archive

- After the day seal, the day's chains are exported to an **`IAuditArchiveTarget`** as `.yaudit` packages (format in 07 §8): canonical entries,
  seals, anchors, Merkle roots, public keys, signed manifest.
- Built-in targets: **file system** (read-only attributes + optional Windows/Linux immutability flags) and **generic HTTP PUT** (works with
  object stores configured for object lock through a pre-signed URL issued by the customer's infrastructure). An S3 SDK target needs package
  approval (`AWSSDK.S3`) and is not proposed now.
- The export job records `audit.exports` (insert-only); purging requires a successful export when the retention class says so (08 §5).

## 9. External witness (optional)

T6 (an operator who holds the signing key and rewrites history) is defeated only by a copy outside the operator's control:

- **Tenant witness:** the daily list of day-seal hashes per tenant is sent to the tenant (e-mail via the notification plug-ins, or a
  tenant-configured webhook). The tenant keeps them; any later rewrite cannot match.
- **RFC 3161 timestamping** of the daily root by a public TSA: needs `System.Security.Cryptography.Pkcs` (Microsoft, MIT) — **package approval
  required**, so it is a later option behind `IAuditWitness`.
- The witness interface is one method: `ValueTask PublishAsync(AuditAnchor daySeal, CancellationToken ct)`.

## 10. Key management

| Key | Algorithm | Where | Rotation | Compromise impact |
|---|---|---|---|---|
| Device key | ECDsa P-256 | device, platform-protected; public key in the device registry | on re-enrollment | device can fabricate its own entries (T9); revoke |
| Anchor signing key | ECDsa P-256 | server, wrapped by the YFex.Privacy root key provider (`IRootKeyProvider`), `IAuditSigningKeyProvider` seam (HSM/cloud KMS later, no package now) | yearly + on demand; old public keys kept forever in `audit.signing_keys` and every export | T6 without a witness |
| Subject / category keys (personal fields) | AES-256-GCM | YFex.Privacy `IKeyVault` | per Privacy policy | personal values readable; chains unaffected |

Anchor signing keys never encrypt anything, and encryption keys never sign, so shredding (08 §3) cannot affect verification.
