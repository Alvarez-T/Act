# YFex infrastructure

`yfex.slnx` is the framework solution. Its core assembly holds reusable value types and contracts; engines and integrations live in separate projects or sibling `YFex.*` repositories. This codebase is under active development. Treat design docs and old status notes as intent until checked against current code.

## Rules

This is an infrastructure repository: [docs/agent/rules/00-start-here.md](docs/agent/rules/00-start-here.md), then `all-projects.md` and `infrastructure.md`. For C#, the `yfex-csharp` skill.

Core-specific: put a cross-cutting **type or contract** in `src/YFex` only when more than one boundary must name it; behaviour goes to the owning library (`Hash256` here, hashing in `YFex.Cryptography`; trusted-time contracts here, the clock in `YFex.Time`).

## Where work belongs

| Concern | Owner |
|---|---|
| Primitives, shared value types, core contracts | `src/YFex` |
| Reactive state, MVVM, navigation, UI contracts | siblings `YFex.State`, `YFex.Mvvm`, `YFex.NavigatR`, `YFex.UI` |
| Message texts per culture: catalog engine, language-file generator | sibling `YFex.Localization` (key types and contracts stay in `src/YFex/Localization`) |
| Validation and pure rules | siblings `YFex.Validation`, `YFex.Rules`, `YFex.Expressions`; gather I/O into an immutable situation before sync evaluation |
| Commands, queries, messaging, offline RPC | siblings `YFex.Cqrs`, `YFex.Messaging` |
| Local app-state storage | sibling `YFex.Persistence` |
| Database access and migration | sibling `YFex.Data` |
| Keys, signatures, sealed bytes | sibling `YFex.Cryptography` |
| Caller permissions and capability discovery | siblings `YFex.Authorization`, `YFex.Capabilities` |
| Logs, audit, privacy | siblings `YFex.Logging`, `YFex.Audit`, `YFex.Privacy` |
| Trusted time, network mechanics, installation identity | siblings `YFex.Time`, `YFex.Networking`, `YFex.Installations` |
| Policies, sagas, event streams, jobs | siblings `YFex.Resilience`, `YFex.Orchestration`, `YFex.EventSourcing`, `YFex.Jobs` |

For other infrastructure siblings and exact documentation routes, read [the infrastructure map](docs/agent/infrastructure-map.md): in full before any design or new capability ([ecosystem-fit](docs/agent/rules/ecosystem-fit.md)), otherwise when the task crosses a boundary.

## Documentation

- Read [docs/index.md](docs/index.md) for the relevant public guide; read the matching `docs/internal/` design and phase log only for internal changes. Sibling `README.md` files describe their package split.
- `docs/agent/*-archive-2026-10-09.md` preserves the former full agent files (partly stale); use it only when a missing historical detail matters.

`CLAUDE.md` points here; maintain instructions in one place.
