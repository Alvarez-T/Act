# 01 — What YFex.State does today (audit 2026-11-19)

Audit of `C:\Dev\YFex\YFex.State` at commit `14a6b65`, the public guide [`public/state.md`](../../public/state.md) and every
consumer found under `C:\Dev\YFex`. "Built" means a generator or runtime type does the work and a test runs it. A test that only
checks an attribute's shape (`tests/YFex.State.Tests/Attributes/AttributesShapeTests.cs`) does not count.

## 1. Attribute by attribute

The State generator has exactly two pipelines (`src/YFex.State.Generator/StateGenerator.cs:22` `[StateCommand]`, `:63`
`[Observable]`). Everything else in `src/YFex.State/Attributes.cs` is either read by another library's generator or by nobody.

### 1.1 Property attributes

| Attribute | Status | Who reads it | Notes |
|---|---|---|---|
| `[Observable]` | ✅ built | State generator | setter, equality, hooks `OnXChanging/OnXChanged`, MVVM args cache, batching, activation cascade |
| `[EqualityComparer]` | ✅ built | State generator | |
| `[NotifyOnTaskCompletion]` | ✅ built | State generator | |
| `[IgnoreActivation]` | ✅ built | State generator | |
| `[Debounce]` on a property / class | ⚠ partly | **Validation** generator (`YFex.Validation.Generator/Parser.Vm.cs:192,223`) | delays only the property's **validation** (forms FM59, `14a6b65`). Its own doc says `[Computed]`/`[ReactsTo]` would follow the pause "when they are built" |
| `[ValidationOptions]`, `[ValidateOn]` | ✅ built | Validation generator | |
| `[ValidateWith]`, `[ValidateAsync]` | ✅ built, `[Obsolete]` | Validation generator | kept "one release" (validation ⚑P1) |
| `[Persist]` | ⚠ different from the guide | **Persistence** generator (`YFex.Persistence.Generator/PersistenceParser.cs:12`, matched by name) | emits `CaptureSnapshot`/`RestoreSnapshot` on demand. The guide promises "persists automatically" — nothing saves on change |
| `[Computed]` | ❌ nothing | nobody (History generator only *skips* it: `UndoableParser.cs:169`) | the model has `ComputedProperties`, always `Empty` (`ObservableParser.cs:202`) |
| `[ObserveItems]` | ❌ nothing | — | |
| `[Propagate]` | ❌ nothing | — | |
| `[Epoch]` | ❌ nothing | — | |
| `[Trackable]` | ❌ nothing | — | forms design (01 line 15, line 560; README FM17) assumes it **exists** and keeps original values |
| `[Snapshot]` | ❌ nothing | — | forms 01 line 15 assumes it exists |
| `[LogChanges]` | ❌ nothing | — | logging 01 line 15 already lists it as "not wired"; logging ⚑L16 (confirmed) turns it into an alias of `[StateLog]` |
| `[ResetTo]` | ❌ nothing | — | |
| `[LoadOnInit]` | ❌ nothing | — | |

### 1.2 Method attributes

| Attribute | Status | Notes |
|---|---|---|
| `[StateCommand]` | ✅ built, with gaps (§2.3) | sync / `Task` / `Task<T>` / `ValueTask` fast path, cancel command, `TargetProperty`, CAS single-run gate, `__OnCanExecute_X` hook (used by Authorization `[RequirePermission]`) |
| `[ReactsTo]` | ❌ nothing | |
| `[Debounce]` on a method | ❌ nothing | |
| `[Throttle]` | ❌ nothing | |
| `[Poll]` | ❌ nothing | `Timing/PollingTimer.cs` exists, used by nobody |
| `[Busy]` | ❌ nothing | |
| `[RequiresAll]` | ❌ nothing | |
| `[Gate]` | ❌ nothing | |
| `[ErrorBucket]` | ❌ nothing | `CommandEmitter.cs:274`: `// Future: route to [ErrorBucket]` |
| `[Queue]` | ❌ nothing | `Commands/CommandQueue.cs` exists, internal, used by nobody (§2.4) |
| `[Cooldown]` | ❌ nothing | |

