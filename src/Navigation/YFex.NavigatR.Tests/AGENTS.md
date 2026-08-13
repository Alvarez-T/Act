# YFex.NavigatR.Tests

**This project does not compile.** `YFex.NavigatR.Tests.csproj` references
`..\YFex.Navigation\YFex.NavigatR.csproj`, but the folder was renamed to `YFex.NavigatR`. Every
NavigatR type is unresolved — 54 `CS0246` errors as of 2026-07-30. The suite has not run since
the rename, so its green/red state is unknown and no assertion in it can be cited as evidence.

Fixing the path is a one-line change: `..\YFex.NavigatR\YFex.NavigatR.csproj`. Measured
out-of-tree with only that corrected, against the current working tree: **124 passed, 5 failed,
129 total.** The suite is mostly healthy. The 5 failures are pre-existing and unrelated to the
rename:

- 3 × `StringRouteTests` — `ConstructRoute` cannot parse a URL segment into a wrapper parameter
  record (`Cannot parse '42' to 'OrderParams'`).
- `NavigatorHostTests.SwitchContextAsync_ResumesTopOfTargetContext`.
- `NavigatorNavigationTests.NavigateTo_Simple_DirectionIsInitialOnFirstNav` — `Navigator.cs`
  hardcodes `NavigationDirection.Forward` on first navigation, so `Initial` is unreachable.

`Navigator.cs` and `NavigatorTestBase.cs` are both dirty in the working tree, so that baseline
reflects in-progress work rather than a clean commit.

`RouteGenerationTests.cs` is commented out in full — it tested
`YFex.NavigatR.SourceGenerator`, which is itself not wired into any build.

`NavigatorTestBase.cs` constructs a `Navigator` from a test `IServiceScope` and registers routes
through `RouteRegistry.Register<TRoute, TViewModel>()` directly, so the suite never needed the
source generator. Follow that pattern for new tests.

Area context: [../AGENTS.md](../AGENTS.md).
