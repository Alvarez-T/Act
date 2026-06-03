# Undo & Redo

**Libraries:** YFex.State.History, YFex.State.History.Persistence, YFex.State.History.Testing

---

## 1. Overview

YFex.State.History adds undo/redo capabilities to any `StateObject` property via the `[Undoable]` attribute and a Roslyn incremental generator. Every property mutation is recorded as a delta (old value + new value + timestamp). Deltas are grouped into undo steps that can be reverted or replayed. The system supports property-level coalescing (rapid keystrokes merge into one step), explicit transactions (group N changes into one step), named scoping (separate undo stacks for different concerns), collection undo for `StateList<T>`, navigation boundaries (prevent undo from crossing page transitions), savepoint tracking (dirty-document detection), and full persistence via MemoryPack serialization. All wiring is source-generated -- no reflection, AOT-compatible, zero manual plumbing.

---

## 2. Core Concepts & Mental Model

### Primitives

| Primitive | Role |
|---|---|
| `UndoContext` | The central coordinator. Owns the undo and redo stacks. Exposes `Undo()`, `Redo()`, `CanUndo`, `CanRedo`, and `IStateCommand` instances for XAML binding. |
| `[Undoable]` | Attribute on properties or classes. Tells the generator to emit delta-recording code in the property setter. |
| `UndoTransactionScope` | `ref struct` returned by `BeginTransaction()`. Groups all changes within its lifetime into a single undo step. |
| `UndoSuspendScope` | `ref struct` returned by `SuspendRecording()`. Suppresses delta recording for the duration. |
| `UndoManager` | Multi-context coordinator. Manages undo/redo across multiple `UndoContext` instances (e.g., a document with separate text and drawing undo stacks). |
| `IUndoable` | Interface generated on classes with a single primary undo scope. Provides `UndoHistory`, `UndoCommand`, `RedoCommand`. |
| `UndoBatchObserver` | `IChangedHandler` that coordinates undo recording with `BatchScope`. When a batch is active, property changes within it are grouped into a single undo step. |

### How Undo Recording Works

```
  Property setter (generated)             UndoContext                    Undo/Redo Stack
  ──────────────────────────             ───────────                    ───────────────
  vm.Title = "Draft"        ──────►  RecordPropertyChange()  ──────►  Push delta:
                                     (owner, prop, old, new)            old="", new="Draft"

  vm.Title = "Final"        ──────►  RecordPropertyChange()  ──────►  Push delta:
                                     (owner, prop, old, new)            old="Draft", new="Final"

  context.Undo()                     PopUndo()               ──────►  Apply reverse:
                                     ApplyGroupReverse()                Title = "Draft"
                                     PushRedo()                         Move group to redo stack

  context.Redo()                     PopRedo()               ──────►  Apply forward:
                                     ApplyGroupForward()                Title = "Final"
                                     PushUndo()                         Move group back to undo stack
```

### Undo Stack Structure

```
  UndoContext
  ├── Undo Stack (linked list of UndoGroups)
  │   ├── Group 3: [delta: Title "B"→"C"]              ← most recent (Undo pops this)
  │   ├── Group 2: [delta: Width 100→200, Height 50→100]  ← transaction
  │   └── Group 1: [delta: Title ""→"B"]
  │
  ├── Redo Stack (linked list of UndoGroups)
  │   └── (empty until Undo is called)
  │
  ├── Savepoint marker → depth 1 (set after last save)
  └── Navigation boundary → depth 0 (prevents undo past page entry)
```

Key rules:
- **Any new mutation clears the redo stack.** After undo, if you make a new change, the redo history is lost.
- **Max depth is bounded.** When `MaxDepth` is exceeded, the oldest undo group is evicted.
- **Coalescing merges rapid changes.** Two changes to the same property within `MergeWindowMs` milliseconds merge into one delta (keeping the original old value and the latest new value).

---

## 3. Integration Model & Lifecycle

### How It Wires Into an App

The `[Undoable]` attribute works alongside `[Observable]`. The generator emits additional code in the property setter that calls `UndoContext.RecordPropertyChange()` after the value changes. The `UndoContext` itself is either auto-generated (property-level or class-level scope) or manually declared (explicit scope).

