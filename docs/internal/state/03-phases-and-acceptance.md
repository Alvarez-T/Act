# 03 — Phases, acceptance, log

Each phase builds on its own; tests, AOT smoke and benchmarks are the last step of each phase (rules/00-start-here §3). A phase
starts only when its decisions (README §6) are answered.

## Gates for every phase

| Gate | Command / check |
|---|---|
| Build | `dotnet build state.slnx -c Release` 0 errors, 0 new warnings; `dotnet build-server shutdown` after generator changes |
| Tests | `tests/YFex.State.Tests`, `YFex.State.History.Tests`, `YFex.Validation.Generator.Tests`; YFex.Validation's own tests (D22 hooks) |
| Behaviour, not shape | each attribute has tests that **run** the generated code (fake clock for anything timed: `FakeTimeProvider`, ⚑ST34); generator snapshot tests for emitted text; one compile test per diagnostic |
| AOT | `app/YFex.State.Smoke` published Native AOT, 0 trim/AOT warnings, exercising the phase's attributes |
| Performance | `benchmark/`: `[Observable]` setter without dependents ≤ +2 ns vs `14a6b65`; 0 B allocated per set, per debounced keystroke and per batch |
| Consumers | YFex.UI gallery, YFex.Mvvm, YFex.NavigatR, YFex.Validation, YFex.Persistence build against the phase |

## ST-P0 — Truth and fixes (no new attributes) — built 2026-11-19, committed `b9d9218` (the wider bitmap moved to ST-P1, ⚑ST7)

| Item | Done when |
|---|---|
| `StateTimer` on the owner's `TimeProvider` (⚑ST51 decided: built in ST-P0; ⚑ST10) replaces `DebounceState` (B1); Blazor render coalescing on it | fake-clock tests; Blazor test: N changes in 8 ms → one render; 0 B per restart |
| B2/B3 `StateList` holds its relay strongly, unsubscribes removed/replaced items, indexer attaches/activates | tests survive `GC.Collect()`; removed item's change not relayed |
| B4/B5 `StateCollection` removal args carry the removed item (`OldItem` in the list's notification); item property changes no longer become `Reset` | WPF-style strict listener test |
| B13 generated commands notify `IsExecuting` with `Status` | binding test on `SaveCommand.IsExecuting` |
| B6: no error (⚑ST7, user); the wider bitmap is the first item of ST-P1 (⚑ST7, 2026-11-25) | — |
| B7 YFEX0101/0102/0601 reported | compile tests |
| public guide: build-status banner (no per-attribute warnings, ⚑ST2), `MvvmCommandAdapter` and Blazor statements corrected, `TimeProvider` documented; the non-compiling examples are rewritten with their phases | review |

## ST-P1 — Dependency core and `[Computed]` — built 2026-11-25, committed (State `0be711f`, Mvvm `f27526b`, UI `cf3f1f0`)

| Item | Done when |
|---|---|
| **first:** `PropertyBitmap128`, chosen by the generator when a hierarchy passes 64 IDs (⚑ST7) | test with 70 properties; `BigForm` spike still correct |
| `StateObject.OnDependencyChanged` (02 §1.1; `TimeProvider` + `StateTimer` exist since ST-P0, ⚑ST10) | unit tests, benchmark gate |
| `StateClassModel` pipeline (02 §1.5) | existing generator snapshots unchanged except added members |
| `[Computed]`: inference, `DependsOn`, chains, cycles, base-class deps, batch = one notification, MVVM args | tests incl. derived class computed over base property |
| validation source (⚑ST8), property `[Debounce]` reaches computed values (⚑ST32) | fake-clock tests |
| child path dependency on a child `StateObject` (`Address.City`) | test with reassigned child |
| gallery `GalleryShellViewModel` `PaneTabs`/`WindowTabs` become `[Computed]` | gallery builds, behaviour unchanged |
| clock in apps (⚑ST39): internal `ClockLocator` in `YFex.UI.Public`, set by `YFexAvaloniaServices.Register` from DI's `TimeProvider`; base `ViewModel` overrides `TimeProvider` from it | test: `AddYFexTime()` app → view model's `TimeProvider` is the `TrustedTimeProvider`; `Set(fake)` drives a debounce in a Mvvm test |

## ST-P2 — Reactions — built 2026-11-25 (uncommitted)

`[ReactsTo]` (all signatures), method `[Debounce]`/`[Throttle]` (leading + trailing), `CancelPrevious`, `ActiveOnly`, batch
coalescing, `Deactivate` cancels, `OnReactionFaulted` + Diagnostics probe, `RunOnMainThread` removed (⚑ST13), YFEX0220–0223;
`[Busy]`/`[ErrorBucket]` on reactions + YFEX0622/0623 (⚑ST53).
Done when the guide's "Async Search with Debounce" recipe runs in a test with a fake clock and N keystrokes produce one search.

## ST-P3 — Commands

`ICommand` on generated commands (⚑ST14), parameters (⚑ST15), `Error` + `OnCommandFaulted` + `[ErrorBucket]` (⚑ST16), multi-name
`[Gate]` + `[RequiresAll]` alias (⚑ST17), `[Busy]` + class `IsBusy` (⚑ST18), `Concurrency` + per-instance `[Queue]`, delete
`CommandQueue` (⚑ST19), `[Cooldown]` (⚑ST20), debounce/throttle on `Execute` (⚑ST21), YFEX0620–0623.
Done when the YFex.UI gallery drops its `XxxUiCommand` wrappers (request to YFex.UI) and Authorization's
`ViewModelGateTests` still pass with a `[Gate]` on the same command.

## ST-P4 — Lifetime and child sources

`[Poll]` (⚑ST22), `[ObserveItems]` (structural + item changes, `Weak`, `ActiveOnly`), list sources for computed values,
`[Propagate]` obsolete. Done when the guide's "Master-Detail" recipe (order total over line subtotals) and "Dashboard Polling"
recipe run as tests.

## ST-P5 — Editing helpers

`[Trackable]` on class and property + `ITrackable` (⚑ST23), `[ResetTo]` incl. `Member` (⚑ST25), `[Epoch]` (⚑ST26),
`[Snapshot]` obsolete (⚑ST24). Done when forms 01 §"original values" example works with a real model and NavigatR's leave guard
can read `ITrackable.IsModified` (request, optional).

## ST-P6 — Integrations and clean-up

Validation's generated debounce and `ValidationBag` pause on `StateTimer` + owner `TimeProvider` (request to YFex.Validation);
`[Persist]` moved to YFex.Persistence (⚑ST27); `[LogChanges]` alias of Logging's `[StateLog]` and the setter probe (⚑ST28, with
Logging phase G); `[LoadOnInit]` removed (⚑ST29); `[ValidateWith]`/`[ValidateAsync]` removed (⚑ST30);
public guide rewritten with every example compiled in a sample project (⚑ST34); `state-internals.md` updated.

