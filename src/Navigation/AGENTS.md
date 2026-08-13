# Navigation area — YFex.NavigatR

Type-safe async navigation for YFex MVVM apps. A destination is an immutable `IRoute` record
carrying its own parameters; an outcome is a `NavigationResult` union the compiler forces you to
handle exhaustively. There are no route strings in the type system, no parameter dictionaries,
and no null results — that single choice explains the source generator, the pooling, and the
lifecycle contract.

Full API guide: [docs/public/navigatr.md](../../docs/public/navigatr.md).
Internals: [docs/internal/navigatr-internals.md](../../docs/internal/navigatr-internals.md).

## Project map

| Project | Exists to | Talks to |
|---------|-----------|----------|
| `YFex.NavigatR` | own the navigation stack, the `INavigable` lifecycle, and the memory pressure that comes from keeping suspended screens alive | `YFex.UI.Abstractions` |
| `YFex.NavigatR.SourceGenerator` | turn `[Route]` / `[Prefetch]` into route records, `OnNavigation` bridges and registration, so callers never write parameter-extraction glue | nothing — `netstandard2.0` Roslyn component |
| `YFex.NavigatR.Tests` | xunit suite over `Navigator`, `NavigablePool`, prefetch, `RouteRegistry` | *(reference is broken — see Current state)* |
| `YFex.UI.Abstractions` | UI contracts (`INavigation`, `IDialog`, `IToast`, `IMessageBox`) so NavigatR and ViewModels need no UI-framework reference | nothing |

App ViewModels do not use NavigatR directly. They inherit `PageViewModel` from `YFex.Mvvm`,
which is the consumer of this area. Dependencies run `YFex.Mvvm` → `YFex.NavigatR`, never back.

## Current state — verified 2026-07-30

Three things are broken or inert here. Do not assume otherwise, and do not report success on a
command that touches them without showing output.

- **`YFex.NavigatR.Tests` does not build.** Its `ProjectReference` points at
  `..\YFex.Navigation\YFex.NavigatR.csproj`; the folder was renamed to `YFex.NavigatR` and the
  path was never updated. 54 `CS0246` errors. The suite has never run since the rename.
- **The source generator is not wired to anything.** `YFex.NavigatR.SourceGenerator` is listed in
  `yfex.slnx` but no project references it with `OutputItemType="Analyzer"`. `[Route]` and
  `[Prefetch]` currently emit nothing, anywhere in the solution.
- **`NavigatRRegistration.RegisterAll` does nothing, and cannot be made to work as designed.**
  It calls `static partial void RegisterGenerated(RouteRegistry)`, whose *defining* declaration
  sits in `YFex.NavigatR`. `EmitRegistration` emits the *implementing* declaration into whichever
  assembly declares the `[Route]` ViewModels — i.e. the app. Partial types and partial methods
  cannot span assemblies, so wiring the generator up would not fix this; it would produce a
  second `YFex.NavigatR.NavigatRRegistration` and a `CS0759`. **Call
  `registry.Register<TRoute, TViewModel>()` per route by hand.** That is what
  `NavigatorTestBase` does and it is the only path that works today.

## Boundaries

- `YFex.NavigatR` references `YFex.UI.Abstractions` and nothing else. A reference to
  `YFex.Mvvm`, `YFex.State` or a UI framework inverts the layering and makes the navigator
  untestable outside a host.
- **`INavigation` lives in `YFex.UI.Abstractions`, not in NavigatR.** NavigatR keeps a one-line
  `global using` alias in `INavigation.cs` so its own files still compile unqualified. Anyone
  implementing the platform hook binds to `YFex.UI.Abstractions.INavigation` —
  `docs/public/navigatr.md` still shows `using YFex.NavigatR` for it and is wrong on that point.
- `YFex.NavigatR.SourceGenerator/Generated/` is `EmitCompilerGeneratedFiles` output. Change the
  generator, never the file.
- Runtime projects are `net11.0` / `LangVersion preview`. The generator is `netstandard2.0` /
  `LangVersion 12` — a Roslyn component must load into the compiler, so C# 13+ syntax and
  `net11.0` APIs are unavailable in that project regardless of what the rest of the repo uses.
  (The root `CLAUDE.md` claims `net8.0;net9.0`; that is stale for this area.)

## Build and test

```bash
dotnet build src/Navigation/YFex.NavigatR/YFex.NavigatR.csproj
```

```bash
dotnet build src/Navigation/YFex.NavigatR.SourceGenerator/YFex.NavigatR.SourceGenerator.csproj
```

`YFex.NavigatR` builds clean apart from one pre-existing `CS8603` in `Navigator.cs`. The
generator builds clean apart from `RS2008` release-tracking warnings on `NAV001`–`NAV007`.
`dotnet test` on `YFex.NavigatR.Tests` fails to compile — see Current state. Measured
out-of-tree with only the `ProjectReference` path corrected: **124 passed, 5 failed, 129 total**.
The suite is substantially healthy; the 5 failures are listed in
[YFex.NavigatR.Tests/AGENTS.md](YFex.NavigatR.Tests/AGENTS.md).

## Conventions

- Every type is `YFex.NavigatR.*`. One exception survives the rename:
  `NavigationNotConfiguredException` is still in `YFex.Navigation.Exceptions`. Match the
  file you are editing rather than "fixing" it in passing — it is a public type.
- Route records are records for a reason: structural equality is what makes prefetch-token
  matching and history deduplication work. A route implemented as a class silently breaks both.
- Navigation failure is a `NavigationResult` case, never an exception. The exceptions in
  `Exceptions/` mark caller bugs (missing parameter, type mismatch, unregistered route).

## Done means

Build clean with no new warnings, and say plainly which projects you built. Any claim about the
test suite requires fixing the `ProjectReference` first — otherwise state that tests could not
run.
