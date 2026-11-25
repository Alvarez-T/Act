# YFex.State — finish what the attributes promise — Plan

> **Status:** PROPOSED 2026-11-19. Docs only, no code. Decisions ⚑ST1–⚑ST52 (§6): decided ⚑ST2, ⚑ST49, ⚑ST50, ⚑ST51, ⚑ST52 (2026-11-19) and every ST-P1 decision — ⚑ST3–ST8, ⚑ST9 (child objects), ⚑ST10, ⚑ST31, ⚑ST32, ⚑ST34, ⚑ST39 (2026-11-25); every ST-P2
decision — ⚑ST11, ⚑ST12, ⚑ST13 (removed), ⚑ST35, ⚑ST53 (2026-11-25); ST-P0 built and committed (`b9d9218`); **ST-P1 built and
committed 2026-11-25** (State `0be711f`, Mvvm `f27526b`, UI `cf3f1f0`); **ST-P2 built 2026-11-25 (uncommitted)**; the rest
pending, answered phase by phase. View integration (02 §9,
> ⚑ST42–ST48) added the same day at the user's request: views should bind generated state directly, with YFex.UI types.
> **Repo:** `C:\Dev\YFex\YFex.State` (`state.slnx`). Existing internals: [state-internals](../state-internals.md),
> [state-history-internals](../state-history-internals.md); public guide [state](../../public/state.md).
> **Origin:** 2026-11-19. A YFex.UI session found that `[Computed]`, `[ReactsTo]` and method `[Debounce]` are declared but not
> generated (also forms 07 finding P4-1). The user asked for a full check and a plan to build **every** function of YFex.State.
> The audit ([01](01-current-state.md)) found that 17 of the 26 attributes in the public guide do nothing, plus 12 defects in
> what is built (two confirmed by experiments). Atlas findings F49, F50.

## Files

| File | What it covers |
|---|---|
| README (this file) | why, scope, shape, ecosystem fit, requests to other libraries, decisions, phases |
| [01 — Current state](01-current-state.md) | every attribute (built / partly / nothing, with file:line), defects B1–B12, public-guide drift, consumers' workarounds |
| [02 — Design and examples](02-design-and-examples.md) | the shared core (dependents dispatch, IDs, `StateTimer`, child sources, class model), then each feature: what you write, what is generated, cons; §9 view integration (core contracts + YFex.UI controls that read them) |
| [03 — Phases and acceptance](03-phases-and-acceptance.md) | ST-P0…ST-P6, gates, done-when, log |

## 1. Why

| Today | Problem |
|---|---|
| The guide documents `[Computed]`, `[ReactsTo]`, `[Poll]`, `[Busy]`, `[Gate]`, `[ErrorBucket]`, `[Trackable]`… with examples | none is generated; code using them compiles and silently does nothing |
| Attribute tests check shapes only (`AttributesShapeTests`) | gave the impression the features work |
| Forms (FM17), search (SK41), validation 12 and the YFex.UI gallery design on top of these attributes | each writes a workaround (hand-set "computed" fields, `XxxUiCommand` wrappers) or is blocked |
| `DebounceState` does not debounce; `StateList` stops relaying item changes after a GC; failed commands lose their exception | defects in the built part (01 §2) |

## 2. Scope

**In:** every function the public guide describes — computed values, reactions, debounce/throttle, polling, command gates, busy
flags, error capture, queues, cooldown, child/list observation, dirty tracking, reset, epochs — plus what consumers already
need: commands as `ICommand`, commands with a parameter, visible command failures, one fake-clock-testable timer.
Defects B1–B12. A public guide whose examples compile.

**Out / elsewhere:**

| Function | Owner |
|---|---|
| saving `[Persist]` properties | YFex.Persistence (⚑ST27) |
| logging property changes (`[LogChanges]`) | YFex.Logging `[StateLog]` (⚑ST28, logging ⚑L16) |
| loading a page's data (`[LoadOnInit]`) | YFex.NavigatR `OnLoaded` / data loaders (⚑ST29) |
| undo / redo | YFex.State.History (unchanged) |
| retries, timeouts, breakers inside a command | YFex.Resilience, called by the method (⚑ST33) |
| background schedules | YFex.Jobs (⚑ST36) |

