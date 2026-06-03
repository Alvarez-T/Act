# YFex Framework

**Solution:** `yfex.slnx` — A modular .NET framework for building reactive, offline-capable MVVM applications with CQRS, real-time messaging, and type-safe navigation.

## Solution Map

```
src/
├── YFex/                              Core primitives, collections, extensions
├── UI/
│   ├── YFex.UI.Abstractions/          IDialog, IMessageBox, IToast, INotification, union types
│   └── YFex.Mvvm/                     PageViewModel, ViewModel base classes (bridges State + NavigatR)
├── State/
│   ├── YFex.State/                    Reactive state engine ([Observable], [Computed], [StateCommand], etc.)
│   ├── YFex.State.Generator/          Source generator for State attributes
│   ├── YFex.State.Mvvm/              MvvmStateObject (INotifyPropertyChanged bridge)
│   ├── YFex.State.Collections/        StateList<T> — observable pooled list
│   ├── YFex.State.Blazor/             StateComponent<TViewModel> for Blazor
│   ├── YFex.State.History/            Undo/redo system (UndoContext, UndoManager)
│   ├── YFex.State.History.Generator/  Source generator for [Undoable]
│   ├── YFex.State.History.Persistence/ MemoryPack serialization for undo snapshots
│   ├── YFex.State.History.Testing/    Fluent assertions for undo tests
│   ├── YFex.State.Testing/            StateRecorder<T> for state change assertions
│   └── YFex.State.Smoke/             Smoke tests (not a library)
├── Navigation/
│   ├── YFex.NavigatR/                 Type-safe async navigation framework
│   ├── YFex.NavigatR.SourceGenerator/ Route + prefetch code generation
│   └── YFex.NavigatR.Tests/           Navigation tests
├── Cqrs/
│   ├── YFex.Cqrs/                     CQRS contracts, configuration builders, compiled registry
│   ├── YFex.Cqrs.SourceGenerator/     Static call helpers, DI registration, RPC contract generation
│   ├── YFex.Cqrs.Analyzer/            Roslyn diagnostics (YFCACHE, YFQUE, YFINV)
│   └── YFex.Cqrs.SourceGenerator.Test/ Generator tests
├── Messaging/
│   ├── YFex.Messaging/                IEventBus, [Live], [Subscribe<>], MessagingHost
│   ├── YFex.Messaging.Generator/      Source generator for [Live] / [Subscribe<>]
│   ├── YFex.Messaging.Fusion/         FusionLiveState<T>, FusionStateBinding<T>
│   ├── YFex.Messaging.Rpc/            Offline outbox, client cache, sync, dispatchers
│   ├── YFex.Messaging.Rpc.Sqlite/     SQLite storage backend
│   ├── YFex.Messaging.Rpc.IndexedDb/  IndexedDB storage backend (Blazor WASM)
│   ├── YFex.Messaging.Rpc.Encryption/ AES-GCM encryption decorator
│   ├── YFex.Messaging.Rpc.Wolverine/  Wolverine handler/dispatcher adapter
│   └── YFex.Messaging.Tests/          Integration tests
├── Database/
│   └── YFex.Data/                      SQL QueryBuilder (Dapper integration)
└── Persistence/
    ├── YFex.Persistence/               Persistence abstractions
    ├── YFex.Persistence.Generator/     Persistence source generator
    └── YFex.Persistence.FileSystem/    File system persistence backend
```

## Dependency Layers (bottom → top)

```
Layer 0: YFex (core primitives)
Layer 1: YFex.UI.Abstractions, YFex.State, YFex.State.Collections
Layer 2: YFex.State.Mvvm, YFex.State.History, YFex.NavigatR, YFex.Cqrs
Layer 3: YFex.Mvvm, YFex.Messaging
Layer 4: YFex.Messaging.Fusion, YFex.Messaging.Rpc
Layer 5: YFex.Messaging.Rpc.Sqlite/IndexedDb/Encryption/Wolverine
```

## Key Architectural Principles

