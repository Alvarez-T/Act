# YFex infrastructure map for agents

Use this file only when a task crosses package or repository boundaries. This is a navigation index, not an API reference or a claim that every design phase is implemented. Verify current source with the RoslynMcp tools and current phase logs before editing.

## Choose the owner

| Need | Implementation owner | First document |
|---|---|---|
| Reusable types named by several libraries | `YFex/src/YFex` | [`docs/public/core.md`](../public/core.md) |
| Local app-state storage, durable queues, caches | `YFex.Persistence` | [`YFex.Persistence/README.md`](../../../YFex.Persistence/README.md) |
| Database commands, SQL, mapping, bulk writes, data migration | `YFex.Data` | [`YFex.Data/README.md`](../../../YFex.Data/README.md) |
| Backup and restore the database/platform does not do: encrypted backup sets, retention, restore drills, per-tenant export/restore, erasure replay after restore; not placement (tenant → database: `YFex.Data.Placement`, planned) | `YFex.Backup` (repo created 2026-11-12, DB-P0 skeleton; DB-P1 next) | [`databases-and-backups/README.md`](../internal/databases-and-backups/README.md) |
| Scope identity and scoped overrides | `YFex.Tenancy` | [`YFex.Tenancy/AGENTS.md`](../../../YFex.Tenancy/AGENTS.md) |
| Scope-specific implementation selection | `YFex.CustomLogic` | [`YFex.CustomLogic/AGENTS.md`](../../../YFex.CustomLogic/AGENTS.md) |
| Shape validation, pure business-rule evaluation, expression language | `YFex.Validation`, `YFex.Rules`, `YFex.Expressions` | [`validation/README.md`](../internal/validation/README.md) |
| CQRS contracts and dispatch | `YFex.Cqrs` | [`docs/public/cqrs.md`](../public/cqrs.md) |
| Event bus, offline outbox, RPC, CQRS approvals, rules admin | `YFex.Messaging` | [`docs/public/messaging.md`](../public/messaging.md) |
| Reactive state, navigation, MVVM base ViewModels, framework-neutral UI contracts | `YFex.State`, `YFex.NavigatR`, `YFex.Mvvm`, `src/UI` | [`docs/index.md`](../index.md) |
| Accounts, sign-in (password, PIN, codes by e-mail/SMS/WhatsApp; Google/Microsoft/OIDC), sessions and tokens; the app-side client (`.Client`) and offline bridge (`.Offline`) | `YFex.Authentication` (P1–P4 built) | [`YFex.Authentication/README.md`](../../../YFex.Authentication/README.md), [`authentication/README.md`](../internal/authentication/README.md) |
| Permission evaluation and actor identity | `YFex.Authorization` | [`YFex.Authorization/README.md`](../../../YFex.Authorization/README.md) |
| Discoverable actions for search and AI | `YFex.Capabilities` | [`YFex.Capabilities/README.md`](../../../YFex.Capabilities/README.md) |
| Provider-neutral search orchestration | `YFex.Search` | [`YFex.Search/AGENTS.md`](../../../YFex.Search/AGENTS.md) |
| Remote file stores (FTP/FTPS, SFTP, S3, Azure Blob, shared folders) and partner file exchange | `YFex.Files` (planned, design proposed) | [`files/README.md`](../internal/files/README.md) |
| Outbound HTTP API clients, per-scope endpoints and credentials | `YFex.Web` | [`YFex.Web/docs/README.md`](../../../YFex.Web/docs/README.md) |
| Keys, cryptographic bytes, signatures, certificates | `YFex.Cryptography` | [`YFex.Cryptography/README.md`](../../../YFex.Cryptography/README.md) |
| Data classification and masking | `YFex.Privacy` | [`YFex.Privacy/README.md`](../../../YFex.Privacy/README.md) |
| Logs and operation telemetry | `YFex.Logging` | [`YFex.Logging/README.md`](../../../YFex.Logging/README.md) |
| Who acts: actor kinds and identity, text form and canonical bytes, ambient actor scope with capture/restore, record stamps, names at read time | `YFex.Actors` | [`YFex.Actors/README.md`](../../../YFex.Actors/README.md), [`actors/README.md`](../internal/actors/README.md) |
| Tamper-evident audit trails | `YFex.Audit` | [`YFex.Audit/README.md`](../../../YFex.Audit/README.md) |
| Trusted time and rollback detection | `YFex.Time` | [`YFex.Time/README.md`](../../../YFex.Time/README.md) |
| Network conditions, probes, discovery, Telnet, SNMP | `YFex.Networking` | [`YFex.Networking/README.md`](../../../YFex.Networking/README.md) |
| Installed-copy identity, enrollment, trusted peers | `YFex.Installations` | [`installations/README.md`](../internal/installations/README.md) |
| Android handhelds: QR enrollment, presence in the store, off-site rules | `YFex.Installations` (+ Cryptography.Android, Authentication, Authorization, Offline) | [`installations/11-mobile-handhelds.md`](../internal/installations/11-mobile-handhelds.md) |
| Offline modes, signed offline envelopes, offline sign-in, site manifest and limits, allowances, signed rule data, central reconciliation and durable stores, site broker services (Phase 4) | `YFex.Offline` (`.Central`, `.Rules`, `.Persistence`, `.Rpc`, `.Central.Rpc`, `.Data`) | [`YFex.Offline/README.md`](../../../YFex.Offline/README.md), [`offline/README.md`](../internal/offline/README.md) |
| Retry, timeout, breaker policy vocabulary | `YFex.Resilience` | [`YFex.Resilience/README.md`](../../../YFex.Resilience/README.md) |
| Sagas, process managers, durable waits | `YFex.Orchestration` | [`YFex.Orchestration/README.md`](../../../YFex.Orchestration/README.md) |
| Event-sourced aggregate write model | `YFex.EventSourcing` | [`YFex.EventSourcing/README.md`](../../../YFex.EventSourcing/README.md) |
| Embedded scheduled jobs | `YFex.Jobs` | [`YFex.Jobs/README.md`](../../../YFex.Jobs/README.md) |
| Running background services: one service host per machine role (`yfex-host`), module profiles from central, tray/CLI control | composition roots; `YFex.Hosting` (planned, plan proposed 2026-11-03) | [`hosting/README.md`](../internal/hosting/README.md) |
| Sending messages to people (e-mail, SMS, WhatsApp, chat, webhooks, in-app), templates, outbox | `YFex.Notifications` (N0 built 2026-11-09: contracts, outbox, dispatcher, e-mail); OTP challenge rules stay in `YFex.Authentication.Delivery` | [`notifications/README.md`](../internal/notifications/README.md) |
| Installing products, firewall rules, enrollment UI, self-update | `YFex.Installer` (future, orientation only) | [`installer/README.md`](../internal/installer/README.md) |
| Desktop + AI automation: task graphs, orchestrator, input/observation, AI agents, secret and session stores | `YFex.Automation` | [`YFex.Automation/README.md`](../../../YFex.Automation/README.md) |
| Consent-gated machine telemetry | `YFex.System` | [`YFex.System/README.md`](../../../YFex.System/README.md) |
| Consent-aware profile signals | `YFex.Profiling` | [`YFex.Profiling/AGENTS.md`](../../../YFex.Profiling/AGENTS.md) |
| Security intelligence and inspection | `YFex.Security` | [`YFex.Security/AGENTS.md`](../../../YFex.Security/AGENTS.md) |
| Device protocols and platform adapters | `YFex.Devices` | [`YFex.Devices/AGENTS.md`](../../../YFex.Devices/AGENTS.md) |
| Cross-platform UI controls | `YFex.UI` | [`YFex.UI/AGENTS.md`](../../../YFex.UI/AGENTS.md) |