## 3. Shape

```
 setter X ──► NotifyChanged(desc X) ──► handlers (MVVM, Blazor, lists)          ◄── unchanged
                    │
                    └─► OnDependencyChanged(id X)   (generated switch, chained base ← derived)
                          ├─ [Computed] Y          → NotifyChanged(desc Y)      → chains to Y's dependents
                          ├─ [ReactsTo] M          → now | batch end | StateTimer (debounce/throttle) → M(ct) on owner thread
                          ├─ [Gate] on command C   → C.CanExecuteChanged
                          ├─ [Trackable]           → IsModified (compare with baseline)
                          └─ [Epoch]               → XEpoch++

 child sources: Address (StateObject), Lines (StateList, [ObserveItems]) ──► OnDependencyChanged(id of the child property)
 Validation bag changes ─────────────────────────────────────────────────► computed values that read Validation
 Activate / Deactivate ──► start/stop [Poll] loops, cancel pending reactions and debounces
 time: StateTimer on the owner's TimeProvider (FakeTimeProvider in tests)
```

## 4. Ecosystem fit

Every library in the [infrastructure map](../../agent/infrastructure-map.md):

| Library | Outcome | Role or reason | Decision |
|---|---|---|---|
| YFex core | used | `YFex.Diagnostics` probes for command/reaction/poll faults and for `StateChanges` (logging 12). View-state contracts considered here and placed in `YFex.State.Public` instead | ⚑ST16, ⚑ST28, ⚑ST42 |
| YFex.Persistence | **better placement** | `[Persist]` is read only by its generator; the attribute should live there; auto-save is its option | ⚑ST27 |
| YFex.Data, YFex.Backup | not a fit | database access and backups; no UI state |
| YFex.Tenancy, YFex.CustomLogic | not a fit | UI state has no scope; no per-tenant implementation |
| YFex.Validation | used; **fits, lacks a feature** | validation results become a computed source (built: `ValidationChanged`); its debounce/pause should use `StateTimer` | ⚑ST8, ⚑ST38 |
| YFex.Rules | used | a `[Computed]` reads a verdict (validation 12 `NeedsApproval`); no change |
| YFex.Expressions | not a fit | formulas here are C# getters; end-user formulas belong to Composer |
| YFex.Composer (design) | not a fit now | composed screens generate their own models; revisit when Composer has a repo |
| YFex.Xml | not a fit | |
| YFex.Cqrs | not a fit | `[StateCommand]` is a UI command; Mvvm calls Cqrs; `ServerOutcome` already in State |
| YFex.Messaging | not a fit | `[ReactsTo]` is inside one object; bus subscriptions belong to Mvvm |
| YFex.NavigatR | used; **fits** | drives Activate/Deactivate (polls, reactions); owns page loading; leave guard could read `ITrackable` | ⚑ST29, ⚑ST37 |
| YFex.Mvvm | used | the base `ViewModel` reads the app's clock from an internal `ClockLocator` (in `YFex.UI.Public`) | ⚑ST39 |
| YFex.UI | consumer; **fits, lacks a feature** | gallery drops `XxxUiCommand` wrappers and hand-set computed fields; controls that show command/busy/error/dirty state from the core contracts (`YButton` feedback, `YLoadState`, `Busy.Source`, page host state) | ⚑ST41, ⚑ST43–ST46, ⚑ST48 |
| YFex.Authentication | not a fit | |
| YFex.Authorization | used | `[RequirePermission]` uses the existing `__OnCanExecute_X` hook; `[Gate]` composes with it, no change | ⚑ST17 |
| YFex.Capabilities, YFex.Search | used; **lacks a feature here** | one command-name rule for both generators (search ⚑SK21, atlas F20); commands with a parameter (⚑SK41) | ⚑ST15, ⚑ST31 |
| YFex.Files, YFex.Web, YFex.Cryptography | not a fit | |
| YFex.Privacy | not a fit | `[Trackable]` keeps originals in memory only; masking of logged values is Logging's with Privacy (L16) |
| YFex.Logging | **better placement** | `[LogChanges]` → `[StateLog]`; State emits the probe | ⚑ST28 |
| YFex.Actors, YFex.Audit | not a fit | UI state is not audited; "who changed it" is Logging 12's refinement |
| YFex.Time | used indirectly | State and Mvvm take the BCL `TimeProvider`, no reference; `AddYFexTime()` makes the app's `TimeProvider` its `TrustedTimeProvider`, which reaches view models through `ClockLocator` (⚑ST39). No `ITrustedClock` use: no security decision depends on a UI pause. `YFex.Time.Testing` is not used by State tests (it wraps the same `FakeTimeProvider`, ⚑ST34) | ⚑ST10, ⚑ST39 |
| YFex.Networking | **fits, optional** | polls could pause while the network is down | ⚑ST40 |
| YFex.Installations, YFex.Offline | not a fit | offline queuing is shown through `ServerOutcome.Queued` |
| YFex.Resilience | **fits, kept outside** | retry/timeout inside a command method; State adds no retry | ⚑ST33 |
| YFex.Orchestration, YFex.EventSourcing | not a fit | |
| YFex.Jobs | **fits by name, not by need** | `[Poll]` is per object, activation-scoped, owner thread; Jobs is per host | ⚑ST36 |
| YFex.Cluster, YFex.Provisioning, YFex.Hosting, YFex.Installer | not a fit | server side |
| YFex.Notifications | not a fit | toasts are YFex.UI's |
| YFex.Licensing | not a fit | gating reaches commands through Authorization's availability hook |
| YFex.Automation, YFex.Inference | not a fit | |
| YFex.System, YFex.Profiling, YFex.Security, YFex.Devices | not a fit | |
| outside: `Microsoft.Extensions.TimeProvider.Testing` | **used (tests only)** | `FakeTimeProvider` for every timed test | ⚑ST34 |
| outside: CommunityToolkit.Mvvm generators | not a fit | similar attributes, but no batching/IDs/activation and a third-party lock-in (ecosystem thesis) | ⚑ST1 |

