# YFex infrastructure

`yfex.slnx` is the framework solution. Its core assembly holds reusable value types and contracts; engines and integrations live in separate projects or sibling `YFex.*` repositories. This codebase is under active development. Treat design docs and old status notes as intent until checked against current code.

## Boundaries

- Keep foundation APIs independent of any product or business domain. Retail, pharmacy, fiscal, money, tenant, and store examples belong in samples or adapters unless a type is independently reusable. Open sets of app concepts are app declared, not framework enums.
- Put a cross-cutting **type or contract** in `src/YFex` only when more than one boundary needs to name it. Put behavior in the owning library: core `Hash256` vs `YFex.Cryptography`, trusted-time contracts vs `YFex.Time`, network conditions vs `YFex.Networking`.
- Reference a library's `*.Public` or abstraction package when only its contract is needed. Composition roots opt into engines and platform implementations. Keep OS-specific code in platform projects.
- Keep library APIs AOT and trimming friendly. Prefer generated metadata and static registration over runtime reflection. Generator projects are separate from runtime projects.
- Use existing YFex value objects instead of primitive strings/numbers for domain identities. Prefer explicit names and type-level classification; avoid repeated member annotations.
- Hot paths: avoid LINQ, boxing, avoidable allocations, and unnecessary third-party packages. Use spans, pooled buffers, and `unsafe` when they improve a measured case. Benchmark relevant alternatives.
- For performance-sensitive discriminated results, prefer the established `[Union]` + `IUnion` tagged-struct pattern with typed `TryGetValue`; never read its boxing `Value` on a hot path. Native `union` can suit cold paths; confirm the project's convention and current preview behavior before changing an existing result shape.

## Where work belongs

| Concern | Owner |
|---|---|
| Primitives, shared value types, core contracts | `src/YFex` |
| Reactive state, MVVM, navigation, UI contracts | `src/State`, `src/UI`, `src/Navigation` |
| Validation and pure rules | `src/Validation`; gather I/O into an immutable situation before sync evaluation |
| Commands, queries, messaging, offline RPC | `src/Cqrs`, `src/Messaging` |
| Local app-state storage | `src/Persistence` |
| Database access and migration | sibling `YFex.Data` |
| Keys, signatures, sealed bytes | sibling `YFex.Cryptography` |
| Caller permissions and capability discovery | siblings `YFex.Authorization`, `YFex.Capabilities` |
| Logs, audit, privacy | siblings `YFex.Logging`, `YFex.Audit`, `YFex.Privacy` |
| Trusted time, network mechanics, installation identity | siblings `YFex.Time`, `YFex.Networking`, `YFex.Installations` |
| Policies, sagas, event streams, jobs | siblings `YFex.Resilience`, `YFex.Orchestration`, `YFex.EventSourcing`, `YFex.Jobs` |

For other infrastructure siblings and exact documentation routes, read [the infrastructure map](docs/agent/infrastructure-map.md) only when the task crosses a boundary.

## Working method

- Discover symbols with RoselYn.MCP `get_names` or `get_feature_slice`; use `get_minimal_context_for` for the symbol in question. Never infer signatures from a design document. If a RoselYn tool fails, use another semantic tool or report the limit.
- Read [docs/index.md](docs/index.md) for the relevant public guide; read the matching `docs/internal/` design and phase log only for internal changes. Sibling `README.md` files describe their package split.
- Before changing a public API, show caller-facing syntax, dependency direction, AOT/trim implications, and effect on consumers. Preserve shared code and optional integrations.
- Validate nontrivial C# edits with `dry_run_compile` before writing, then run affected build/tests and any relevant AOT or allocation gate. Do not claim a full solution build from a targeted check.
- Respect unrelated in-progress edits. `docs/agent/*-archive-2026-10-09.md` preserves the former full agent files; use it only when a missing historical detail matters.

`CLAUDE.md` points here; maintain instructions in one place.
