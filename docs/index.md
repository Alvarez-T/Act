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
| [UI Abstractions](public/ui-abstractions.md) | YFex.UI.Abstractions, YFex.Mvvm | Dialogs, message boxes, toasts, notifications, PageViewModel hierarchy |
| [Core Utilities](public/core.md) | YFex, YFex.Data | Percentual, enum extensions, match extensions, collections, SQL query builder |

### Internal Documentation

For framework contributors working on YFex internals.

| Document | What It Covers |
|---|---|
| [State Internals](internal/state-internals.md) | Property ID allocation, bitmap dispatch, activation cascade, MVVM marshaling, generator pipelines |
| [State.History Internals](internal/state-history-internals.md) | Undo stack data structures, delta recording, coalescing, persistence serialization |
| [NavigatR Internals](internal/navigatr-internals.md) | Navigator state machine, pool eviction, prefetch lifecycle, return value plumbing, generator emission |
| [CQRS Internals](internal/cqrs-internals.md) | Registry build process, expression compilation, group expansion, analyzer implementation |
| [Messaging Internals](internal/messaging-internals.md) | Event bus buckets, outbox replay, Fusion integration, RPC bridging, storage backends |
| [Source Generators](internal/source-generators.md) | All five generator pipelines: architecture, models, emission patterns, diagnostics, performance |

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
```

---

## Where to Start

**Building a desktop/mobile MVVM app?**
Start with [Reactive State](public/state.md) to learn `[Observable]`, `[Computed]`, and `[StateCommand]`. Then read [Navigation](public/navigatr.md) for routing between pages, and [UI Abstractions](public/ui-abstractions.md) for dialogs and toasts.

**Building a client-server app with offline support?**
Start with [CQRS](public/cqrs.md) to define your queries and commands, then [Messaging & Offline](public/messaging.md) to set up the outbox, client cache, and Fusion integration.

**Adding undo/redo to an existing app?**
Read [Undo & Redo](public/state-history.md). It builds on top of the reactive state engine, so familiarity with `[Observable]` properties helps.

**Contributing to the framework?**
Read the [Source Generators](internal/source-generators.md) overview first, then dive into the specific internal doc for the subsystem you're working on.

---

## Build & Test

```bash
dotnet build yfex.slnx
dotnet test yfex.slnx
```

Most libraries multi-target `net8.0;net9.0`. Source generators target `netstandard2.0`.