**17 of the 26 attributes in the public guide do nothing**, and the compiler says nothing: they compile and are ignored.

## 2. Defects in what is built

### 2.1 Confirmed by running code

| # | Defect | Evidence | Effect |
|---|---|---|---|
| B1 ✅ ST-P0 | `DebounceState.NextToken()` never cancels the previous token. `TryReset()` succeeds on an uncancelled source and **removes** the earlier wait's registration instead of cancelling it | `Timing/DebounceState.cs:24`; experiment: two `Task.Delay(50, token)` → both fire. The test `DebounceStateTests.cs:22` (`..._CancelsPrevious`) never checks the first token | Blazor `StateComponent` renders once per change instead of once per 8 ms window; any future debounce built on it fires every call |
| B2 ✅ ST-P0 | `StateList<T>` holds its item relay (`ItemChangedRelay`) **only weakly**: `new WeakChangedHandler(new ItemChangedRelay(this))` | `Collections/StateList.cs:192`; experiment: item change relayed before `GC.Collect()`, not after | item-level changes stop reaching the list's subscribers at a random moment |

### 2.2 Found by reading

| # | Defect | Evidence | Effect |
|---|---|---|---|
| B3 ✅ ST-P0 | Removing an item never unsubscribes the list from it; the indexer setter attaches/detaches nothing and does not activate the new item | `StateList.cs:200` ("cleaned up lazily on the next reap pass" — there is no reap pass), `:47` | removed items keep raising changes through the list (until B2's GC); replaced items are never activated or observed |
| B4 ✅ ST-P0 | `StateCollection` raises `Remove` with the wrong items: it reads the source **after** the removal (`GetRange(n.Index, n.Count)`) | `Mvvm/Collections/StateCollection.cs:51` | `OldItems` holds the item that moved into the slot (or nothing when it was the last); strict listeners (WPF views) can throw |
| B5 ✅ ST-P0 | An item's property change relayed by the list arrives with `Kind = PropertyChanged` and becomes a collection **`Reset`** | `StateCollection.cs:60` | every edit of a line rebuilds the whole list in the UI (selection and scroll lost, cost O(n)) |
| B6 | More than 64 property IDs in a class hierarchy alias silently (`1UL << 64` = bit 0) | `Internal/PropertyBitmap.cs:43`; `state-internals.md` claims "the generator emits a diagnostic" — it does not | in a batch, property 64 fires as property 0 |
| B7 ✅ ST-P0 | 7 of 10 declared diagnostics are never reported: YFEX0101, 0102 (`[Observable]` not partial / not in a `StateObject` → silently skipped), 0210–0212 (`[Computed]`), 0301, 0601 (`[StateCommand]` not partial → silently skipped) | `DiagnosticDescriptors.cs:12–81`; only 0110, 0610, 0611 are used | the user gets an unrelated compiler error (missing partial implementation, missing `XCommand`) or nothing |
| B8 | A failed async command **loses its exception**: `HandleFault(ex)` sets `Status = Faulted` and drops `ex`; nothing logs it | `CommandEmitter.cs:271–275` | a save that fails is invisible except for the status |
| B9 | A `[StateCommand]` method with a parameter other than `CancellationToken` generates a call without arguments | `CommandParser.cs` (no parameter check), `CommandEmitter.cs:298,358` | error inside generated code; `IStateCommand<T>` exists but is never generated (search ⚑SK41 needs it) |
| B10 | Generated commands are not `System.Windows.Input.ICommand` | `CommandEmitter.cs` (class implements `IAsyncStateCommand` + `INotifyPropertyChanged`) | every XAML view model writes a second property per command: `public ICommand SaveUiCommand => field ??= new MvvmCommandAdapter(SaveCommand);` (YFex.UI gallery: 20+ of them, e.g. `OverlaysPageViewModel.cs:27–56`) |
| B11 | `CommandQueue` runs commands on the thread pool (`Task.Run`, `ConfigureAwait(false)`) and keeps **static** named queues for the process lifetime | `Commands/CommandQueue.cs:18,31,35` | breaks the one-owner-thread rule (`state-internals.md`); queues shared by unrelated view models; never freed |
| B12 | Three debounce timers, none testable with a fake clock: `DebounceState`, `ValidationBag.EndPauseAsync` (`Validation/ValidationBag.cs:295`), the Validation generator's `__vDebounced_X` (`Emitter.Vm.cs:149`). The last two start one `Task.Delay` per keystroke and never cancel it | | tests wait in real time; a timer object per keystroke |
| B13 ✅ ST-P0 | A generated command raises `PropertyChanged` only for `Status`; `IsExecuting` is derived from it and never notified | `CommandEmitter.cs` (`Status` setter invokes `__statusArgs` only) | `{Binding SaveCommand.IsExecuting}` — the binding YFex.UI's `docs/agent/mvvm.md` recommends — never updates |
| B14 | Blazor `StateComponent` listens to the view model only; a command's status change is not a view-model change | `YFex.State.Blazor/StateComponent.cs:26` | `disabled="@ViewModel.SaveCommand.IsExecuting"` (the guide's example) does not re-render when the command starts |