1. **Source generators over reflection.** Every framework feature uses Roslyn incremental generators. No runtime reflection on hot paths. AOT-compatible.
2. **Three-phase pipeline.** Registration → compilation → runtime. All metadata is collected, validated, and pre-compiled into frozen lookup tables at startup.
3. **Zero-allocation hot paths.** `FrozenDictionary<Type, Policy>` lookups, `PropertyBitmap64` for batch dispatch, `ReadOnlySpan<T>` iteration, pooled arrays.
4. **Discriminated unions via `[Union]` attribute.** Result types, navigation results, and dialog outcomes use struct unions for type-safe exhaustive matching without boxing.
5. **Activation lifecycle.** `StateObject.Activate()/Deactivate()` cascades through the object graph. NavigatR drives activation via `OnNavigation`/`OnSuspend`/`OnResume`.
6. **Offline-first CQRS.** Commands marked `IQueueable` are enqueued when offline and replayed on reconnect. Queries marked `ICacheable` serve from local storage when disconnected.

## Build & Test

```bash
dotnet build yfex.slnx
dotnet test yfex.slnx
```

Multi-targeting: most libraries target `net8.0;net9.0`. Source generators target `netstandard2.0`.

## Source Generator Projects

| Generator | Triggers on | Emits |
|---|---|---|
| `YFex.State.Generator` | `[Observable]`, `[Computed]`, `[StateCommand]`, `[ReactsTo]`, `[Poll]`, etc. | Property setters, computed deps, command wrappers, activation cascade |
| `YFex.State.History.Generator` | `[Undoable]`, `[UndoContext]`, `[UndoableCollection]` | Delta recording, undo context fields, batch observers |
| `YFex.NavigatR.SourceGenerator` | `[Route]`, `[Prefetch]` | Route records, OnNavigation bridges, result plumbing, registration |
| `YFex.Cqrs.SourceGenerator` | `IQuery<T>`, `ICommand`, `IEvent` nested records + `IAggregateConfiguration<T>` | Static call helpers, AddYFexConfigurations, RPC contracts (YFEX_RPC) |
| `YFex.Messaging.Generator` | `[Live]`, `[Subscribe<T>]` | Live state properties, event subscriptions, lifecycle wiring |

## Conventions

- **Attributes drive behavior.** Properties are `[Observable]`, methods are `[StateCommand]`, events are `[Subscribe<T>]`. The generators emit all glue code.
- **Partial classes required.** Any class using generator attributes must be `partial`.
- **`StateObject` is the base.** ViewModels inherit `StateObject` → `MvvmStateObject` → `ViewModel` → `PageViewModel`. Each layer adds capabilities.
- **Configuration over attributes for CQRS.** Validation, authorization, caching, invalidation rules live in `IAggregateConfiguration<T>` classes, not on message records. Records carry only marker interfaces (`IQueueable`, `ICacheable`).
- **`ref struct` scopes for correctness.** `BatchScope`, `UndoTransactionScope`, `UndoSuspendScope` are ref structs preventing async misuse.

## Common Tasks

| Task | Approach |
|---|---|
| Add observable property | `[Observable] public partial string Name { get; set; }` on a `StateObject` subclass |
| Add async command | `[StateCommand] async Task DoSomethingAsync(CancellationToken ct)` |
| React to property change | `[ReactsTo(nameof(Name))] void OnNameChanged()` |
| Add undo support | `[Undoable] [Observable] public partial string Name { get; set; }` |
| Navigate to a page | `await navigator.NavigateTo(new MyRoute())` |
| Subscribe to event | `[Subscribe<MyEvent>] void OnMyEvent(in MyEvent e)` |
| Define CQRS query | Nested record `public partial record GetXQuery(int Id) : IQuery<X>` |
| Configure query caching | `b.Query<GetXQuery, X>().Cacheable().ScopedByUser()` in configuration class |

## Documentation

See `docs/` for the full wiki:
- `docs/public/` — Public API guides (capabilities, patterns, usage)
- `docs/internal/` — Internal implementation details
