# 04 — Backends

Each backend is a **thin adapter**: our contract outside, the library inside. Library types never appear in our public API, so a
backend can be replaced (FluentFTP → own client, AWSSDK → lean signer) without touching callers.

## 1. Capability matrix (declared upper bound)

| Capability | FileSystem | Ftp/Ftps | Sftp | S3 (+compatible) | AzureBlob | InMemory |
|---|---|---|---|---|---|---|
| Directories | ✓ | ✓ | ✓ | prefixes | prefixes (✓ with HNS/ADLS) | toggle |
| AtomicRename | ✓ (same volume) | ✓ | ✓ | ✗ | ✗ (✓ with HNS) | toggle |
| ServerCopy | ✓ | ✗ | ✗ (✓ with `copy-data` ext.) | ✓ | ✓ | ✓ |
| ConditionalWrite | partial (`CreateNew`) | ✗ | partial (`CreateNew`) | ✓ (`If-Match`/`If-None-Match`) | ✓ | ✓ |
| RangeRead | ✓ | ✓ (`REST`) | ✓ | ✓ | ✓ | ✓ |
| ResumableWrite | ✓ | ✓ (`REST`/`APPE`) | ✓ | ✓ multipart | ✓ blocks | ✓ |
| SignedUrls | ✗ | ✗ | ✗ | ✓ | ✓ (SAS) | fake |
| ServerChecksum | ✗ | `HASH`/`XSHA256` when offered | ✗ | ✓ (CRC32C/SHA-256 when requested) | MD5/CRC64 | ✓ |
| StrongVersions | ✗ | ✗ | ✗ | ✓ ETag/version id | ✓ ETag | ✓ |

## 2. Credentials and trust (all backends)

- **Secrets** (⚑F11): options hold **secret names**; at connect, `ISecretStore.GetAsync(name)` → bytes → handed to the library →
  `CryptographicOperations.ZeroMemory`. Libraries that need `string` passwords (FluentFTP, SSH.NET password auth) get one short-lived
  string — documented limitation; private keys are passed as streams over pooled buffers, cleared after load.
- **Rotation:** a failed authentication re-reads the secret once (it may have been rotated) before reporting `AuthenticationFailed`.
- **Trust** (⚑F12): never the library default "accept anything".
- **Endpoints** come from configuration only; nothing derived from file contents or partner messages (SSRF).

## 3. `YFex.Files.Ftp` — FluentFTP (⚑F4)

- `AsyncFtpClient`; `FtpSecurity { ExplicitTls, ImplicitTls, Plaintext }`. Plaintext requires `AllowPlaintext()` and emits
  `files.store.plaintext_enabled` at start (audit) — partners still run it; we make it visible.
- **FTPS trust:** FluentFTP `ValidateCertificate` → `FtpsTrust`: `System()` (OS chain + host name), `Roots(CertificateTrust)`
  (YFex.Cryptography, private CA), `Pinned(Hash256 spkiSha256)`. TLS 1.2+ only; TLS session reuse on the data channel (many
  servers require it).
- Listing: `MLSD` when `FEAT` advertises it (exact size/modified/type), else `LIST` parsed by FluentFTP's parsers with server
  dialect auto-detect; `Modified` null when unknown → weak versions.
- Passive mode default; `PassivePorts` range for firewalls; active mode opt-in only. Connection pool per store (control channel
  reuse), `NOOP` keep-alive, reconnect on 421.
- Uploads: `STOR` to `.tmp` + `RNFR/RNTO` (atomic rename on the server); resume with `REST`/`APPE` when the server supports it.
- Own-client option kept: FTP is small; if FluentFTP becomes a problem (AOT, size, maintenance), an own client behind the same
  contract is ~1.5 k lines plus dialect tests.

## 4. `YFex.Files.Sftp` — SSH.NET (⚑F3)

- `SftpClient` over a `ConnectionInfo` built by us: auth methods password / private key (OpenSSH, PKCS#8; passphrase from
  `ISecretStore`) / keyboard-interactive (password prompt only).
- **Host key pinning** — `HostKeyReceived`: compare `FingerPrintSHA256` against `SshHostKeyFingerprint` (one or more, for rotation);
  mismatch → `CanTrust = false` → `TrustRejected` + audit event `files.store.host_key_mismatch`. TOFU only with
  `TrustOnFirstUse(approve: …)` that records the key through an app callback (audited). Algorithms: we restrict to modern KEX/host
  key/cipher sets (no `ssh-rsa` SHA-1, no CBC) with an explicit `AllowLegacyAlgorithms()` for old partner servers.
