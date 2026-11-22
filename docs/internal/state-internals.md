# State Internals

This document covers the internal architecture of `YFex.State` and `YFex.State.Generator`: how observable properties are identified and dispatched, how activation cascades through object graphs, how MVVM bridging works, and how the source generator pipelines transform attributes into emitted code. Read this when you're modifying the state engine, the generator, or debugging property notification issues.

> **Status 2026-11-19:** only `[Observable]`, `[StateCommand]`, `[EqualityComparer]`, `[NotifyOnTaskCompletion]` and `[IgnoreActivation]` are generated here (plus `[Debounce]`/validation timing by YFex.Validation and `[Persist]` by YFex.Persistence). The other attributes in `Attributes.cs` do nothing yet: see the audit and completion plan in [state/](state/README.md).

---

## Property ID Allocation

Each `[Observable]` property gets a dense `uint` ID, starting at `ParentPropertyCount` (the count of observable properties in ancestor classes). Properties are sorted alphabetically for deterministic ordering across compilations. IDs index into `PropertyBitmap64` for batch dispatch.

> **Why alphabetical sorting?** Without deterministic ordering, incremental generator caching breaks — the emitted code would differ between compilations even when source hasn't changed, causing unnecessary IDE rebuilds.

```
class Base : StateObject
  [Observable] Age  → ID 0
  [Observable] Name → ID 1
  ParentPropertyCount = 0, PropertyCount = 2

class Derived : Base
  [Observable] Email → ID 2   (starts at ParentPropertyCount = 2)
  [Observable] Phone → ID 3
```

---

## Bitmap-Based Batch Dispatch

`PropertyBitmap32` / `PropertyBitmap64` are compact bitfields that track which properties changed during a batch update. The flow:

```
BeginUpdate() → increments _updateDepth
NotifyChanged(descriptor) → if _updateDepth > 0: set bit in _pendingMask
ExitUpdate() → decrements _updateDepth; if reaches 0: DispatchPending(_pendingMask)
```

`DispatchPending(mask)` uses `BitOperations.TrailingZeroCount` to drain set bits in O(set-bits), firing one notification per changed property. The generator emits a per-class override that chains from most-derived to base class.

> **Why bitmaps instead of a list?** A 64-bit mask fits 64 properties in a single `ulong`. Checking "did anything change?" is a single zero-test. Draining uses CPU intrinsics (`tzcnt`). No heap allocation, no list iteration, no hash lookups.

> **Warning:** `PropertyBitmap64` supports at most 64 observable properties per class hierarchy. **No diagnostic exists yet**: a 65th ID silently aliases bit 0 in a batch (defect B6, fixed in [state plan](state/README.md) ST-P0). A StateObject with 64+ observable properties should be decomposed.

---

## Activation Cascade

The generator emits `OnActivateCascading()` / `OnDeactivateCascading()` overrides that activate/deactivate all `[Observable]` properties whose type inherits `StateObject` or implements `IActivatable` (unless `[IgnoreActivation]`).

```csharp
// Generated code (simplified):
protected override void OnActivateCascading()
{
    base.OnActivateCascading();
    if (_address is IActivatable a) a.Activate();
    if (_items is IActivatable b) b.Activate();
    // nullable properties are null-checked before activation
    if (_optionalChild is IActivatable c) c.Activate();
}
```

> **Why cascade?** When a ViewModel activates (e.g., on navigation), all its child StateObjects need to start listening for changes, connect to event buses, etc. Without cascading, every ViewModel would need manual wiring.

`StateList<T>` activates each item implementing `IActivatable` on add, and deactivates on remove.

---

## MVVM Args Caching

Each concrete class that inherits `MvvmStateObject` gets a per-class static array of `PropertyChangedEventArgs` / `PropertyChangingEventArgs`, indexed by property ID.

```csharp
// Generated code (simplified):
private static readonly PropertyChangedEventArgs[] __changedArgs = new[]
{
    new PropertyChangedEventArgs("Age"),    // ID 0
    new PropertyChangedEventArgs("Name"),   // ID 1
};

protected override PropertyChangedEventArgs GetPropertyChangedArgs(uint id)
    => id < 2 ? __changedArgs[id] : base.GetPropertyChangedArgs(id);
```

The `GetPropertyChangedArgs(uint id)` override chains to `base` when the ID belongs to a parent class.

> **Why this design?** This replaces the old CRTP `StateObject<TSelf>` pattern and the `MvvmArgsCache<TSelf>` that caused `IndexOutOfRangeException` with inheritance. The per-class static array with chained overrides is both simpler and correct for deep hierarchies.

---

## SynchronizationContext Marshaling

`MvvmStateObject` captures `SynchronizationContext.Current` on construction. During batch flush:

- **On the captured context:** fire `PropertyChanged` events inline (no marshaling overhead).
- **Off-thread:** collect all `PropertyChangedEventArgs` into a buffer, then `Post` a single callback that fires them all on the UI thread.

