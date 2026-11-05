# 06 — Platforms, install, security, AOT

## 1. What Hosting installs and what it leaves to YFex.Installer (⚑H6, ⚑H7)

| Concern | YFex.Hosting (`install` verb) | [YFex.Installer](../installer/README.md) |
|---|---|---|
| Service registration, start mode, restart on failure | ✅ | uses Hosting's verb or the same library code |
| Service account, folders, ACLs, operators group | ✅ | — |
| Tray auto-start | ✅ (`--tray`) | Start-menu entry, uninstall entry |
| Enrollment | `--enroll CODE` (until the Installer exists) | enrollment UI/QR, technician flow |
| Firewall rules (TCP 7443, beacon UDP) | **checks only** (05 §3) | **creates** them with admin consent |
| Power plan (no sleep on AC for brokers, offline 10 §2) | — | ✅ |
| Antivirus exclusions documentation, signed binaries | — | ✅ |
| **Updates** of `yfex-host`, `yfex-tray`, app hosts (self-update) | exposes version + "update pending" state; drains and stops cleanly when asked | ✅ **owns it** (installer plan §4) |
| `YFex.Security.Service` (elevated) | — | ✅ installs it as its own service |
| Uninstall | `uninstall [--keep-data]` | full product removal |

## 2. Platforms (⚑H8 recommendation)

| | Windows 10/11, Server 2019+ | Linux (x64, arm64; arm32 JIT) | macOS 13+ (H4) |
|---|---|---|---|
| Lifetime | `AddWindowsService` (`YFex.Hosting.Windows`) | `AddSystemd`, `Type=notify`, `WatchdogSec=60` (`.Linux`) | launchd daemon (`.MacOS`) |
| Account | **virtual account `NT SERVICE\YFexHost[-{instance}]`** (no password, no logon right, network as machine) | system user `yfex` (nologin), group `yfex-operators` | `_yfex` daemon user |
| Binaries | `%ProgramFiles%\YFex\Hosting\` (read-only to the account) | `/opt/yfex/hosting/` | `/Library/YFex/Hosting/` |
| Bootstrap `hosting.json` | `%ProgramData%\YFex\Hosting\{instance}\hosting.json` (Admins write, account read) | `/etc/yfex/hosting/{instance}.json` (root write) | `/Library/Preferences/YFex/Hosting/{instance}.json` |
| State (encrypted KV, last-good profile) | `%ProgramData%\YFex\Hosting\{instance}\state\` (account + Admins only) | `/var/lib/yfex/{instance}/` (`StateDirectory`, 0700) | `/Library/Application Support/YFex/Hosting/{instance}/` |
| Installations machine storage | `%ProgramData%\YFex\Installations\yfex-host\` (installations 09) | `/var/lib/yfex/installations/yfex-host/` | as installations 09 |
| Logs | `%ProgramData%\YFex\Hosting\{instance}\logs\` (operators read) | `/var/log/yfex/{instance}/` + journald | `/Library/Logs/YFex/{instance}/` |
| Keys | `CngSigningKeyStore` machine scope, TPM first (**G2 spike**) | `FileSigningKeyStore` under the root key (0600); TPM store later (crypto 14 §8.1) | file store (daemons have no Keychain); **no broker** |
| Build | Native AOT win-x64 (win-arm64 later) | Native AOT linux-x64, linux-arm64; linux-arm self-contained JIT | Native AOT osx-arm64 |

## 3. `install` in detail

Windows (SCM API through `.Windows`, not `sc.exe` text parsing):

1. Create/refresh service `YFexHost[-{instance}]`, display "YFex Host[ ({instance})]", `start= delayed-auto`, account
   `NT SERVICE\YFexHost…`, recovery: restart after 5 s, 30 s, 2 min; reset after 1 day; `SERVICE_CONFIG_FAILURE_ACTIONS_FLAG` on (non-crash
   failures count too).
2. Create local group **YFex Host Operators** (if absent).
3. Create folders with ACLs of §2; write `hosting.json` if absent (never overwrite).
4. Register the instance in `%ProgramData%\YFex\Hosting\instances\{instance}.json` (for the tray).
5. `--tray`: `HKLM\Software\Microsoft\Windows\CurrentVersion\Run\YFexTray`.
6. Grant the service account access to its machine key if one exists (G2).

Linux: user/group, folders, unit `/etc/systemd/system/yfex-host@.service` (template, `%i` = instance) with `User=yfex`,
`StateDirectory=yfex/%i`, `RuntimeDirectory=yfex/%i`, `LogsDirectory=yfex/%i`, `Restart=on-failure`, `RestartSec=5`,
hardening (`NoNewPrivileges=yes`, `ProtectSystem=strict`, `ProtectHome=yes`, `PrivateTmp=yes`); `systemctl enable --now`.

All steps are **idempotent**; `install` on an installed instance repairs it. A dry run (`--what-if`) prints the steps.

## 4. Threats

| Threat | Mitigation |
|---|---|
| Someone edits `hosting.json` to enable a module | modules come from the **signed** central profile; the local file holds only bootstrap data and a first-run profile used before central is reached; folder writable only by Admins |
| Malicious local process talks to the control channel | pipe ACL (connect) + per-message group check + audit; `FirstPipeInstance` against pipe squatting; no network listener |
| Local override abused to keep a module off/on | only if the profile allows it, time-limited, audited, visible in tray and central status |
| Plugin/DLL planting next to the exe | no plugin loading (⚑H2); Native AOT single file; binaries folder read-only to the account |
| Replay of an old profile | bundle versions are monotonic per stream (Installations floors) |
| Host running as LocalSystem by accident | `install` sets the virtual account; status reports the account; test fails if the service runs as SYSTEM |
| Support bundle leaks personal data | Privacy masks; reasons are keys; no raw log text above Information from modules marked sensitive |
| Crash loop fills the disk with logs | crash-loop breaker; log retention caps |

## 5. AOT and trimming

- `YFex.Hosting`, `.Control`, `.Windows`, `.Linux`, `.Installations`: `IsAotCompatible`, warnings as errors.
- `yfex-host` and `yfex-tray` publish with Native AOT; the **AOT smoke** (`tests/YFex.Hosting.AotSmoke`) publishes `yfex-host` with all
  built-in modules and must report **0 IL warnings** and run `status` against itself.
- Configuration binding only through the binding source generator; codec hand-written; no `Enum.Parse`, no reflection-based DI scanning.
- **Architecture test:** `YFex.Hosting.Host`'s referenced assemblies (transitively) contain only `YFex.*` infrastructure assemblies on an
  allow list and Microsoft/BCL packages — **no app assembly** (⚑O20, offline 4f acceptance).
