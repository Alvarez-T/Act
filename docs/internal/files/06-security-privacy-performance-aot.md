# 06 — Security, privacy, performance, AOT

## 1. Threats

| Threat | Mitigation | Where |
|---|---|---|
| man-in-the-middle on SFTP/FTPS | pinned host key / certificate trust required; TOFU only opt-in + audited; modern algorithms by default | 04 §3–§4, ⚑F12 |
| plaintext FTP leaks credentials | refused unless `AllowPlaintext()`; audit event at start | 04 §3 |
| credentials in config, logs, dumps | secret names only; bytes cleared; `Detail` of failures masked; options `ToString` redacted | 04 §2, ⚑F11 |
| path traversal from partner names | every name parsed as `FilePath`/`FileName`; `..`, absolute, drive, control chars rejected | 02 §2 |
| zip bombs / zip slip | `ZipLimits`, ratio checked while inflating, names validated | 05 §6, ⚑F16 |
| oversized files fill the disk | `MaxSize` / `MaxFileSize` enforced while streaming | 02 §6 |
| replay / double processing | ledger by version/checksum, content dedup, claims | 05 §4–§5 |
| partial uploads read as complete | stable rules; our uploads atomic (tmp + rename or object put) | 05 §3, 02 §8 |
| SSRF via configured endpoints | endpoints from configuration only; S3/Blob URLs never built from file content | 04 §2 |
| malicious content | optional `IInboundFileInspector` hook (antivirus, type sniffing) before the handler; library does not interpret content | 05 §2 |
| signed URL leakage | short lifetime (default 15 min, max 7 days), method-limited, audited | 04 §5 |

## 2. Privacy (⚑F13)

File names often carry personal data (tax ids, names in `boleto_<cpf>.pdf`). `FilePath` implements `ILogValue<FilePath>`: logs
write `"/inbox/*.pdf"` (directory + masked name + extension). Audit records keep the full path (protected store). Contents are
never logged. Store/exchange names are app identifiers, logged plainly. The types are classified on the type (no per-member
attributes), following the Privacy rules.

## 3. Performance — hot paths and budgets

| Path | Technique | Budget (benchmark gate) |
|---|---|---|
| `FilePath.TryParse` (string, canonical) | `SearchValues<char>` forbidden-set scan (vectorized), ASCII fast path, NFC check `IsNormalized` only when non-ASCII | 0 B, < 40 ns for 64 chars |
| `FilePath.TryParse` (span) | same + one string allocation | 1 alloc |
| `Name`/`Extension`/`Parent`/segment enumeration | offsets in the struct, `ref struct` enumerator | 0 B |
| glob match over listings | compiled pattern → span matcher, no regex | 0 B, < 30 ns per name |
| listing (FTP MLSD/LIST, SFTP, S3) | one path string per entry; `FileEntry` struct; `FileVersion` inline ≤ 64 B (`InlineArray`) | ≤ 1 alloc per entry beyond the library's own |
| streaming copy | `System.IO.Pipelines`, `MemoryPool<byte>.Shared`, 64 KiB segments, no `byte[]` per chunk | 0 B per chunk steady state |
| SHA-256 while streaming | incremental hash over `ReadOnlySequence<byte>` segments (⚑C-F1), hardware SHA where available | ≥ 1 GB/s on SHA-NI hardware; no extra pass |
| ledger key | `Hash256` over canonical bytes written to a stack buffer (`stackalloc` ≤ 1 KiB, pooled above) | 0 B |
| results | native `union` with record cases only on cold I/O; `[Union]` tagged struct for `FileChecksum` (no boxing) | — |

`unsafe` / explicit `Vector128/256` code is used **only** where a benchmark beats `SearchValues`/`IndexOfAny` (candidate: LIST line
tokenizing for very large legacy listings); every such site has a safe twin and a test comparing both.

Measured with BenchmarkDotNet (`MemoryDiagnoser`, `InProcessEmitToolchain` on net11) in `YFex.Files.Benchmarks`; the gates above are
asserted in tests with `GC.GetAllocatedBytesForCurrentThread()` where practical.

## 4. .NET 10 / 11 and C# 14/15 features used

| Feature | Use |
|---|---|
| C# 15 `union` (net11 preview) | cold result types (`ReadResult`, `WriteResult`, …) |
| `[Union]` tagged struct (project pattern) | `FileChecksum` |
| static abstract interface members | `IFileStoreBackend<TSelf,TOptions>`, `IFileStoreOptions`, value types' `ISpanParsable`/`IUtf8SpanParsable` |
| C# 14 extension members | `Stream` overloads, `MoveAsync`/`WriteAtomicallyAsync` on `IFileStore` |
| C# 14 `field` keyword | validated option properties without backing-field noise |
| `params ReadOnlySpan<T>` | `FileStoreCapabilities` helpers, pattern lists |
| `SearchValues<char>` / `SearchValues<string>` | path validation, marker/extension checks |
| `InlineArray` | `FileVersion` inline token |
| `System.IO.Pipelines` | all transfers |
| async `ZipArchive` (.NET 10) | Exchange zip |
| `Utf8.TryWrite`, `CompositeFormat` | name templates, log text |
| `TimeProvider` / `ITrustedClock` | settle times, leases, signed URL expiry; fake time in tests |
| `Lock` type | pool/registry synchronisation |
| configuration binding source generator, `System.Text.Json` source generation | options and ledger JSON without reflection |

## 5. AOT and trimming

- `IsAotCompatible` on every src project; `YFex.Files.AotSmoke` = **one published app per backend** (FileSystem, Ftp, Sftp, S3,
  AzureBlob, Exchange) exercising connect, list, range read, write (multipart for S3/Blob), delete, rename, trust callbacks →
  **0 IL/AOT warnings** is a phase gate.
- No reflection, no `Activator`, no `Type.GetType` in our code; DI registration is generic (§9 of 02).
- Third-party warnings that appear later are not suppressed silently: a warning on a needed path reopens ⚑F3–F5 (lean client or
  another library).
- `.Public` multi-targets net10.0 (⚑F20): `union` results need an alternative there — net10 consumers get the same types compiled
  as `[Union]`-style closed hierarchies (the implementation session confirms how the preview `union` lowers and picks one shape for
  both TFMs, or drops net10 if it cannot be equal).

## 6. Platforms

Core and backends are OS-neutral (BCL sockets/TLS/file APIs, managed libraries). `FileSystemFileStore` handles Windows UNC/long
paths and case-insensitivity flags, Linux/macOS permissions (files created `0600`/`0640` per option). Integration tests run on
Windows and in Linux containers through `linuxKit` (OpenSSH, vsftpd/Pure-FTPd, MinIO, Azurite); macOS runs listed as pending until
a Mac is available (cross-platform rule).