## ST-P7 — View integration (YFex.UI, Avalonia first)

Core contracts `ICommandState`, `IBusyState`, `IErrorState`, `ITrackable` land with ST-P3/ST-P5 (⚑ST42). Then in YFex.UI:
`YButton` (and command bar, menu items) feedback from `ICommandState` (⚑ST43), `YLoadState` (⚑ST44), `Busy.Source` (⚑ST45),
page host / header / tab state (⚑ST46), fluent `.Busy()`/`.Source()`, a gallery page "State in views", StressKit run on each new
control, Blazor `StateComponent` re-render on command state (B14). Uno and Blazor controls follow in their own phases (⚑ST48).
Done when the gallery's save/search/load demos have **one binding per control** (no wrappers, no hand-bound spinners or error
texts) and a page with a running command and unsaved changes shows its progress line and dirty dot with no binding in the view.

## Decisions each phase needs

| Phase | Decisions to answer first |
|---|---|
| ST-P0 | built. Decided: ⚑ST2, ⚑ST49, ⚑ST50, ⚑ST51, ⚑ST52 |
| ST-P1 | **all decided 2026-11-25:** ⚑ST3–ST8, ⚑ST9 (child `StateObject` part), ⚑ST10, ⚑ST31, ⚑ST32, ⚑ST34, ⚑ST39 — ready to build |
| ST-P2 | **all decided 2026-11-25:** ⚑ST11, ⚑ST12, ⚑ST13 (removed), ⚑ST35, ⚑ST53 — built |
| ST-P3 | ⚑ST14–ST21, ⚑ST33, ⚑ST42, ⚑ST47 |
| ST-P4 | ⚑ST9 (lists), ⚑ST22, ⚑ST36, ⚑ST40 |
| ST-P5 | ⚑ST23–ST26, ⚑ST37 |
| ST-P6 | ⚑ST1, ⚑ST27–ST30, ⚑ST38, ⚑ST41 |
| ST-P7 | ⚑ST43–ST46, ⚑ST48 |

## Order and size

| Phase | Depends on | Relative size |
|---|---|---|
| ST-P0 | — | S |
| ST-P1 | P0 | L (core + generator restructure) |
| ST-P2 | P1 | M |
| ST-P3 | P1 (gates use dependents) | L |
| ST-P4 | P1, P2 | M |
| ST-P5 | P1 | M |
| ST-P6 | P1–P5, Logging phase G for ⚑ST28 | M |
| ST-P7 | P3 (contracts, `Error`), P5 for the dirty dot; YFex.UI work | M |

P2, P3 and P5 are independent of each other after P1.

## Log

