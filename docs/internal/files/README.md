# YFex.Files — remote file stores (FTP/FTPS, SFTP, S3, Azure Blob, folders) and partner file exchange — Plan

> **Status:** PROPOSED 2026-10-29. No code. ⚑F1 (name) confirmed by the user 2026-10-29; ⚑F2–F22 wait for the user (§5).
> **Repo:** `C:\Dev\YFex\YFex.Files` (sibling, template YFex.Networking / YFex.Time). **Core types:** none in v1 (⚑F2) except the
> proposed `DataSize` (⚑F21).
> **Origin:** the user asked where an FTP client/server belongs (2026-10-29). Answer: not Authentication, Authorization or YFex.Web —
> a new library with one store contract that FTP, SFTP and cloud buckets all implement, plus a partner-exchange layer on top.

## Files

| File | What it covers |
|---|---|
| README (this file) | why, scope, shape, packages, decisions, phase summary |
| [01 — Inventory](01-current-state-and-inventory.md) | what exists today (nothing for remote files), what we reuse (file:line), the AOT probe of the candidate libraries |
| [02 — Model and core types](02-model-and-core-types.md) | `FilePath`, `FileEntry`, `FileVersion`, `FileChecksum`, `FileStoreCapabilities`, results, `IFileStore`, static-abstract backend registration |
| [03 — API and examples](03-api-and-examples.md) | **dev-side code first**: register stores, list/read/write, move, conditional writes, signed URLs, testing |
| [04 — Backends](04-backends.md) | `.FileSystem`, `.Ftp` (FluentFTP), `.Sftp` (SSH.NET), `.S3` (AWSSDK v4), `.AzureBlob`, trust and credentials per backend, the lean-client escape hatch |
| [05 — Exchange](05-exchange.md) | `YFex.Files.Exchange`: inbox/outbox, stable-file detection, claim, ledger/deduplication, zip, text encodings, retries, audit |
| [06 — Security, privacy, performance, AOT](06-security-privacy-performance-aot.md) | threats, path/zip safety, logging masks, hot paths and budgets, .NET 10/11 features used, AOT gates |
| [07 — Phases, acceptance, log](07-phases-and-acceptance.md) | phases F0–F6 with acceptance, test/benchmark/AOT gates, implementation log |

## 1. Why

Partner integrations exchange files — `.txt` layouts, `.csv`, `.zip`, `.xml`, PDFs — over whatever the partner runs: an FTP/FTPS
server, an SFTP server, an S3 bucket (or S3-compatible: MinIO, Cloudflare R2, Wasabi), an Azure Blob container, or a shared folder.
Each app would otherwise pick a library per partner, copy the "upload as `.tmp` then rename" trick, forget host-key pinning, log
file names that contain personal data, and re-download the same file after a restart.

Two layers solve it:

1. **One store contract** (`IFileStore`) with a small common core (list, info, read, write, delete) and **declared capabilities**
   (atomic rename, conditional write, range read, signed URLs, server checksums…). Every backend implements it; callers do not know
   whether a partner is SFTP or S3.
2. **Exchange** (`YFex.Files.Exchange`): what an integration really needs on top — poll an inbox, wait until a file is complete,
   claim it once, hand it to the app, archive or quarantine it, never process it twice; send outbound files atomically.

A future **server** (expose an `IFileStore` over FTP/SFTP, e.g. a partner-facing SFTP whose files live in S3) reuses the same
contract and is deliberately not in v1 (⚑F17).

## 2. Scope

**In:** the store contract and value types; backends for local/UNC folders, FTP/FTPS, SFTP, S3 and S3-compatible, Azure Blob; an
in-memory store with fault injection; a **conformance test suite** every backend must pass; credentials through YFex.Cryptography
`ISecretStore`; host-key and certificate trust through YFex.Cryptography; streaming with `System.IO.Pipelines`; checksums while
streaming; the exchange layer (polling, stable-file detection, claim, ledger, zip packing/unpacking with limits, text decoding by
declared encoding, retry through YFex.Resilience, scheduling through YFex.Jobs, audit events, metrics); AOT smoke and benchmarks.

