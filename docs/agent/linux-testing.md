# Linux testing for agents (this Windows PC)

Use this when code must run on Linux: `dotnet test` on Linux, Native AOT `linux-x64`, a distro or musl difference, OpenSSL versions,
TPM 2.0 (tpm2-openssl), the Secret Service (libsecret), Unix file modes, or debugging a Linux-only crash. Android has its own kit
([`android-testing.md`](android-testing.md)); macOS/iOS cannot run on this PC.

## One command

```bat
linuxKit verify-crypto
```

In Git Bash type `linuxKit.bat …` (bash does not add `.bat`); in cmd or PowerShell `linuxKit` works. Source:
`C:\Dev\Scripts\linuxKit.cs`, launcher `C:\Dev\Scripts\Exe\linuxKit.bat`.

That mirrors YFex.Cryptography (and YFex core, plus every `*.cs` of the ecosystem for `ArchitectureTests`) into the Linux disk, starts
a software TPM behind a resource manager and an unlocked GNOME Keyring, and runs `scripts/verify-linux.sh --require-tpm
--require-secret-service` (cryptography 14 §8.1, L1–L7).

## What is installed (decided 2026-10-27)

| Piece | Details |
|---|---|
| WSL 3.0.3 | one UAC prompt (`wsl --install --no-distribution`); no reboot was needed on this PC |
| Distro `yfex-linux` | Ubuntu 26.04.1 LTS, OpenSSL 3.5.5, systemd, kernel 6.18 (WSL), disk `%USERPROFILE%\WSL\yfex-linux\ext4.vhdx` (sparse) |
| User | `agent` (uid 1000, passwordless sudo, groups `docker`, `tss`). Not root, so file-permission tests are real |
| .NET | same SDK as Windows (`dotnet --version`), glibc in `/opt/dotnet`, musl in `/opt/dotnet-musl`; dotnet-dump/-trace/-counters, vsdbg in `/opt/vsdbg` |
| Native tools | clang/lld (AOT), gdb, strace, ltrace, tcpdump, socat, libcap2-bin, acl |
| Security | tpm2-tools, tpm2-openssl, tpm2-abrmd, swtpm, libtss2 TCTIs (swtpm, tabrmd, device), gnome-keyring, libsecret, dbus |
| Containers | Docker Engine 29 (Ubuntu `docker.io`, Apache-2.0; **not** Docker Desktop) inside the distro; qemu-user binfmt for arm64 |
| `.wslconfig` | 8 GB RAM, mirrored networking (localhost shared with Windows), 10 min idle timeout, sparse VHD |

Everything is defined inside `linuxKit.cs` (provision script, image setup, `.wslconfig`): `linuxKit provision` recreates it
(idempotent, ~6 min with packages cached). To start from zero: `wsl --unregister yfex-linux`, then `linuxKit provision`.
Apt uses `us.archive.ubuntu.com`: `archive.ubuntu.com` gave ~100 KB/s from this PC (override with `LINUXKIT_APT_MIRROR`).

## Contract