| Date | Entry |
|---|---|
| 2026-11-19 | Plan proposed after the audit in [01](01-current-state.md); B1 and B2 confirmed by experiments; decisions ⚑ST1–⚑ST41 pending; atlas F49, F50 added |
| 2026-11-19 | View integration added at the user's request (02 §9, ST-P7, ⚑ST42–ST48); B13/B14 found; ST-P0 decisions ⚑ST49–ST51 |
| 2026-11-19 | User: ⚑ST2 no warnings; ⚑ST50 ok; ⚑ST52 (names of `StateList`/`StateCollection`) added, recommendation: merge into `StateList<T>` |
| 2026-11-19 | **ST-P0 built (uncommitted).** `Timing/StateTimer` (restartable one-shot on `TimeProvider`, owner-context post, stale-call guard, no alloc per restart) + `StateObject.TimeProvider` (⚑ST10 recommended option); `DebounceState` + its tests removed (B1); Blazor `StateComponent` coalesces on `StateTimer` (first change schedules, later ones join; `RenderWindow` 8 ms). `StateList`: relay held strongly by the list, items keep only a weak link (B2); remove/clear/replace unsubscribe and (de)activate (B3); removal carries `OldItem` when someone listens. `StateCollection`: Remove event carries the removed item (B4), item property changes ignored (B5, ⚑ST50). Commands notify `IsExecuting` with `Status` (B13). `PlacementDiagnostics`: YFEX0101/0102/0601 reported as errors at the member (B7, ⚑ST49); YFEX0102 message and YFEX0110 help link fixed. Smoke app: `PublishAot` + Scenario 10 (StateTimer). Tests: `StateTimerTests` (7), `StateListRelayTests` (8), `IsExecutingNotificationTests` (2), `StatePlacementDiagnosticTests` (4), allocation test for restarts. Gates: State 382/382 Release, 383/383 Debug; History 65/65; generator 54/54; Native AOT win-x64 0 IL/AOT warnings, smoke passes; consumers build: Validation, Persistence, NavigatR, Mvvm, YFex.UI; Authorization solution fails at restore with NU1510 in YFex.Installations/YFex.Networking (unrelated package-pruning warnings as errors), its `ViewModelGateTests` pass 3/3. Not verified: Blazor rendering (no Blazor test project); Linux. |
| 2026-11-19 | **⚑ST52 built (user: merge).** `StateList<T>` is now `IList<T>`, `IList`, `INotifyCollectionChanged` and `INotifyPropertyChanged` (`Count`, `Item[]`); event args only while a view listens (0 B per insert/remove without listeners, tested); `Insert` supported; removal/replace events carry the right items. `StateCollection<T>`, `StateListExtensions.ToStateCollection()` and their 13 tests removed (no user outside YFex.State); `StateListBindingTests` (12) added. Gates: State 381/381, History 65/65, generator 54/54; Mvvm and YFex.UI build. |
| 2026-11-25 | **ST-P1 decisions answered (user).** Recommended options for ⚑ST3, ST4, ST5, ST6, ST8, ST9 (child objects), ST31, ST34 (Microsoft `FakeTimeProvider`, not `YFex.Time.Testing`); ⚑ST7: wider bitmap first in ST-P1; ⚑ST10: keep ST-P0's `StateTimer` + `StateObject.TimeProvider`; ⚑ST32: as forms ⚑FM59 (value immediate, dependents and validation after the pause, messages after `AfterPause`); ⚑ST39 revised: internal `ClockLocator` in `YFex.UI.Public` set by the platform from DI (YFex.Time's clock when `AddYFexTime()` is used), read by the base `ViewModel` — a locator inside Mvvm could not be set, since the platforms do not reference Mvvm. |
| 2026-11-25 | **ST-P1 built** (committed: State `0be711f`, Mvvm `f27526b`, UI `cf3f1f0`, docs `ca1a3c1`). Runtime: `PropertyBitmap128` (0–127 inline, 128+ in words allocated once per object: no limit, ⚑ST7); `NotifyChanged` → `NotifyDerived` → virtual `OnDependencyChanged(uint)` (⚑ST6); batch drain calls `DispatchPending(uint propertyId)` per pending ID (breaking for hand-written overrides: only a test helper had one); `AttachChildSources()` (first `Subscribe`, `Activate`), `OnChildChanged(uint, in ChangedNotification)` and `Internal/ChildSource` (⚑ST9 child objects). Generator: `[Computed]` pipeline (`ComputedParser`: getter walk with the semantic model, field-backed names by fallback, child paths one level, `Validation` source, `DependsOn` adds incl. `"Address.City"`), `StateIds` (IDs from symbols for base properties; `ParentPropertyCount` now counts `[Computed]` too), one `StateClassModel` per class from the `[Observable]` + `[Computed]` pipelines (⚑ST31; commands join in ST-P3 with gates), `DependentsEmitter` (switch, per-source `StateTimer` for `[Debounce]` sources ⚑ST32, child sources, `OnValidationCreated` subscription ⚑ST8), MVVM args caches cover computed IDs; diagnostics YFEX0210–0213, 0215, 0216 (0214 is ST-P4). Clock (⚑ST39): internal `ClockLocator` in `YFex.UI.Public`, set by `YFexAvaloniaServices.Register` from DI's `TimeProvider`; `YFex.Mvvm.ViewModel` overrides `TimeProvider => ClockLocator.Current`. Gallery: `PaneTabs`/`WindowTabs` are `[Computed]` (⚑ST41 part). Tests: `ComputedTests` (14: chains, batch once, derived over base, Validation source, debounce with fake clock, DependsOn, child path incl. reassignment and first subscriber, field-backed source, generic class, 0 B per set, restart allocation), `WideModelTests` (3: 140 properties, bitmap words, 0 B), `ComputedDiagnosticTests` (9), YFex.Avalonia `ViewModelClockTests` (2). Gates: State 413/413 Release, 414/414 Debug; History 65/65; generator 59/59; AOT win-x64 0 IL/AOT warnings, smoke Scenario 11 passes; Validation, Persistence, NavigatR, Mvvm, FormsSpike build; YFex.Avalonia.Tests (Debug) 770/771 — the one failure, `GeometryEditorTests.Renderer_FallsBackWhenThemeBrushesAreTransparent` (accent fallback colour), is in the geometry renderer, which ST-P1 does not touch; not confirmed against a clean tree. Not run: the setter benchmark (the repo's `benchmark/` has no project; allocation gates pass); Linux; Blazor. YFex.UI Release build was blocked by a running gallery (PID 39452) holding its output, so YFex.UI was built and tested in Debug. |
| 2026-11-25 | **ST-P2 decisions answered (user).** Recommended options for ⚑ST11, ⚑ST12, ⚑ST35; ⚑ST13: `RunOnMainThread` removed, not obsoleted (no code used it); ⚑ST53 added and decided: the reaction side of `[Busy]`/`[ErrorBucket]` (and their diagnostics) moves into ST-P2, because the acceptance recipe uses them. |
| 2026-11-25 | **ST-P2 built (uncommitted).** Runtime: `StateObject.OnReactionFaulted(string, Exception)` (virtual hook), generated-only `RunPendingReactions()` (after the batch's notifications, ⚑ST12) and `CancelReactions()` (from `Deactivate`, ⚑ST11); `Internal/ReactionProbe` (one `YFex.Diagnostics` descriptor per reaction, kind `UiAction`, `Debug`; each run is an operation). Attributes: `ReactsTo.ActiveOnly` added, `ReactsTo.RunOnMainThread` removed (⚑ST13); `[Busy(name)]`/`[ErrorBucket(name)]` constructors; docs on `[Debounce]`/`[Throttle]`. Generator: `ReactionParser` (shapes, triggers from `StateIds` incl. base and computed members, YFEX0220–0223, YFEX0622/0623), reactions join `StateClassModel` (a class with only reactions now gets a model; it emits no `DispatchPending`), `DependentsEmitter` calls `__Trigger_M()` after a trigger's computed dependents (and from a debounced trigger's pause, ⚑ST32), `ReactionEmitter` (trigger → batch flag / schedule → debounce `StateTimer`, throttle leading + trailing with the window started before the run, run with busy counter, error bucket, probe and hook; async `async void` runner with `CancelPrevious` or a shared token source), setters keep the first old value for `M(old, new)`. `StateIds.Member` knows read-only and task-notifier members. Tests: `ReactionTests` (20: order, batch once, old/new incl. batch, sync fault, property and method debounce, throttle, never-activated object, `ActiveOnly`, `Deactivate` drops timers and cancels runs, derived over base, guide recipe ×3 incl. `CancelPrevious` and the error bucket, overlapping busy, probe with a recording engine, 0 B per set with a sync reaction, debounced keystroke restarts), `ReactionDiagnosticTests` (20). Gates: State 434/434 Debug, 433/433 Release; History 65/65; generator 79/79; Native AOT win-x64 0 IL/AOT warnings, smoke Scenario 12 passes; `state.slnx` Release 0 errors, 16 warnings all pre-existing. Consumers build in Release, 0 errors: Validation, Persistence, NavigatR, Mvvm, YFex.UI (gallery included); their State-generated warnings are the existing gallery `IsLoading` hides `PageViewModel.IsLoading` (CS0108) and non-nullable properties without initializer (CS8604), none from reactions; consumer test suites not run (no consumer uses `[ReactsTo]`). Test lesson: xunit sets its own synchronization context right before the test method (after the constructor), and waits for every `async void` started under it, so reaction tests clear the context inside each method. Not run: the setter benchmark (no `benchmark/` project); Linux; Blazor. |
