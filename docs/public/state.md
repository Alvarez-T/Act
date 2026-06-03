# Reactive State

**Libraries:** YFex.State, YFex.State.Mvvm, YFex.State.Collections, YFex.State.Blazor, YFex.State.Testing

---

## 1. Overview

YFex.State is a source-generator-driven reactive state management library for .NET. You declare intent with attributes (`[Observable]`, `[Computed]`, `[StateCommand]`), and a Roslyn incremental generator emits all the property setters, equality checks, change notifications, command wrappers, and lifecycle wiring at compile time. No runtime reflection. No expression trees. Full AOT and trimming compatibility. The same `StateObject` graph works unchanged across WPF, Avalonia, MAUI (via `MvvmStateObject`), and Blazor (via `StateComponent<T>`), because the core notification system is framework-agnostic -- host adapters handle marshalling to the UI thread or triggering re-renders.

---

## 2. Core Concepts & Mental Model

### Primitives

| Primitive | Role |
|---|---|
| `StateObject` | Base class. Owns change notification, batching, activation lifecycle, and validation. Every ViewModel ultimately inherits from this. |
| `INotifyChanged` | Framework-agnostic change notification interface. One method pair: `Subscribe`/`Unsubscribe` with `IChangedHandler`. |
| `ChangedNotification` | Struct describing what changed: `PropertyName`, `PropertyId` (dense uint for bitmap ops), `Kind` (property change or collection mutation), `OldItem`, `Index`, `Count`. |
| `PropertyBitmap64` | 64-bit bitmask. Each `[Observable]` property gets a bit position. During a batch scope, changed bits accumulate; on exit, only the set bits dispatch notifications. Zero-allocation coalescing. |
| `BatchScope` | `ref struct` returned by `BeginUpdate()`. While alive, notifications are deferred. On dispose, pending changes fire in one burst. |
| `IActivatable` | Lifecycle contract: `Activate()` / `Deactivate()` / `IsActive`. Cascades through the object graph. |
| `ValidationBag` | Per-object container for validation results. O(1) `HasErrors` check, per-property error messages. |

### How It Works

```
  Your Code (attributes)          Source Generator (compile time)          Runtime
  ─────────────────────           ──────────────────────────────          ────────
  [Observable]                    → property setter with equality         → SetField()
  public partial string Name       check + NotifyChanged call               fires IChangedHandler

  [Computed(DependsOn=...)]       → dependency tracking + re-fire         → auto-invalidates
  public partial string Summary    when dependencies change                 when deps change

  [StateCommand]                  → IAsyncStateCommand property           → lock-free concurrency
  async Task SaveAsync(ct)          with status tracking                    gate, status enum

  [ReactsTo(nameof(X))]           → subscription wiring in                → method called when
  void OnXChanged()                 OnActivated/OnDeactivated               X changes
```

### Object Graph & Notification Flow

```
  PageViewModel (MvvmStateObject)
  ├── [Observable] Name : string          ─── setter fires ChangedNotification
  ├── [Observable] Age : int              ─── setter fires ChangedNotification
  ├── [Computed] Summary : string         ─── re-fires when Name or Age change
  ├── [Observable] Items : StateList<T>   ─── collection mutations fire ItemsAdded/Removed/...
  │   └── ItemChangedRelay                ─── relays per-item changes upward
  └── SaveCommand : IAsyncStateCommand    ─── generated from [StateCommand] method
      ├── IsExecuting : bool
      └── Status : CommandStatus
```

Notifications flow **outward** from the object that changed. Handlers (`IChangedHandler`) receive `OnChanging` (always immediate, pre-change) and `OnChanged` (post-change, deferred during batches). `MvvmStateObject` translates these into `INotifyPropertyChanged` / `INotifyDataErrorInfo` for XAML binding. `StateComponent<T>` translates them into debounced `StateHasChanged()` calls for Blazor.

---

## 3. Integration Model & Lifecycle

### The Activation Lifecycle

```
new()  →  Activate()  →  [live: mutations, notifications, polls]  →  Deactivate()  →  Dispose()
              │                                                            │
              ├── cascades to child StateObject properties                 ├── stops [Poll] timers
              ├── starts [Poll] timers                                     ├── cancels [ReactsTo] async ops
              ├── triggers [LoadOnInit] loads                              └── cascades to children
              └── fires OnActivated() hook                                     (children first, then parent)
```

Key rules:
- `Activate()` and `Deactivate()` are **idempotent**. Calling `Activate()` on an already-active object is a no-op.
- Children activate **after** the parent. Children deactivate **before** the parent.
- Properties marked `[IgnoreActivation]` are excluded from the cascade.
- `IsActive` reflects the current state.

### What's Stateful vs What the Host Manages

