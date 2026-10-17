# YFex infrastructure map for agents

Use this file only when a task crosses package or repository boundaries. This is a navigation index, not an API reference or a claim that every design phase is implemented. Verify current source with RoselYn.MCP and current phase logs before editing.

## Choose the owner

| Need | Implementation owner | First document |
|---|---|---|
| Reusable types named by several libraries | `YFex/src/YFex` | [`docs/public/core.md`](../public/core.md) |
| Local app-state storage, durable queues, caches | `YFex/src/Persistence` | [`src/Persistence/README.md`](../../src/Persistence/README.md) |
| Database commands, SQL, mapping, bulk writes, data migration | `YFex.Data` | [`YFex.Data/README.md`](../../../YFex.Data/README.md) |
| Scope identity and scoped overrides | `YFex.Tenancy` | [`YFex.Tenancy/AGENTS.md`](../../../YFex.Tenancy/AGENTS.md) |
| Scope-specific implementation selection | `YFex.CustomLogic` | [`YFex.CustomLogic/AGENTS.md`](../../../YFex.CustomLogic/AGENTS.md) |
| Shape validation, pure business-rule evaluation | `YFex/src/Validation` | [`validation/README.md`](../internal/validation/README.md) |
| CQRS contracts and dispatch | `YFex/src/Cqrs` | [`docs/public/cqrs.md`](../public/cqrs.md) |
| Event bus, offline outbox, RPC | `YFex/src/Messaging` | [`docs/public/messaging.md`](../public/messaging.md) |
| Reactive state, navigation, framework-neutral UI contracts | `YFex/src/State`, `src/Navigation`, `src/UI` | [`docs/index.md`](../index.md) |
| Permission evaluation and actor identity | `YFex.Authorization` | [`YFex.Authorization/README.md`](../../../YFex.Authorization/README.md) |
| Discoverable actions for search and AI | `YFex.Capabilities` | [`YFex.Capabilities/README.md`](../../../YFex.Capabilities/README.md) |
| Provider-neutral search orchestration | `YFex.Search` | [`YFex.Search/AGENTS.md`](../../../YFex.Search/AGENTS.md) |
| Outbound HTTP API clients, per-scope endpoints and credentials | `YFex.Web` | [`YFex.Web/docs/README.md`](../../../YFex.Web/docs/README.md) |
| Keys, cryptographic bytes, signatures, certificates | `YFex.Cryptography` | [`YFex.Cryptography/README.md`](../../../YFex.Cryptography/README.md) |
| Data classification and masking | `YFex.Privacy` | [`YFex.Privacy/README.md`](../../../YFex.Privacy/README.md) |
| Logs and operation telemetry | `YFex.Logging` | [`YFex.Logging/README.md`](../../../YFex.Logging/README.md) |
| Tamper-evident audit trails | `YFex.Audit` | [`YFex.Audit/README.md`](../../../YFex.Audit/README.md) |
| Trusted time and rollback detection | `YFex.Time` | [`YFex.Time/README.md`](../../../YFex.Time/README.md) |
| Network conditions, probes, discovery, Telnet, SNMP | `YFex.Networking` | [`YFex.Networking/README.md`](../../../YFex.Networking/README.md) |
| Installed-copy identity, enrollment, trusted peers | `YFex.Installations` | [`installations/README.md`](../internal/installations/README.md) |
| Offline modes, signed offline envelopes, site manifest and limits, allowances, signed rule data, site broker services | `YFex.Offline` (`.Central`, `.Rules`) | [`YFex.Offline/README.md`](../../../YFex.Offline/README.md), [`offline/README.md`](../internal/offline/README.md) |
| Retry, timeout, breaker policy vocabulary | `YFex.Resilience` | [`YFex.Resilience/README.md`](../../../YFex.Resilience/README.md) |
| Sagas, process managers, durable waits | `YFex.Orchestration` | [`YFex.Orchestration/README.md`](../../../YFex.Orchestration/README.md) |
| Event-sourced aggregate write model | `YFex.EventSourcing` | [`YFex.EventSourcing/README.md`](../../../YFex.EventSourcing/README.md) |
| Embedded scheduled jobs | `YFex.Jobs` | [`YFex.Jobs/README.md`](../../../YFex.Jobs/README.md) |
| Consent-gated machine telemetry | `YFex.System` | [`YFex.System/README.md`](../../../YFex.System/README.md) |
| Consent-aware profile signals | `YFex.Profiling` | [`YFex.Profiling/AGENTS.md`](../../../YFex.Profiling/AGENTS.md) |
| Security intelligence and inspection | `YFex.Security` | [`YFex.Security/AGENTS.md`](../../../YFex.Security/AGENTS.md) |
| Device protocols and platform adapters | `YFex.Devices` | [`YFex.Devices/AGENTS.md`](../../../YFex.Devices/AGENTS.md) |
| Cross-platform UI controls | `YFex.UI` | [`YFex.UI/AGENTS.md`](../../../YFex.UI/AGENTS.md) |

