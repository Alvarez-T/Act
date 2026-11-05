# 02 — Model and core types

Everything here lives in `YFex.Files.Public` (⚑F2) unless marked core. Signatures are the **design**; the implementation session
confirms them with `dry_run_compile` and records deviations in the 07 log.

## 1. Vocabulary

| Term | Meaning |
|---|---|
| **store** | one configured location: a partner's SFTP home folder, a bucket + prefix, a container, a local folder. Named by `FileStoreName` |
| **backend** | the technology implementing a store (`FileSystem`, `Ftp`, `Sftp`, `S3`, `AzureBlob`, `InMemory`) |
| **path** | a store-relative, `/`-separated, validated `FilePath`. Never an OS path, never a URI |
| **entry** | what a listing or info returns: path, kind, size, modified, version, checksum |
| **capability** | an optional behaviour a backend declares (`FileStoreCapabilities`) |

## 2. Value types

All are `readonly struct`, implement `IEquatable<T>`, `ISpanParsable<T>`, `IUtf8SpanParsable<T>`, `ISpanFormattable`,
`IUtf8SpanFormattable` (static abstract members — generic code parses any of them without reflection), have a nested
`JsonConverter` and **no implicit conversion from `string`** (parse is partial → explicit, `Length` rule).

### `FilePath`

```csharp
public readonly struct FilePath : IEquatable<FilePath>, IComparable<FilePath>, ISpanParsable<FilePath>, IUtf8SpanParsable<FilePath>,
    IUtf8SpanFormattable, ILogValue<FilePath>, ICanonicalValue
{
    public static FilePath Root { get; }                       // "/"
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out FilePath result);
    public static FilePath operator /(FilePath parent, FileName child);   // combine: no string concat at call sites

    public ReadOnlySpan<char> Value { get; }                   // "/inbox/2026/orders-01.zip"
    public FileName Name { get; }                              // "orders-01.zip" (span slice, 0 B)
    public ReadOnlySpan<char> Extension { get; }               // ".zip"
    public FilePath Parent { get; }                            // "/inbox/2026"
    public bool IsRoot { get; }
    public bool StartsWith(FilePath prefix);                   // segment-aware ("/in" is not a prefix of "/inbox")
    public SegmentEnumerator EnumerateSegments();              // ref struct, 0 B
}
```

Rules (one validator, `SearchValues<char>` for the forbidden set, vectorized):
- absolute, `/`-separated; a single `/` between segments; no trailing `/` except root;
- segments never `.` or `..`, never empty, no control chars (U+0000–U+001F, U+007F), no `\`, `:`, `*`, `?`, `"`, `<`, `>`, `|`
  (portable across Windows shares, FTP servers, S3 keys);
- ≤ 1 024 UTF-8 bytes total (the S3 key limit), ≤ 255 UTF-8 bytes per segment;
- Unicode normalized to **NFC** on parse (macOS/Windows/S3 disagree otherwise); comparison **ordinal**, case-sensitive (S3, SFTP and
  FTP are case-sensitive; the file-system backend reports `CaseInsensitive` as a capability and the conformance suite checks it).

Storage: one `string` (the canonical form). Parsing an already-canonical `string` returns a `FilePath` over **the same instance**
(0 B); parsing a span allocates the string once. `Name`, `Extension`, `Parent` are offsets into it (an `int` pair inside the
struct), so walking a listing does not allocate per property.

### `FileName`

A single validated segment (same rules, no `/`). `FilePath / FileName` combines. `FileName.WithExtension(".tmp")` for the upload
convention.

### `FileStoreName`

App-declared name of a configured store (`"partner-acme"`), lower-case ASCII, `[a-z0-9-]`, ≤ 64. Like `PolicyName`: an open set
declared by the app, never a framework enum.

### `FileVersion`