## 5. Requests to other libraries

| Library | Change | Breaking | Decision | Phase that needs it |
|---|---|---|---|---|
| YFex.Validation | generated `[Debounce]` checks and `ValidationBag` pause on `StateTimer` + owner `TimeProvider` (one timer, fake-clock tests) | no | ⚑ST38 | ST-P6 |
| YFex.Persistence | own the `[Persist]` attribute (`YFex.State.PersistAttribute` → `YFex.Persistence`); optional debounced auto-save | **yes** (namespace) | ⚑ST27 | ST-P6 |
| YFex.Logging | `[StateLog]` + `YFex.Diagnostics.StateChanges` (designed, logging ⚑L16, phase G) | no | ⚑ST28 | ST-P6 |
| YFex.NavigatR | leave guard raises a concern when the page model is `ITrackable` and modified | no (optional) | ⚑ST37 | after ST-P5 |
| YFex.UI (`YFex.UI.Public` + platforms) | new **internal** `ClockLocator` next to `MessageCatalogLocator` (`Set`/`Reset`/`Current`, default `TimeProvider.System`; visible only through the existing `InternalsVisibleTo` to Mvvm and the platforms); `YFexAvaloniaServices.Register` (then Uno, Blazor) sets it from DI's `TimeProvider` | no | ⚑ST39 | ST-P1 |
| YFex.Mvvm | base `ViewModel` (every view model, not only pages) overrides `TimeProvider => ClockLocator.Current` | no | ⚑ST39 | ST-P1 |
| YFex.UI | gallery: bind generated commands directly; `[Computed]` for shell tab placement | no | ⚑ST41 | ST-P1, ST-P3 |
| YFex.UI (Avalonia, then Uno/Blazor) | `YButton`/command-bar/menu feedback from `ICommandState`; `YLoadState`; `Busy.Source` attached; `YPageHost`/`YPageHeader`/tab header page state; fluent `.Busy()`, `.Source()`; gallery page + StressKit | no (new; button feedback changes visuals, ⚑ST43) | ⚑ST43–ST46, ⚑ST48 | ST-P7 |
| YFex.State (new package `YFex.State.Public`) | `ICommandState`, `IBusyState`, `IErrorState`, `ITrackable`, `CommandStatus` (moved assembly, same namespace, type-forwarded); YFex.UI platform projects reference it like `YFex.NavigatR.Public` (YFex.UI rule 4 gets the same exception) | no | ⚑ST42 | ST-P3 |
| YFex.Capabilities | shared command-name rule incl. parameters | no | search ⚑SK21/⚑SK41 | ST-P3 |
| forms docs | 01/README: `[Trackable]` is planned (ST-P5), `[Snapshot]` replaced | no | ⚑ST23, ⚑ST24 | now |

