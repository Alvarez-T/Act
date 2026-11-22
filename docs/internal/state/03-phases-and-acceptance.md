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

## ST-P0 — Truth and fixes (no new attributes) — built 2026-11-19 (uncommitted), except the wider bitmap (⚑ST7 timing open) and ST52 names

| Item | Done when |
|---|---|
| `StateTimer` on the owner's `TimeProvider` (⚑ST51 decided: built in ST-P0; ⚑ST10) replaces `DebounceState` (B1); Blazor render coalescing on it | fake-clock tests; Blazor test: N changes in 8 ms → one render; 0 B per restart |
| B2/B3 `StateList` holds its relay strongly, unsubscribes removed/replaced items, indexer attaches/activates | tests survive `GC.Collect()`; removed item's change not relayed |
| B4/B5 `StateCollection` removal args carry the removed item (`OldItem` in the list's notification); item property changes no longer become `Reset` | WPF-style strict listener test |
| B13 generated commands notify `IsExecuting` with `Status` | binding test on `SaveCommand.IsExecuting` |
| B6: no error (⚑ST7, user); the wider bitmap lands here if ⚑ST7's timing question is answered "ST-P0" | test with 70 properties |
| B7 YFEX0101/0102/0601 reported | compile tests |
| public guide: build-status banner (no per-attribute warnings, ⚑ST2), `MvvmCommandAdapter` and Blazor statements corrected, `TimeProvider` documented; the non-compiling examples are rewritten with their phases | review |

## ST-P1 — Dependency core and `[Computed]`

| Item | Done when |
|---|---|
| `StateObject.OnDependencyChanged`, `TimeProvider`, `StateTimer` (02 §1.1, §1.3) | unit tests, benchmark gate |
| `StateClassModel` pipeline (02 §1.5) | existing generator snapshots unchanged except added members |
| `[Computed]`: inference, `DependsOn`, chains, cycles, base-class deps, batch = one notification, MVVM args | tests incl. derived class computed over base property |
| validation source (⚑ST8), property `[Debounce]` reaches computed values (⚑ST32) | fake-clock tests |
| child path dependency on a child `StateObject` (`Address.City`) | test with reassigned child |
| `PropertyBitmap128` | test with 70 properties |
| gallery `GalleryShellViewModel` `PaneTabs`/`WindowTabs` become `[Computed]` | gallery builds, behaviour unchanged |

## ST-P2 — Reactions

`[ReactsTo]` (all signatures), method `[Debounce]`/`[Throttle]` (leading + trailing), `CancelPrevious`, `ActiveOnly`, batch
coalescing, `Deactivate` cancels, `OnReactionFaulted` + Diagnostics probe, `RunOnMainThread` obsolete, YFEX0220–0223.
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
| ST-P0 | built. Decided: ⚑ST2, ⚑ST49, ⚑ST50, ⚑ST51, ⚑ST52; ⚑ST10 built with the recommendation (confirm in ST-P1) |
| ST-P1 | ⚑ST3–ST8, ⚑ST9 (child `StateObject` part), ⚑ST10, ⚑ST31, ⚑ST32, ⚑ST34, ⚑ST39 |
| ST-P2 | ⚑ST11–ST13, ⚑ST35 |
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
