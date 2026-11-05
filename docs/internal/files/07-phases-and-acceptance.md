# 07 — Phases, acceptance, implementation log

**No code before the user confirms ⚑F2–F22** (README §5.2). Each phase ships on its own and ends with: Release build with
`--artifacts-path C:/Temp/files-artifacts` and `dotnet build-server shutdown`, all touched test projects green (counts recorded
below), AotSmoke publish with **0 IL/AOT warnings** for every backend touched, benchmark gates of 06 §3 met, Linux run through
`linuxKit`, the memory file `yfex-files.md` and the infrastructure map updated.

## Repo layout (template: YFex.Networking)

```
YFex.Files/
  Directory.Build.props        net11.0 (.Public also net10.0, ⚑F20), LangVersion preview, Nullable, ImplicitUsings,
                               TreatWarningsAsErrors, IsAotCompatible on src/*, YFexRoot
  files.slnx                   /src/ /tests/ /benchmarks/
  AGENTS.md  README.md
  src/YFex.Files.Public/   src/YFex.Files/   src/YFex.Files.Testing/
  src/YFex.Files.Sftp/     src/YFex.Files.Ftp/   src/YFex.Files.S3/   src/YFex.Files.AzureBlob/
  src/YFex.Files.Exchange/ src/YFex.Files.Exchange.Data/
  tests/YFex.Files.Tests/  tests/YFex.Files.<Backend>.IntegrationTests/  tests/YFex.Files.Exchange.Tests/
  tests/YFex.Files.AotSmoke/ (one app per backend)   benchmarks/YFex.Files.Benchmarks/
```

Integration tests are `[Trait("Category", "Integration")]` and use containers (OpenSSH, vsftpd/Pure-FTPd with TLS, MinIO, Azurite)
started through `linuxKit`; unit tests use `InMemoryFileStore`, loopback and golden bytes.

## Phase F0 — contracts, file system, testing kit

- `.Public`: `FilePath`, `FileName`, `FileStoreName`, `FileVersion`, `FileChecksum`, `FileEntry`, `FileStoreCapabilities`,
  results (02 §5), `ReadOptions`/`WriteOptions`, interfaces (02 §7), `IFileStoreBackend<TSelf,TOptions>`.
- Core `DataSize` (⚑F21) + Cryptography C-F1 (incremental SHA-256) — requests sent to their owners first.
- `YFex.Files`: `AddYFexFiles`, `FileStoreRegistry` (lazy open, pool), `FileSystemFileStore`, `FileTransfer` (move/copy/atomic write),
  streaming checksum, meter, extension members.
- `.Testing`: `InMemoryFileStore`, `ChaosFileStore`, `FileStoreConformance<TStore>`.
- **Acceptance:** `FilePath` tests (every rule of 02 §2, NFC, 1 024/255-byte limits, segment-aware prefix, JSON, mask); conformance
  green for FileSystem (Windows + Linux) and InMemory (every capability combination); cross-store copy verifies checksums;
  cancellation leaves no visible partial file; benchmark gates for parse/match/stream; AotSmoke 0 warnings.

## Phase F1 — SFTP

- `.Sftp` over SSH.NET (⚑F3): auth methods, pinned host keys (C-F2 home decided), algorithm policy, pool, resume, atomic rename.
- **Acceptance:** conformance against OpenSSH container; wrong host key → `TrustRejected` + audit event, no bytes sent after KEX;
  wrong password → `AuthenticationFailed` (after one secret re-read); 1 GiB transfer with steady memory (≤ 2 MiB working set growth);
  AotSmoke (connect, list, range read, write, rename, host-key callback) 0 warnings.

## Phase F2 — FTP/FTPS

- `.Ftp` over FluentFTP (⚑F4): explicit/implicit TLS, `FtpsTrust`, MLSD + LIST fallback, passive range, tmp + rename, resume.
- **Acceptance:** conformance against vsftpd (TLS) and Pure-FTPd; plaintext refused without opt-in; certificate mismatch →
  `TrustRejected`; LIST-only server yields weak versions and null `Modified` where unknown; AotSmoke 0 warnings.

## Phase F3 — S3 (+ compatible) and Azure Blob

- `.S3` (AWSSDK v4) with flavors Aws/MinIO/R2/Generic; `.AzureBlob` (+ HNS detection).
- **Acceptance:** conformance against MinIO and Azurite (+ a recorded run against real AWS/Azure accounts when credentials are
  provided — listed as pending otherwise); multipart/blocks with bounded memory; conditional writes; signed URLs expire;
  `MovedNonAtomic` reported; SDK retries off and Resilience in charge; AotSmoke 0 warnings for both.

## Phase F4 — Exchange

- `.Exchange` (05): definitions, poller job, stable rules, claim strategies, in-memory + key-value ledger, zip, text, outbound
  atomic send, handler contract, Resilience/Audit/metrics wiring.
- **Acceptance:** scenario tests on `InMemoryFileStore` + `ChaosFileStore` with fake time: a file uploaded slowly is not taken before
  it settles; two pollers never process the same file (all three claim strategies); restart mid-handler resumes without double
  processing; re-sent identical content → `Duplicate`; zip bomb / zip slip samples rejected; Latin-1 decoding; invalid bytes →
  `Reject` with position; outbound re-send with same `SendKey` is a no-op; AotSmoke 0 warnings.

## Phase F5 — SQL ledger, hosting, identity add-ons

- `.Exchange.Data` on YFex.Data (ledger + lease tables, generated mapping); poller hosted in YFex.Hosting; `*.Identity` packages
  only if an app needs them (⚑F18, AOT re-probe of Azure.Identity / AWS credential chains).
- **Acceptance:** `.Data` tests on the YFex.Data test database; two hosts on one database never double-process; AotSmoke 0 warnings.

## Phase F6 — servers (later, ⚑F17)

Separate design addendum before any code.

## Implementation log

*(empty — design proposed 2026-10-29)*