| YFex.State owns | Host framework owns |
|---|---|
| Property values and change tracking | UI thread / `SynchronizationContext` |
| Validation results | Data binding infrastructure |
| Command execution state | Button enabled/disabled rendering |
| Activation lifecycle | Page navigation (drives `Activate`/`Deactivate`) |
| Batch notification coalescing | Render scheduling |

### Wiring Into an App

**XAML apps (WPF / Avalonia / MAUI):** Inherit `MvvmStateObject`. It captures the `SynchronizationContext` on construction and marshals all `PropertyChanged` events to the UI thread. Register ViewModels in DI and bind normally.

**Blazor apps:** Inherit `StateComponent<TViewModel>`. Register the ViewModel with DI. The component auto-subscribes to changes and coalesces `StateHasChanged()` with an 8ms debounce window.

---

## 4. Step-by-Step Usage ("Hello World")

### Package Reference

```xml
<PackageReference Include="YFex.State" />
<!-- For XAML binding (WPF / Avalonia / MAUI): -->
<PackageReference Include="YFex.State.Mvvm" />
<!-- For Blazor: -->
<PackageReference Include="YFex.State.Blazor" />
```

> **Note:** `YFex.State` bundles the source generator automatically. No separate analyzer package is needed.

### Minimal ViewModel

```csharp
using YFex.State;
using YFex.State.Mvvm;

namespace MyApp.ViewModels;

// The class MUST be partial -- the generator emits the other half
public partial class GreetingViewModel : MvvmStateObject
{
    // Generates a setter with equality check + PropertyChanged notification
    [Observable]
    public partial string Name { get; set; }

    // Re-notifies whenever Name changes
    [Computed(DependsOn = new[] { nameof(Name) })]
    public partial string Greeting => $"Hello, {Name}!";
}
```

### Using It

```csharp
var vm = new GreetingViewModel();
vm.Activate(); // starts the lifecycle -- polls, reactions, load-on-init

vm.Name = "Alice";
// vm.Greeting is now "Hello, Alice!"
// PropertyChanged fired for both Name and Greeting

vm.Name = "Alice"; // no-op: equality check prevents redundant notification
```

### Adding a Command

```csharp
using YFex.State;
using YFex.State.Mvvm;

namespace MyApp.ViewModels;

public partial class GreetingViewModel : MvvmStateObject
{
    [Observable]
    public partial string Name { get; set; }

    [Computed(DependsOn = new[] { nameof(Name) })]
    public partial string Greeting => $"Hello, {Name}!";

    // Generates: public IAsyncStateCommand SaveCommand { get; }
    // with IsExecuting, Status tracking, and lock-free concurrency gate
    [StateCommand]
    async Task SaveAsync(CancellationToken ct)
    {
        await Task.Delay(1000, ct); // simulate save
    }
}
```

In XAML:
```xml
<TextBox Text="{Binding Name}" />
<TextBlock Text="{Binding Greeting}" />
<Button Command="{Binding SaveCommand}" Content="Save" />
```

In Blazor:
```razor
@inherits StateComponent<GreetingViewModel>

<input @bind="ViewModel.Name" />
<p>@ViewModel.Greeting</p>
<button @onclick="() => ViewModel.SaveCommand.Execute()" disabled="@ViewModel.SaveCommand.IsExecuting">
    Save
</button>
```

---

## 5. Deep Dive: Core API & Features

### Property Attributes

#### `[Observable]`

Marks a property or field as reactive. The generator emits a setter with equality checking and change notification.

```csharp
// Property-backed (preferred)
[Observable] public partial string Name { get; set; }

// Field-backed (generates a public property named "Count")
[Observable] private int _count;
```

**Equality strategies** (auto-detected by type):

| Type | Strategy |
|---|---|
| `bool`, `int`, `Guid`, `DateTime`, other value types | Direct `==` operator |
| `float` / `double` | NaN-aware comparison (`x != y` instead of `!x.Equals(y)`) |
| `string` | `StringComparison.Ordinal` |
| Reference types | `ReferenceEquals` first, then `.Equals()` |
| Custom | Provide `[EqualityComparer(typeof(MyComparer))]` |

```csharp
// Custom equality: useful for complex types or case-insensitive strings
[Observable]
[EqualityComparer(typeof(CaseInsensitiveComparer))]
public partial string SearchQuery { get; set; }

public class CaseInsensitiveComparer : IEqualityComparer<string>
{
    public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    public int GetHashCode(string obj) => obj.ToUpperInvariant().GetHashCode();
}
```

#### `[Computed]`

Read-only derived property. Automatically re-notifies when any dependency changes.

```csharp
[Computed(DependsOn = new[] { nameof(FirstName), nameof(LastName) })]
public partial string FullName => $"{FirstName} {LastName}";
```

The generator wires up internal subscriptions so that when `FirstName` or `LastName` fires a change notification, `FullName`'s `PropertyChanged` fires too.

#### `[NotifyOnTaskCompletion]`

For `Task` or `Task<T>` properties. Fires `PropertyChanged` again when the task completes, so bindings update to show the result.

