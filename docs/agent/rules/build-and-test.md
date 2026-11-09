# Building and testing YFex

- **Generator DLL locks:** the RoslynMcp server locks generator DLLs under `bin/`. Build and test with `--artifacts-path C:/Temp/<repo>-artifacts` (or in Release), and run `dotnet build-server shutdown` when a build reports locked files.
- **Solutions:** when a solution restore fails because of a missing sibling project, build the affected projects one by one.
- **Restore failure NU1301** (private feed returns 401):
  ```
  dotnet restore <proj> --source https://api.nuget.org/v3/index.json --source "$HOME/.nuget/packages"
  ```
  Then build with `--no-restore`.
- **Shell edits:** bash heredocs mangle backslashes, `\uXXXX` and CRLF. Write edit scripts with the file-writing tool and keep each file's line endings (several docs use CRLF).
- **Tests:**
  - Never block on async code under the xunit sync context.
  - A `StateObject` asserts its owner thread in DEBUG, so don't `await` between setters.
  - Loopback test servers listen on an explicit IP address. On Windows, `localhost` tries `::1` first, and a refused connection costs about 2 s.
  - Allocation tests need a long warm-up (tier-0 JIT allocates).
- **Generators:** generated containers must be `partial` all the way up.
  - A project declaring `[Sensitive]` must reference `YFex.Privacy.Generator` as an analyzer.
  - View models with validation rules need both the State and Validation generators.
- **AOT apps:** set `InvariantGlobalization=false` (`Money` needs pt-BR culture data; with `true`, `string.Normalize` also becomes a no-op).
- **Stale AOT smoke:** a failed publish can leave an old smoke exe that still "passes". Never pipe `dotnet publish` into `head`, which hides the errors. Check the exe timestamp.
- **Benchmarks:** BenchmarkDotNet in these repos needs `InProcessEmitToolchain`.
- **Other platforms:** use the `linux-testing` and `android-testing` skills (`../linux-testing.md`, `../android-testing.md`).
