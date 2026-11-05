# 07 — Phases, acceptance, log

> Gates for every phase: `dotnet build hosting.slnx` 0 warnings; tests green; AOT smoke 0 IL warnings (win-x64; linux-x64 through
> `linuxKit` from H0); allocation tests for the codec and status reads; docs updated (README status, offline 11, infrastructure map).

## Phase H0 — the host for the site broker (needed by Offline 4f)

- **G2 spike first:** TPM machine key created by `CngSigningKeyStore` opened by `NT SERVICE\YFexHost` (sign + TLS client cert). Result
  decides the ACL step of `install`.
- Repo `YFex.Hosting` (template YFex.Networking): `YFex.Hosting` (ids, catalog, supervisor, generations, local profile, verbs),
  `.Windows`, `.Linux`, `.Host` (`yfex-host` with base modules + `offline.broker`), `.Testing`, tests, AOT smoke, benchmarks (status read,
  catalog lookup).
- `install`/`uninstall` (06 §3), `--enroll`, `status` (console only in H0: prints from the running process' status file; the control
  channel comes in H2).
- **Acceptance:**
  - `yfex-host install` + `--enroll` on a Windows PC → service runs as the virtual account, enrolled as `SiteBroker`, broker `Dormant`
    until the roster says `Active`, then serves `offline.*`.
  - Same on WSL `yfex-linux` (systemd) and on the Raspberry Pi (JIT build).
  - Architecture test: no app assembly in `yfex-host`.
  - A module that throws at start is restarted with backoff and becomes `Faulted` after 5 failures in 10 min; the host keeps running.
  - Killing the process → the service manager restarts it; the broker answers again within 30 s.
  - The Offline 4f benchmark (30 simulated terminals) runs against `yfex-host`, not a test-only host.

## Phase H1 — profiles from central

- `YFex.Hosting.Installations` (bundle kind, tracker, status relay), `YFex.Hosting.Central` (layers per scope, Tenancy resolution,
  publisher, status store, commands/queries, permissions), last-good profile, generations on module-set changes, override (H17), audit.
- **Acceptance:** enabling a module at the tenant layer reaches a store PC through the broker's gossip and starts it without a process
  restart (new generation); a site layer overrides the tenant; a forged or older profile is refused; with central unreachable at boot the
  last-good profile is used; status of every host is visible at central within one `StatusInterval`.

## Phase H2 — control channel and CLI

- `YFex.Hosting.Control` (codec golden-pinned, server, client, per-message authorization), CLI verbs, live logs, diagnosis, support bundle.
- **Acceptance:** a member of *YFex Host Operators* restarts a module with `yfex-host modules restart` and an audit record reaches central;
  a standard user sees status but gets `Denied` on restart; a generation switch does not drop a `logs --follow` client; diagnosis text
  contains no unmasked IP/host beyond Privacy rules.

## Phase H3 — more modules

- `devices.serial-bridge`, `profiling.machine`, `data.schema`; `files.exchange` in a sample app host; logging shipping and audit
  forwarding when those libraries have them; license gating when Licensing exists.
- **Acceptance per module:** switched on/off from central; listener checks reported; documented settings keys in `hosting-catalog.json`.

## Phase H4 — later

- `YFex.Hosting.MacOS` (launchd, no broker), Linux TPM keys (with Cryptography/Installations), operator sign-in for control actions,
  YFex.Security.Service adopting the control contract (optional), win-arm64 build.

## Phase H5 — tray (last, user 2026-11-03)

- `yfex-tray` (Avalonia, AOT) over the H2 channel ([04](04-control-channel-and-tray.md) §3); Profiling session providers in the tray
  (needs the Profiling ask); auto-start registration (`install --tray`).
- **Acceptance:** the tray shows two instances (generic + an app host); an operator restarts a module from the tray (audited); a standard
  user gets `Denied`; host unreachable → red icon + toast within 2 min; a generation switch does not drop the tray connection; without
  consent no session provider is created.

## Implementation log

### 2026-10-19 — ⚑H1 decided (orientation only)

User: "build the shared service host as a new YFex.Hosting repo, together with Offline Phase 4". No code.

### 2026-11-03 — Plan written (docs only, no code)

User asked for the full design plan like the other `docs/internal` plans, with a summary first. Answers given on the summary:
H2 OK; H3 "from Tenancy or installations from central"; H4 generic, several kinds/instances; H5 yes; H6 no, with YFex.Installer; H7 OK;
H9 OK; H10 Jobs part of the host; self-update belongs to YFex.Installer (new [installer orientation](../installer/README.md)). Added on
request: tray control (04), Profiling/Logging/Networking integration and a survey of the other libraries (05). Open for the user: ⚑H8,
⚑H11–H21. Asks to other libraries: Installations (additive publisher registration, G4),
Profiling (session-signal seam).

### 2026-11-03 — Tray moved last; "who talks to central" settled as a rule (docs only)

User: "let the tray as the last phase" → new Phase H5; H2 is control channel + CLI only. User asked whether sending profiling/log data
to central is Hosting's responsibility → **no**: each library owns its data, spool and shipper; Installations/Messaging.Rpc own the
road to central; Hosting only runs them and sends its own `hosting.status` ([05](05-ecosystem-integration.md) §0). Open: ⚑H22.