```csharp
[Observable]
[NotifyOnTaskCompletion]
public partial Task<List<Item>> Items { get; set; }

// Usage: set Items to a running task; the UI binds to Items.Result.
// PropertyChanged fires once on assignment, then again when the task completes.
```

#### `[ObserveItems]`

Subscribes to item-level changes in a `StateList<T>` where items implement `INotifyChanged`.

```csharp
[Observable]
[ObserveItems(Weak = true, ActiveOnly = true)]
public partial StateList<OrderLine> Lines { get; set; }
```

| Parameter | Default | Effect |
|---|---|---|
| `Weak` | `false` | Use weak references for item subscriptions (prevents memory leaks with long-lived lists) |
| `ActiveOnly` | `false` | Only subscribe to items while the owning object is active |

#### `[Propagate]`

Forwards change notifications from a child `StateObject` property as if they originated from the parent. Useful for flattening nested ViewModels.

```csharp
[Observable]
[Propagate]
public partial AddressViewModel Address { get; set; }

// When Address.Street changes, this ViewModel's handlers see it too.
```

#### `[Epoch]`

Increments an `int` counter on every change. Useful for cache-busting or binding to trigger re-evaluation.

```csharp
[Observable]
[Epoch]
public partial string Data { get; set; }

// Generated: public int DataEpoch { get; } -- increments every time Data changes
```

#### `[Trackable]`

Participates in `IsModified` / `MarkAsClean()` dirty tracking.

```csharp
[Observable]
[Trackable]
public partial string Title { get; set; }

// After MarkAsClean(), modifying Title sets IsModified = true
```

#### `[Snapshot]`

Adds the property to a named snapshot group for save/restore.

```csharp
[Observable]
[Snapshot(Group = "FormState")]
public partial string Name { get; set; }

[Observable]
[Snapshot(Group = "FormState")]
public partial int Age { get; set; }
```

#### `[Persist]`

Persists the property value to `IPersistenceStore` automatically.

```csharp
[Observable]
[Persist(Key = "user.theme")]
public partial string Theme { get; set; }
```

| Parameter | Default | Effect |
|---|---|---|
| `Key` | `null` (auto-generated from class + property name) | Custom storage key |

#### `[LogChanges]`

Emits structured log output on every property change via `[LoggerMessage]`.

```csharp
[Observable]
[LogChanges]
public partial string Username { get; set; }
// Logs: "Property 'Username' changed on MyViewModel"
```

#### `[ResetTo]`

Generates a `Reset()` method that restores the property to its default value.

```csharp
[Observable]
[ResetTo(DefaultValue = "Untitled")]
public partial string Title { get; set; }

// Generated: void ResetTitle() => Title = "Untitled";
```

#### `[LoadOnInit]`

Triggers an async load method on first access or activation.

```csharp
[Observable]
[LoadOnInit]
public partial List<Customer> Customers { get; set; }

// The generator wires activation to call LoadCustomersAsync() automatically
```

#### `[IgnoreActivation]`

Prevents a child `StateObject` property from participating in the activation cascade.

```csharp
[Observable]
[IgnoreActivation]
public partial SharedCache Cache { get; set; }
// Cache.Activate() is NOT called when this ViewModel activates
```

---

### Validation

#### `[ValidateWith]` (synchronous)

```csharp
using YFex.State;
using YFex.State.Validation;

public partial class RegisterViewModel : MvvmStateObject
{
    [Observable]
    [ValidateWith(typeof(EmailValidator))]
    public partial string Email { get; set; }
}

// Validators use C# static interface methods -- no allocations, no DI needed
public class EmailValidator : IValidator<string>
{
    public static ValidationResult Validate(string value)
        => value?.Contains('@') == true
            ? ValidationResult.Success
            : new ValidationResult
            {
                PropertyName = "Email",
                Message = "Must contain @",
                Severity = ValidationSeverity.Error
            };
}
```

#### `[ValidateAsync]` (asynchronous)

```csharp
[Observable]
[ValidateAsync(typeof(UniqueEmailValidator))]
public partial string Email { get; set; }

public class UniqueEmailValidator : IAsyncValidator<string>
{
    public static async ValueTask<ValidationResult> ValidateAsync(
        string value, CancellationToken ct)
    {
        bool exists = await CheckEmailExistsAsync(value, ct);
        return exists
            ? new ValidationResult { PropertyName = "Email", Message = "Already taken" }
            : ValidationResult.Success;
    }
}
```

#### Accessing Validation Results

```csharp
vm.Validation.HasErrors     // O(1) bool check
vm.Validation.ErrorCount    // total count across all properties
vm.Validation.GetError("Email")    // string? -- first error message for the property
vm.Validation.GetErrors("Email")   // IEnumerable<string> -- all messages
vm.Validation.All                  // IEnumerable<ValidationResult>
vm.Validation.ClearAll()           // resets everything
```