Other `YFex.*` folders may be infrastructure, product-specific libraries, experiments, or planned work. Locate their solution and symbols before assigning ownership. Do not pull business modules (`YFex.Farma`, `YFex.Finance`, `YFex.Fiscal`, `YFex.Payment`, etc.) into a reusable infrastructure contract.

`YFex.Axamly` contains design-time completion and preview tooling; `YFex.LoggY` is an Avalonia logging app; `YFex.Brain` currently exposes an entity model. `YFex.Avalonia` did not load as a RoselYn solution in this survey, while `YFex.UI` has the documented cross-framework control solution. Treat these as task-specific locations rather than default owners of framework contracts.

## Boundaries that repeatedly caused mistakes

- **Core is types first.** A shared `Hash256` or `ITimeSource` can live there; key storage, clocks, networking, authorization, and audit engines do not.
- **Validation and Rules stay domain neutral.** `Money`, `Tenant`, and `Store` may be application values, never built into the general rule engine's core contract. Use a type extension point when the app decides the value domain.
- **Persistence and Data serve different purposes.** Persistence is app-state storage and offline durability; Data is database access, SQL, mapping, and migration. Put bridging adapters at their boundary.
- **Authorization makes decisions; Capabilities describes actions.** A capability manifest can read permission metadata but does not grant access.
- **Logging and Audit differ.** Libraries probe logging through `YFex.Diagnostics` in core; composition roots install `YFex.Logging`. Audit records durable, tamper-evident facts through `YFex.Audit.Public` contracts and its engine.
- **Networking discovers candidates; Installations decides trust.** Presence on a LAN never proves tenant membership. Trusted time belongs to `YFex.Time`; platform clock reads alone are insufficient for security decisions.
- **Optional engines stay optional.** Consumers use `*.Public` or abstraction packages. Platform-specific behavior belongs in `.Windows`, `.MacOS`, or `.Linux` packages when provided.
- **Preview C# unions are context dependent.** The `union` declaration may store value-type cases through `object`; the project's `[Union]` tagged struct is the nonboxing path where performance matters. Verify actual SDK and pattern behavior before migrating.

## Current documentation routes

- Public caller guides: [`docs/index.md`](../index.md), then only the relevant `docs/public/<topic>.md`.
- Contributor design and acceptance: `docs/internal/<topic>/README.md`, then its phase/acceptance file. Some docs describe proposals or older progress; inspect the target repo's `README.md` and current symbols before calling a feature complete.
- Generator architecture: [`docs/internal/source-generators.md`](../internal/source-generators.md). Check current generator APIs semantically before copying old examples.
- Former full agent files: [`AGENTS archive`](AGENTS-archive-2026-10-09.md) and [`CLAUDE archive`](CLAUDE-archive-2026-10-09.md). They preserve detailed examples, but their build matrix, status notes, and API examples may be stale.

## Efficient task input

Give the target repo/assembly and the intended caller; state the invariant, allowed dependencies, performance budget if measured, API shape if fixed, and what counts as done. A short example of the desired call site and one counterexample from this ecosystem are more useful than a full source dump. For a bug, name the symbol and observed failure; the agent can discover the surrounding code through RoselYn.MCP.