**Out:**
- **Local app-state storage** → `YFex.Persistence` (`IKeyValueStore`, `IDurableQueue`). Different job: app state on this machine.
- **HTTP APIs** → YFex.Web. A partner that exposes REST is not a file store.
- **File servers** (FTP/SFTP server) → later packages (⚑F17). **Mounting** drives, sync clients (Dropbox-style), file watching of
  remote stores in real time (polling only; cloud event notifications are an app integration).
- **Business file layouts** (bank remittance, fiscal, EDI) → product libraries; Exchange gives bytes or decoded text, never parses
  a layout.
- **Payload encryption formats** (PGP/OpenPGP files some banks require) → a YFex.Cryptography request if needed (⚑F22), not here.
- Changing remote permissions/ACLs, bucket policies, lifecycle rules: provisioning is the operator's job.

## 3. Shape

```
 YFex.Files.Public (contracts, AOT, no I/O)                  backends (one package each)                     consumers
 ┌────────────────────────────────────────────┐   ┌───────────────────────────────────────────┐   ┌──────────────────────┐
 │ FilePath  FileName  FileStoreName           │   │ YFex.Files            FileSystemFileStore │   │ app integrations     │
 │ FileEntry FileVersion FileChecksum ([Union])│◄──│   (+ registry, Move/Copy helpers, metrics)│   │ YFex.Files.Exchange  │
 │ FileStoreCapabilities (flags)               │   │ YFex.Files.Ftp        FluentFTP adapter   │   │ (later) .Server.*    │
 │ IFileStore  IRenameFiles ICopyFiles         │   │ YFex.Files.Sftp       SSH.NET adapter     │   └──────────────────────┘
 │ ISignFileUrls  IFileStoreBackend<TSelf,TOpt>│   │ YFex.Files.S3         AWSSDK.S3 v4 adapter│
 │ results: native unions (cold) ; FileStore-  │   │ YFex.Files.AzureBlob  Azure.Storage.Blobs │   ┌──────────────────────┐
 │ Failure ; ReadOptions/WriteOptions structs  │   │ YFex.Files.Testing    InMemory + Chaos +  │   │ YFex.Cryptography    │
 └────────────────────────────────────────────┘   │                       conformance suite    │──►│ ISecretStore, trust  │
                                                  └───────────────────────────────────────────┘   │ YFex.Resilience/Jobs │
                                                                                                   │ YFex.Audit/Logging   │
                                                                                                   └──────────────────────┘
```

## 4. Packages

| Package | TFM | Contents | Depends on |
|---|---|---|---|
| `YFex.Files.Public` | net11.0 (+ net10.0, ⚑F20) | value types, results, interfaces, `FileStoreCapabilities` | YFex core |
| `YFex.Files` | net11.0 | `FileStoreRegistry` + `AddYFexFiles`, `FileSystemFileStore`, `FileTransfer` helpers (move/copy across stores with declared atomicity), checksum streaming, meter `YFex.Files` | `.Public`, YFex.Cryptography (secrets, hashing), M.E.DI/Options abstractions |
| `YFex.Files.Ftp` | net11.0 | FTP + FTPS (explicit/implicit) over **FluentFTP** (⚑F4) | `YFex.Files`, FluentFTP |
| `YFex.Files.Sftp` | net11.0 | SFTP over **SSH.NET** (⚑F3), host-key pinning | `YFex.Files`, SSH.NET |
| `YFex.Files.S3` | net11.0 | S3 + S3-compatible over **AWSSDK.S3 v4** (⚑F5) | `YFex.Files`, AWSSDK.S3 |
| `YFex.Files.AzureBlob` | net11.0 | Azure Blob over **Azure.Storage.Blobs** (⚑F5) | `YFex.Files`, Azure.Storage.Blobs |
| `YFex.Files.Exchange` | net11.0 | inbox/outbox, claim, ledger, zip, encodings ([05](05-exchange.md)) | `YFex.Files`, YFex.Resilience, YFex.Jobs.Abstractions, YFex.Audit.Public |
| `YFex.Files.Exchange.Data` | net11.0 | SQL ledger + claim leases on YFex.Data (⚑F14) | `.Exchange`, YFex.Data |
| `YFex.Files.Testing` | net11.0 | `InMemoryFileStore` (capability toggles), `ChaosFileStore`, `FileStoreConformance<TStore>` suite, fake clock hooks | `YFex.Files` |
| tests, AotSmoke, Benchmarks | | `YFex.Files.Tests`, per-backend `*.IntegrationTests` (containers via `linuxKit`), `YFex.Files.AotSmoke` (one publish per backend, 0 warnings), BenchmarkDotNet | |