`MvvmStateObject` implements `INotifyDataErrorInfo`, so XAML validation error templates work automatically.

---

### Method Attributes

#### `[StateCommand]`

Generates an `IStateCommand` (sync) or `IAsyncStateCommand` (async) property from a method.

```csharp
[StateCommand]
async Task SaveAsync(CancellationToken ct)
{
    await _repo.SaveAsync(ct);
}
// Generates: public IAsyncStateCommand SaveCommand { get; }
```

**Command status tracking:**

```csharp
vm.SaveCommand.IsExecuting  // true while the async method is running
vm.SaveCommand.Status       // Idle → Executing → Succeeded | Faulted | Canceled
vm.SaveCommand.CanExecute() // false while already executing (lock-free gate)
```

`CommandStatus` values: `Idle = 0`, `Executing = 1`, `Succeeded = 2`, `Faulted = 3`, `Canceled = 4`.

**Parameters:**

| Parameter | Default | Effect |
|---|---|---|
| `IncludeCancelCommand` | `false` | Generates a companion `CancelSaveCommand` that cancels the `CancellationToken` |
| `CancelCommandName` | `null` (auto: `"Cancel" + method name`) | Override the cancel command name |
| `TargetProperty` | `null` | Auto-assign the return value to an `[Observable]` property |

```csharp
[StateCommand(IncludeCancelCommand = true, TargetProperty = nameof(SearchResults))]
async Task<List<Item>> SearchAsync(CancellationToken ct)
{
    return await _api.SearchAsync(Query, ct);
}
// Generates: SearchCommand, CancelSearchCommand
// SearchResults is set to the return value on success
```

**ValueTask fast path:** If the method returns `ValueTask` or `ValueTask<T>` and completes synchronously, the command skips the `Executing` state entirely. This prevents UI flicker for fast operations like in-memory lookups.

#### `[ReactsTo]`

Subscribes a method to one or more property changes. Wired during activation, unwired during deactivation.

```csharp
[ReactsTo(nameof(SearchQuery), RunOnMainThread = true, CancelPrevious = true)]
async Task OnSearchQueryChanged()
{
    Results = await _search.SearchAsync(SearchQuery);
}
```

| Parameter | Default | Effect |
|---|---|---|
| `PropertyNames` | (required, positional) | Which properties trigger this reaction |
| `RunOnMainThread` | `false` | Marshal the callback to `SynchronizationContext.Current` |
| `CancelPrevious` | `false` | Cancel any in-flight previous invocation before starting a new one |

> **Note:** `[ReactsTo]` supports `AllowMultiple = true`. A single method can react to different sets of properties with different options.

#### `[Debounce]` / `[Throttle]`

Apply to any `[ReactsTo]` or `[StateCommand]` method.

```csharp
[ReactsTo(nameof(SearchQuery))]
[Debounce(300)] // waits 300ms after the LAST change before executing
async Task OnSearchQueryChanged() { /* ... */ }

[ReactsTo(nameof(ScrollPosition))]
[Throttle(100)] // executes at most once per 100ms
void OnScrollPositionChanged() { /* ... */ }
```

| Attribute | Parameter | Behavior |
|---|---|---|
| `[Debounce(ms)]` | `Milliseconds` | Resets the timer on each trigger. Fires once after `ms` of silence. |
| `[Throttle(ms)]` | `Milliseconds` | Fires immediately, then suppresses for `ms`. |

#### `[Poll]`

Recurring execution on a timer. Starts on `Activate()`, stops on `Deactivate()`.

```csharp
[Poll(IntervalMs = 5000, ActiveOnly = true)]
async Task RefreshDataAsync(CancellationToken ct)
{
    Data = await _api.FetchAsync(ct);
}
```

| Parameter | Default | Effect |
|---|---|---|
| `IntervalMs` | (required) | Milliseconds between executions |
| `ActiveOnly` | `true` | Pause when `Deactivate()` is called, resume on `Activate()` |

#### `[Busy]`

Sets a named `bool` property to `true` during method execution.

```csharp
[Observable] public partial bool IsLoading { get; set; }

[StateCommand]
[Busy(nameof(IsLoading))]
async Task LoadAsync(CancellationToken ct)
{
    Items = await _repo.GetAllAsync(ct);
}
// IsLoading = true while LoadAsync runs, false when it completes
```

#### `[RequiresAll]`

N-of-N gate: the command's `CanExecute` returns `true` only when **all** named `bool` properties are `true`.

```csharp
[Observable] public partial bool HasName { get; set; }
[Observable] public partial bool HasEmail { get; set; }

[StateCommand]
[RequiresAll(nameof(HasName), nameof(HasEmail))]
async Task SubmitAsync(CancellationToken ct) { /* ... */ }
// SubmitCommand.CanExecute() == true only when HasName && HasEmail
```

#### `[Gate]`