Opaque change token: S3/Blob **ETag** or version id, SFTP/FTP/file system **modified time + size** (synthesized, flagged
`IsWeak`). Equality is ordinal on the token bytes; stored inline up to 64 bytes (`InlineArray`), longer tokens on the heap — so a
listing entry with a typical ETag does not allocate for the version.

```csharp
public readonly struct FileVersion : IEquatable<FileVersion>, IUtf8SpanFormattable
{
    public static FileVersion None { get; }
    public bool IsWeak { get; }                                // synthesized from time + size: not safe for conditional writes
    public static FileVersion FromToken(ReadOnlySpan<byte> utf8);
    public static FileVersion FromTimestamp(DateTimeOffset modified, long size);
}
```

### `FileChecksum` — `[Union]` tagged struct (hot: one per entry)

```csharp
[Union]
public readonly struct FileChecksum : IUnion, IEquatable<FileChecksum>
{   // cases: None | Sha256(Hash256) | Md5(16 bytes) | Crc32C(uint) | Crc64Nvme(ulong) — backends report what they have
    public FileChecksumKind Kind { get; }
    public bool TryGetSha256(out Hash256 value);
    public bool TryGetCrc32C(out uint value);
    ...
}
```

32 bytes inline + tag, no boxing, typed `TryGet…` (never the boxing `Value` on hot paths, infrastructure-map rule).

### `DataSize` (core, ⚑F21)

`readonly struct DataSize(long bytes)`: `FromKibibytes`, `FromMebibytes`, arithmetic and comparison operators via
`IAdditionOperators<…>`/`IComparisonOperators<…>` (static abstract), `IUtf8SpanFormattable` ("1.5 MiB"), parse "10MiB"/"10 MB".
Used for `FileEntry.Size`, limits (`MaxFileSize`, zip limits) and metrics. If ⚑F21 is refused, `long Bytes` everywhere.

## 3. `FileEntry`

```csharp
public readonly record struct FileEntry(
    FilePath Path, FileEntryKind Kind,              // File | Directory (Directory only on backends with real folders)
    DataSize Size, DateTimeOffset? Modified,        // null when the server does not report it (old FTP LIST)
    FileVersion Version, FileChecksum Checksum);
```

A struct (≈ 120 bytes) so `IAsyncEnumerable<FileEntry>` yields without per-entry object allocation beyond the path string.

## 4. Capabilities

```csharp
[Flags]
public enum FileStoreCapabilities : uint            // closed set: the library owns it
{
    None = 0,
    Directories       = 1 << 0,   // real folders (FTP/SFTP/file system); otherwise prefixes
    AtomicRename      = 1 << 1,   // IRenameFiles is atomic within the store
    ServerCopy        = 1 << 2,   // ICopyFiles without download
    ConditionalWrite  = 1 << 3,   // WriteCondition.IfVersion / CreateNew enforced by the server
    RangeRead         = 1 << 4,
    ResumableWrite    = 1 << 5,   // SFTP offset, FTP REST/APPE, S3 multipart, Blob blocks
    Append            = 1 << 6,
    SignedUrls        = 1 << 7,   // ISignFileUrls
    ServerChecksum    = 1 << 8,   // entries carry a server-computed checksum
    StrongVersions    = 1 << 9,   // FileVersion is not weak
    CaseInsensitive   = 1 << 10,
    Versioning        = 1 << 11,  // object versions are kept
}
```

Every backend states its flags **statically** (§9); some are narrowed at connect time (an FTP server without `MLSD`, a bucket
without versioning) and reported by `IFileStore.Capabilities`.

## 5. Results

Cold I/O → **native `union`** with record cases (⚑F6, Networking ⚑N27 style). Failures are values; exceptions only for programmer
errors (invalid arguments, disposed store).