## 6. Decisions

Pending unless marked decided. "Rec." = recommended option.

| # | Question | Rec. | Alternatives | Where |
|---|---|---|---|---|
| ST1 | Build everything the guide lists? | **every function, but some through their owner** (§2 Out) and some attributes merged (`[RequiresAll]`→`[Gate]`, `[Propagate]`→child sources, `[Snapshot]`→`[Trackable]`) | build all 26 attributes literally in State; remove the unbuilt ones; adopt CommunityToolkit.Mvvm | 02 §7 |
| ST2 | Until built | **decided 2026-11-19 (user): no warnings** — every attribute is going to be implemented. (Proposed: warning YFEX0190 per unbuilt attribute + a status column)| warning YFEX0190 (rejected); `[Obsolete]` | 02 §8 |
| ST3 | `[Computed]` shape | **decided 2026-11-25 (user): normal get-only property, the getter is the formula** | `partial` property + compute method (the guide's form is not valid C#) | 02 §2.1 |
| ST4 | Finding dependencies | **decided 2026-11-25 (user): inferred from the getter; `DependsOn` adds; warnings for method calls / deep chains** | `DependsOn` required (the guide's gotcha) | 02 §2.3 |
| ST5 | Computed caching | **decided 2026-11-25 (user): notify only (getter runs on read)** | cached value + compare (`Cached = true`, needs partial + method) | 02 §2.4 |
| ST6 | Where dependents run | **decided 2026-11-25 (user): one virtual `StateObject.OnDependencyChanged(uint)`, generated switch per class, chained** | calls inside each setter (breaks derived/other-assembly dependents) | 02 §1.1 |
| ST7 | IDs and bitmap | **decided: no YFEX0103 error (user 2026-11-19); the wider bitmap (`PropertyBitmap128`, chosen when a hierarchy passes 64 IDs) is the first item of ST-P1, before `[Computed]` adds IDs (user 2026-11-25).** Computed and generated properties take IDs | diagnostic only; `InlineArray` of any width | 02 §1.2 |
| ST8 | Validation as a source | **decided 2026-11-25 (user): `Validation.…` in a getter subscribes to `ValidationChanged`** | `DependsOn = ["Validation"]` by hand | 02 §2.2 |
| ST9 | Child objects and lists | **child `StateObject` part decided 2026-11-25 (user): child path sources** (`Address.City`: subscribe to the child, filter by name, move on reassignment), `[Propagate]` obsolete. **Lists part pending (ST-P4):** list structure always, items with `[ObserveItems]` | build `[Propagate]` as re-raising child names on the parent | 02 §1.4 |
| ST10 | Timer and clock | **decided 2026-11-25 (user): keep what ST-P0 built** — one `StateTimer` (restartable, owner thread, no alloc per restart) on `StateObject.TimeProvider` (virtual, default `TimeProvider.System`) | static ambient clock; keep `DebounceState` + `Task.Delay` | 02 §1.3 |
| ST11 | Reactions on inactive objects | **decided 2026-11-25 (user): run always; `Deactivate` cancels pending/in-flight; `ActiveOnly = true` option** | only while active (the guide) — breaks never-activated child models and tests | 02 §3.3 |
| ST12 | Reaction timing | **decided 2026-11-25 (user): after the notification; once at the end of a batch** | before notification; per set inside a batch | 02 §3.3 |
| ST13 | `RunOnMainThread` | **decided 2026-11-25 (user): removed** — no code used it (only a shape test); setters run on the owner thread and async continuations return to it. (Proposed: obsolete) | obsolete; build a marshal | 02 §3.3 |
| ST14 | Commands as `ICommand` | generated class implements `System.Windows.Input.ICommand` (BCL) | keep `MvvmCommandAdapter` wrappers; generate a second `XxxUiCommand` | 02 §4.1 |
| ST15 | Command parameters | one value parameter (+ optional `CancellationToken`), new `IAsyncStateCommand<T>` | none (today, B9) | 02 §4.2 |
| ST16 | Command failures | `Error` on the command, `OnCommandFaulted` hook, Diagnostics probe; `[ErrorBucket]` copies to a property | only `[ErrorBucket]` | 02 §4.3 |
| ST17 | Gates | `[Gate(params names)]` = all true, raises `CanExecuteChanged`; `[RequiresAll]` obsolete alias; composes with `__OnCanExecute_X` | keep both attributes | 02 §4.4 |
| ST18 | Busy | ctor with a property name; no name → generated class `IsBusy` (counter) | name required | 02 §4.5 |
| ST19 | Concurrency | `StateCommand(Concurrency = Drop \| CancelPrevious)` + per-instance `[Queue(Name, Capacity)]` on the owner thread; delete `CommandQueue` | fix the static thread-pool queue | 02 §4.6 |
| ST20 | Cooldown | from the end of a run (any outcome); `CanExecuteChanged` when it ends | from the start; success only | 02 §4.7 |
| ST21 | Debounce/throttle on commands | apply to `Execute()` (UI); `ExecuteAsync()` runs at once | both | 02 §4.7 |
| ST22 | `[Poll]` | always activation-scoped, first run at once (`RunOnActivate`), overlapping ticks skipped, `ActiveOnly` obsolete | keep `ActiveOnly`; pause while the page is hidden (NavigatR visibility) | 02 §5 |
| ST23 | `[Trackable]` | compare with a baseline per property; class-level target; `ITrackable` (`IsModified`, `MarkAsClean`, `RevertChanges`, `GetChanges`) | a "touched" flag; leave dirty tracking to History | 02 §6.1 |
| ST24 | `[Snapshot]` | obsolete; `[Trackable]` baseline + History + Persistence cover it | build named groups | 02 §7 |
| ST25 | `[ResetTo]` | build `ResetX()`/`ResetAll()`; `Member = nameof(...)` for non-constant defaults | remove (use `RevertChanges`) | 02 §6.2 |
| ST26 | `[Epoch]` | build (small) | remove | 02 §6.3 |
| ST27 | `[Persist]` placement | move the attribute to YFex.Persistence (breaking namespace); auto-save is a Persistence option | keep it in State, read by name | 02 §7 |
| ST28 | `[LogChanges]` | obsolete alias of Logging's `[StateLog(UserInput)]`; State emits the probe (logging 12 §2) | build its own `[LoggerMessage]` output | 02 §7 |
| ST29 | `[LoadOnInit]` | remove; NavigatR `OnLoaded` / data loaders; `OnActivated` elsewhere | build "call `LoadXAsync` on first Activate" | 02 §7 |
| ST30 | `[ValidateWith]`/`[ValidateAsync]` | remove in ST-P6 (validation ⚑P1's "one release") | keep longer | 02 §7 |
| ST31 | Generator structure | **decided 2026-11-25 (user): one `StateClassModel` per class from several attribute pipelines; shared helpers (atlas F20)** | keep separate emitters and pass data through partial hooks | 02 §1.5 |
| ST32 | Property `[Debounce]` reach | **decided 2026-11-25 (user), State's side of forms ⚑FM59:** the value and its own `PropertyChanged` change on every key; the property's validation, `[Computed]` dependents, gates and reactions wait for the pause; when error messages show is Validation's `ShowErrors.AfterPause`. No control-side delay (FM59: per binding and per platform, and a command would see stale text) | validation only (today); delay in the control | 02 §2.2 |
| ST33 | Retries in commands | none in State; the method uses YFex.Resilience | `[Retry]` on commands through a Resilience pipeline | §2 |
| ST34 | Tests and docs | **decided 2026-11-25 (user): Microsoft `FakeTimeProvider` (test-only package; not `YFex.Time.Testing`, which wraps it and would pull YFex.Time into State's tests); behaviour tests per attribute; guide examples compiled in a sample** | real-time waits; shape tests | 03 gates |
| ST35 | Throttle semantics | **decided 2026-11-25 (user): leading + trailing (the last change always runs)** | leading only (drops the last value) | 02 §3.3 |
| ST36 | `[Poll]` vs YFex.Jobs | State keeps `[Poll]` (per object, activation, owner thread) | route polls through Jobs | §4 |
| ST37 | NavigatR leave guard | reads `ITrackable.IsModified` when the page model implements it (opt-out per page) | each page writes its own concern | §5 |
| ST38 | Validation timing | Validation's generated debounce and `ValidationBag` pause move onto `StateTimer` | keep its own `Task.Delay` timers | §5 |
| ST39 | Clock in apps | **decided 2026-11-25 (user): an internal static `ClockLocator` in `YFex.UI.Public`** (pattern of `MessageCatalogLocator`; never public — visible to Mvvm and the platforms through `InternalsVisibleTo`). The platform sets it at startup from DI's `TimeProvider` — YFex.Time's `TrustedTimeProvider` when the app calls `AddYFexTime()`; the base `ViewModel` (all view models) overrides `TimeProvider => ClockLocator.Current`; default `TimeProvider.System`; tests `Set(fake)`/`Reset()`. A locator inside Mvvm was rejected: the platforms that read DI do not reference Mvvm, so nothing could set it | `PageViewModel` takes `TimeProvider` by constructor injection (first proposal); every model uses `TimeProvider.System` | §5 |
| ST40 | Polls and network state | not now; later an optional `[Poll(PauseOffline = true)]` over YFex.Networking conditions | build now | §4 |
| ST41 | Consumer clean-up | YFex.UI gallery drops `XxxUiCommand` wrappers (after ST-P3) and hand-set computed fields (after ST-P1) | leave them | §5 |
| ST42 | Where the view-state contracts live | **revised 2026-11-19: a new leaf `YFex.State.Public`** (references core only) that YFex.Avalonia/Uno/Blazor reference, as they reference `YFex.NavigatR.Public`: the owner keeps its contracts, the map's ".Public when another library needs the contracts without the engine" rule fits exactly, and types keep their namespaces (`YFex.State.Commands.CommandStatus` moves assembly with `[TypeForwardedTo]`, no source break) | **YFex core** (first proposal: no new package, but the contracts are State's, not cross-cutting, and need a new core namespace); **`YFex.UI.Public`** (holds UI *services* that platforms implement and Mvvm consumes — the opposite direction; State, a lower layer, would depend on the UI repo, the kind of repo cycle atlas F29 reports) | 02 §9.2 |
| ST43 | Command feedback on buttons | **automatic** when `Command` is an `ICommandState` (busy + disabled, error state + tooltip); `CommandFeedback="None"` opts out; click-to-cancel opt-in | opt-in per button; bind by hand (today) | 02 §9.3 |
| ST44 | A load/error/empty presenter | build **`YLoadState`** (`Source` = command or `Task`, templates Loading/Error/Empty/Content, Retry re-runs the command) | each screen composes `YSkeleton` + text by hand | 02 §9.3 |
| ST45 | Busy over a region | attached **`Busy.Source`** on any panel (command, `IBusyState` or bool) with appear delay | a `YBusyHost` wrapper control | 02 §9.3 |
| ST46 | Page-level state without bindings | `YPageHost`/`YPageHeader`/tab header read `IBusyState`, `ITrackable`, `IErrorState` from the page model (progress line, dirty dot, error banner); `ShowsPageState` opt-out | every page binds its header by hand | 02 §9.3 |
| ST47 | Object-level error | generated `IErrorState.LastError` = last fault of any command/reaction/poll of the object, cleared on the next success or `ClearError()` | only per-command `Error` and `[ErrorBucket]` | 02 §9.4 |
| ST48 | Platforms | Avalonia first; Uno and Blazor get the same API in their own phases (YFex.UI rule 3); Blazor `StateComponent` re-renders on command state (B14) | all three at once | 02 §9.5 |
| ST49 | Severity of the diagnostics that start being reported (YFEX0101, 0102, 0601) | **agreed 2026-11-19:** **error**, as declared (today the same code already fails with an unrelated compiler error, or silently does nothing) | warning for one release | 01 B7 |
| ST50 | `StateCollection` and item property changes (B5) | **built in ST-P0 (user "ok" 2026-11-19):** **ignore them**: XAML items notify through their own `INotifyPropertyChanged`; only structural changes become collection events. Item changes are for the *model* (`[ObserveItems]`), not the view. Note: no project outside YFex.State uses `StateList`/`StateCollection` today | raise `Replace` for that index (rebuilds the item's container) | 01 B5 |
| ST51 | `DebounceState` fix before `StateTimer` exists | **decided 2026-11-19 (user): build `StateTimer` in ST-P0** and fix B1 with it (Blazor's render coalescing moves onto it; `DebounceState` removed). This pulls the clock question ⚑ST10 into ST-P0 | minimal fix now, `StateTimer` in ST-P1 (recommended first) | 01 B1 |
| ST53 | `[Busy]`/`[ErrorBucket]` in ST-P2 (found 2026-11-25: the ST-P2 acceptance recipe uses them, planned for ST-P3) | **decided 2026-11-25 (user): the reaction side in ST-P2** — `[Busy(name)]`/`[ErrorBucket(name)]` constructors, both on `[ReactsTo]` methods, `OnReactionFaulted`, the Diagnostics probe, YFEX0622/0623 (shared with commands); the command side stays in ST-P3 (⚑ST16, ⚑ST18) | keep ST-P2 as planned and trim the recipe to `[ReactsTo]` + `[Debounce]` | 02 §3 |
| ST52 | Names of `StateList<T>` and `StateCollection<T>` (user asked 2026-11-19) | **decided + built 2026-11-19 (user): merge** them: `StateList<T>` also implements `INotifyCollectionChanged` + non-generic `IList` (event args built only when someone listens; no marshaling needed under the one-owner-thread rule), `StateCollection<T>` and `ToStateCollection()` are removed; keep the name `StateList<T>` (same family as `StateObject`, no churn) | keep two types, renamed: `ObservableList<T>` + `BindableList<T>` (reads like `[Observable]`, but `ObservableList` suggests XAML-bindable like `ObservableCollection`, which it is not), or `StateList<T>` + `StateListAdapter<T>` (says what it is); merged under the name `ObservableList<T>` | 01 §2.2 B4/B5 |

## 7. Phase summary

| Phase | Content | Unblocks |
|---|---|---|
| **ST-P0** | `StateTimer` + `TimeProvider`, defects B1–B5, B7, B13 (no new attributes) | correct behaviour of what is built |
| **ST-P1** ✅ built 2026-11-25 | dependents dispatch, class model, `[Computed]`, property debounce reach, `PropertyBitmap128`, `ClockLocator` | forms FM59 remainder, gallery shell, validation 12 |
| **ST-P2** ✅ built 2026-11-25 | `[ReactsTo]`, method `[Debounce]`/`[Throttle]`, reaction faults, `[Busy]`/`[ErrorBucket]` on reactions (⚑ST53) | search-as-you-type without hand code |
| **ST-P3** | commands: `ICommand`, parameters, `Error`, `[ErrorBucket]`, `[Gate]`, `[Busy]`, concurrency/`[Queue]`, `[Cooldown]` | search ⚑SK41, gallery wrappers |
| **ST-P4** | `[Poll]`, `[ObserveItems]`, list sources | dashboards, order totals |
| **ST-P5** | `[Trackable]` + `ITrackable`, `[ResetTo]`, `[Epoch]` | forms FM17, NavigatR leave guard |
| **ST-P6** | Validation/Persistence/Logging moves, removals, guide rewrite | — |
| **ST-P7** | view integration in YFex.UI (Avalonia first): `YButton` command feedback, `YLoadState`, `Busy.Source`, page host state; contracts land in core during ST-P3 | screens without hand bindings for busy/error/dirty |

Details and acceptance: [03](03-phases-and-acceptance.md).
