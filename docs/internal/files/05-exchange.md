# 05 — Exchange (`YFex.Files.Exchange`)

The layer partner integrations use. Domain-neutral: it moves, claims and tracks files; it never parses a business layout.

## 1. Definitions

```csharp
public sealed record InboundExchange(ExchangeName Name, FileStoreName Store, FilePath Inbox, FileNamePattern Match,
    StableFileRule Stable, ClaimRule Claim, Disposition AfterSuccess, Disposition AfterFailure, TimeSpan PollInterval,
    DataSize MaxFileSize, ZipRule? Unzip, TextEncoding? Text);

public sealed record OutboundExchange(ExchangeName Name, FileStoreName Store, FilePath Outbox, FileNameTemplate Naming,
    UploadRule Upload, DataSize MaxFileSize);
```

`ExchangeName` = app-declared value object (like `FileStoreName`). `FileNamePattern` = glob (`*`, `?`, `[a-z]`) compiled once into a
span matcher (0 B per match). `FileNameTemplate` = `{date:yyyyMMdd}`, `{seq:0000}`, `{guid}`, `{name}` formatted with
`Utf8.TryWrite` into a pooled buffer.

## 2. Inbound flow

```
 poll (Jobs, PollInterval; paused while NoNetwork)
   └─ List(Inbox, Match) ─► for each entry:
        ├─ ledger says done for (store, path, version|checksum)?  → skip
        ├─ Stable? (§3) no → remember observation, next poll
        ├─ Claim (§4) failed → someone else has it, skip
        ├─ Read → temp (local pipe/file) with MaxFileSize, checksum
        ├─ Unzip (§6) / decode text (§7) as declared
        ├─ handler.HandleAsync(file)  → Done | Retry(after) | Reject(reason)
        ├─ Disposition (archive / delete / move to error) — via FileTransfer.MoveAsync (atomic or MovedNonAtomic)
        └─ ledger: record outcome + checksum; audit files.received / files.rejected
```

Concurrency: one poller per exchange per cluster (claim/lease below); handler parallelism bounded (`MaxParallelFiles`, default 1 —
partners often expect order).

## 3. Stable-file detection (⚑F15)

| Rule | When to use | How |
|---|---|---|
| `Settle(time)` (default 30 s) | partner uploads straight to the final name | same size + version/modified across two observations ≥ `time` apart (observations in the ledger, survive restarts) |
| `Marker(".done")` | partner writes `x.zip` then `x.zip.done` | process `x.zip` only when the marker exists; marker follows the disposition |
| `TempRename(".tmp", ".part")` | partner uploads `x.tmp` then renames | ignore entries with those extensions |
| `AtomicStore` | S3/Blob | a listed object is complete; still checks size > 0 unless allowed |

## 4. Claim

| Store capability | Claim |
|---|---|
| `AtomicRename` | rename `inbox/x.zip` → `inbox/.processing/x.zip.<nodeId>`; success = ours. Crash recovery: entries in `.processing` older than the lease are returned |
| no rename, `ConditionalWrite` (S3/Blob) | lease object `.<name>.lease` written with `CreateNew` (+ TTL inside); delete after disposition |
| neither | ledger lease row (`.Exchange.Data`, SQL row lock / CAS) — single-writer by database |

Claim rule is chosen from capabilities automatically; the app may force one (`ClaimRule.LedgerOnly` when the partner forbids
writes in their outbox).

## 5. Ledger (⚑F14)

`IExchangeLedger`: key = `Hash256` over canonical bytes of (exchange, store, path, version or content SHA-256). Records:
observations (stable check), outcome, attempts, checksum, timestamps (trusted clock), disposition result. Implementations:
in-memory (tests), `KeyValueLedger` over Persistence `IKeyValueStore` (single machine), SQL in `.Exchange.Data` (YFex.Data;
generated mapping, no reflection). Retention: completed rows kept `Retention` (default 90 days) for dedup and support.

**Duplicate protection:** the same content under a new name (partner re-sends) is detected by content checksum when
`DeduplicateByContent()` is on (default for inbound) → outcome `Duplicate`, disposition `AfterSuccess`, handler not called.

## 6. Zip (⚑F16)

- .NET 10 async `ZipArchive` APIs over the downloaded temp file (zip needs seeking: download first, never stream-unzip from the
  network).
- `ZipLimits`: max entries 10 000, max total expanded 1 GiB, max ratio 100× per entry (checked while inflating, not trusting the
  header), max entry size = `MaxFileSize`; entry names parsed as `FilePath` relative to a virtual root → absolute/`..`/drive names
  rejected (zip slip); nested zips not expanded unless `ExpandNested(depth)`; encrypted entries rejected (ZipCrypto) unless the app
  handles them.
- Outbound: `ZipBuilder` streams entries into the pipe with `CompressionLevel` chosen per partner; deterministic timestamps
  optional (stable checksums for re-sends).

## 7. Text (⚑F19)

The handler can ask for text only when the exchange declares `TextEncoding`: `Utf8`, `Utf8WithBom`, `Latin1`, `Windows1252`
(registers `CodePagesEncodingProvider` only in that case), `Ascii`. Decoding uses `Decoder` over pipe segments (no full-string load
unless `ReadAllTextAsync` is called); invalid bytes → `Reject` with position, not silent replacement (`DecoderFallback.ExceptionFallback`
mapped to a result). Line endings preserved; `ReadLinesAsync` yields `ReadOnlyMemory<char>` lines from a pooled buffer.

## 8. Outbound flow

`SendAsync(exchange, source)` → name from template → `FileTransfer.WriteAtomicallyAsync` (tmp + rename, or direct object put) →
verify (size/checksum via `GetInfoAsync` when the backend reports it) → ledger + audit `files.sent`. Idempotent by an optional
`SendKey` (app id of the business document): a re-send with the same key and checksum is a no-op returning the first result.
Encryption/signing steps (OpenPGP, ⚑F22) plug in as `IOutboundTransform` when a Cryptography implementation exists.

## 9. Integration points

| With | How |
|---|---|
| YFex.Jobs | each exchange is a scheduled job (`IJob`); manual "poll now" command for support |
| YFex.Resilience | per-exchange policy: retry transient failures with backoff, breaker per store, no retries while `NoNetwork` |
| YFex.Audit | events `files.received`, `files.rejected`, `files.duplicate`, `files.sent`, `files.store.*` (trust/plaintext) with store, exchange, masked path, checksum, size |
| YFex.Logging | masked paths (⚑F13), meter `YFex.Files.Exchange`: files/bytes in/out, poll duration, failures by kind, ledger size |
| YFex.Authorization | manual commands (`files.exchange.poll`, `files.exchange.reprocess`) are app CQRS commands with permissions — not inside the library |
| YFex.Hosting | the poller runs as a hosted service in the machine's single host |
| Offline | exchanges normally run at central; a site may run one (local share) — no special offline mode in v1 |