Cloud identity chains (AWS SSO/instance roles, Azure managed identity) are **opt-in add-on packages** (⚑F18):
`YFex.Files.S3.Identity`, `YFex.Files.AzureBlob.Identity`.

## 5. Decisions (⚑F)

### 5.1 Confirmed

| # | Decision |
|---|---|
| F1 | (user, 2026-10-29) Repo/library name **`YFex.Files`**. Not in Authentication, Authorization or YFex.Web: those are consumers (a future server uses Authentication to verify users and Authorization for path permissions through adapters). |

### 5.2 Proposed (wait for the user)

| # | Question | Recommendation | Alternatives | Where |
|---|---|---|---|---|
| **F2** | Home of the contract | **`YFex.Files.Public`** in the repo; move a type to core only when a second library must name it | core `YFex/src/YFex/Files` now | 02 §1 |
| **F3** | SFTP implementation | **wrap SSH.NET** (maintained, 2026.0.0; AOT probe 0 warnings) behind our contract; own host-key check, secrets, errors | own SSH stack (key exchange, ciphers, channels: a security project) | 04 §4 |
| **F4** | FTP/FTPS implementation | **wrap FluentFTP** (maintained, 55.1.0; probe 0 warnings) | own client (~1.5 k lines; simple protocol, but many server dialects) — keep as a later option behind the same contract | 04 §3 |
| **F5** | S3 / Azure | **wrap the official SDKs** (AWSSDK.S3 v4, Azure.Storage.Blobs 12.x); a lean signer client (SigV4 / SharedKey) only if a measured problem appears (size, AOT path, pipeline) | own REST clients from day one | 04 §5–§7 |
| **F6** | Result style | cold I/O (`ReadAsync`, `WriteAsync`, `DeleteAsync`, `GetInfoAsync`) returns **native `union`** with record cases; values on hot paths (`FileChecksum`, `FilePath` parse) are **`[Union]` tagged structs / `TryX`**, 0 B; listing is `IAsyncEnumerable<FileEntry>` (struct) | exceptions; `[Union]` everywhere | 02 §5 |
| **F7** | Streaming primitive | **`PipeReader`/`PipeWriter` first**; `Stream` overloads as C# 14 extension members over the pipe API | `Stream` only | 02 §6 |
| **F8** | Optional operations | **capability flags + small segregated interfaces** (`IRenameFiles`, `ICopyFiles`, `ISignFileUrls`, `IAppendFiles`); never `NotSupportedException` | one fat interface that throws | 02 §7 |
| **F9** | Move when there is no atomic rename (S3, Blob) | `FileTransfer.MoveAsync` does copy + verify + delete and returns **`MovedNonAtomic`**, so callers see the semantics | refuse the move; silently copy+delete | 02 §8 |
| **F10** | Backend registration | **static abstract interface** `IFileStoreBackend<TSelf, TOptions>` (`Kind`, `Capabilities`, `Open`) + named stores; no reflection, no `Activator` | keyed DI with factories per backend | 02 §9 |
| **F11** | Credentials | **only secret names** resolved through `ISecretStore` at connect time, bytes cleared after use; inline passwords only with `AllowInlineSecretForDevelopment()` (startup warning, refused in Release) | connection strings in options | 04 §2 |
| **F12** | Trust | SFTP: **pinned host key required** (`SshHostKeyFingerprint`, SHA-256); trust-on-first-use only as an explicit, audited opt-in. FTPS: system trust, `CertificateTrust` roots or a pinned certificate. **Plain FTP** only with `AllowPlaintext()` (warning + audit event at start) | accept-any by default (the libraries' default) | 04 §2 |
| **F13** | Paths in logs | `FilePath` is `ILogValue` with mask **directory + `*` + extension** (`/inbox/*.zip`); full path only in audit records (which are protected) | log full paths | 06 §2 |
| **F14** | Exchange ledger store | `IExchangeLedger` with in-memory (tests/single run) and **YFex.Data SQL** (`.Exchange.Data`); Persistence `IKeyValueStore` adapter for single-machine installs | Persistence only | 05 §5 |
| **F15** | Stable-file default | **size + version/modified unchanged across two polls ≥ `SettleTime` (default 30 s)**; per partner: marker file (`x.done`), rename convention (`.tmp` → final), or "atomic store" (S3/Blob: a visible object is complete) | marker files only | 05 §3 |
| **F16** | Zip limits | defaults: ≤ 10 000 entries, ≤ 1 GiB expanded total, ≤ 100× ratio per entry, no absolute/`..` names (zip slip), no nested zip expansion unless enabled | no limits (zip bombs) | 05 §6 |
| **F17** | Server | **not in v1.** Later `YFex.Files.Server.Sftp` / `.Server.Ftp` expose an `IFileStore`; users via an Authentication adapter, path permissions via Authorization; runs in YFex.Hosting | build a server now | 04 §9 |
| **F18** | Cloud identity chains | **opt-in `*.Identity` packages**; the base backends take keys/SAS from `ISecretStore` only | SDK default chain in the base package (pulls more code, AOT unverified) | 04 §5 |
| **F19** | Text files | the library moves **bytes**; Exchange decodes text only with a partner-declared `TextEncoding` (UTF-8, Latin-1/Windows-1252 via `CodePagesEncodingProvider` opt-in), BOM handling explicit | auto-detect encodings | 05 §7 |
| **F20** | Target frameworks | `.Public` multi-targets **net11.0 + net10.0** (LTS apps can read the contract); engines net11.0 only | net11.0 only | 06 §4 |
| **F21** | Size value type | new core **`DataSize`** (readonly struct over `long` bytes, `IUtf8SpanFormattable` "12.4 MB", parse "10MiB") — reusable by Networking, Logging retention, Offline bundles | `long` bytes in our API | 02 §2 |
| **F22** | Encrypted payloads (OpenPGP) | **out**; if a partner needs it, a YFex.Cryptography request (format + keys), applied by Exchange as a pipeline step | implement OpenPGP here | 05 §8 |

## 6. Phases (details and acceptance in [07](07-phases-and-acceptance.md))

| Phase | Content |
|---|---|
| F0 | Repo scaffold; `.Public` types and interfaces; `YFex.Files` registry + `FileSystemFileStore`; `.Testing` (in-memory, chaos, **conformance suite**); AotSmoke; benchmarks of `FilePath`, listing, streaming checksum; core `DataSize` (if ⚑F21) |
| F1 | `.Sftp` (SSH.NET): password/key auth from `ISecretStore`, pinned host key, resume, conformance green against an OpenSSH container |
| F2 | `.Ftp` (FluentFTP): FTPS explicit/implicit, certificate trust, MLSD + LIST fallback, passive ports, conformance against vsftpd/Pure-FTPd containers |
| F3 | `.S3` (+ MinIO/R2 via service URL, path style) and `.AzureBlob` (Azurite): multipart, conditional writes, signed URLs, conformance |
| F4 | `.Exchange`: inbox/outbox, stable detection, claim, ledger (in-memory), zip, encodings, Resilience/Jobs/Audit wiring |
| F5 | `.Exchange.Data` (SQL ledger + leases); Hosting adoption (poller as a hosted service); `*.Identity` packages if needed |
| F6 | (later, ⚑F17) servers |
