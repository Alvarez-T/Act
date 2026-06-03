# Source Generators

This document covers the architecture, pipeline design, model structure, emission patterns, diagnostic strategy, and performance considerations shared across all five YFex source generators. Read this when you're building a new generator, debugging incremental caching issues, or understanding why a specific code pattern is emitted.

---

## Architectural Principles

All five generators follow the same architecture:

1. **Roslyn incremental generators** (`IIncrementalGenerator`) — never the legacy `ISourceGenerator`
2. **`ForAttributeWithMetadataName`** as the entry predicate — never `CreateSyntaxProvider` with broad predicates
3. **`EquatableArray<T>`** for structural equality on all model collections — required for incremental caching
4. **Three-stage pipeline**: Parse → Group → Emit

> **Why `ForAttributeWithMetadataName`?** It's the most efficient entry point Roslyn provides. The compiler maintains an index of attributes, so the predicate runs in O(1) per attributed symbol instead of walking the entire syntax tree. Broad `CreateSyntaxProvider` predicates (e.g., "all class declarations") cause the generator to re-run on every keystroke, destroying IDE performance.

---

## Common Patterns

### Pipeline Shape

```
ForAttributeWithMetadataName("FullyQualifiedAttributeName")
  → Predicate: IsCandidateSyntax(SyntaxNode) — quick syntax-only check
  → Transform: Transform(GeneratorAttributeSyntaxContext) → RawModel?
  → Diagnostics: report errors via context.ReportDiagnostic
  → Group: GroupByClass(ImmutableArray<RawModel>) → ClassModel
  → Emit: Emit(SourceProductionContext, ClassModel) → generated source
```

> **📌 Note:** `IsCandidateSyntax` runs on the **syntax tree** (fast, no semantic model). `Transform` runs on the **semantic model** (slower, but has type information). Keeping the predicate cheap prevents IDE lag.

### EquatableArray\<T\>

Every generator defines its own `EquatableArray<T>`:

```csharp
readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    // Wraps T[] with structural equality
    // Element-wise comparison for Equals/GetHashCode
}
```

> **Why copy-paste instead of sharing?** Source generators target `netstandard2.0` and are loaded as analyzers. They can't reference shared libraries without complex packaging. Each generator project includes its own copy.

> **⚠️ Warning:** If any model field uses a raw `T[]` instead of `EquatableArray<T>`, the incremental pipeline will re-emit on every keystroke because array reference equality always returns `false`. This is the #1 source of "generator is slow" bugs.

### Naming Conventions

| Convention | Example |
|---|---|
| `*RawModel` | Per-attribute-instance model (before grouping) |
| `*ClassModel` | Grouped-by-class model (ready for emission) |
| `*Parser.Transform()` | Semantic analysis: attribute → model |
| `*Parser.GroupByClass()` | Merge raw models by (namespace, className) |
| `*Emitter.Emit()` | Model → generated C# source string |
| Generated files | `{Namespace}.{ClassName}.{Feature}.g.cs` |

---

## 1. YFex.State.Generator

**Triggers:** `[Observable]`, `[Computed]`, `[StateCommand]`, and 20+ additional attributes

**Two independent pipelines:**

### Observable Pipeline

| Stage | Input | Output |
|---|---|---|
| Candidate | `PropertyDeclarationSyntax` or `VariableDeclaratorSyntax` | Pass/fail |
| Transform | Semantic analysis: partial check, StateObject inheritance, type, equality strategy | `ObservablePropertyRawModel` |
| Group | Merge by (Namespace, ClassName) | `ObservableClassModel` |
| Emit | Per-class partial | Static descriptors, property setters, DispatchPending, MVVM cache, activation |

**Key decisions in Transform:**

| Decision | How It's Determined |
|---|---|
| Equality strategy | Auto-detected: `bool` → `DirectEquals`, `float/double` → `FloatNaN`, `string` → `StringOrdinal`, reference types → `DefaultEquals` |
| `ParentPropertyCount` | Walking the ancestor chain counting `[Observable]` properties |
| `IsMvvm` | Checking for `MvvmStateObject` in base types |
| `ParticipatesInActivation` | Type inherits `StateObject` or implements `IActivatable` (unless `[IgnoreActivation]`) |

