# 02 — Design and examples

Each section shows **what the developer writes**, **what the generator emits** (simplified; real output uses `global::` names),
the cons, and the decision it depends on (§ README 6). Code that is not marked "generated" is written by the app.

## 1. The shared core

Every missing feature has the same shape: *something changed → run what depends on it, at the right time, on the owner thread*.
Building that once is the plan; the attributes are then small.

### 1.1 One dependents dispatch in `StateObject` (⚑ST6)

Today a setter calls `NotifyChanged(in __desc_X)` and nothing else can react, except the user's `OnXChanged` partial hook — which
only the class that declares `X` can implement. A `[Computed]` in a **derived** class that reads a base property cannot hook
into the base setter.

`StateObject` gets one virtual, called after every change notification (also inside a batch):

```csharp
// YFex.State/StateObject.cs (proposed)
protected void NotifyChanged(in ChangedNotification descriptor)
{
    AssertOwnerThread();
    if (_updateDepth > 0) _pendingMask.Set(descriptor.PropertyId);
    else _handlers.NotifyAll(this, in descriptor);
    OnDependencyChanged(descriptor.PropertyId);        // NEW: dependents of this property
}

/// <summary>Generated per class: a switch over the property IDs that something in this class depends on.
/// Overrides call base last, so a derived class's computed values can depend on base properties.</summary>
protected virtual void OnDependencyChanged(uint propertyId) { }
```

Generated per class (only when the class has dependents):

```csharp
protected override void OnDependencyChanged(uint id)
{
    switch (id)
    {
        case 0u: /* Quantity  */ NotifyChanged(in __desc_Subtotal); break;
        case 1u: /* UnitPrice */ NotifyChanged(in __desc_Subtotal); break;
        case 2u: /* Subtotal  */ __RaiseCanExecuteChanged_Save(); __React_Recalculate(); break;
    }
    base.OnDependencyChanged(id);
}
```

- Inside a batch, `NotifyChanged(__desc_Subtotal)` only sets Subtotal's bit, so a computed value notifies **once** per batch
  without extra code. Reactions are queued and run once in `OnUpdateCompleted` (§3).
- A computed value that feeds another one, a gate or a reaction chains through the same switch (`case 2u` above).
- Cost: one virtual call per change. Gate: setter benchmark ≤ +2 ns (03 §gates). Classes without dependents keep the base no-op.

**Alternative (rejected):** emit the calls inside each setter. Fails for dependents in derived classes and in other assemblies.

*As built (ST-P1):* dependents are raised with `NotifyDerived` (= `NotifyChanged` without the owner-thread check: debounce
timers and validation events raise derived values, they write nothing). The batch drain calls `DispatchPending(uint propertyId)`
once per pending ID instead of passing the bitmap, so generated code no longer depends on the bitmap's width.

### 1.2 IDs for computed and generated properties (⚑ST7)

`[Computed]`, `[Epoch]` counters, `IsModified` and `IsBusy` notify like properties, so they take IDs after the class's
`[Observable]` ones. `ParentPropertyCount` counts all of them in ancestors. The 64-ID ceiling (B6) is removed by a wider
bitmap (`PropertyBitmap128`, two `ulong`s, still no allocation), chosen by the generator when a hierarchy passes 64; no error in
the meantime (user, ⚑ST7). It lands first in ST-P1, before `[Computed]` adds IDs (⚑ST7, 2026-11-25).
*As built:* `PropertyBitmap128` keeps IDs 0–127 inline and IDs from 128 up in words allocated once per object, so there is no
limit at all and no generator choice; tested with 140 properties.

### 1.3 One timer for everything time-based (⚑ST10)

Debounce, throttle, cooldown, poll, the validation pause and Blazor's render coalescing all need "call me back on the owner
thread after N ms, and let me move or cancel that". One primitive replaces the three copies (B12):

```csharp
namespace YFex.State.Timing;

/// <summary>A restartable one-shot timer that calls back on the owner's SynchronizationContext (or inline when there is none).
/// One ITimer per instance, created on first use and moved with Change(): no allocation per keystroke.</summary>
public struct StateTimer
{
    public void Start(StateObject owner, TimeSpan due, Action<StateObject> callback);   // (re)start: replaces a pending call
    public void Cancel();
    public bool IsPending { get; }
    public void Dispose();
}
```