> **Rule: one owner thread.** A state object is written and activated only on the thread that created it — the UI thread for view models. `[Observable]` setters, `Activate` and `Deactivate` check this in DEBUG (`StateObject.AssertOwnerThread` throws `InvalidOperationException`, "Cross-thread mutation is a programming error"; `OwnerThreadTests` pin it). In Release the check is compiled out, so a background write is not reported: it races with UI reads of the field and with `INotifyChanged` handlers, which run on the writing thread.
>
> The marshaling above protects **event delivery**, not the write: it routes notifications to the captured context and is not permission to mutate off-thread. Background work returns values and the owner thread applies them — post to the captured `SynchronizationContext` or the UI dispatcher. NavigatR's `OnLoaded` (load off-thread, apply on the owner thread) and `[Live]` state fed by Fusion already work this way.

---

## Feature Switches

`FeatureSwitches` provides compile-time toggleable features:

| Switch | Default | Effect |
|---|---|---|
| `EnableINotifyPropertyChangingSupport` | `true` | Controls whether `OnChanging` notifications fire `PropertyChangingEventHandler`. Disable to skip the overhead when no subscriber uses `INotifyPropertyChanging`. |

---

## Handler List

`StateObject` stores subscribers in a 2-slot inline array (`HandlerSlot`) for the common 1-2 subscriber case. Beyond 2 subscribers, it falls back to a dynamically allocated list.

```
0 subscribers: no allocation
1 subscriber:  slot[0] filled, slot[1] empty
2 subscribers: slot[0] + slot[1] filled
3+ subscribers: fallback to List<IChangedHandler>
```

> **Why 2 slots?** Profiling showed that the vast majority of StateObjects have at most 2 subscribers (the parent and one computed/reaction). The inline array avoids a `List<T>` heap allocation for this common case.

---

## SetField Variants

The generator picks the appropriate `SetField` overload based on the property declaration:

| Variant | Use Case |
|---|---|
| `SetField<T>(ref T, T, descriptor)` | Standard property setter |
| `SetField<T>(ref T, T, IEqualityComparer<T>, descriptor)` | Custom equality via `[EqualityComparer]` |
| `SetField<T>(T old, T new, Action<T>, descriptor)` | Callback-based setter (e.g., for model relay) |
| `SetField<TModel, T>(T old, T new, TModel, Action<TModel,T>, descriptor)` | Model relay — zero delegate allocation via static lambda + model capture |
| `SetFieldAndNotifyOnCompletion(ref TaskNotifier?, Task?, descriptor)` | Re-fires notification when the `Task` completes (for `[NotifyOnTaskCompletion]`) |

---

## Validation Integration

`ValidationBag` is lazy-initialized on first access. It stores `ValidationResult` per property name with O(1) error count tracking.

`MvvmStateObject` wires `ValidationBag.ValidationChanged` to `ErrorsChanged` (also lazy, only on first `Validation` property access). This means there's zero overhead for StateObjects that don't use validation.

---

## Generator Pipeline Architecture

### Observable Pipeline

```
ForAttributeWithMetadataName("YFex.State.ObservableAttribute")
  -> ObservableParser.IsCandidateSyntax (PropertyDeclarationSyntax | VariableDeclaratorSyntax)
  -> ObservableParser.Transform -> ObservablePropertyRawModel?
  -> ObservableParser.GroupByClass -> ObservableClassModel
  -> ObservableEmitter.Emit
```

**Key decisions in Transform:**

| Decision | How It's Determined |
|---|---|
| Equality strategy | Auto-detected from type: `bool` -> `DirectEquals`, `float/double` -> `FloatNaN`, `string` -> `StringOrdinal`, reference types -> `DefaultEquals` |
| `ParentPropertyCount` | Walking the ancestor chain counting `[Observable]` properties |
| `IsMvvm` | Checking for `MvvmStateObject` in base types |
| `ParticipatesInActivation` | Type inherits `StateObject` or implements `IActivatable` (unless `[IgnoreActivation]`) |

### Command Pipeline

```
ForAttributeWithMetadataName("YFex.State.StateCommandAttribute")
  -> CommandParser.IsCandidateSyntax (MethodDeclarationSyntax)
  -> CommandParser.Transform -> CommandMethodRawModel?
  -> CommandParser.GroupByClass -> CommandClassModel
  -> CommandEmitter.Emit
```

**Return type classification:** `void` -> `IStateCommand`, `Task` -> `IAsyncStateCommand`, `Task<T>` -> typed, `ValueTask` -> fast path (skips `Executing` state if synchronous), `ValueTask<T>` -> fast path + typed.

### Emitted Code Per Observable Property

1. **Static `ChangedNotification` descriptor** (cached, never re-allocated)
2. **`DispatchPending` override** — walks bitmap, fires descriptors in ID order
3. **Property setter** with equality check -> `NotifyChanging` -> field set -> `NotifyChanged`
4. **MVVM args cache entry** (if `MvvmStateObject`)
5. **Activation sync** (if property type is `IActivatable`)

### Emitted Code Per Command

1. `int __executing_*` field (lock-free gate via `Interlocked.CompareExchange`)
2. `CancellationTokenSource?` field (if `HasCancellationToken`)
3. Lazy `IAsyncStateCommand` property
4. Nested private command class implementing `IAsyncStateCommand` + `INotifyPropertyChanged`
5. `CommandStatus` tracking: `Idle -> Executing -> Succeeded / Faulted / Canceled`
6. **ValueTask fast path:** skips the `Executing` state entirely if the task completes synchronously — avoids two `PropertyChanged` fires for a no-op command