Other `YFex.*` folders may be infrastructure, product-specific libraries, experiments, or planned work. Locate their solution and symbols before assigning ownership. Do not pull business modules (`YFex.Farma`, `YFex.Finance`, `YFex.Fiscal`, `YFex.Payment`, etc.) into a reusable infrastructure contract.

`YFex.Axamly` contains design-time completion and preview tooling; `YFex.LoggY` is an Avalonia logging app; `YFex.Brain` currently exposes an entity model. `YFex.Avalonia` did not load as a RoslynMcp solution in this survey, while `YFex.UI` has the documented cross-framework control solution. Treat these as task-specific locations rather than default owners of framework contracts.

## Boundaries that repeatedly caused mistakes

- **Persistence and Data serve different purposes.** Persistence is app-state storage and offline durability; Data is database access, SQL, mapping, and migration. Put bridging adapters at their boundary.
- **Authentication proves who; Authorization decides what.** Authentication tokens carry identity facts (subject, session, methods, assurance), never permissions.
- **Authorization makes decisions; Capabilities describes actions.** A capability manifest can read permission metadata but does not grant access.
- **Logging and Audit differ.** Libraries probe logging through `YFex.Diagnostics` in core; composition roots install `YFex.Logging`. Audit records durable, tamper-evident facts through `YFex.Audit.Public` contracts and its engine.
- **Networking discovers candidates; Installations decides trust.** Presence on a LAN never proves tenant membership. Trusted time belongs to `YFex.Time`; platform clock reads alone are insufficient for security decisions.
- **Optional engines stay optional.** Where a library already ships a `*.Public` contracts package, consumers reference it; new libraries add one only when another library needs the contracts without the engine. Platform-specific behavior belongs in `.Windows`, `.MacOS`, or `.Linux` packages.

## Current documentation routes

- Ecosystem status, dependency map and known inconsistencies: [`docs/atlas`](../atlas/README.md) (`atlas.json` curated, `atlas.generated.json` scanned by `C:\Dev\Scripts\yfexAtlas.cs`). Update a component's status there when a phase lands.
- Public caller guides: [`docs/index.md`](../index.md), then only the relevant `docs/public/<topic>.md`.
- Contributor design and acceptance: `docs/internal/<topic>/README.md`, then its phase/acceptance file. Some docs describe proposals or older progress; inspect the target repo's `README.md` and current symbols before calling a feature complete.
- Generator architecture: [`docs/internal/source-generators.md`](../internal/source-generators.md). Check current generator APIs semantically before copying old examples.
- Running code on Android (headless emulator, USB handhelds, `androidKit probe`): [`android-testing.md`](android-testing.md).
- Running code on Linux (WSL2 `yfex-linux` + Docker distro matrix, swtpm, Secret Service, `linuxKit verify-crypto`): [`linux-testing.md`](linux-testing.md).
- Former full agent file: [`AGENTS archive`](AGENTS-archive-2026-10-09.md). It preserves detailed examples, but their build matrix, status notes, and API examples may be stale.

## Efficient task input

Give the target repo/assembly and the intended caller; state the invariant, allowed dependencies, performance budget if measured, API shape if fixed, and what counts as done. A short example of the desired call site and one counterexample from this ecosystem are more useful than a full source dump. For a bug, name the symbol and observed failure; the agent can discover the surrounding code through the RoslynMcp tools.