```
  ┌──────────────────────────┐
  │    Your ViewModel        │
  │                          │
  │  [Undoable][Observable]  │   ── generator emits setter that calls ──►  UndoContext
  │  public partial string   │                                              │
  │  Title { get; set; }     │                                              ├── Undo stack
  │                          │                                              ├── Redo stack
  │  UndoHistory (generated) │◄─── exposed as property ────────────────────┘
  │  UndoCommand (generated) │◄─── IStateCommand for XAML binding
  │  RedoCommand (generated) │◄─── IStateCommand for XAML binding
  └──────────────────────────┘
```

### Lifecycle Integration

- **Activation:** The generator wires `UndoBatchObserver` as a handler on the `StateObject`. This ensures batch scopes (`BeginUpdate()`) are coordinated with undo grouping -- all changes in a batch become one undo step.
- **Navigation:** When using NavigatR, the generator calls `PushNavigationBoundary()` on navigation entry and `PopNavigationBoundary()` on exit. This prevents undo from reverting changes made on a previous page.
- **Command suspension:** When `[Undoable]` and `[StateCommand]` coexist, the generator registers the async command with `RegisterCommandSuspension()`. `CanUndo`/`CanRedo` return `false` while the command is executing, preventing undo mid-operation.

---

## 4. Step-by-Step Usage ("Hello World")

### Package Reference

```xml
<PackageReference Include="YFex.State.History" />
<!-- YFex.State is pulled in transitively -->
<!-- For XAML binding, also add: -->
<PackageReference Include="YFex.State.Mvvm" />
```

> **Note:** `YFex.State.History` bundles its source generator automatically. No separate analyzer package is needed.

### Minimal Undoable ViewModel

```csharp
using YFex.State;
using YFex.State.History;
using YFex.State.Mvvm;

namespace MyApp.ViewModels;

public partial class NoteViewModel : MvvmStateObject
{
    // Both attributes required: [Observable] for change notification,
    // [Undoable] for delta recording
    [Undoable]
    [Observable]
    public partial string Title { get; set; }

    [Undoable]
    [Observable]
    public partial string Body { get; set; }
}
```

The generator creates:
- An `UndoContext` field shared by both properties.
- `UndoHistory` property (the `UndoContext` instance).
- `UndoCommand` and `RedoCommand` (`IStateCommand` for XAML binding).
- Implements `IUndoable` on the class.

### Using It

```csharp
var vm = new NoteViewModel();
vm.Activate(); // activates the ViewModel and wires up undo observation

vm.Title = "Draft";
vm.Body = "Hello world";
vm.Title = "Final Draft";

vm.UndoHistory.CanUndo; // true
vm.UndoHistory.Undo();  // Title reverts to "Draft"
vm.UndoHistory.Undo();  // Body reverts to ""
vm.UndoHistory.Undo();  // Title reverts to ""

vm.UndoHistory.CanRedo; // true
vm.UndoHistory.Redo();  // Title becomes "Draft" again
```

### XAML Binding

```xml
<TextBox Text="{Binding Title}" />
<TextBox Text="{Binding Body}" AcceptsReturn="True" />

<Button Command="{Binding UndoCommand}" Content="Undo" />
<Button Command="{Binding RedoCommand}" Content="Redo" />
```

`CanExecute` updates automatically as the stacks change.

---

## 5. Deep Dive: Core API & Features

### UndoContext

The central undo/redo coordinator.

#### Core Operations

```csharp
context.Undo();      // revert the most recent change group
context.Redo();      // re-apply the most recently undone group
context.CanUndo;     // bool -- false when stack is empty or at boundary
context.CanRedo;     // bool -- false when redo stack is empty
context.UndoDepth;   // int -- current undo stack depth
context.RedoDepth;   // int -- current redo stack depth
context.Clear();     // clears both stacks without recording
```

#### Commands (for MVVM binding)

```csharp
context.UndoCommand  // IStateCommand -- bind to a button
context.RedoCommand  // IStateCommand -- bind to a button
// CanExecute updates automatically
```

#### Configuration

```csharp
context.MaxDepth = 100;               // max undo entries (default: 100)
context.MergeWindowMs = 500;          // coalescing window in ms (default: 500)
context.AllowUndoPastBoundary = false; // respect navigation boundaries (default: false)
```

#### History Inspection

