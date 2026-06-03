# State.History Internals

This document covers the internal architecture of `YFex.State.History`: the undo stack data structure, delta recording mechanism, coalescing algorithm, collection undo support, persistence serialization, and integration with async commands and navigation boundaries. Read this when you're modifying undo behavior, debugging delta recording, or extending the persistence layer.

---

## Undo Stack Data Structure

The undo stack is a **doubly-linked list** of `UndoGroup` nodes. The redo stack is **singly-linked** (oldest-entry eviction isn't needed since redo is cleared on new edits).

```
Undo Stack (doubly-linked):
  [Group1] <-> [Group2] <-> [Group3]  <- top
                                         ↑ push here, pop here

  When MaxDepth exceeded: evict from head (Group1) in O(1)

Redo Stack (singly-linked):
  [Group4] -> [Group5]  <- top
```

> **Why a linked list instead of a `Stack<T>`?** A `Stack<T>` doesn't support O(1) eviction from the bottom. When `MaxDepth` is exceeded, we need to drop the oldest group without touching the rest of the stack. A doubly-linked list gives us O(1) for both push/pop at top and eviction at bottom.

Each `UndoGroup` contains an intrusive singly-linked chain of `UndoDeltaBase` nodes — zero allocation per delta beyond the delta object itself (no `List<T>`, no array).

---

## Object Pool

`UndoGroup` instances are pooled (max 16) to reduce GC pressure during rapid undo/redo cycling. When a group is evicted (max depth exceeded), it's returned to the pool after `Reset()`.

> **Note:** The pool size of 16 was chosen to match the typical `MaxDepth` range (10-50). In practice, most applications never create more than 16 groups in a single undo/redo burst, so the pool absorbs all allocations.

---

## Delta Recording

The generator emits `OnXxxChanging` partial implementations that call `UndoContext.RecordPropertyChange()`:

```csharp
// Generated code:
partial void OnNameChanging(string oldValue, string newValue)
{
    if (ctx.IsReplaying || ctx.IsSuspended) return;   // guard: don't record during undo/redo
    __EnsureUndoSubscribed();
    ctx.RecordPropertyChange(this, "Name", __undoSetter_Name,
        oldValue, newValue, DateTime.UtcNow.Ticks, mergeWindowMs);
}
```

> **Warning:** The `IsReplaying` guard is critical. Without it, undoing a change would record a new delta (the undo itself), creating an infinite loop. Similarly, `IsSuspended` prevents recording during programmatic bulk updates where undo tracking is intentionally disabled.

### Static Setter Delegates

Each class gets one static setter per undoable property:

```csharp
// Generated code:
private static readonly Action<object, object?> __undoSetter_Name =
    static (owner, v) => ((MyClass)owner).Name = (string)v!;
```

> **Why `Action<object, object?>`?** The undo stack is type-erased — it stores deltas for any property on any StateObject. The static setter avoids allocating a closure per recorded change. One allocation per class (the static field), zero per recorded change.

---

## Coalescing Algorithm

When a new delta arrives for the same property on the same owner within `MergeWindowMs` of the top delta:

1. Check `CanMergeWith(incoming)` — same owner, same property, same setter
2. If yes: `MergeWith(incoming)` — update `NewValue` to incoming's value, keep original `OldValue`
3. Result: rapid typing produces **one** undo step instead of one per keystroke

```
User types "Hello":
  Delta: "" -> "H"        (recorded)
  Delta: "H" -> "He"      (merged: "" -> "He")
  Delta: "He" -> "Hel"    (merged: "" -> "Hel")
  Delta: "Hel" -> "Hell"  (merged: "" -> "Hell")
  Delta: "Hell" -> "Hello" (merged: "" -> "Hello")

Single undo step: "Hello" -> ""
```

> **Tip:** The default `MergeWindowMs` is 300ms. Set it to 0 to disable coalescing (every change becomes its own undo step). Set it higher for properties that change continuously, like slider values.

---

## Collection Deltas

`UndoableCollectionObserver<T>` maintains a shadow copy of the `StateList<T>`. On any mutation:

1. Capture `beforeSnapshot` (the shadow copy)
2. Let the mutation complete (StateList fires post-change notifications)
3. Capture `afterSnapshot` (current list state)
4. Record `CollectionUndoDelta<T>(before, after)`

Undo restores via `Clear() + AddRange(before)`.

> **Why snapshot-based instead of operation-based?** Operation-based undo (recording "insert at index 3") fails for complex mutations like sort, reverse, or filter-and-remove. The snapshot approach is O(N) but correct for all mutation types, including reorder. For typical UI lists (< 1000 items), the copy cost is negligible.

---

## Batch Observer

`UndoBatchObserver` bridges `StateObject.BeginUpdate()` batch boundaries into `UndoContext` group boundaries:

- `OnBatchFlushStarting` -> `BeginGroup()` on all registered contexts
- `OnBatchFlushCompleted` -> `EndGroup()` on all registered contexts

This ensures all changes within a `BeginUpdate()` scope are grouped as a single undo step.

```csharp
// Without batch observer: 3 undo steps
vm.Name = "Alice";
vm.Age = 30;
vm.Email = "alice@example.com";

// With batch observer: 1 undo step
using (vm.BeginUpdate())
{
    vm.Name = "Alice";
    vm.Age = 30;
    vm.Email = "alice@example.com";
}
```

---

## Persistence Serialization

### Capture Flow

```
UndoContext.CaptureUndoStack()
  -> UndoCaptureGroup[] (oldest -> newest order)
    -> each group: UndoCaptureDelta[] (property deltas only; collection deltas excluded)
      -> serialize values via System.Text.Json -> SerializedUndoDelta.OldValueJson/NewValueJson
  -> MemoryPack.Serialize(UndoSnapshotPayload)
  -> byte[]
```

> **Note:** Collection deltas are excluded from persistence because they reference live object lists that can't be reliably round-tripped through serialization. Only scalar property deltas survive persistence.

### Restore Flow

```
byte[]
  -> MemoryPack.Deserialize<UndoSnapshotPayload>
  -> foreach SerializedUndoGroup:
    -> foreach SerializedUndoDelta:
      -> Type.GetType(ValueTypeName) -> deserialize JSON
      -> ownerResolver(OwnerTypeName) -> live object instance
      -> setterResolver(OwnerTypeName, PropertyName) -> setter delegate
    -> UndoContext.RestoreStacks(undoGroups, redoGroups, setterResolver, ownerResolver)
```

Unresolvable deltas (renamed types, removed properties) are silently skipped. This makes persistence forward-compatible — adding or removing properties doesn't crash deserialization of old snapshots.

> **Warning:** This uses `Type.GetType()` and `System.Text.Json` with dynamic types, making it **not AOT-safe**. The `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` attributes on `UndoSnapshotProvider` signal this to trimming analyzers.

---

## Async Command Suspension

`UndoContext.RegisterCommandSuspension(IAsyncStateCommand cmd)` subscribes to the command's `StatusChanged` event. While `IsExecuting`, `CanUndo`/`CanRedo` return `false`.

> **Why block undo during async commands?** An async command might have partially updated the object (e.g., set `IsLoading = true`, started a network call). Undoing at this point would leave the object in an inconsistent state — `IsLoading` would revert to `false` while the network call is still in flight.

---

## Navigation Boundaries

`PushNavigationBoundary()` records the current undo depth. `CanUndo` returns `false` when the stack depth equals the boundary depth (unless `AllowUndoPastBoundary` is set).

```
[Navigate to Settings page]
  PushNavigationBoundary(depth=5)
  User makes 3 changes on Settings → undo stack depth = 8
  Undo 3 times → depth = 5 → CanUndo = false (at boundary)
  
[Navigate back]
  PopNavigationBoundary()
  Undo resumes from depth 5
```

> **Why boundaries?** Without them, pressing Ctrl+Z on the Settings page could undo changes from the Products page that the user has already "committed" by navigating away. Boundaries scope undo to the current page's changes.