The clock is the BCL `TimeProvider`, read from the owner:

```csharp
// StateObject (proposed)
/// <summary>The clock for [Debounce], [Throttle], [Cooldown], [Poll] and the validation pause. Override to inject one
/// (Mvvm's PageViewModel takes it from DI; tests pass a FakeTimeProvider).</summary>
protected internal virtual TimeProvider TimeProvider => TimeProvider.System;
```

Not `ITrustedClock` (YFex.Time): UI pauses make no security decision. In apps the base `ViewModel` reads the clock from an
internal `ClockLocator` in `YFex.UI.Public` that the platform sets from DI's `TimeProvider` — YFex.Time's `TrustedTimeProvider`
when the app calls `AddYFexTime()` (⚑ST39). Tests use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` (⚑ST34) — no
real-time waits.

### 1.4 Child sources (⚑ST9)

A dependency can be *inside* another object: `Address.City`, the lines of a `StateList`, an item's `Subtotal`. The owner
subscribes to that child (`INotifyChanged`) and turns its changes into `OnDependencyChanged(id of the child property)`:

| Read in a computed / reaction | Owner subscribes to | Triggers on |
|---|---|---|
| `Address.City` (child `StateObject`) | `Address` | any change of `Address` whose name is `City` (path filter) |
| `Lines.Count`, `Lines.AsSpan()` (a `StateList<T>`) | `Lines` | added / removed / replaced / cleared |
| `Lines` with `[ObserveItems]` | `Lines` + item relay | the above + any item property change |

The subscription moves when the property is reassigned (the setter already has the old and new value) and is removed on
`Deactivate` when `ActiveOnly`. `[Propagate]` becomes unnecessary (⚑ST9): its only use was making a parent react to a child.

### 1.5 One class-level generator model (⚑ST31)

Today `[Observable]` and `[StateCommand]` are two pipelines that never see each other. A gate needs the command and the
property; a computed value needs every property of the hierarchy. New pipelines (`ForAttributeWithMetadataName` per attribute:
`Computed`, `ReactsTo`, `Poll`, `Trackable`, …) feed one `StateClassModel` per class (observables, computed, reactions, polls,
commands, dependents map). Equatable records only (no symbols), helpers shared per atlas F20.

## 2. `[Computed]` (⚑ST3, ST4, ST5, ST8, ST32)

### 2.1 You write

```csharp
public partial class OrderLineModel : MvvmStateObject
{
    [Observable] public partial int Quantity { get; set; }
    [Observable] public partial decimal UnitPrice { get; set; }
    [Observable, Debounce(300)] public partial decimal Discount { get; set; }

    [Computed] public decimal Subtotal => Quantity * UnitPrice - Discount;          // deps found in the getter
    [Computed] public bool IsFree => Subtotal == 0;                                   // computed → computed
    [Computed] public bool CanSave => !Validation.HasErrors && Quantity > 0;          // Validation is a source (⚑ST8)
    [Computed(DependsOn = [nameof(UnitPrice)])] public string PriceText => Format(UnitPrice);  // method call: say it
}
```

**Shape (⚑ST3):** a normal, non-partial, get-only property. The getter is the formula; the generator adds only the notification.
The guide's `public partial string X => …` is not valid C# and goes away.

### 2.2 Generated

```csharp
private static readonly ChangedNotification __desc_Subtotal = new() { PropertyName = "Subtotal", PropertyId = 3u };
private static readonly ChangedNotification __desc_IsFree   = new() { PropertyName = "IsFree",   PropertyId = 4u };
private static readonly ChangedNotification __desc_CanSave  = new() { PropertyName = "CanSave",  PropertyId = 5u };
private StateTimer __debounce_Discount;

protected override void OnDependencyChanged(uint id)
{
    switch (id)
    {
        case 0u: /* Quantity  */ NotifyChanged(in __desc_Subtotal); NotifyChanged(in __desc_CanSave); break;
        case 1u: /* UnitPrice */ NotifyChanged(in __desc_Subtotal); NotifyChanged(in __desc_PriceText); break;
        case 2u: /* Discount — [Debounce(300)]: dependents wait for the pause (⚑ST32) */
            __debounce_Discount.Start(this, TimeSpan.FromMilliseconds(300),
                static o => ((OrderLineModel)o).NotifyChanged(in __desc_Subtotal));
            break;
        case 3u: /* Subtotal  */ NotifyChanged(in __desc_IsFree); break;
    }
    base.OnDependencyChanged(id);
}

