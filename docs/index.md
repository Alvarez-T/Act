# YFex Framework

YFex is a modular .NET framework for building reactive, offline-capable MVVM applications. It combines a source-generator-driven reactive state engine, type-safe navigation, CQRS with offline queueing, and real-time messaging into a cohesive stack that runs on .NET 8+ and is fully AOT-compatible.

Every framework feature uses Roslyn incremental source generators instead of runtime reflection. All metadata is collected, validated, and pre-compiled into frozen lookup tables at startup. Hot paths are zero-allocation.

---

## Architecture

```
Layer 5  Storage Backends
         YFex.Messaging.Rpc.Sqlite / IndexedDb / Encryption / Wolverine
         ─────────────────────────────────────────────────────────────────
Layer 4  Offline & RPC
         YFex.Messaging.Rpc (Outbox, Client Cache, Sync, Dispatchers)
         YFex.Messaging.Fusion (FusionLiveState, FusionStateBinding)
         ─────────────────────────────────────────────────────────────────
Layer 3  Application Services
         YFex.Mvvm (PageViewModel, ViewModel — bridges State + NavigatR)
         YFex.Messaging (IEventBus, [Live], [Subscribe<T>])
         ─────────────────────────────────────────────────────────────────
Layer 2  Domain Frameworks
         YFex.State.Mvvm (INotifyPropertyChanged bridge)
         YFex.State.History (Undo/Redo: UndoContext, UndoManager)
         YFex.NavigatR (Type-safe async navigation, [Route], Prefetch)
         YFex.Cqrs (IQuery<T>, ICommand, Configuration API)
         ─────────────────────────────────────────────────────────────────
Layer 1  Reactive Core
         YFex.State ([Observable], [Computed], [StateCommand], [ReactsTo])
         YFex.State.Collections (StateList<T>)
         YFex.UI.Abstractions (IDialog, IMessageBox, IToast)
         ─────────────────────────────────────────────────────────────────
Layer 0  Primitives
         YFex (SpecializedDictionary, Percentual, Unit, Extensions)
```

Each layer depends only on the layers below it. Source generator projects (`netstandard2.0`) sit alongside their runtime libraries but have no runtime dependency on them.

Three families extend this stack from sibling repos: **YFex.Resilience** (a Layer-1 policy vocabulary consumed by CQRS and orchestration; Polly is an internal detail behind a seam), **YFex.Orchestration** (a Layer-5 saga / process-manager engine built on CQRS, Compensation, Resilience, Persistence, and Messaging), and **YFex.EventSourcing** (an event-sourcing write model built on CQRS, Persistence and Messaging — aggregates whose state is an append-only stream, with an in-house AoT store for client/offline and Marten for the server). All are AOT-clean and source-generated. See [Resilience](public/resilience.md), [Orchestration](public/orchestration.md) and [Event Sourcing](public/event-sourcing.md).

---

## Documentation

### Public API Guides

For .NET developers building applications with YFex.

| Guide | Libraries | What You'll Learn |
|---|---|---|
| [Reactive State](public/state.md) | YFex.State, YFex.State.Mvvm, YFex.State.Collections, YFex.State.Blazor | Observable properties, computed values, commands, batch updates, validation |
| [Undo & Redo](public/state-history.md) | YFex.State.History, YFex.State.History.Persistence, YFex.State.History.Testing | Undo/redo with coalescing, transactions, navigation boundaries, persistence |
| [Navigation](public/navigatr.md) | YFex.NavigatR | Type-safe routing, prefetch, return values, pool eviction, history policies |
| [CQRS](public/cqrs.md) | YFex.Cqrs, YFex.Cqrs.SourceGenerator, YFex.Cqrs.Analyzer | Queries, commands, events, validation, authorization, caching, offline queueing |
| [Messaging & Offline](public/messaging.md) | YFex.Messaging, YFex.Messaging.Fusion, YFex.Messaging.Rpc + backends | Event bus, live state, offline outbox, client cache, Fusion integration |
| [Resilience](public/resilience.md) | YFex.Resilience, YFex.Resilience.Polly | Retry, timeout, circuit breaker, rate limiter, bulkhead, fallback, hedging — one policy vocabulary over Polly |
| [Orchestration](public/orchestration.md) | YFex.Orchestration + Generator, .Rpc, .Rpc.Wolverine | Sagas, process managers, reverse-order compensation, durable timeouts, event correlation, dead-lettering |
| [Event Sourcing](public/event-sourcing.md) | YFex.EventSourcing + Generator, .Marten | Event-sourced aggregates, fold/replay, `[AggregateCommandHandler]` ↔ CQRS, projections, snapshots, offline sync, Marten server |
| [UI Abstractions](public/ui-abstractions.md) | YFex.UI.Abstractions, YFex.Mvvm | Dialogs, message boxes, toasts, notifications, PageViewModel hierarchy |
| [Core Utilities](public/core.md) | YFex, YFex.Data | Percentual, enum extensions, match extensions, collections, SQL query builder |
| [System & Platform](public/system.md) | YFex.System, YFex.System.Windows | Consent-gated telemetry, privacy scrubbing, platform/environment capture |
| [Security & Network Intelligence](public/security.md) | YFex.Security + Windows.Security, Storage, Capture, Analysis, Service, Cli (+ Hooking/Proxy/ApiReverser) | Passive DNS/TCP/TLS/process capture, JA3, baselines, anomaly detection, app-layer capture & API reversing |

