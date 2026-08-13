# YFex.NavigatR.SourceGenerator

## Why this exists

Removes the glue a `[Route]` ViewModel would otherwise hand-write: the route record, the
`INavigable.OnNavigation` explicit implementation, extraction of the route parameter into a typed
`OnNavigation(T, CancellationToken)` partial, `[Prefetch]` result injection, and the
`RouteRegistry` population. Separate assembly because a Roslyn component must load into the
compiler process — it cannot ship inside the `net11.0` runtime library it generates for.

## Not currently wired

No project in `yfex.slnx` references this with `OutputItemType="Analyzer"`, so `[Route]` and
`[Prefetch]` emit nothing today. Treat any statement that "the generator wires this up" —
including in [docs/public/navigatr.md](../../../docs/public/navigatr.md) — as describing intent,
not the current build. Verify before relying on generated output.

Analyzer references are **not** transitive: the reference has to go on whichever project declares
the `[Route]` ViewModels, not on `YFex.NavigatR`. `YFex.Mvvm.csproj` already does this for the
State, Messaging and Persistence generators and carries a comment saying why — it simply omits
this one, which reads like an oversight rather than a decision.

`EmitRegistration` is broken independently of the wiring. It emits an implementing
`static partial void RegisterGenerated` into `namespace YFex.NavigatR` in the *consuming*
assembly, while the defining declaration lives in `YFex.NavigatR` itself. Partial methods and
partial types cannot span assemblies, so this yields `CS0759` plus a shadowing
`NavigatRRegistration` type. Fixing the analyzer reference alone will not make
`NavigatRRegistration.RegisterAll` work — the emitted shape has to change first.

`RouteGenerationTests.cs` in `YFex.NavigatR.Tests` is commented out in full, so there is no
regression coverage either.

## Rules

- `netstandard2.0`, `LangVersion 12`, `ImplicitUsings` disabled — deliberately behind the rest of
  the repo because the compiler host constrains it. Do not raise these to match `YFex.NavigatR`.
- `EnforceExtendedAnalyzerRules` is on: no file I/O, no `Console`, no capturing `Compilation` or
  symbols in the incremental pipeline's cached state.
- `Generated/` is `EmitCompilerGeneratedFiles` output for inspection. Never edit it.
- `EquatableArray.cs` exists so pipeline values compare by structure. Any new model type flowing
  through `IIncrementalGenerator` must be value-equatable, or caching silently degrades to
  re-running on every keystroke.

## Diagnostics

`NAV001` `[Route]` requires partial · `NAV002` must implement `INavigable` · `NAV003` `RouteType`
must implement `IRoute` · `NAV004` route name conflict · `NAV005` `[Prefetch]` must return `Task`
or `Task<T>` · `NAV007` duplicate `[Prefetch]` return type. Declared in `NavigatrGenerator.cs`;
`NAV006` is unused.