### 2.3 Gaps inside `[StateCommand]` (built, but less than the guide says)

- no parameters (B9), not an `ICommand` (B10), exception lost (B8);
- concurrent `Execute()` while running is **dropped** (CAS gate) — there is no queue or cancel-previous mode;
- a sync command has no `Status` and its exception escapes to the caller.

## 3. Public guide drift (`public/state.md`)

| Line | The guide says | Reality |
|---|---|---|
| 35, 132, 163, 240, 794, 823, 843 | `[Computed] public partial string X => …` | not generated; also **not valid C#** — a partial property needs a declaration without body plus an implementation |
| 41, 496–514, 863 | `[ReactsTo]` "wired during activation" | not generated |
| 68–73 | Activate "starts [Poll] timers, triggers [LoadOnInit]"; Deactivate "cancels [ReactsTo] async ops"; then `Dispose()` | none of it; `StateObject` has no `Dispose` |
| 120, 1120 | `[Poll]` `ActiveOnly` default `true`; example `[Poll(IntervalMs = 5000)]` | default `false`; `IntervalMs` is get-only → the example does not compile |
| 560, 603, 785 | `[Busy(nameof(IsLoading))]`, `[ErrorBucket(nameof(LastError))]` | no such constructors → do not compile |
| 355 | `[ResetTo(DefaultValue = "Untitled")]` | `DefaultValue` is get-only → does not compile |
| 624, 1125 | `[Queue]` `Capacity` default 1 | 64 |
| 1118 | `[Debounce]` `Milliseconds` required | default 500 (FM59) |
| 689 | "The generated `SaveCommand` property is already an `ICommand`" | it is not (B10) |
| 388–434, 770 | validation with `[ValidateWith(typeof(…))]` / `IValidator<T>` | obsolete; YFex.Validation rules replace it |
| `state-internals.md` "PropertyBitmap64" | ">64 → generator emits a diagnostic" | no diagnostic (B6) |

## 4. Consumers that already work around the gaps

| Where | Workaround |
|---|---|
| `YFex.UI/.../GalleryShellViewModel.cs:106` | a computed value stored as `[Observable]` fields and set by hand in `OnShellTabsChanged` ("`[Computed]` is not implemented by its generator yet") |
| YFex.UI gallery view models (8 files) | `XxxUiCommand` wrappers around every generated command (B10) |
| forms 07 P4-1 | open finding: "a YFex.State decision: build or remove them" |
| search-capabilities ⚑SK41 | needs `[StateCommand]` with a parameter |
| forms 01/02, README FM17 | design relies on `[Trackable]` (IsModified, original values) and `[Snapshot]` |
| validation 12 line 237 | example `[Computed] public bool NeedsApproval => …` |

## 5. What works well and stays

Property IDs and bitmap batching, the per-class MVVM args caches, the activation cascade, `SetField` variants, the `TaskNotifier`
path, the one-owner-thread rule with its DEBUG check, the D22 validation hooks (`__OnValidate_X`, `__OnBatchValidated`), the
`__OnCanExecute_X` command hook, `StateRecorder<T>`, State.History. The plan builds on these; it does not replace them.