// Validation as a source: wired when the bag is created (zero cost until then)
protected override void OnValidationCreated(ValidationBag bag)
{
    base.OnValidationCreated(bag);
    bag.ValidationChanged += static … => NotifyChanged(in __desc_CanSave);
}
```

MVVM args caches get entries for the computed IDs, so `PropertyChanged("Subtotal")` reaches XAML with no allocation.

### 2.3 Finding dependencies (⚑ST4)

The generator walks the getter with the semantic model:

| In the getter | Dependency |
|---|---|
| `Quantity` / `this.Quantity` (an `[Observable]` or `[Computed]` of this class or a base) | that property |
| `Address.City` | `Address`, plus child path `City` (§1.4) |
| `Lines.Sum(l => l.Subtotal)` | `Lines` (structural); item changes only with `[ObserveItems]` — warning YFEX0214 otherwise |
| `Validation.…` | the validation source (⚑ST8) |
| a method call on `this`, a static, a field that is not observable | nothing found → warning YFEX0212 unless `DependsOn` names it |

`DependsOn` **adds** to what is found (it never hides an inferred one); it accepts `"Validation"` and one child level
(`"Address.City"`); an unknown name → error YFEX0215. Cycles → error YFEX0210. A getter that writes state → error YFEX0213. A
setter, `partial`, `static` or an auto-property → error YFEX0216. A deeper chain through a child that raises changes
(`Address.Country.Name`) → warning YFEX0211. YFEX0212 covers instance method calls, non-readonly fields and settable
non-observable properties of the class; any `DependsOn` silences it (the author has said what it depends on).

### 2.4 Cons

- Notify-only (⚑ST5): the getter runs on every read; a computed value that did not actually change still notifies. A cached
  form (`[Computed(Cached = true)] public partial decimal Total { get; }` + `private decimal ComputeTotal()`) is the rejected
  alternative for v1: two members per value and an evaluation on every change.
- Inference cannot see through methods or other objects' private state; `DependsOn` is the escape hatch.

## 3. `[ReactsTo]` and method `[Debounce]` / `[Throttle]` (⚑ST11–ST13, ST35)

### 3.1 You write

```csharp
public partial class CustomerSearchModel : MvvmStateObject
{
    [Observable] public partial string? Query { get; set; }
    [Observable] public partial IReadOnlyList<CustomerRow> Results { get; set; } = [];
    [Observable] public partial bool IsSearching { get; set; }
    [Observable] public partial Exception? SearchError { get; set; }

    [ReactsTo(nameof(Query), CancelPrevious = true), Debounce(300), Busy(nameof(IsSearching)), ErrorBucket(nameof(SearchError))]
    private async Task SearchAsync(CancellationToken ct)
        => Results = await _customers.FindAsync(Query, ct);       // continuation is back on the owner thread

    [ReactsTo(nameof(Quantity))]                                  // sync, with old and new value (one property only)
    private void OnQuantity(int oldValue, int newValue) => Log.QuantityChanged(oldValue, newValue);
}
```

Accepted signatures: `void M()`, `void M(T oldValue, T newValue)` (one property), `Task|ValueTask M()`, `Task|ValueTask
M(CancellationToken)`. Anything else → YFEX0221. A name that is not an observable/computed property → YFEX0220.

### 3.2 Generated

```csharp
private StateTimer __debounce_Search;
private CancellationTokenSource? __cts_Search;

protected override void OnDependencyChanged(uint id)
{
    switch (id)
    {
        case 0u: /* Query */
            if (IsInUpdate) __pendingReactions.Set(0); // once at batch end (OnUpdateCompleted)
            else __debounce_Search.Start(this, __300ms, static o => ((CustomerSearchModel)o).__RunSearch());
            break;
    }
    base.OnDependencyChanged(id);
}

private async void __RunSearch()          // async void only here: every exception is caught below
{
    __cts_Search?.Cancel();               // CancelPrevious
    var cts = __cts_Search = new CancellationTokenSource();
    IsSearching = true;                    // [Busy]
    try { SearchError = null; await SearchAsync(cts.Token); }
    catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
    catch (Exception ex) { SearchError = ex; OnReactionFaulted("Search", ex); }   // [ErrorBucket] + hook + Diagnostics probe
    finally { if (__cts_Search == cts) { IsSearching = false; __cts_Search = null; } cts.Dispose(); }
}