### Internal Documentation

For framework contributors working on YFex internals.

| Document | What It Covers |
|---|---|
| [State Internals](internal/state-internals.md) | Property ID allocation, bitmap dispatch, activation cascade, MVVM marshaling, generator pipelines |
| [State.History Internals](internal/state-history-internals.md) | Undo stack data structures, delta recording, coalescing, persistence serialization |
| [NavigatR Internals](internal/navigatr-internals.md) | Navigator state machine, pool eviction, prefetch lifecycle, return value plumbing, generator emission |
| [CQRS Internals](internal/cqrs-internals.md) | Registry build process, expression compilation, group expansion, analyzer implementation |
| [Messaging Internals](internal/messaging-internals.md) | Event bus buckets, outbox replay, Fusion integration, RPC bridging, storage backends |
| [Resilience Internals](internal/resilience-internals.md) | Overlay value type, frozen resource/fallback registries, pipeline caching, Polly v8 composition & deviations |
| [Orchestration Internals](internal/orchestration-internals.md) | Saga forward-run & compensation, StepBitmap64, PM runtime, delay-scheduler, generator emission, broker/inbox/DLQ |
| [Orchestration + Resilience Design](internal/orchestration-resilience-design.md) | Original design rationale, layering, AoT strategy, phasing (P0–P3), locked decisions |
| [Event Sourcing Internals](internal/event-sourcing-internals.md) | In-house `IKeyValueStore` store (`es:` ordinal keys), aggregate fold serialization, generator emission, session optimistic append, projection subscriptions, snapshot cadence, sync/rebase |
| [Event Sourcing Design](internal/event-sourcing-design.md) | Proposed ES layer: aggregate domain model, `[EventSourced]`/`[AggregateCommandHandler]` ↔ CQRS, projections, offline sync, Marten provider |
| [Source Generators](internal/source-generators.md) | All generator pipelines: architecture, models, emission patterns, diagnostics, performance |

---

## Installation

Add the packages you need. Each feature is a separate NuGet package.

```bash
# Core primitives and extensions
dotnet add package YFex

# Reactive state engine (includes source generator automatically)
dotnet add package YFex.State

# MVVM bridge (INotifyPropertyChanged)
dotnet add package YFex.State.Mvvm

# Undo/redo
dotnet add package YFex.State.History

# Type-safe navigation
dotnet add package YFex.NavigatR

# CQRS
dotnet add package YFex.Cqrs

# Messaging and event bus
dotnet add package YFex.Messaging

# Offline-capable RPC with client cache and outbox
dotnet add package YFex.Messaging.Rpc

# Storage backends (pick one)
dotnet add package YFex.Messaging.Rpc.Sqlite      # Desktop / server
dotnet add package YFex.Messaging.Rpc.IndexedDb    # Blazor WASM

# Full MVVM ViewModel stack
dotnet add package YFex.Mvvm

# Resilience policies (retry, timeout, circuit breaker, …) + Polly engine
dotnet add package YFex.Resilience
dotnet add package YFex.Resilience.Polly

# Sagas & process managers (multi-step transactions, compensation, timeouts)
dotnet add package YFex.Orchestration
dotnet add package YFex.Orchestration.Rpc            # cross-service broker + DLQ (server)
```

---

## Where to Start

**Building a desktop/mobile MVVM app?**
Start with [Reactive State](public/state.md) to learn `[Observable]`, `[Computed]`, and `[StateCommand]`. Then read [Navigation](public/navigatr.md) for routing between pages, and [UI Abstractions](public/ui-abstractions.md) for dialogs and toasts.

**Building a client-server app with offline support?**
Start with [CQRS](public/cqrs.md) to define your queries and commands, then [Messaging & Offline](public/messaging.md) to set up the outbox, client cache, and Fusion integration.

**Coordinating multi-step transactions or long-running workflows?**
Read [Orchestration](public/orchestration.md) for sagas (atomic multi-step with reverse-order compensation) and process managers (event-driven state machines with durable timeouts). Pair it with [Resilience](public/resilience.md) to protect the commands they dispatch (retry, timeout, circuit breaker, fallback).

**Adding undo/redo to an existing app?**
Read [Undo & Redo](public/state-history.md). It builds on top of the reactive state engine, so familiarity with `[Observable]` properties helps.

**Collecting telemetry or reading the host environment?**
Read [System & Platform](public/system.md) for consent-gated telemetry and platform capture. For network/process monitoring, anomaly detection, and (authorized) app-layer traffic capture, see [Security & Network Intelligence](public/security.md).

**Contributing to the framework?**
Read the [Source Generators](internal/source-generators.md) overview first, then dive into the specific internal doc for the subsystem you're working on.

---

## Build & Test

```bash
dotnet build yfex.slnx
dotnet test yfex.slnx
```

Most libraries multi-target `net8.0;net9.0`. Source generators target `netstandard2.0`.