Single-property gate for `CanExecute`.

```csharp
[Observable] public partial bool IsOnline { get; set; }

[StateCommand]
[Gate(nameof(IsOnline))]
async Task SyncAsync(CancellationToken ct) { /* ... */ }
// SyncCommand.CanExecute() == IsOnline
```

#### `[ErrorBucket]`

Captures exceptions into a named property instead of throwing. Useful for displaying errors in the UI.

```csharp
[Observable] public partial Exception? LastError { get; set; }

[StateCommand]
[ErrorBucket(nameof(LastError))]
async Task LoadAsync(CancellationToken ct)
{
    Items = await _api.GetAsync(ct); // if this throws, LastError is set instead
}
```

#### `[Queue]`

Enqueues command invocations into a bounded FIFO channel for serialized execution.

```csharp
[StateCommand]
[Queue(Name = "uploads", Capacity = 10)]
async Task UploadFileAsync(CancellationToken ct) { /* ... */ }
// Invocations queue up; processed one at a time, FIFO order
```

| Parameter | Default | Effect |
|---|---|---|
| `Name` | `null` (per-method) | Named queue shared across multiple commands |
| `Capacity` | `1` | Max queued items. Excess invocations are dropped. |

#### `[Cooldown]`

Minimum delay between successive executions.

```csharp
[StateCommand]
[Cooldown(2000)] // at least 2 seconds between executions
async Task RefreshAsync(CancellationToken ct) { /* ... */ }
```

---

### Batching

Group multiple property changes into a single notification burst.

```csharp
using (vm.BeginUpdate())
{
    vm.FirstName = "Alice";
    vm.LastName = "Smith";
    vm.Age = 30;
}
// All three PropertyChanged events fire HERE, after the batch scope exits.
// Computed properties that depend on any of them re-evaluate once.
```

`BeginUpdate()` returns a `BatchScope` (`ref struct`). While active:
- `NotifyChanged` calls are deferred -- the property ID is recorded in a `PropertyBitmap64`.
- `NotifyChanging` (pre-change) calls still fire immediately.
- On dispose, `DispatchPending` translates the bitmap back to notifications and fires them all.

> **Warning:** `BatchScope` is a `ref struct`. It cannot be stored in fields, captured by lambdas, or used across `await` boundaries.

### Subscribing to Changes

```csharp
vm.Subscribe(myHandler);   // IChangedHandler interface
vm.Unsubscribe(myHandler);
```

`IChangedHandler` provides four callbacks:

| Callback | When | Deferred by batch? |
|---|---|---|
| `OnChanging(source, notification)` | Before value changes | No -- always immediate |
| `OnChanged(source, notification)` | After value changes | Yes -- deferred during `BatchScope` |
| `OnBatchFlushStarting(source)` | Before batch drain begins | N/A |
| `OnBatchFlushCompleted(source)` | After batch drain completes | N/A |

---

### MvvmStateObject

Extends `StateObject` with `INotifyPropertyChanged`, `INotifyPropertyChanging`, and `INotifyDataErrorInfo` for XAML/MVVM binding.

**Key behaviors:**
- Captures `SynchronizationContext` on construction; marshals all notifications to the UI thread.
- Coalesces batch notifications into a single `Post` to the UI thread (one marshalling call per batch, not per property).
- `INotifyDataErrorInfo` delegates to `ValidationBag` -- XAML validation error templates work automatically.

#### MvvmCommandAdapter

Bridges `IStateCommand` / `IAsyncStateCommand` to `System.Windows.Input.ICommand` for XAML binding. The generated `SaveCommand` property is already an `ICommand`. Compiled binding systems (Avalonia, WinUI) bind to the typed overloads for zero-boxing.

---

### StateList\<T\>

Zero-allocation observable list backed by `ArrayPool<T>`. Notifications flow through `INotifyChanged` (not `INotifyCollectionChanged`).

```csharp
using YFex.State.Collections;

var list = new StateList<string>(initialCapacity: 16);
list.Add("Alice");
list.AddRange(new[] { "Bob", "Charlie" });
list.Remove("Bob");

// Zero-allocation iteration via Span
foreach (var item in list.AsSpan()) { /* ... */ }
```

**API summary:**

| Method | Notes |
|---|---|
| `Add(T)` | Fires `ItemsAdded` notification |
| `AddRange(ReadOnlySpan<T>)` | Batch add, single notification |
| `Remove(T)` | Fires `ItemsRemoved` |
| `RemoveAt(int)` | Fires `ItemsRemoved` |
| `Clear()` | Fires `ItemsCleared` |
| `IndexOf(T)` | Returns index or -1 |
| `AsSpan()` | `ReadOnlySpan<T>` over the live buffer -- zero-allocation iteration |
| `this[int]` | Indexer, fires `ItemReplaced` on set |
| `Count` | Current item count |