// Deactivate: pending debounce cancelled, in-flight reaction cancelled
```

### 3.3 Rules

- **When (⚑ST12):** after the change notification; inside a batch, once at the end of the outermost batch.
- **Active only? (⚑ST11):** reactions run whether or not the object is active (objects that are never activated — child models,
  tests — must still work); `Deactivate` cancels pending and in-flight async reactions. `[ReactsTo(…, ActiveOnly = true)]`
  skips changes while inactive.
- **`RunOnMainThread` (⚑ST13):** removed (no user). Setters already run on the owner thread (one-owner-thread rule); async
  continuations return to it.
- **Order (built):** a trigger's `[Computed]` dependents are raised first (their own reactions run inside that), then the
  trigger's reactions. Batched changes are raised in ID order, then each pending reaction runs once.
- **`CancelPrevious` (built):** cancels the running run's token; whatever it ends with afterwards (a result write the method
  makes itself, an exception) is not reported. Without a token parameter only the reporting is skipped. Without
  `CancelPrevious`, overlapping runs share one token source that only `Deactivate` cancels.
- **`[Busy]` / `[ErrorBucket]` (⚑ST53, built for reactions):** a counter per busy property (overlapping runs keep it true until
  the last ends); the bucket is cleared when a run starts and set on a fault. Targets: YFEX0622 (writable `bool`
  `[Observable]`), YFEX0623 (writable `[Observable]` that holds an `Exception`).
- **Throttle (⚑ST35):** leading and trailing edge — the first change runs at once, changes during the window are folded into one
  last run at its end (the last value is never lost). `Debounce` and `Throttle` on the same method → YFEX0222.
- **Faults:** `protected virtual void OnReactionFaulted(string reaction, Exception ex)` plus a `YFex.Diagnostics` probe (⚑ST16):
  every run is an operation (`ReactionProbe.Describe("Ns.Class.Method", assembly)`, kind `UiAction`, level `Debug`), so work it
  starts nests under it and a fault is recorded with its exception. Sync faults are caught too: a setter never throws a
  reaction's exception.

### 3.4 Cons

- A reaction that writes a property it reacts to loops; the generator warns when it sees that write in the method (YFEX0223), not
  through calls.
- `void M(T oldValue, T newValue)` needs the old value at dispatch time: inside a batch the generator keeps the first old value
  per batch (one field per such reaction).

## 4. Commands (⚑ST14–ST21)

### 4.1 Commands are `ICommand` (⚑ST14)

```csharp
// before (today, YFex.UI gallery)
[StateCommand] private Task DoSmall() => Rename(…);
public ICommand SmallUiCommand => field ??= new MvvmCommandAdapter(DoSmallCommand);   // XAML binds this one

// after
[StateCommand] private Task DoSmall() => Rename(…);    // XAML binds DoSmallCommand directly
```

The generated class also implements `System.Windows.Input.ICommand` explicitly (BCL `System.ObjectModel`, no UI framework
reference). `CanExecuteChanged` of `ICommand` (EventHandler) is raised together with `IStateCommand.CanExecuteChanged`
(Action). `MvvmCommandAdapter` stays for hand-written commands.

### 4.2 Parameters (⚑ST15, search ⚑SK41)

```csharp
[StateCommand] private Task OpenAsync(CustomerRow row, CancellationToken ct) => _nav.OpenAsync(row.Id, ct);
// generated: public IAsyncStateCommand<CustomerRow> OpenCommand { get; }   (+ ICommand with a typed cast)
```

New `IAsyncStateCommand<T>`; one value parameter plus an optional `CancellationToken`. More parameters → YFEX0620.

### 4.3 Failures are visible (⚑ST16)

```csharp
vm.SaveCommand.Status   // Faulted
vm.SaveCommand.Error    // NEW: the exception of the last run (null when it succeeded or is running)
protected override void OnCommandFaulted(string command, Exception ex) => _toast.Show(ex.Message);  // NEW hook
```

`[ErrorBucket(nameof(SaveError))]` copies `Error` into an `[Observable] Exception?` property and clears it when the next run
starts. Every fault also goes to a `YFex.Diagnostics` probe, so a host with YFex.Logging records it. Sync commands get the same
`Error`/`Status` and stop throwing to the binding.

### 4.4 Gates (⚑ST17)

```csharp
[Observable] public partial bool IsOnline { get; set; }
[Computed] public bool CanSave => !Validation.HasErrors && IsModified;