```csharp
public sealed record FileStoreFailure(FileStoreFailureKind Kind, bool IsTransient, string Detail);  // Detail masked, no secrets
public enum FileStoreFailureKind : byte { Unreachable, Timeout, AuthenticationFailed, AccessDenied, TrustRejected,
    QuotaExceeded, Throttled, Conflict, ProtocolError, IntegrityMismatch, LimitExceeded, Cancelled, Unknown }

public sealed record FileFound(FileEntry Entry);
public sealed record FileMissing(FilePath Path);
public readonly union FileInfoResult(FileFound, FileMissing, FileStoreFailure);

public sealed record FileRead(FileEntry Entry, DataSize BytesRead, FileChecksum Computed);
public readonly union ReadResult(FileRead, FileMissing, VersionChanged, FileStoreFailure);

public sealed record FileWritten(FileEntry Entry, FileChecksum Computed);
public sealed record AlreadyExists(FilePath Path, FileVersion Current);
public sealed record VersionChanged(FilePath Path, FileVersion Expected, FileVersion Current);
public readonly union WriteResult(FileWritten, AlreadyExists, VersionChanged, FileStoreFailure);

public sealed record FileDeleted(FilePath Path);
public readonly union DeleteResult(FileDeleted, FileMissing, VersionChanged, FileStoreFailure);

public sealed record Moved(FileEntry Entry);
public sealed record MovedNonAtomic(FileEntry Entry);                // ⚑F9: copy + verify + delete
public readonly union MoveResult(Moved, MovedNonAtomic, FileMissing, AlreadyExists, FileStoreFailure);
```

`FileStoreFailure.IsTransient` drives YFex.Resilience predicates (timeouts, throttling, unreachable = transient; auth/trust/access
= not).

Listing errors: `ListAsync` yields entries and **ends** with an optional failure through `FileListing` (below) instead of throwing
mid-enumeration.

## 6. Streaming (⚑F7)

```csharp
public readonly record struct ReadOptions(ByteRange? Range = null, FileVersion IfVersion = default,
    FileChecksumKind Compute = FileChecksumKind.Sha256, DataSize? MaxSize = null);

public readonly record struct WriteOptions(WriteCondition Condition = WriteCondition.Overwrite, FileVersion IfVersion = default,
    DataSize? ExpectedSize = null, FileChecksum Expected = default, MediaType? ContentType = null);

public enum WriteCondition : byte { Overwrite, CreateNew, IfVersion }
```

- Read writes into a **`PipeWriter`** supplied by the caller; write reads from a **`PipeReader`**. Backends that only offer
  `Stream` (SDKs) are bridged with `PipeReader.Create(stream, new(pool, bufferSize: 64 KiB))` / `PipeWriter.AsStream()` — no
  intermediate `byte[]` per chunk.
- The checksum is computed **while bytes pass** (one pass, pooled buffers, ⚑C-F1); `ExpectedSize`/`Expected` checksum mismatch →
  `FileStoreFailure(IntegrityMismatch)` and, for writes, the partial object is deleted (or the temp name left for cleanup).
- `MaxSize` refuses oversized downloads early (`LimitExceeded`) — inbound partner files are untrusted.
- `Stream` convenience overloads are **C# 14 extension members** in `YFex.Files` (`store.ReadAsync(path, stream)`), so the
  interface stays small.

`ByteRange` = `(long Offset, long? Length)`; `MediaType` = small value object (`type/subtype`, validated) — candidate for core if
Web wants it.

## 7. Interfaces (⚑F8)