**Lifecycle integration:**
- Items implementing `IActivatable` are auto-activated/deactivated with the list.
- Items implementing `INotifyChanged` can relay their changes upward (see `[ObserveItems]`).

#### StateCollection\<T\> (XAML adapter)

Wraps `StateList<T>` with `INotifyCollectionChanged` for XAML `ItemsSource` binding:

```csharp
using YFex.State.Mvvm.Collections;

var collection = myStateList.ToStateCollection();
// Bind to ItemsSource in XAML
```

---

### Blazor Integration

```razor
@using YFex.State.Blazor
@inherits StateComponent<MyViewModel>

<p>@ViewModel.Name</p>
<input @bind="ViewModel.Name" />
```

`StateComponent<TViewModel>`:
- Auto-subscribes to the ViewModel's change notifications via `IChangedHandler`.
- Coalesces `StateHasChanged()` calls with an 8ms debounce window to prevent render thrash when multiple properties change in quick succession.
- The ViewModel is injected via `[Inject]` -- register it in DI.
- Disposes the subscription automatically.

---

## 6. Common Patterns & Recipes

### Form Editing with Validation

```csharp
using YFex.State;
using YFex.State.Mvvm;
using YFex.State.Validation;

public partial class ContactFormViewModel : MvvmStateObject
{
    [Observable]
    [ValidateWith(typeof(RequiredValidator))]
    [Trackable] // participates in IsModified tracking
    public partial string Name { get; set; }

    [Observable]
    [ValidateWith(typeof(EmailValidator))]
    [Trackable]
    public partial string Email { get; set; }

    [Observable]
    [ValidateWith(typeof(PhoneValidator))]
    public partial string Phone { get; set; }

    [StateCommand]
    [RequiresAll(nameof(IsFormValid))] // gate: can't save until form is valid
    [Busy(nameof(IsSaving))]
    async Task SaveAsync(CancellationToken ct)
    {
        await _repo.SaveContactAsync(new Contact(Name, Email, Phone), ct);
    }

    [Observable] public partial bool IsSaving { get; set; }

    // Computed from validation state
    [Computed(DependsOn = new[] { nameof(Name), nameof(Email), nameof(Phone) })]
    public partial bool IsFormValid => !Validation.HasErrors;
}

public class RequiredValidator : IValidator<string>
{
    public static ValidationResult Validate(string value)
        => string.IsNullOrWhiteSpace(value)
            ? new ValidationResult { PropertyName = "", Message = "Required", Severity = ValidationSeverity.Error }
            : ValidationResult.Success;
}
```

### Master-Detail with StateList

```csharp
using YFex.State;
using YFex.State.Collections;
using YFex.State.Mvvm;

public partial class OrderViewModel : MvvmStateObject
{
    [Observable]
    [ObserveItems(Weak = true)] // react to changes inside each line item
    public partial StateList<OrderLineViewModel> Lines { get; set; } = new();

    [Observable]
    public partial OrderLineViewModel? SelectedLine { get; set; }

    [Computed(DependsOn = new[] { nameof(Lines) })]
    public partial decimal Total => Lines.AsSpan()
        .ToArray() // for LINQ -- or iterate manually for zero-alloc
        .Sum(l => l.Subtotal);

    [StateCommand]
    void AddLine()
    {
        var line = new OrderLineViewModel();
        Lines.Add(line);
        SelectedLine = line;
    }
}

public partial class OrderLineViewModel : MvvmStateObject
{
    [Observable] public partial string Product { get; set; }
    [Observable] public partial int Quantity { get; set; }
    [Observable] public partial decimal UnitPrice { get; set; }

    [Computed(DependsOn = new[] { nameof(Quantity), nameof(UnitPrice) })]
    public partial decimal Subtotal => Quantity * UnitPrice;
}
```

### Async Search with Debounce

```csharp
using YFex.State;
using YFex.State.Mvvm;

public partial class SearchViewModel : MvvmStateObject
{
    private readonly ISearchService _search;

    [Observable] public partial string Query { get; set; }
    [Observable] public partial List<SearchResult> Results { get; set; }
    [Observable] public partial bool IsSearching { get; set; }
    [Observable] public partial Exception? SearchError { get; set; }

    [ReactsTo(nameof(Query), CancelPrevious = true)]
    [Debounce(300)]
    [Busy(nameof(IsSearching))]
    [ErrorBucket(nameof(SearchError))]
    async Task OnQueryChangedAsync()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            Results = new();
            return;
        }
        Results = await _search.SearchAsync(Query);
    }
}
```

This stacks four behaviors declaratively:
- **Debounce:** waits 300ms after the user stops typing.
- **CancelPrevious:** if a previous search is still running, cancel it.
- **Busy:** sets `IsSearching = true` for the duration.
- **ErrorBucket:** any exception goes to `SearchError` instead of crashing.

### Dashboard Polling