[StateCommand, Gate(nameof(IsOnline), nameof(CanSave))]       // all must be true
private Task SaveAsync(CancellationToken ct) => …;
```

Generated: `CanExecute` adds `&& _owner.IsOnline && _owner.CanSave`; `OnDependencyChanged` raises `CanExecuteChanged` when
`IsOnline` or `CanSave` change. Composes with the existing `__OnCanExecute_Save` hook (Authorization `[RequirePermission]`).
`[RequiresAll]` becomes an `[Obsolete]` alias of the multi-name `[Gate]`. A gate name that is not a `bool` property → YFEX0621.

### 4.5 Busy (⚑ST18)

```csharp
[StateCommand, Busy(nameof(IsSaving))] private Task SaveAsync(CancellationToken ct) => …;   // IsSaving true while it runs
[StateCommand, Busy] private Task LoadAsync(CancellationToken ct) => …;                       // no name: the class's IsBusy
```

Without a name the generator adds `public bool IsBusy` (a generated observable, true while any `[Busy]` method of the class
runs; a counter, so overlapping runs keep it true). `[Busy]` also works on reactions and polls. Attribute change: add the
constructor `BusyAttribute(string propertyName)` (the guide's example then compiles).

### 4.6 Concurrency: drop, queue, cancel previous (⚑ST19)

```csharp
[StateCommand]                                                         // today: a click while running is ignored
[StateCommand(Concurrency = CommandConcurrency.CancelPrevious)]      // search-as-you-type commands
[StateCommand, Queue(Capacity = 10)]                                  // uploads: run one after another, FIFO
[StateCommand, Queue(Name = "printer")]                               // shares one queue with other commands of this object
```

The queue lives in the owning object (not a static), runs on the owner thread, and refuses runs beyond `Capacity`
(`Status` unchanged, `OnCommandRefused` hook). `Commands/CommandQueue.cs` and `ICommandWorkItem` are deleted (B11).

### 4.7 Cooldown, debounce and throttle on commands (⚑ST20, ST21)

```csharp
[StateCommand, Cooldown(2000)] private Task RefreshAsync(CancellationToken ct) => …;   // CanExecute false for 2 s after a run
[StateCommand, Throttle(500)] private void Zoom() => …;                                // repeated key presses
```

Cooldown starts when a run ends (success or failure) and raises `CanExecuteChanged` when it ends (one `StateTimer`).
Debounce/throttle apply to `Execute()` (the UI path); `ExecuteAsync()` (code) runs at once.

## 5. `[Poll]` (⚑ST22)

```csharp
[Poll(5000)]
private async Task RefreshAsync(CancellationToken ct) => Metrics = await _api.GetMetricsAsync(ct);
```

Generated: on `Activate`, a loop on `TimeProvider.CreateTimer`: first run at once (`[Poll(5000, RunOnActivate = false)]` waits
a period first); a tick that arrives while the previous run is still going is skipped; `Deactivate` stops the loop and cancels
the run; faults go to `OnReactionFaulted("Refresh", ex)` (polls are reactions to time). `[Busy]` and `[ErrorBucket]` work.
`ActiveOnly` becomes obsolete: a poll is always tied to activation (the guide already documents that; the code default said
`false`).

## 6. Editing helpers

### 6.1 `[Trackable]` — dirty state with original values (⚑ST23, forms FM17)

```csharp
[Trackable]                                      // NEW target: the class → every [Observable] property
public partial class CustomerEditModel : MvvmStateObject, ITrackable
{
    [Observable] public partial string? Name { get; set; }
    [Observable] public partial string? Email { get; set; }
    [Observable, Trackable(false)] public partial bool IsPanelOpen { get; set; }    // screen state: not a change
}