```csharp
context.UndoHistory  // IReadOnlyList<UndoHistoryEntry>
context.RedoHistory  // IReadOnlyList<UndoHistoryEntry>

// Each entry:
entry.Label        // string? -- transaction label, or null for auto-groups
entry.Description  // string -- human-readable description
entry.Timestamp    // DateTime -- when the change was recorded
entry.DeltaCount   // int -- number of property changes in this group
```

---

### Scoping Modes

The `[Undoable]` attribute supports three scoping strategies that control how `UndoContext` instances are created and shared.

#### Mode A: Property-Level (Default)

Each class with `[Undoable]` properties gets one auto-generated `UndoContext` shared by all undoable properties in that class.

```csharp
public partial class MyViewModel : MvvmStateObject
{
    [Undoable] [Observable] public partial string Name { get; set; }
    [Undoable] [Observable] public partial int Age { get; set; }
    // Both share the same auto-generated UndoContext
    // Generated: UndoContext UndoHistory, IStateCommand UndoCommand/RedoCommand
}
```

#### Mode B: Class-Level Named Scope

Apply `[Undoable]` at the class level with a `Scope` name. All `[Observable]` properties participate.

```csharp
[Undoable(Scope = "Document")]
public partial class DocumentViewModel : MvvmStateObject
{
    [Observable] public partial string Title { get; set; }
    [Observable] public partial string Body { get; set; }
    // Generated: __undoCtx_Document shared by Title and Body
}
```

#### Mode C: Explicit Context

Declare your own `UndoContext` property and point `[Undoable]` to it. Use this when you need multiple independent undo stacks in one class, or when you want to share a context across objects.

```csharp
public partial class EditorViewModel : MvvmStateObject
{
    [UndoContext]
    public UndoContext TextHistory { get; } = new();

    [UndoContext]
    public UndoContext FormatHistory { get; } = new();

    [Undoable(Context = nameof(TextHistory))]
    [Observable]
    public partial string Content { get; set; }

    [Undoable(Context = nameof(FormatHistory))]
    [Observable]
    public partial bool IsBold { get; set; }

    [Undoable(Context = nameof(FormatHistory))]
    [Observable]
    public partial int FontSize { get; set; }
}
// TextHistory and FormatHistory have independent undo stacks
```

#### Excluding a Property

Use `Exclude = true` with class-level `[Undoable]` to opt out specific properties.

```csharp
[Undoable(Scope = "Document")]
public partial class DocumentViewModel : MvvmStateObject
{
    [Observable] public partial string Title { get; set; }     // undoable
    [Observable] public partial string Body { get; set; }      // undoable

    [Undoable(Exclude = true)]
    [Observable] public partial bool IsPreviewOpen { get; set; } // NOT undoable
}
```

---

### Transactions

Group multiple changes into a single undo step.

```csharp
using (context.BeginTransaction("Resize"))
{
    vm.Width = 100;
    vm.Height = 200;
    vm.X = 50;
    vm.Y = 50;
}
// All four changes are one undo step labeled "Resize"
// context.Undo() reverts all four at once
```

The label is optional but useful for undo history display:

```csharp
using (context.BeginTransaction("Format as Header"))
{
    vm.FontSize = 24;
    vm.IsBold = true;
}
// context.UndoHistory[0].Label == "Format as Header"
```

> **Warning:** `UndoTransactionScope` is a `ref struct` -- it cannot cross `await` boundaries, be stored in fields, or captured by lambdas. This is intentional: an async transaction could leave the undo stack in an inconsistent state.

---

### Suspend Recording

Temporarily suppress delta capture for programmatic changes that should not be undoable.

```csharp
using (context.SuspendRecording())
{
    vm.InternalCounter = 42;  // not recorded
    vm.CacheVersion++;        // not recorded
}
// No undo entries created
```

> **Warning:** `UndoSuspendScope` is also a `ref struct`. Same restrictions as `UndoTransactionScope`.