```csharp
using YFex.State;
using YFex.State.Mvvm;

public partial class DashboardViewModel : MvvmStateObject
{
    private readonly IMetricsApi _api;

    [Observable] public partial DashboardMetrics Metrics { get; set; }
    [Observable] public partial DateTime LastRefreshed { get; set; }

    // Polls every 5 seconds, pauses when the page is deactivated
    [Poll(IntervalMs = 5000, ActiveOnly = true)]
    async Task RefreshMetricsAsync(CancellationToken ct)
    {
        Metrics = await _api.GetCurrentMetricsAsync(ct);
        LastRefreshed = DateTime.UtcNow;
    }
}
```

### Blazor Page with DI

```csharp
// Program.cs
builder.Services.AddTransient<TodoViewModel>();
```

```razor
@page "/todos"
@using YFex.State.Blazor
@inherits StateComponent<TodoViewModel>

<h1>Todos (@ViewModel.Items.Count)</h1>

<input @bind="ViewModel.NewItemText" />
<button @onclick="() => ViewModel.AddCommand.Execute()"
        disabled="@(!ViewModel.AddCommand.CanExecute())">
    Add
</button>

<ul>
    @foreach (var item in ViewModel.Items.AsSpan().ToArray())
    {
        <li>@item.Text</li>
    }
</ul>
```

---

## 7. Testing & Mocking

### StateRecorder\<T\>

`StateRecorder<TVm>` subscribes to all change notifications from a `StateObject` and records them for assertion.

```csharp
using YFex.State.Testing;

[Fact]
public void Name_change_fires_notification()
{
    var vm = new PersonViewModel();
    using var recorder = new StateRecorder<PersonViewModel>(vm);

    vm.Name = "Alice";

    recorder.AssertChanged(nameof(vm.Name));
    recorder.AssertChangeCount(nameof(vm.Name), 1);
}

[Fact]
public void Batch_coalesces_notifications()
{
    var vm = new PersonViewModel();
    using var recorder = new StateRecorder<PersonViewModel>(vm);

    using (vm.BeginUpdate())
    {
        vm.Name = "Alice";
        vm.Name = "Bob";
        vm.Name = "Charlie";
    }

    // Only one notification for Name, despite three assignments
    recorder.AssertChangeCount(nameof(vm.Name), 1);
}

[Fact]
public void Properties_change_in_expected_order()
{
    var vm = new PersonViewModel();
    using var recorder = new StateRecorder<PersonViewModel>(vm);

    vm.Name = "Alice";
    vm.Age = 30;

    recorder.AssertChangedInOrder(nameof(vm.Name), nameof(vm.Age));
}
```

**Full assertion API:**

| Method | Asserts |
|---|---|
| `AssertChanged(name)` | Property changed at least once |
| `AssertNeverChanged(name)` | Property never changed |
| `AssertChangeCount(name, n)` | Property changed exactly `n` times |
| `AssertChangedInOrder(params names)` | Properties changed in the given subsequence order |
| `AssertChanging(name)` | Pre-change notification fired at least once |
| `AssertNeverChanging(name)` | Pre-change notification never fired |
| `AssertChangingThenChanged(name)` | Both pre-change and post-change fired, in correct order |
| `AssertChangingCount(name, n)` | Pre-change notification fired exactly `n` times |
| `Clear()` | Reset all recorded events |

**Raw access:**

```csharp
recorder.Events          // IReadOnlyList<ChangedNotification> -- post-change
recorder.ChangingEvents  // IReadOnlyList<ChangedNotification> -- pre-change
```

---

## 8. Troubleshooting & Gotchas

### Forgetting `partial`

```csharp
// WRONG: generator cannot emit code into a non-partial class
public class MyViewModel : MvvmStateObject
{
    [Observable] public partial string Name { get; set; } // compiler error
}

// CORRECT
public partial class MyViewModel : MvvmStateObject
{
    [Observable] public partial string Name { get; set; }
}
```

The class, **and** any containing types, must be `partial`.

### Wrong Base Class

`[Observable]` and `[StateCommand]` require the class to inherit from `StateObject` (or a subclass like `MvvmStateObject`). Using them on a plain class generates a diagnostic error.

### BatchScope Across Await

```csharp
// WRONG: ref struct cannot cross await boundaries
using (vm.BeginUpdate())
{
    vm.Name = "Alice";
    await Task.Delay(100); // COMPILE ERROR: cannot use BatchScope across await
    vm.Age = 30;
}
```

`BatchScope` is a `ref struct` by design -- this prevents misuse in async code where notifications could be deferred indefinitely.

### Activation Cascade Pitfalls

```csharp
// Problem: Child StateObject is created after Activate()
vm.Activate();
vm.Details = new DetailsViewModel(); // Details is NOT activated!

// Solution: Set children before activating, or activate manually
vm.Details = new DetailsViewModel();
vm.Activate(); // cascades to Details
// OR
vm.Details = new DetailsViewModel();
vm.Details.Activate(); // manual activation
```