> **💡 Tip:** `ParentPropertyCount` is critical for stable property ID allocation across inheritance. If a base class adds a new `[Observable]` property, all derived class IDs shift. This is intentional — it keeps the bitmap dispatch correct — but means adding properties to base classes triggers regeneration of all derived classes.

### Command Pipeline

| Stage | Input | Output |
|---|---|---|
| Candidate | `MethodDeclarationSyntax` | Pass/fail |
| Transform | Return type classification, CancellationToken detection, attribute options | `CommandMethodRawModel` |
| Group | Merge by (Namespace, ClassName) | `CommandClassModel` |
| Emit | Per-class partial | Fields, lazy property, nested command class |

**Return type classification:**

| Return Type | Generated Interface | Notes |
|---|---|---|
| `void` | `IStateCommand` | Sync command |
| `Task` | `IAsyncStateCommand` | Async, no result |
| `Task<T>` | `IAsyncStateCommand` | Async with typed result |
| `ValueTask` | `IAsyncStateCommand` | Zero-flicker fast path: skips `Executing` state if completed sync |
| `ValueTask<T>` | `IAsyncStateCommand` | Fast path + typed result |

> **Why the ValueTask fast path?** Many "async" commands complete synchronously (cache hit, validation-only). With `Task`, the command always transitions through `Executing` → `Succeeded`, causing two `PropertyChanged` events and a visible loading flicker. With `ValueTask`, if `IsCompletedSuccessfully` is true, the command skips `Executing` entirely — zero UI flicker.

---

## 2. YFex.State.History.Generator

**Triggers:** `[Undoable]`, `[UndoContext]`, `[UndoableCollection]`

**Two merged pipelines:**

| Pipeline | Attribute | Output |
|---|---|---|
| Undoable | `[Undoable]` on property/class | `UndoableRawModel` with property info |
| UndoContext | `[UndoContext]` on property | `UndoableRawModel` with context info |

Both streams are merged and grouped by class into `UndoableClassModel`.

**Emitted per class:**
1. Auto-created `UndoContext` fields (for Mode A/B scoping)
2. Static setter delegates per property (`Action<object, object?>`)
3. Lazy `UndoBatchObserver` subscription
4. `OnXxxChanging` partial implementations (delta recording with `IsReplaying`/`IsSuspended` guards)
5. Command wrapper properties (`XxxUndoCommand`, `XxxRedoCommand`)
6. `IUndoable` implementation (if single primary scope)

**Diagnostics:** YFEX0701–0709

> **📌 Note:** Class-level `[Undoable]` (Mode B) is processed by `TransformClassLevel`, which discovers all `[Observable]` properties on the class. In the current implementation (V1), only the first property is instrumented when applied at class level — a known limitation. V2 should use `SelectMany` after `ForAttributeWithMetadataName` to handle all properties.

---

## 3. YFex.NavigatR.SourceGenerator

**Triggers:** `[Route]`, `[Prefetch]`

**Single pipeline with three emission stages:**

| Stage | Output File |
|---|---|
| Route generation | `{Namespace}.{RouteName}Route.g.cs` |
| ViewModel partial | `{Namespace}.{ClassName}.NavigatR.g.cs` |
| Registration | `NavigatRRegistration.g.cs` |

**Model:** `RouteViewModel` — namespace, className, routeType/routeName, parameter type, prefetch methods list, result type.

**Key emission details:**
- If `[Route("name")]` (string form): generates a sealed record implementing `IRoute`
- If `Parameter` set: route record gets a constructor parameter; `OnNavigation` bridge extracts and passes typed parameter
- If `[Prefetch]` methods: renames original method to `*Core`, wraps with prefetch-aware interceptor that checks `_prefetchTask` before calling the core method
- If `INavigable<TResult>`: generates `TaskCompletionSource`, `Returns()`, `Cancel()`, `Deny()`, `WaitForResultAsync()`
- Multiple `[Prefetch]` methods with different `Task<T>` return types: each becomes a parameter in the generated `OnNavigation` partial

**Diagnostics:** NAV001–NAV007

