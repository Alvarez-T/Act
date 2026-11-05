# YFex.Installer — install, enroll, firewall, updates — Orientation

> **Status:** ORIENTATION 2026-11-03. No code, no decisions yet (⚑IN1–IN8 below wait for the user). Built **after everything else**
> (user, 2026-10-07, installations README). This file collects what other plans have assigned to the Installer so it is not lost.
> **Repo (proposed):** `C:\Dev\YFex\YFex.Installer` (sibling).
> **Origin:** installations ⚑I20 moved the command-line tool, firewall rules and service install here (2026-10-07); hosting ⚑H6/H7 and
> the user's 2026-11-03 answer — "self update yes, but it is the Installer's responsibility" — added the elevated Security service and
> **self-update**.

## 1. What other plans assigned to the Installer

| From | Item |
|---|---|
| installations README §I20, 03 §1, 06 §3 | enrollment UI (code / QR), technician flow; until then `yfex-host --enroll CODE` |
| installations 06 §3, 09; offline 10 §2 | **Windows firewall** inbound rules: TCP 7443 (peer endpoint) + beacon UDP, **Private profile only**, with administrator consent; notes from installations 08 (a clicked "Allow" pop-up hides a missing rule; a block rule beats the port rule) |
| offline 10 §2 | power plan on broker PCs (no sleep on AC); documented antivirus exclusions for data folders; publisher-signed binaries |
| hosting 06 §1 (⚑H7) | uses Hosting's `install`/`uninstall` for services; adds Start-menu and uninstall entries, desktop integration of `yfex-tray` |
| hosting ⚑H6 | installs **`YFex.Security.Service`** as its own elevated service (packet capture needs rights the host must not have) |
| **user 2026-11-03** | **self-update** of `yfex-host`, `yfex-tray`, app hosts and desktop apps (§4) |

## 2. Products installed

| Product | Kind | Notes |
|---|---|---|
| `yfex-host` (+ `yfex-tray`) | service + per-user tray | generic, no app code; updated only when its modules' protocols change (offline 07 §2) |
| app hosts (e.g. `farma-integrations`) | service | app release cadence |
| desktop/POS apps | user app | app release cadence; per-user or per-machine |
| `YFex.Security.Service` | elevated service | separate (hosting ⚑H6) |

## 3. Shape (to design)

- One library + a small bootstrapper per platform (Windows: MSI/MSIX or an own setup exe — ⚑IN1; Linux: .deb/.rpm + tarball; macOS: pkg).
- Steps are **idempotent** and re-runnable as "repair"; each step reports through the same status keys as Hosting.

## 4. Self-update (user, 2026-11-03)

What the Installer will own (design to be written in this folder):

| Concern | Direction (recommendation, not decided) |
|---|---|
| **Who decides the version** | central, per scope on the Tenancy chain (tenant → site → installation), like Hosting profiles: a signed bundle `installer.release` addressed to the installation says "product X: version V, channel stable, window 22:00–06:00" (⚑IN2) |
| **Who signs packages** | the **vendor** (like Licensing: a tenant central forwards, cannot sign itself code); signature with YFex.Cryptography document signatures, keys pinned in the installed product (⚑IN3) |
| **Download** | from central or the vendor CDN once per site; the **site broker caches** and serves terminals over a peer channel (`installer.packages`), so 30 checkouts do not each download from the internet (⚑IN4) |
| **Verify** | signature + SHA-256 before anything is written to the program folder; refuse downgrades except an explicit rollback version from central |
| **Apply** | side-by-side version folders (`…\Hosting\versions\1.4.2\`), switch the service's binary path (or a stable launcher) and restart; the host gets a **drain** request over its control channel first ("update pending" in the tray), finishes its generation cleanly, then stops (⚑IN5) |
| **Rollback** | if the new version does not report `Running` (Hosting status) within 5 min, switch back and report `installer.update.rolled-back` |
| **When** | maintenance window from the release bundle; never while the store is selling unless forced; desktop apps update at close / shift end |
| **Updating the updater** | a small separate updater component (Windows: a one-shot elevated service or scheduled task) that is itself updated by the main updater (⚑IN6) |
| **Offline sites** | the broker keeps the last packages; an installation offline for long applies on reconnect; a version too old for central's protocol floor is told to update (Installations floors) |
| **Visibility** | update state in the Hosting status relay and the tray; audit records `installer.update.*` |

## 5. Open questions (for when the Installer is designed)

| # | Question |
|---|---|
| IN1 | Windows packaging: MSI (WiX), MSIX (no services in some modes), or an own setup exe? |
| IN2 | Release targeting: central per scope (recommended) or vendor-only rings? |
| IN3 | Package signing key: vendor-only (recommended) or also the tenant for on-premises custom builds? |
| IN4 | Site distribution: broker cache over a peer channel (recommended) or each machine downloads? |
| IN5 | Apply: side-by-side folders + service binary switch (recommended) or in-place replace with a launcher stub? |
| IN6 | Who updates the updater, and with which privilege model? |
| IN7 | Linux: own updater, or rely on the distro package manager (apt/dnf repository)? |
| IN8 | Do desktop apps share the machine updater, or embed an update client? |