model.MarkAsClean();               // after load / save: current values become the baseline
model.Name = "Ana";               // IsModified → true (notifies)
model.Name = originalName;        // IsModified → false: compared with the baseline, not a flag
model.RevertChanges();            // back to the baseline (one batch)
model.GetChanges();               // [("Name", "Ana B.", "Ana")] for "you changed 2 fields" summaries (allocates; on demand)
```

Generated: one `__orig_X` field per tracked property, a modified bitmap, `IsModified` as a generated observable, the
`ITrackable` members. `ITrackable` (in YFex.State) lets NavigatR's leave guard and Forms' command bar use any model.
Cons: memory for one copy per tracked field; a tracked `StateList` counts as modified on any mutation and is not reverted
(undo is State.History's job).

### 6.2 `[ResetTo]` (⚑ST25)

```csharp
[Observable, ResetTo("Untitled")] public partial string Title { get; set; }
[Observable, ResetTo(Member = nameof(Today))] public partial DateOnly Date { get; set; }   // non-constant default
// generated: public void ResetTitle(); public void ResetDate(); public void ResetAll()   (one batch)
```

Constructor argument must convert to the property type (YFEX0240). Today's `ResetTo(object?)` keeps working.

### 6.3 `[Epoch]` (⚑ST26)

```csharp
[Observable, Epoch] public partial ImageSource? Preview { get; set; }
// generated: public int PreviewEpoch { get; }  (+1 on every change, notifies) — cache keys, "something changed" bindings
```

## 7. Moved to their owners

| Attribute | Goes to | Why |
|---|---|---|
| `[Persist]` | YFex.Persistence (⚑ST27) | its generator already owns the behaviour; the attribute moves next to it. Auto-save on change ("the guide's promise") is a Persistence option built on `StateTimer` |
| `[LogChanges]` | YFex.Logging `[StateLog]` (⚑ST28; logging ⚑L16 confirmed) | the State generator emits the `YFex.Diagnostics.StateChanges` probe in setters (logging 12 §2), Logging decides roles and masking |
| `[LoadOnInit]` | NavigatR `OnLoaded` / route data loaders (⚑ST29) | page loading already has an owner with off-thread load + owner-thread apply; non-page models use `OnActivated` |
| `[Snapshot]` | `[Trackable]` baseline, State.History, Persistence (⚑ST24) | three owners already cover save/restore |
| `[Propagate]` | child sources (§1.4, ⚑ST9) | |
| `[RequiresAll]` | multi-name `[Gate]` (⚑ST17) | |
| `[ValidateWith]`, `[ValidateAsync]` | YFex.Validation `[ValidateWith<TRule>]` (⚑ST30) | already obsolete |

## 8. Unbuilt attributes (⚑ST2: decided, no warnings)

No warning (user 2026-11-19): every attribute is going to be implemented. The proposal was a YFEX0190 warning per unbuilt
attribute; rejected.

## 9. View integration: bind less by hand (⚑ST42–ST48)

### 9.1 The problem

The generator produces many view-facing members — per command `Status`, `IsExecuting`, `Error`, `CanExecute`, a cancel command;
per object `IsBusy`, `IsModified`, error buckets, computed values. Today a view binds each one by hand:

```xml
<!-- today: one save button, five bindings, plus an XxxUiCommand wrapper in the view model (B10) -->
<YButton Content="Save" Command="{Binding SaveUiCommand}"
         IsEnabled="{Binding !SaveCommand.IsExecuting}" />                 <!-- never updates: B13 -->
<YSpinner IsVisible="{Binding SaveCommand.IsExecuting}" />
<TextBlock Text="{Binding SaveError.Message}" IsVisible="{Binding SaveError, Converter={x:Static ObjectConverters.IsNotNull}}" />
```

Every screen repeats it, and every screen does it slightly differently. YFex.UI's rule 4 forbids controls from referencing
YFex.State, so a control cannot simply look at a generated command.

### 9.2 Contracts both sides can see (⚑ST42)

A few small interfaces in a new leaf package **`YFex.State.Public`** (references core only; revised 2026-11-19, first proposal was
core). YFex.State's generated code implements them; YFex.UI platform projects reference `YFex.State.Public` exactly as they
reference `YFex.NavigatR.Public` — never the State engine. Types keep their namespaces.

```csharp
namespace YFex.State.Commands;     // assembly YFex.State.Public (proposed)

/// <summary>A command whose run state a view can show. Generated [StateCommand]s implement it.</summary>
public interface ICommandState : System.ComponentModel.INotifyPropertyChanged
{
    CommandStatus Status { get; }          // Idle, Executing, Succeeded, Faulted, Canceled (moved to this assembly, type-forwarded)
    bool IsExecuting { get; }
    Exception? Error { get; }              // ⚑ST16
    System.Windows.Input.ICommand? Cancel { get; }   // the generated CancelXCommand, when there is one
}