- **stdout** is one JSON object, **stderr** is progress (the command's output is echoed there live).
- **Exit code:** 0 ok/passed, 1 command or tests failed, 2 usage error, 3 environment problem, 4 timeout.
- `run`/`test`/`verify-crypto` write `C:\Dev\YFex\_ignore\linuxKit-runs\<time>-<command>-<distro>\`: `output.log`, `result.json`,
  `run.sh` + `session.sh` + `command.sh` (exact scripts), `host.log`, `swtpm.log`, `tpm2-abrmd.log`, `trx\*.trx`, collected files.

## Commands

| Command | Does |
|---|---|
| `doctor` | WSL/kernel/distro, OpenSSL, SDKs, swtpm/tpm2/keyring/gdb/docker versions, images, disk |
| `provision [--build] [--sdk v]` | create/repair everything; `--build` also builds all container images |
| `up` / `down` | start (warm) / `wsl --terminate yfex-linux` |
| `images [--build] [--distro d]` | list or (re)build `linuxkit/<distro>:1` |
| `sync [paths]` / `path <winpath>` | mirror now / show the Linux paths of a Windows path |
| `exec -- <cmd>` | quick command, stdout inside the JSON, no run folder |
| `run [--cwd p] [--swtpm] [--keyring] -- <cmd>` | sync + run in a run folder |
| `test [csproj\|dir] [--filter f]` | sync + `dotnet test` (TRX) → `summary` with counts and failed tests |
| `verify-crypto` | YFex.Cryptography 14 §8 on swtpm + keyring (`--no-swtpm`, `--no-keyring` to drop one) |

Targets: `--distro wsl` (default, the distro itself), `ubuntu` (26.04), `ubuntu24` (OpenSSL 3.0), `debian` (13), `fedora` (44),
`alpine` (3.22, musl). Containers are deleted after each run; the SDK (`/opt/dotnet` or `/opt/dotnet-musl`), the NuGet cache, the
mirror and `/mnt/c` are mounted. An image is built the first time it is used (a few minutes). `--debug` allows ptrace in a container,
`--platform linux/arm64` emulates arm64, `-p 8443:8443` publishes a port, `-e K=V` sets a variable, `--root` runs as root.
Checked 2026-10-27: `debian` = OpenSSL 3.5.7, swtpm 0.7.1, tpm2-openssl provider present; `alpine` = OpenSSL 3.5.9, swtpm 0.10.2,
tpm2-abrmd and secret-tool present but **no tpm2-openssl provider package**, so TPM-through-OpenSSL tests cannot run on musl (they
should skip there). If an image build fails with a log that just stops mid-install, rerun it: the first Alpine build was interrupted
once and the same Dockerfile then built cleanly.

## How syncing works

Building from `/mnt/c` is 5–10× slower and writes Linux `bin/obj` into the Windows tree, so the kit mirrors with rsync into
`/home/agent/w/c/<path>` (`C:\Dev\YFex\X` → `/home/agent/w/c/Dev/YFex/X`) and builds with `--artifacts-path ~/art/<distro>/<repo>`.
The sync set is the repo of `--cwd` plus the repos reached by `ProjectReference`/`Import` from the projects under `--cwd`
(`$(YFexRoot)`-style properties from `Directory.Build.props` are expanded), plus `Directory.Build.*`/`global.json`/`NuGet.config` above
them. `bin`, `obj`, `.git`, `.vs`, `node_modules`, `TestResults` are skipped; changed `*.sh` get LF endings and `+x`. Only changed
files are copied (seconds). `--sync <dir>` adds a folder, `--sync-cs <dir>` adds only its `*.cs` (whole-ecosystem scans), `--no-sync`
skips, `--in-place` runs from `/mnt/c`.

## TPM and keyring details

- `--swtpm` starts swtpm on a free port pair (2321+), then **tpm2-abrmd** (`--session --flush-all`) in front of it on a private D-Bus
  session, and exports `TPM2OPENSSL_TCTI=TPM2TOOLS_TCTI=tabrmd:bus_type=session`. The resource manager matters: a client talking to
  swtpm directly (`swtpm:port=2321`, the recipe in cryptography 14 §8.2) runs out of transient object slots after a few keys
  (`TPM_RC_OBJECT_MEMORY`, 0x902, "cannot create primary"), which real hardware avoids through `/dev/tpmrm0`. `-e LINUXKIT_TPM_RM=0`
  reproduces the direct connection.
- There is **no** `/dev/tpm0`/`/dev/tpmrm0`: the WSL kernel has no `CONFIG_TCG_VTPM_PROXY`, and no hardware TPM passthrough.
  swtpm proves the code path, not a chip's protection or timing.
- `--keyring` runs inside `dbus-run-session` with a fresh GNOME Keyring login collection (password `linuxkit`) in a temporary
  `XDG_DATA_HOME`; the kit checks it with `secret-tool` before running the command.

## Limits

- No SELinux/AppArmor enforcement (WSL kernel; containers share it). LSM claims need a real Linux machine or a VM.
- arm64 runs are emulated (qemu-user): slow, smoke tests only. Cross-publishing AOT `linux-arm64` from x64 is not set up.
- First runs are slow (NuGet restore, first build, image build); later runs are incremental.

## Timings measured 2026-10-27 (Core Ultra 7 258V, 8 GB for WSL)

| Step | Time |
|---|---|
| cold start of the distro | 6.6 s |
| command in a running distro | 0.12 s (`linuxKit exec` ≈ 5 s, mostly `dotnet run` of the script) |
| swtpm + tpm2-abrmd + keyring + 6 TPM primaries | 1.5 s |
| incremental sync (YFex.Cryptography + YFex core) | a few seconds |
| `verify-crypto` (3 test projects + AOT publish + smoke), warm | ~75 s |