> **⚠️ Warning:** NAV007 fires when multiple `[Prefetch]` methods return the same `Task<T>` type. Each return type must be unique because the generated `OnNavigation` partial method uses the return type to distinguish parameters (e.g., `OnNavigation(int parameter, ProductData fetchProduct, StockData fetchStock, CancellationToken ct)`).

---

## 4. YFex.Cqrs.SourceGenerator

**Triggers:** `IQuery<T>`, `ICommand`, `ICommand<T>`, `IEvent` (nested records), `IAggregateConfiguration<T>`

**Three pipelines (Pipeline C gated on `YFEX_RPC`):**

### Pipeline A: Static Call Helpers

Detects partial classes containing nested `Queries`/`Commands`/`Events` static classes with records implementing CQRS interfaces.

**Naming:** `GetCustomerByIdQuery` → `GetCustomerById()`. `CreateCustomerCommand` → `CreateCustomer()`. Events → `Raise{EventName}()`.

> **📌 Note:** If the user already declared a method with the generated name, the generator skips emission silently. This is intentional — the user can override the generated helper without conflicts or suppressions.

### Pipeline B: Configuration Registration

Scans for `IAggregateConfiguration<T>` implementations. Emits `AddYFexConfigurations()` extension.

### Pipeline C: RPC Contracts (YFEX_RPC)

Per aggregate:
1. `I{Aggregate}RpcContract : IComputeService` — interface with `[ComputeMethod]` on queries
2. `{Aggregate}RpcContractImpl` — server implementation forwarding to `IHandlerInvoker`
3. Registration partials — `fusion.AddServer<>()` / `FusionMessageBusBuilder` configuration

> **💡 Tip:** The `YFEX_RPC` symbol is injected by `YFex.Messaging.Rpc.targets` (shipped in the NuGet package's `build/` folder). Any project that references `YFex.Messaging.Rpc` gets the symbol automatically. You don't need to define it manually.

---

## 5. YFex.Messaging.Generator

**Triggers:** `[Subscribe<T>]`, `[Live]`

**Two independent pipelines:**

### Subscribe Pipeline

Detects methods with `[Subscribe<T>]` on `StateObject` or `MessagingHost` subclasses.

**Emitted:**
- Private adapter class implementing `IEventRecipient<T>` (sync: `void Method(in T)`) or `IAsyncEventRecipient<T>` (async: `ValueTask Method(T, CancellationToken)`)
- `OnActivateCascading()` override: subscribe to `EventBusProvider.Current`
- `OnDeactivateCascading()` override: dispose subscription tokens
- For `MessagingHost`: wired into `OnHostStarting()` instead of activation

> **📌 Note:** The adapter class holds a reference to the ViewModel instance. For `KeepAlive = false` (default), the adapter wraps the reference in `WeakReference<T>`. For `KeepAlive = true`, it holds a strong reference. The generated code picks the appropriate adapter variant based on the attribute parameter.

### Live Pipeline

Detects methods with `[Live]`.

**Emitted:**
- `ILiveState<T>` backing field
- Computed property returning `_liveState?.Value`
- Companion properties: `IsXLoading`, `XError`, `IsXStale`
- `RefreshXAsync()` method
- Activation lifecycle: create `ILiveState<T>` via `LiveStateProvider.Current.Create()` on activate, dispose on deactivate
- `DependsOn` wiring: subscribe to named observable properties, call `RecomputeAsync` on change
- Poll support: `Task.Delay(PollMs)` loop during activation

**Diagnostics:** YFLIV0001–0002

---

## Performance Considerations

All generators use `ForAttributeWithMetadataName` — the most efficient incremental predicate, avoiding full syntax tree walks.

| Optimization | Effect |
|---|---|
| `EquatableArray<T>` on all model collections | Prevents re-emission when source hasn't changed |
| `IsCandidateSyntax` as syntax-only predicate | Avoids semantic model creation for non-candidates |
| Per-class grouping before emission | One generated file per class instead of per-property |
| `ForAttributeWithMetadataName` | O(1) attribute lookup instead of O(N) syntax walk |

> **💡 Tip:** To debug incremental caching issues, add a `#pragma warning disable` comment to a generated file and check if it reappears on the next build. If it does, the model's `Equals` implementation has a bug — some field isn't participating in equality.
