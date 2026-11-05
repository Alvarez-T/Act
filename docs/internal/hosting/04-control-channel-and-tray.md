# 04 — Control channel, tray and CLI

> Phases: control channel + CLI = **H2**; tray = **H5, the last phase** (user 2026-11-03).

> **Can a tray icon control a Windows service? Yes, but not from inside the service.** Since Windows Vista, services run in
> **session 0**, which has no desktop: a service cannot show an icon, a window or a toast. The standard design, used here, is a
> **separate small tray program** that runs in each signed-in user's session and talks to the service over a local, access-controlled
> channel. The same holds on Linux (a systemd service has no display) and macOS (a launchd daemon has none either).

```
 user session (PC-GERENTE\maria)                 session 0 / system
 ┌──────────────────────────┐   named pipe    ┌────────────────────────────────────┐
 │ yfex-tray (Avalonia)     │ ◄─────────────► │ yfex-host  ─ control server (shell) │
 │  icon · menu · windows   │  ACL + codec v1 │  status · modules · jobs · logs ·   │
 │  session signals ────────┼────────────────►│  diagnose · override · consent      │
 └──────────────────────────┘                 └────────────────────────────────────┘
 yfex-host status / modules / logs (CLI) ── same channel, same rules
```

## 1. The channel (⚑H12)

| | Windows | Linux | macOS |
|---|---|---|---|
| Endpoint | `\\.\pipe\yfex-host-{instance}` | `/run/yfex/{instance}/control.sock` (systemd `RuntimeDirectory`) | `/var/run/yfex/{instance}/control.sock` |
| Created with | `NamedPipeServerStreamAcl.Create` + `PipeSecurity` (§2), `PipeOptions.CurrentUserOnly` **off**, `FirstPipeInstance` on (no squatting) | socket file mode 0660, group `yfex-operators` | same as Linux, group `_yfex` |
| Caller identity | `GetImpersonationUserName` / client SID via `ImpersonateNamedPipeClient` for the audit record | `SO_PEERCRED` (uid, gid, pid) | `LOCAL_PEERCRED` |

- **Codec:** small versioned binary request/reply/push, one byte message tag, length-prefixed, golden-pinned (same style as Offline's
  broker codecs). No JSON on the wire, no reflection. Max message 1 MiB; max 8 concurrent clients; idle clients closed after 10 min.
- **Pushes:** status changes, module state changes, log records (only while a log view is open), toasts. A client that falls behind
  loses log pushes (counted), never status.
- **Messages v1:** `GetStatus`, `ListModules`, `RestartModule`, `ListJobs`, `RunJob`, `PauseJob`/`ResumeJob`, `TailLogs(filter)`,
  `RunDiagnosis`, `GetConsent`/`SetConsent`, `SessionSignals` (tray → host, ⚑H15), `SetOverride`/`ClearOverride`, `Subscribe`.
- The channel is part of the **shell** (02 §4): it survives generation switches; module messages during a switch answer `Busy`.
- The channel **never** listens on a network port. Remote support uses the status relay to central (05 §5), not this channel.

## 2. Who may do what (⚑H13)

| Action | Windows principals | Linux/macOS | Audited |
|---|---|---|---|
| status, list modules/jobs, diagnose (masked) | `INTERACTIVE`, Administrators, `YFex Host Operators` | any member of `yfex-operators`, root | no (read) |
| restart module, run/pause job, live logs | Administrators, `YFex Host Operators` | `yfex-operators`, root | **yes** |
| set/clear override (only if profile allows) | Administrators, `YFex Host Operators` | same | **yes** |
| consent change (machine scope) | Administrators | root | **yes** |
| start/stop/restart the **service itself** | Administrators through the **service manager** (UAC prompt from the tray) — not through the channel | `systemctl` (polkit) | by the OS |

- The pipe ACL only decides who may **connect** (a pipe is read/write once open). The server checks **every message** against the
  caller's token groups (Windows: `WindowsIdentity` from impersonation; Unix: uid/gid from peer credentials) and answers `Denied`.
- Groups are created by `install` (06 §3). Adding people to them is an admin task (or YFex.Installer).
- Every audited action becomes a YFex.Audit record (`hosting.module.restarted`, `hosting.job.run`, `hosting.override.set`…) with the OS
  account (SID/uid), machine, instance and reason; relayed to central with the other audit items (05 §6).
- **Later (H4 phase):** sensitive actions may also require an operator sign-in (code + PIN through the site verifiers of
  YFex.Authentication.Offline) and a `hosting.*` permission from the site authorization bundle — for shops where everyone shares one
  Windows account.

## 3. The tray (`yfex-tray`, ⚑H14)

Avalonia app with `TrayIcon` + `NativeMenu` and a few small windows; Native AOT; view models on YFex.State (`[Observable]`); text in
pt-BR/en from resources. One tray per user session shows **every** host instance on the machine (registered in
`%PROGRAMDATA%\YFex\Hosting\instances\*.json` / `/etc/yfex/hosting/instances/`).

| Menu / window | Content | Source |
|---|---|---|
| **Icon state** | green Running · yellow Degraded/Dormant-only-problem · red Faulted or host unreachable · grey stopped | `HostStatus` push |
| Status window | per instance: version, generation, profile source + version, uptime; modules with state, reason, since | Hosting |
| Modules | restart a module; `Dormant` explained ("this PC is the standby broker") | Hosting supervisor |
| Jobs | next run, last outcome, run now, pause/resume | YFex.Jobs |
| Network | conditions line ("Sem internet — rede da loja OK"), watched endpoints, **Diagnose** → masked report, copy to clipboard | YFex.Networking + Privacy |
| Time | trusted-time state, uncertainty, findings (`time.rollback`…) | YFex.Time |
| Installation | id, display name, site, role, enrollment and certificate expiry, firewall check (`Missing` → "ask support") | YFex.Installations + Networking |
| Logs | live tail with level/module filter; "Open in LoggY" for files | YFex.Logging live tap |
| Privacy & consent | machine-scope telemetry consent (`ConsentRecord` categories); default off | core `YFex.Consent`, Profiling |
| Support bundle | zip of status + diagnosis + last 2 000 warnings/errors, masked | Hosting + Networking + Logging + Privacy |
| Service | Start / Stop / Restart (UAC elevation through the service manager) | OS |
| **Toasts** | module `Faulted`, host unreachable > 2 min, certificate expiring < 7 days, disk > 80 % of a journal cap, profile rejected | pushes |

- **Auto-start:** `install --tray` registers it for all users (Windows `HKLM\…\Run`, Linux `/etc/xdg/autostart/yfex-tray.desktop`, macOS
  LaunchAgent). Full desktop integration (Start menu, uninstall entry) is YFex.Installer's.
- **Linux:** the tray uses StatusNotifierItem over D-Bus; GNOME needs the AppIndicator extension. Headless servers use the CLI.
- **No business UI.** The tray never signs anyone in to the app, never shows app data. It is an operations tool.
- **Session signals (⚑H15):** while the user has consented, the tray runs YFex.Profiling's session providers (idle, lock/unlock, active
  window title category, behaviour counts — never raw keys) and sends batches to the host's `profiling.machine` module. No consent → the
  providers are not created.

## 4. The CLI

The verbs of 03 §4 use the same client (`YFex.Hosting.Control`). Output is plain text (tables) or `--json`. Exit code 0 / 1 error /
4 access denied / 5 host not running. The CLI is the only control on headless Linux servers and in scripts.