- Large transfers use SSH.NET's pipelined read/write; resume by offset; `posix-rename@openssh.com` for atomic overwrite rename when
  offered, else `rename` (fails if target exists → delete-then-rename reported as non-atomic).
- Keep-alive interval, operation timeout, connection pool (1 session, N channels — SSH.NET client per connection).
- **Own SSH** is not planned: key exchange, ciphers, MACs, channel windows and their CVE history make it a security project.

## 5. `YFex.Files.S3` — AWSSDK.S3 v4 (⚑F5)

- `AmazonS3Client` with explicit `BasicAWSCredentials` / session credentials from `ISecretStore`; **no default credential chain** in
  the base package (⚑F18 → `YFex.Files.S3.Identity` for profiles, SSO, instance/pod roles).
- S3-compatible: `ServiceUrl` + `ForcePathStyle` (MinIO, R2, Wasabi, Ceph). Capabilities narrowed per provider profile
  (`S3Flavor.Aws | MinIO | R2 | Generic`): R2/MinIO differences in checksums, conditional writes, `ListObjectsV2` paging.
- Listing: `ListObjectsV2` with delimiter `/` for non-recursive; continuation tokens surfaced by `FileListing`.
- Writes: single `PutObject` ≤ 16 MiB, multipart above (part size 16 MiB default, 4 parallel parts, bounded memory = parts × size);
  abort incomplete multipart on failure/cancel. `If-None-Match: *` for `CreateNew`, `If-Match` for `IfVersion` (when the flavor
  supports it). Checksum: request SHA-256 (or CRC32C for multipart) and verify ours against the server's.
- Signed URLs: `GetPreSignedURL` with lifetime ≤ 7 days, method-limited.
- SDK retries are set to **off/minimal**; YFex.Resilience owns retry so behaviour is configured once (§8).

## 6. `YFex.Files.AzureBlob` — Azure.Storage.Blobs

- `BlobContainerClient` with `StorageSharedKeyCredential` (account key from `ISecretStore`) or a SAS token; managed identity /
  Entra via `YFex.Files.AzureBlob.Identity` (⚑F18, Azure.Identity; AOT re-probe required).
- Block blobs; staged blocks for large uploads (`StageBlock`/`CommitBlockList`) with bounded parallelism; `ETag` conditions;
  user-delegation or service SAS for signed URLs.
- Accounts with hierarchical namespace (ADLS Gen2) report `Directories | AtomicRename` (DFS rename API) — flavor detected at open.
- SDK retry policy minimised (as S3).

## 7. S3/Azure lean clients — the escape hatch

Not built in v1. Built **only** when a measurement asks for it (binary size on a terminal, an AOT warning on a needed path, SDK
pipeline conflict). Shape:

| Part | Size estimate | Notes |
|---|---|---|
| SigV4 signer | ~300 lines | canonical request over spans, HMAC-SHA256 chain through YFex.Cryptography `MessageAuthentication`/HMAC, AWS test-suite vectors |
| Azure SharedKey / SAS | ~150 lines | canonical headers/resource, HMAC-SHA256 |
| requests | ~8 per provider | list, head, get (range), put, multipart create/part/complete/abort, delete, copy |
| parsers | span-based | S3 XML via `XmlReader` over pooled UTF-8 (no `XDocument`), Blob XML likewise |

Same `IFileStore`, same conformance suite → callers do not change. S3-compatible stores come for free with the signer.

## 8. Retries and timeouts

Backends perform **one attempt** per call (plus the connection re-open inside the pool). Retry, timeout and circuit breaking are
YFex.Resilience policies applied by the caller or by Exchange, keyed on `FileStoreFailure.IsTransient`, and suppressed while
`INetworkConditions` reports `NoNetwork` (Networking ⚑N20 pattern).

## 9. Server (later, ⚑F17)

`YFex.Files.Server.Sftp` / `.Server.Ftp` would expose any `IFileStore` (an S3 bucket can back a partner-facing SFTP):

- users through an **Authentication adapter** (password/public key verified by YFex.Authentication; no second user table);
- path permissions through an **Authorization** policy per store and path prefix (`files.read`, `files.write` scoped to a folder);
- runs in YFex.Hosting; listener via YFex.Networking free-port/firewall checks; Audit for every session and transfer.

An own SFTP server needs an SSH server stack — the same "do not build SSH" problem as ⚑F3; candidates must be evaluated then.