public interface IBusyState : INotifyPropertyChanged { bool IsBusy { get; } }                  // ⚑ST18 class IsBusy
public interface IErrorState : INotifyPropertyChanged { Exception? LastError { get; } void ClearError(); }   // ⚑ST47
public interface ITrackable : INotifyPropertyChanged { bool IsModified { get; } void MarkAsClean(); void RevertChanges(); }  // ⚑ST23
```

### 9.3 What the view writes then (YFex.UI work, ⚑ST43–ST46)

```xml
<!-- the same save button: one binding. YButton sees ICommandState: disabled + spinner (icon slot, width kept) while running,
     error state with the message as tooltip after a fault, click-to-cancel when the command has a Cancel (opt-in) -->
<YButton Content="Save" Brush="Accent" Command="{Binding SaveCommand}" />

<!-- a region that loads: Loading (skeleton after AppearDelay, no flash under 300 ms), Error (message + Retry = the same
     command), Empty, then Content. Source = a command or a Task property ([NotifyOnTaskCompletion]) -->
<YLoadState Source="{Binding LoadCommand}" IsEmpty="{Binding Results.Count, Converter={x:Static Converters.IsZero}}">
  <YLoadState.Loading><YSkeleton Lines="6" /></YLoadState.Loading>
  <YDataGrid ItemsSource="{Binding Results}" />
</YLoadState>

<!-- busy over any panel without a new control -->
<StackPanel y:Busy.Source="{Binding SearchCommand}"> … </StackPanel>
```

**Page level, zero bindings (⚑ST46):** `YPageHost`, `YPageHeader` and the dock tab header read the page view model (their
`DataContext`/`Page`) through the contracts: `IBusyState.IsBusy` → a progress line under the header; `ITrackable.IsModified` → a
dot on the tab and a default leave concern ("unsaved changes", ⚑ST37); `IErrorState.LastError` → a dismissable banner.
`ShowsPageState="False"` turns it off per host.

Fluent markup gets the same: `YButton().Bind(vm?.SaveCommand)` (unchanged, now with state),
`StackPanel().Busy(vm?.SearchCommand)`, `YLoadState().Source(vm?.LoadCommand)`.

### 9.4 Generator side (State)

- Generated commands implement `ICommand` (⚑ST14) and `ICommandState`, and notify `IsExecuting`, `Error` and `Status` (fixes
  B13).
- A class with `[Busy]` (no name) implements `IBusyState`; a class whose commands/reactions/polls can fault implements
  `IErrorState` (`LastError` = the last fault of any of them, cleared on the next successful run or `ClearError()`); a
  `[Trackable]` class implements `ITrackable`. No attribute needed beyond the ones already there.
- Blazor: `StateComponent` also subscribes to the `ICommandState`s of the view model's commands and re-renders on their changes
  (fixes B14).

### 9.5 Cons

- One more package (`YFex.State.Public`); `CommandStatus` changes assembly (`[TypeForwardedTo]`, same namespace: no source or
  binary break). YFex.UI rule 4 ("controls never reference YFex.State") gets the same exception it has for `YFex.NavigatR.Public`.
- Automatic command feedback on every `YButton` changes how existing screens look; `CommandFeedback="None"` opts out, and the
  default is a decision (⚑ST43).
- One API on every platform (YFex.UI rule 3): Uno and Blazor need the same controls; Avalonia first (⚑ST48).

## 10. Diagnostics summary

| ID | Severity | When |
|---|---|---|
| YFEX0101, 0102, 0601 | Error | now reported (B7) |
| YFEX0210–0213, 0215, 0216 | Error/Warning | computed: cycle, deep chain, hidden dependency, writes state, unknown `DependsOn`, wrong shape (built ST-P1) |
| YFEX0214 | Warning | computed: list items without `[ObserveItems]` (ST-P4) |
| YFEX0220–0223 | Error/Warning | reaction: unknown property, bad signature, debounce + throttle, writes its own trigger |
| YFEX0240 | Error | `[ResetTo]` value does not convert |
| YFEX0620–0623 | Error | command: >1 value parameter, gate not bool, busy target not bool `[Observable]`, error bucket not `Exception?` |