### Equality Suppression Surprises

```csharp
vm.Name = "Alice";
vm.Name = "Alice"; // no-op! Equality check prevents notification.

// If you need to force a re-notification, use a batch:
using (vm.BeginUpdate())
{
    vm.Name = null!;
    vm.Name = "Alice"; // now it fires
}
```

### Computed Without DependsOn

If you omit `DependsOn`, the computed property never re-evaluates automatically. Always declare dependencies explicitly.

### StateCommand Concurrent Execution

`IAsyncStateCommand` has a lock-free concurrency gate. If you call `Execute()` while the command is already running, the second call is silently ignored. This is by design to prevent double-submits.

---

## 9. Reference Summary

### All Property Attributes

| Attribute | Target | Parameters | Default |
|---|---|---|---|
| `[Observable]` | Property, Field | -- | -- |
| `[Computed]` | Property | `DependsOn: string[]?` | `null` |
| `[EqualityComparer]` | Property, Field | `typeof(TComparer)` | (auto-detected) |
| `[NotifyOnTaskCompletion]` | Property, Field | -- | -- |
| `[ObserveItems]` | Property | `Weak: bool`, `ActiveOnly: bool` | `false`, `false` |
| `[Propagate]` | Property | -- | -- |
| `[Epoch]` | Property, Field | -- | -- |
| `[Trackable]` | Property, Field | -- | -- |
| `[Snapshot]` | Property, Field | `Group: string?` | `null` |
| `[Persist]` | Property, Field | `Key: string?` | `null` |
| `[LogChanges]` | Property, Field | -- | -- |
| `[ResetTo]` | Property, Field | `DefaultValue: object?` | `null` |
| `[LoadOnInit]` | Property | -- | -- |
| `[IgnoreActivation]` | Property, Field | -- | -- |
| `[ValidateWith]` | Property, Field | `ValidatorType: Type` | (required) |
| `[ValidateAsync]` | Property, Field | `ValidatorType: Type` | (required) |

### All Method Attributes

| Attribute | Parameters | Default |
|---|---|---|
| `[StateCommand]` | `IncludeCancelCommand: bool`, `CancelCommandName: string?`, `TargetProperty: string?` | `false`, `null`, `null` |
| `[ReactsTo]` | `PropertyNames: string[]`, `RunOnMainThread: bool`, `CancelPrevious: bool` | (required), `false`, `false` |
| `[Debounce]` | `Milliseconds: int` | (required) |
| `[Throttle]` | `Milliseconds: int` | (required) |
| `[Poll]` | `IntervalMs: int`, `ActiveOnly: bool` | (required), `true` |
| `[Busy]` | `PropertyName: string?` | `null` |
| `[RequiresAll]` | `PropertyNames: string[]` | (required) |
| `[Gate]` | `PropertyName: string` | (required) |
| `[ErrorBucket]` | `PropertyName: string?` | `null` |
| `[Queue]` | `Name: string?`, `Capacity: int` | `null`, `1` |
| `[Cooldown]` | `Milliseconds: int` | (required) |

### StateObject Protected API

| Member | Description |
|---|---|
| `BeginUpdate() : BatchScope` | Start a batch scope; defers notifications until disposed |
| `NotifyChanging(descriptor)` | Fire pre-change notification (always immediate) |
| `NotifyChanged(descriptor)` | Fire post-change notification (deferred during batch) |
| `SetField<T>(ref field, value, descriptor) : bool` | Compare + set + notify; returns `true` if value changed |
| `SetField<T>(ref field, value, comparer, descriptor) : bool` | Same with custom `IEqualityComparer<T>` |
| `OnActivated()` | Virtual hook -- fires after this object transitions to active |
| `OnDeactivated()` | Virtual hook -- fires after this object transitions to inactive |
| `Validation : ValidationBag` | Lazy-created validation container |
| `IsActive : bool` | Whether this object is currently activated |

### CommandStatus Enum

| Value | Int | Meaning |
|---|---|---|
| `Idle` | 0 | Not running, no previous result |
| `Executing` | 1 | Currently running |
| `Succeeded` | 2 | Last execution completed successfully |
| `Faulted` | 3 | Last execution threw an exception |
| `Canceled` | 4 | Last execution was canceled |

### ChangedNotification Fields

| Field | Type | Description |
|---|---|---|
| `PropertyName` | `string` | Name of the changed property |
| `PropertyId` | `uint` | Dense ID for bitmap operations |
| `Kind` | `ChangeKind` | `PropertyChanged`, `ItemsAdded`, `ItemsRemoved`, `ItemReplaced`, `ItemsCleared`, `ItemsReset` |
| `OldItem` | `object?` | Previous value (for property changes) or removed item |
| `Index` | `int` | Collection index (for collection changes) |
| `Count` | `int` | Number of items affected (for collection changes) |
