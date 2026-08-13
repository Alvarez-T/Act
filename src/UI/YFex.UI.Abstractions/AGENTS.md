# YFex.UI.Abstractions

## Why this exists

Holds the contracts through which framework code asks a UI to do something — show a dialog,
raise a toast, swap the displayed view — without referencing any UI framework. It is the reason
`YFex.NavigatR` can drive navigation and `YFex.Mvvm` can open a dialog while both stay testable
in a plain console process. Merge it into either and every consumer inherits Avalonia/Blazor.

It depends on no other YFex project, by design. Anything added here must hold for every UI
target the framework supports.

## Owns / does not own

**Owns** — `INavigation` (the platform hook: `PerformNavigation(object view)`,
`OnNavigationDenied()`, `UserBecameInactive` / `UserBecameActive`), `IDialog` + `IDialogHandle`,
`IMessageBox`, `IToast`, `INotification`, and the answer unions (`OkCancel`, `YesNo`,
`YesNoCancel`, and their `*Ignored` variants).

**Does not own** — the navigation stack (`YFex.NavigatR`), ViewModel base classes (`YFex.Mvvm`),
and any concrete implementation. Every interface here is implemented by the host app.

## Rules

- **`INavigation` is implemented by the host, not by the framework.** It lives here rather than in
  `YFex.NavigatR` so a UI project can satisfy it without referencing the navigator.
  `YFex.NavigatR/INavigation.cs` is a `global using` alias to this type, not a second interface —
  `docs/public/navigatr.md` still shows `using YFex.NavigatR;` above an `INavigation`
  implementation and is wrong on that point.
- Outcomes are `[Union]` types, never `bool` or `enum`. A caller that must handle "user dismissed
  the dialog without answering" gets the `*Ignored` variant; adding a case is a compile break at
  every call site, which is the point.
- Interfaces only, plus the unions and the extension methods that make them ergonomic. A type
  needing a dependency does not belong here.

Guide: [docs/public/ui-abstractions.md](../../../docs/public/ui-abstractions.md).