```csharp
public interface IFileStore : IAsyncDisposable
{
    FileStoreName Name { get; }
    FileStoreKind Kind { get; }                       // FileSystem | Ftp | Sftp | S3 | AzureBlob | InMemory | (app-defined: Custom)
    FileStoreCapabilities Capabilities { get; }

    FileListing ListAsync(FilePath prefix, ListOptions options = default, CancellationToken cancellationToken = default);
    ValueTask<FileInfoResult> GetInfoAsync(FilePath path, CancellationToken cancellationToken = default);
    ValueTask<ReadResult> ReadAsync(FilePath path, PipeWriter destination, ReadOptions options = default, CancellationToken cancellationToken = default);
    ValueTask<WriteResult> WriteAsync(FilePath path, PipeReader source, WriteOptions options = default, CancellationToken cancellationToken = default);
    ValueTask<DeleteResult> DeleteAsync(FilePath path, FileVersion ifVersion = default, CancellationToken cancellationToken = default);
}

public interface IRenameFiles  { ValueTask<MoveResult> RenameAsync(FilePath from, FilePath to, bool overwrite, CancellationToken ct); }
public interface ICopyFiles    { ValueTask<WriteResult> CopyAsync(FilePath from, FilePath to, WriteOptions options, CancellationToken ct); }
public interface IAppendFiles  { ValueTask<WriteResult> AppendAsync(FilePath path, PipeReader source, CancellationToken ct); }
public interface ISignFileUrls { ValueTask<SignedFileUrl> SignAsync(FilePath path, SignedUrlAccess access, TimeSpan lifetime, CancellationToken ct); }
public interface IEnsureDirectories { ValueTask<FileStoreFailure?> EnsureDirectoryAsync(FilePath path, CancellationToken ct); }
```

`FileListing` is a small class implementing `IAsyncEnumerable<FileEntry>` plus `FileStoreFailure? Failure` read after enumeration
(and `ContinuationToken` for S3/Blob paging when a caller pages manually). `ListOptions(bool Recursive, int PageSize, FileNamePattern? Filter)`.

Callers test capabilities with a type pattern (`store is IRenameFiles rename`) — AOT-safe, no reflection — and the flag says
whether the operation is **atomic** where that matters.

## 8. Cross-store helpers (`YFex.Files`)

`FileTransfer` (static, C# 14 extension members on `IFileStore` where natural):

- `MoveAsync(store, from, to)` → `IRenameFiles` when `AtomicRename`, else copy (server copy when `ServerCopy`) + verify checksum/size
  + delete → `MovedNonAtomic` (⚑F9).
- `CopyAsync(source, sourcePath, target, targetPath)` between **different stores** (SFTP → S3): one pipe, one pass, checksum
  verified at the end, `MaxSize` honoured.
- `WriteAtomicallyAsync(store, path, source)`: on stores without atomic object puts, write `name.tmp` (`FileName.WithExtension`)
  then rename; on S3/Blob write directly (an object becomes visible only when complete).

## 9. Backend registration with static abstract members (⚑F10)

```csharp
public interface IFileStoreBackend<TSelf, TOptions>
    where TSelf : class, IFileStore, IFileStoreBackend<TSelf, TOptions>
    where TOptions : class, IFileStoreOptions
{
    static abstract FileStoreKind Kind { get; }
    static abstract FileStoreCapabilities DeclaredCapabilities { get; }    // upper bound; instance narrows
    static abstract ValueTask<TSelf> OpenAsync(FileStoreName name, TOptions options, FileStoreServices services, CancellationToken ct);
    static abstract ValidationResult Validate(TOptions options);           // startup check, no I/O
}

// registration (generic, trimming sees every type; no Activator, no reflection)
services.AddYFexFiles(files => files
    .Add<SftpFileStore, SftpStoreOptions>("partner-acme", o => …)
    .Add<S3FileStore, S3StoreOptions>("archive", o => …));
```

`FileStoreServices` carries `ISecretStore`, `TimeProvider`/`ITrustedClock`, `INetworkConditions?`, `ILogWriter`, meter — passed in,
not resolved by service locator. `IFileStoreRegistry.GetAsync(FileStoreName)` opens lazily, pools connections per store
(SFTP/FTP sessions are expensive), and re-opens after a transient failure.

`IFileStoreOptions` (interface with `static abstract FileStoreKind Kind`) lets the Options validator and the configuration binder
source-generator (`EnableConfigurationBindingGenerator`) handle each backend's options without reflection.
