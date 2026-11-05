# 03 — API and examples (dev side first)

Examples use a neutral partner ("Acme Logistics") and are **design syntax**: confirm the real API with RoselYn once built.

## 1. Register stores

```csharp
services.AddYFexFiles(files => files
    .Add<SftpFileStore, SftpStoreOptions>("partner-acme", o =>
    {
        o.Host = "sftp.acme.example";  o.Port = 22;  o.UserName = "yfex-shop";
        o.PrivateKeySecret = "files/partner-acme/key";              // ISecretStore name (⚑F11), never the key itself
        o.HostKey = SshHostKeyFingerprint.Parse("SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s");   // ⚑F12
        o.Root = FilePath.Parse("/exchange/yfex");
    })
    .Add<FtpFileStore, FtpStoreOptions>("legacy-bank", o =>
    {
        o.Host = "ftp.bank.example";  o.Security = FtpSecurity.ExplicitTls;
        o.Trust = FtpsTrust.Pinned(Hash256.Parse("…"));             // or .System() / .Roots(certificateTrust)
        o.UserName = "shop42";  o.PasswordSecret = "files/legacy-bank/password";
        o.PassivePorts = PortRange.Any;
    })
    .Add<S3FileStore, S3StoreOptions>("archive", o =>
    {
        o.Bucket = "yfex-archive";  o.Prefix = FilePath.Parse("/tenant-7");  o.Region = "sa-east-1";
        o.AccessKeySecret = "files/archive/access-key";  o.SecretKeySecret = "files/archive/secret-key";
        // S3-compatible: o.ServiceUrl = new("https://<account>.r2.cloudflarestorage.com"); o.PathStyle = true;
    })
    .Add<FileSystemFileStore, FileSystemStoreOptions>("drop-share", o => o.RootDirectory = @"\\fileserver\drops"));
```

Options are validated at startup (`IFileStoreBackend.Validate`): a missing host key, an inline secret in Release, plain FTP without
`AllowPlaintext()` → startup error with the store name.

## 2. List, read, write

```csharp
IFileStore acme = await registry.GetAsync(new FileStoreName("partner-acme"), ct);

FileListing listing = acme.ListAsync(FilePath.Parse("/outbox"), new ListOptions(Filter: FileNamePattern.Parse("*.zip")), ct);
await foreach (FileEntry entry in listing)
    Console.WriteLine($"{entry.Path} {entry.Size}");               // FilePath logs masked by default (⚑F13)
if (listing.Failure is { } failure) return failure;

var pipe = new Pipe();
ReadResult read = await acme.ReadAsync(entry.Path, pipe.Writer, new ReadOptions(MaxSize: DataSize.FromMebibytes(200)), ct);
switch (read)
{
    case FileRead r:          logger.Info($"got {r.BytesRead}, sha256 {r.Computed}"); break;
    case FileMissing:         break;                                // someone else took it
    case VersionChanged:      break;                                // the partner rewrote it while we read
    case FileStoreFailure f when f.IsTransient: retry(); break;
    case FileStoreFailure f:  alert(f); break;
}

// Stream convenience (extension member in YFex.Files)
await using FileStream local = File.Create(tempPath);
ReadResult r2 = await acme.ReadAsync(entry.Path, local, ct: ct);

// upload without a half-written file ever being visible
WriteResult written = await acme.WriteAtomicallyAsync(FilePath.Parse("/inbox/orders-0001.txt"), PipeReader.Create(source), ct);
```

## 3. Optional capabilities

```csharp
if (store is ISignFileUrls signer)                                  // S3/Blob only
{
    SignedFileUrl url = await signer.SignAsync(path, SignedUrlAccess.Read, TimeSpan.FromMinutes(15), ct);
    await partnerApi.NotifyAsync(url.Uri);                          // the partner downloads directly from the bucket
}

MoveResult moved = await store.MoveAsync(from, to, ct);              // FileTransfer helper (⚑F9)
if (moved is MovedNonAtomic) logger.Info("moved by copy + delete");

WriteResult w = await store.WriteAsync(path, reader, new WriteOptions(Condition: WriteCondition.IfVersion, IfVersion: seen.Version), ct);
if (w is VersionChanged) reload();                                  // only where Capabilities has ConditionalWrite
```

## 4. Between stores

```csharp
// pull from the partner's SFTP, archive in S3, one pass, checksum verified on both ends
WriteResult archived = await FileTransfer.CopyAsync(acme, entry.Path, archive, FilePath.Parse("/acme/2026/10") / entry.Path.Name,
    new CopyOptions(MaxSize: DataSize.FromMebibytes(500)), ct);
```

## 5. Exchange (details in [05](05-exchange.md))

```csharp
services.AddYFexFileExchange(x => x
    .Inbound("acme-orders", store: "partner-acme", inbox: "/outbox", e => e
        .Match("ORD_*.zip").SettleFor(TimeSpan.FromSeconds(30))
        .Unzip(ZipLimits.Default).Text(TextEncoding.Latin1)
        .AfterSuccess(Disposition.MoveTo("/outbox/processed")).AfterFailure(Disposition.MoveTo("/outbox/error"))
        .PollEvery(TimeSpan.FromMinutes(2)).Handler<AcmeOrdersHandler>())
    .Outbound("acme-invoices", store: "partner-acme", outbox: "/inbox", e => e.Naming("INV_{date:yyyyMMdd}_{seq:0000}.txt")));

public sealed class AcmeOrdersHandler : IInboundFileHandler
{
    public async ValueTask<HandlerOutcome> HandleAsync(InboundFile file, CancellationToken ct)
    {   // file.Path, file.Entry, file.Checksum, file.OpenText()/OpenBytes(); runs once per (store, path, version/checksum)
        await mediator.SendAsync(new ImportOrders(file.Checksum, await file.ReadAllTextAsync(ct)), ct);
        return HandlerOutcome.Done;                                    // | Retry(after) | Reject(reason) → error folder
    }
}

await exchange.SendAsync("acme-invoices", invoiceBytes, ct);           // atomic upload, ledger entry, audit event
```

## 6. Testing

```csharp
var store = new InMemoryFileStore("partner-acme", FileStoreCapabilities.Directories | FileStoreCapabilities.AtomicRename);
await store.SeedAsync("/outbox/ORD_1.zip", zipBytes, modified: clock.GetUtcNow());
var chaos = new ChaosFileStore(store, faults => faults.FailEvery(3, FileStoreFailureKind.Timeout).SlowReads(TimeSpan.FromSeconds(2)));

// every backend, including app-defined ones, runs the same suite
public sealed class SftpConformance(SftpContainerFixture f) : FileStoreConformance<SftpFileStore>(f.OpenStore);
```

The conformance suite checks: path round trips (Unicode NFC, spaces, 255-byte segments), listing by prefix with and without
recursion, info vs listing agreement, read ranges, conditional writes **only when declared**, rename atomicity when declared,
`MovedNonAtomic` otherwise, delete semantics, checksum verification, `MaxSize`, cancellation mid-transfer leaving no visible
partial file, failure kinds for wrong password / wrong host key / missing bucket.