Common use cases:
- Restoring state from a file (you don't want "undo" to revert the file load).
- Resetting to defaults.
- Internal bookkeeping properties.

---

### Coalescing

Consecutive changes to the **same property** within a configurable time window merge into one undo step. This prevents every keystroke from becoming a separate undo entry.

```csharp
[Undoable(MergeWindowMs = 500)]  // default: 500ms
[Observable]
public partial string SearchQuery { get; set; }
```

Typing "hello" character-by-character within 500ms of each keystroke:

```
Keystroke   Time    Undo Stack Effect
────────    ────    ──────────────────
'h'         0ms     Push delta: "" → "h"
'e'         100ms   Merge: "" → "he"       (within 500ms, same property)
'l'         200ms   Merge: "" → "hel"
'l'         300ms   Merge: "" → "hell"
'o'         400ms   Merge: "" → "hello"

Result: ONE undo step that reverts "hello" → ""
```

If the user pauses for more than 500ms and then types more, a new undo group starts.

You can set `MergeWindowMs = 0` to disable coalescing for a specific property (every change is a separate undo step).

---

### IUndoable Interface

When a class has a single primary undo scope (Mode A or Mode B), the generator implements `IUndoable`:

```csharp
public interface IUndoable
{
    UndoContext UndoHistory { get; }
    IStateCommand UndoCommand { get; }
    IStateCommand RedoCommand { get; }
}
```

This lets framework code work with any undoable ViewModel polymorphically:

```csharp
void WireUndoShortcuts(IUndoable vm)
{
    // Ctrl+Z → Undo, Ctrl+Y → Redo
    if (vm.UndoCommand.CanExecute()) vm.UndoCommand.Execute();
}
```

---

### UndoManager -- Multi-Context Coordination

`UndoManager` coordinates undo/redo across multiple `UndoContext` instances. When you call `Undo()`, it finds the context with the most recent change (by timestamp) and undoes that.

```csharp
using YFex.State.History;

var manager = new UndoManager();
manager.Register(textContext);
manager.Register(drawingContext);

// Undoes the globally most recent change, regardless of which context owns it
manager.Undo();
manager.Redo();

// MVVM binding
manager.UndoCommand  // IStateCommand
manager.RedoCommand  // IStateCommand
manager.CanUndo      // bool
manager.CanRedo      // bool
```

#### Savepoints

Mark the current state as "clean" for dirty-document tracking. A savepoint tracks whether any changes have been made since the last save.

```csharp
manager.SetSavepoint(); // marks all registered contexts as "clean"

vm.Title = "changed";
manager.IsAtSavepoint;  // false -- there are unsaved changes

manager.Undo();
manager.IsAtSavepoint;  // true -- back to the saved state

// Also works on individual contexts:
context.SetSavepoint();
context.IsAtSavepoint;  // true
vm.Title = "changed";
context.IsAtSavepoint;  // false
context.ClearSavepoint(); // removes the savepoint marker entirely
```

Use this for "unsaved changes" prompts:

```csharp
if (!manager.IsAtSavepoint)
{
    // Show "You have unsaved changes. Save before closing?" dialog
}
```

---

### Collection Undo

Track `StateList<T>` mutations (add, remove, clear, replace) as undoable operations.

```csharp
[UndoableCollection(Scope = "Document")]
[Observable]
public partial StateList<string> Tags { get; set; }
```

The generator creates an `UndoableCollectionObserver<T>` that records full before/after snapshots of the list. `Undo()` restores the entire list state.

```csharp
vm.Tags = new StateList<string>();
vm.Activate();

vm.Tags.Add("important");
vm.Tags.Add("draft");

vm.UndoHistory.Undo(); // Tags: ["important"]
vm.UndoHistory.Undo(); // Tags: []
vm.UndoHistory.Redo(); // Tags: ["important"]
```

| Parameter | Default | Effect |
|---|---|---|
| `Scope` | `null` | Named undo scope to join (same as `[Undoable(Scope = ...)]`) |
| `Context` | `null` | Explicit `UndoContext` property to use |

> **Note:** `[UndoableCollection]` requires the property type to be `StateList<T>`. Using it on other collection types generates diagnostic YFEX0708.

---

### Navigation Boundaries

Prevent undo from crossing page transitions. When a user navigates to a new page, you typically don't want "undo" on that page to revert changes made on the previous page.

```csharp
context.PushNavigationBoundary();  // mark current depth as boundary
// ... user works on child page ...
context.PopNavigationBoundary();   // remove boundary on navigate-back
```

When `AllowUndoPastBoundary = false` (the default), `CanUndo` returns `false` at a navigation boundary even if there are older undo entries.

> **Tip:** When using NavigatR, navigation boundaries are managed automatically by the generated `OnNavigation`/`OnSuspend` overrides. You only need manual boundary management for custom navigation scenarios.

---

### Batch + Undo Interaction

When a `BatchScope` is active, the `UndoBatchObserver` (wired by the generator) groups all property changes in the batch into a single undo step:

```csharp
using (vm.BeginUpdate())
{
    vm.Width = 100;
    vm.Height = 200;
}
// One undo step reverts both Width and Height
// (This is automatic -- no explicit transaction needed)
```

This differs from `BeginTransaction()` in that `BeginUpdate()` is about notification coalescing (a `StateObject` concern), while `BeginTransaction()` is about undo grouping (a `History` concern). When both are present, the batch scope implicitly creates a transaction.

---

## 6. Common Patterns & Recipes

### Text Editor with Coalescing

```csharp
using YFex.State;
using YFex.State.History;
using YFex.State.Mvvm;

public partial class TextEditorViewModel : MvvmStateObject
{
    [Undoable(MergeWindowMs = 500)] // keystrokes within 500ms merge
    [Observable]
    public partial string Content { get; set; }

    [Undoable(MergeWindowMs = 0)] // every format change is a separate undo step
    [Observable]
    public partial bool IsBold { get; set; }

    [Undoable(MergeWindowMs = 0)]
    [Observable]
    public partial int FontSize { get; set; }

    // "Find and Replace" as a transaction: one undo reverts all replacements
    [StateCommand]
    void ReplaceAll(string find, string replace)
    {
        using (UndoHistory.BeginTransaction("Replace All"))
        {
            Content = Content.Replace(find, replace);
        }
    }
}
```

### Drawing Canvas with Separate Undo Stacks

```csharp
using YFex.State;
using YFex.State.Collections;
using YFex.State.History;
using YFex.State.Mvvm;

public partial class CanvasViewModel : MvvmStateObject
{
    [UndoContext]
    public UndoContext ShapeHistory { get; } = new() { MaxDepth = 200 };

    [UndoContext]
    public UndoContext ToolHistory { get; } = new() { MaxDepth = 50 };

    // Shape mutations go to ShapeHistory
    [Undoable(Context = nameof(ShapeHistory))]
    [Observable]
    public partial ShapeData? SelectedShape { get; set; }

    [UndoableCollection(Context = nameof(ShapeHistory))]
    [Observable]
    public partial StateList<ShapeData> Shapes { get; set; } = new();

    // Tool settings go to ToolHistory
    [Undoable(Context = nameof(ToolHistory))]
    [Observable]
    public partial string BrushColor { get; set; } = "#000000";

    [Undoable(Context = nameof(ToolHistory))]
    [Observable]
    public partial int BrushSize { get; set; } = 2;

    // Global undo across both stacks
    public UndoManager GlobalUndo { get; } = new();

    protected override void OnActivated()
    {
        GlobalUndo.Register(ShapeHistory);
        GlobalUndo.Register(ToolHistory);
    }
}
```

### Form with Save/Load and Undo

```csharp
using YFex.State;
using YFex.State.History;
using YFex.State.Mvvm;

[Undoable(Scope = "Form")]
public partial class CustomerFormViewModel : MvvmStateObject
{
    [Observable] public partial string FirstName { get; set; }
    [Observable] public partial string LastName { get; set; }
    [Observable] public partial string Email { get; set; }

    [Undoable(Exclude = true)] // don't track UI state in undo
    [Observable]
    public partial bool IsEditing { get; set; }

    [StateCommand]
    async Task SaveAsync(CancellationToken ct)
    {
        await _repo.SaveAsync(new Customer(FirstName, LastName, Email), ct);
        UndoHistory.SetSavepoint(); // mark as clean after save
    }

    [StateCommand]
    async Task LoadAsync(int customerId, CancellationToken ct)
    {
        var c = await _repo.GetAsync(customerId, ct);

        // Don't record the load as undoable changes
        using (UndoHistory.SuspendRecording())
        {
            FirstName = c.FirstName;
            LastName = c.LastName;
            Email = c.Email;
        }

        UndoHistory.Clear();        // start fresh
        UndoHistory.SetSavepoint(); // mark as clean
    }

    // For "unsaved changes" prompt
    public bool HasUnsavedChanges => !UndoHistory.IsAtSavepoint;
}
```

### Undo with NavigatR Integration

```csharp
using YFex.State;
using YFex.State.History;
using YFex.State.Mvvm;

[Undoable(Scope = "Page")]
public partial class DetailPageViewModel : MvvmStateObject
{
    [Observable] public partial string Notes { get; set; }
    [Observable] public partial int Rating { get; set; }

    // Navigation boundaries are managed automatically by NavigatR.
    // When the user navigates to this page, PushNavigationBoundary() is called.
    // When they navigate away, PopNavigationBoundary() is called.
    // Undo on this page won't revert changes from the previous page.

    // AllowUndoPastBoundary defaults to false.
    // Set to true if you explicitly want cross-page undo:
    // UndoHistory.AllowUndoPastBoundary = true;
}
```

---

## 7. Testing & Mocking

### UndoContext Assertions

The `YFex.State.History.Testing` package provides fluent extension methods for `UndoContext` and `UndoManager`.

```csharp
using YFex.State.History.Testing;

[Fact]
public void Undo_reverts_property_change()
{
    var vm = new NoteViewModel();
    vm.Activate();

    vm.Title = "Draft";
    vm.Title = "Final";

    vm.UndoHistory
        .ShouldBeAbleToUndo()
        .ShouldHaveUndoDepth(2)
        .ShouldNotBeAbleToRedo();

    vm.UndoHistory.Undo();

    Assert.Equal("Draft", vm.Title);

    vm.UndoHistory
        .ShouldBeAbleToUndo()
        .ShouldHaveUndoDepth(1)
        .ShouldBeAbleToRedo()
        .ShouldHaveRedoDepth(1);
}
```

### Savepoint Assertions

```csharp
[Fact]
public void Savepoint_tracks_dirty_state()
{
    var vm = new NoteViewModel();
    vm.Activate();

    vm.UndoHistory.SetSavepoint();

    vm.Title = "changed";
    vm.UndoHistory.ShouldNotBeAtSavepoint();

    vm.UndoHistory.Undo();
    vm.UndoHistory.ShouldBeAtSavepoint();
}
```

### UndoManager Assertions

```csharp
[Fact]
public void Manager_coordinates_across_contexts()
{
    var ctx1 = new UndoContext();
    var ctx2 = new UndoContext();
    var manager = new UndoManager();
    manager.Register(ctx1);
    manager.Register(ctx2);

    // ... make changes via ctx1 and ctx2 ...

    manager
        .ShouldBeAbleToUndo()
        .ShouldBeAtSavepoint(); // true only if ALL contexts are at savepoint
}
```

### Transaction Testing

```csharp
[Fact]
public void Transaction_groups_changes_into_one_undo_step()
{
    var vm = new NoteViewModel();
    vm.Activate();

    using (vm.UndoHistory.BeginTransaction("Batch Edit"))
    {
        vm.Title = "New Title";
        vm.Body = "New Body";
    }

    vm.UndoHistory.ShouldHaveUndoDepth(1); // one step, not two

    vm.UndoHistory.Undo();
    Assert.Equal("", vm.Title);
    Assert.Equal("", vm.Body);
}
```

### Full Assertion API

**UndoContext assertions** (all return the `UndoContext` for chaining):

| Method | Asserts |
|---|---|
| `ShouldBeAbleToUndo()` | `CanUndo == true` |
| `ShouldNotBeAbleToUndo()` | `CanUndo == false` |
| `ShouldBeAbleToRedo()` | `CanRedo == true` |
| `ShouldNotBeAbleToRedo()` | `CanRedo == false` |
| `ShouldHaveUndoDepth(n)` | `UndoDepth == n` |
| `ShouldHaveRedoDepth(n)` | `RedoDepth == n` |
| `ShouldBeAtSavepoint()` | `IsAtSavepoint == true` |
| `ShouldNotBeAtSavepoint()` | `IsAtSavepoint == false` |
| `ShouldHaveHistoryCount(n)` | Undo history list count equals `n` |

**UndoManager assertions** (all return the `UndoManager` for chaining):

| Method | Asserts |
|---|---|
| `ShouldBeAbleToUndo()` | `CanUndo == true` (any registered context) |
| `ShouldNotBeAbleToUndo()` | `CanUndo == false` |
| `ShouldBeAbleToRedo()` | `CanRedo == true` |
| `ShouldNotBeAbleToRedo()` | `CanRedo == false` |
| `ShouldBeAtSavepoint()` | All registered contexts are at their savepoints |

---

## 8. Troubleshooting & Gotchas

### `[Undoable]` Without `[Observable]`

```csharp
// WRONG: generates diagnostic YFEX0702
[Undoable]
public partial string Title { get; set; }

// CORRECT: both attributes required
[Undoable]
[Observable]
public partial string Title { get; set; }
```

`[Undoable]` hooks into the generated setter from `[Observable]`. Without `[Observable]`, there's no setter to hook.

### Mixing `Scope` and `Context`

```csharp
// WRONG: generates diagnostic YFEX0701
[Undoable(Scope = "Doc", Context = nameof(MyHistory))]
[Observable]
public partial string Title { get; set; }
```

`Scope` (auto-generated context) and `Context` (explicit context) are mutually exclusive.

### `[UndoContext]` on Wrong Type

```csharp
// WRONG: generates diagnostic YFEX0704
[UndoContext]
public string NotAnUndoContext { get; }

// CORRECT: must be UndoContext type
[UndoContext]
public UndoContext MyHistory { get; } = new();
```

### Transaction Scope Across Await

```csharp
// WRONG: ref struct cannot cross await boundaries
using (context.BeginTransaction("Save"))
{
    vm.Status = "Saving";
    await SaveAsync(ct);        // COMPILE ERROR
    vm.Status = "Saved";
}
```

`UndoTransactionScope` and `UndoSuspendScope` are `ref struct`s by design. If you need async transactions, batch the synchronous property changes before or after the async call:

```csharp
// CORRECT: separate sync changes from async work
using (context.BeginTransaction("Pre-Save"))
{
    vm.Status = "Saving";
    vm.LastSaveAttempt = DateTime.UtcNow;
}

await SaveAsync(ct);

using (context.BeginTransaction("Post-Save"))
{
    vm.Status = "Saved";
    vm.SaveCount++;
}
```

### Forgetting to Activate

```csharp
var vm = new NoteViewModel();
// vm.Activate() NOT called!

vm.Title = "Hello";
vm.UndoHistory.Undo(); // may not work as expected -- UndoBatchObserver not wired
```

Always call `Activate()` before expecting undo to work correctly. The generator wires the `UndoBatchObserver` during activation.

### Redo Lost After New Change

```csharp
vm.Title = "A";
vm.Title = "B";
vm.UndoHistory.Undo();  // Title = "A", redo stack has "B"
vm.Title = "C";          // NEW CHANGE: redo stack is CLEARED
vm.UndoHistory.CanRedo;  // false -- "B" is gone
```

This is standard undo/redo behavior. A new mutation after undo forks the timeline and discards the redo branch.

### Collection Undo Uses Snapshots

`[UndoableCollection]` records full before/after snapshots of the list, not individual item deltas. For large lists, this can be memory-intensive. Consider whether you need collection-level undo or if tracking individual item properties via `[Undoable]` on the item ViewModel is more appropriate.

---

## 9. Reference Summary

### All Attributes

| Attribute | Target | Parameters | Default |
|---|---|---|---|
| `[Undoable]` | Class, Property, Field | `Scope: string?`, `Context: string?`, `MergeWindowMs: int`, `Exclude: bool` | `null`, `null`, `500`, `false` |
| `[UndoContext]` | Property | -- | -- |
| `[UndoableCollection]` | Property, Field | `Scope: string?`, `Context: string?` | `null`, `null` |

### UndoContext API

| Member | Type | Description |
|---|---|---|
| `Undo()` | `void` | Revert the most recent change group |
| `Redo()` | `void` | Re-apply the most recently undone group |
| `CanUndo` | `bool` | Whether undo is available (respects boundaries) |
| `CanRedo` | `bool` | Whether redo is available |
| `UndoDepth` | `int` | Current undo stack depth |
| `RedoDepth` | `int` | Current redo stack depth |
| `UndoCommand` | `IStateCommand` | For XAML binding |
| `RedoCommand` | `IStateCommand` | For XAML binding |
| `IsAtSavepoint` | `bool` | Whether current state matches the savepoint |
| `IsReplaying` | `bool` | `true` during Undo/Redo application |
| `IsSuspended` | `bool` | `true` when recording is suspended |
| `MaxDepth` | `int` | Max undo entries (default: 100) |
| `MergeWindowMs` | `int` | Coalescing window in ms (default: 500) |
| `AllowUndoPastBoundary` | `bool` | Allow undo past navigation boundaries (default: false) |
| `UndoHistory` | `IReadOnlyList<UndoHistoryEntry>` | Inspectable undo history |
| `RedoHistory` | `IReadOnlyList<UndoHistoryEntry>` | Inspectable redo history |
| `BeginTransaction(label?)` | `UndoTransactionScope` | Group changes into one undo step |
| `SuspendRecording()` | `UndoSuspendScope` | Suppress delta recording |
| `SetSavepoint()` | `void` | Mark current state as clean |
| `ClearSavepoint()` | `void` | Remove savepoint marker |
| `PushNavigationBoundary()` | `void` | Mark current depth as navigation boundary |
| `PopNavigationBoundary()` | `void` | Remove navigation boundary |
| `RegisterCommandSuspension(cmd)` | `void` | Suspend undo/redo during async command |
| `Clear()` | `void` | Clear both stacks |

### UndoManager API

| Member | Type | Description |
|---|---|---|
| `Register(context)` | `void` | Add a context to the coordination pool |
| `Unregister(context)` | `void` | Remove a context |
| `Undo()` | `void` | Undo the globally most recent change |
| `Redo()` | `void` | Redo the globally most recent undone change |
| `CanUndo` | `bool` | Any registered context can undo |
| `CanRedo` | `bool` | Any registered context can redo |
| `UndoCommand` | `IStateCommand` | For XAML binding |
| `RedoCommand` | `IStateCommand` | For XAML binding |
| `IsAtSavepoint` | `bool` | All registered contexts at their savepoints |
| `SetSavepoint()` | `void` | Set savepoint on all registered contexts |
| `UndoHistory` | `IReadOnlyList<UndoHistoryEntry>` | Combined history across all contexts |
| `RedoHistory` | `IReadOnlyList<UndoHistoryEntry>` | Combined redo history |

### UndoHistoryEntry

| Field | Type | Description |
|---|---|---|
| `Label` | `string?` | Transaction label (null for auto-groups) |
| `Description` | `string` | Human-readable description |
| `Timestamp` | `DateTime` | When the change was recorded |
| `DeltaCount` | `int` | Number of property changes in this group |

### Persistence API (YFex.State.History.Persistence)

```csharp
using YFex.State.History.Persistence;

var provider = new UndoSnapshotProvider(context)
{
    // Resolve live object instances by type name (for rehydrating deltas)
    OwnerResolver = typeName => typeName switch
    {
        "MyApp.ViewModels.NoteViewModel" => vm,
        _ => null
    },
    // Resolve property setters by type + property name
    SetterResolver = (typeName, propName) => /* resolve setter delegate */
};

// Capture undo/redo stacks to bytes (MemoryPack serialization)
byte[]? snapshot = await provider.CaptureAsync(ct);

// Restore from bytes
if (snapshot is not null)
    await provider.RestoreAsync(snapshot, version: 1, ct);
```

| Property | Type | Description |
|---|---|---|
| `Discriminator` | `string` | Unique key for the snapshot (for multi-context persistence) |
| `Version` | `int` | Schema version for migration support |
| `OwnerResolver` | `Func<string, object?>` | Maps type names to live instances |
| `SetterResolver` | `Func<string, string, Action<object, object?>?>` | Maps type+property to setter delegates |

### Diagnostics

| Code | Severity | Description |
|---|---|---|
| YFEX0701 | Error | `[Undoable]` has both `Scope` and `Context` set (mutually exclusive) |
| YFEX0702 | Error | `[Undoable]` without `[Observable]` |
| YFEX0703 | Error | `[Undoable(Context = ...)]` references unknown property |
| YFEX0704 | Error | `[UndoContext]` property must be of type `UndoContext` |
| YFEX0705 | Error | Class must be `partial` |
| YFEX0706 | Error | Class must inherit `StateObject` |
| YFEX0707 | Error | `[Undoable]` on computed or read-only property |
| YFEX0708 | Error | `[UndoableCollection]` on non-`StateList<T>` property |
| YFEX0709 | Warning | Class-level `[Undoable]` with no `[Observable]` properties |
