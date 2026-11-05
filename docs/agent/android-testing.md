# Android testing for agents (this Windows PC)

Use this when a task needs code to run on Android: a .NET for Android app, a device test, or the R7 key store probe. It covers the headless emulator and USB devices. Avalonia UI on Windows is tested with `Avalonia.Headless` (no device needed). iOS cannot run on Windows.

## One command

```bat
androidKit probe
```

In Git Bash type `androidKit.bat probe` (bash does not add `.bat` by itself); in cmd or PowerShell `androidKit` works.

This builds the R7 probe, starts the emulator without a window, installs and runs the probe, saves the result, logcat and a screenshot, uninstalls it and stops the emulator. It takes about 2 minutes, most of it the build. `androidKit` is on PATH (`C:\Dev\Scripts\Exe\androidKit.bat` → `C:\Dev\Scripts\androidKit.cs`).

For a USB handheld, add its serial (from `androidKit devices`):

```bat
androidKit probe --serial 8a236a37
```

For a fast non-UI run on Android 16, use the ATD test image (about 3x faster, no screenshots):

```bat
androidKit probe --atd
```

`--atd` uses its own port (`emulator-5586`), so it can run at the same time as the default emulator.

## Contract

- **stdout** is one JSON object. **stderr** is human progress. Parse stdout and ignore stderr.
- **Exit code:** 0 ok/passed, 1 test failed, 2 usage error, 3 environment problem (no SDK, device missing or unauthorized), 4 timeout.
- `test` and `probe` write a run folder (`runDir` in the JSON) under `C:\Dev\YFex\_ignore\androidKit-runs\`: `result.json`, `probe.json` (the app's own result), `instrument.txt`, `logcat.txt`, `screen.png`.
- The kit never changes the AVD: it boots with `-read-only -no-snapshot-save`, so apps it installs are gone after `down`.

## Commands

| Command | Does |
|---|---|
| `androidKit doctor` | SDK/tool versions, acceleration, AVDs, devices, free disk |
| `androidKit devices` | adb devices as JSON |
| `androidKit up` / `down` | Start (and wait for boot) / stop the kit emulator `emulator-5584` |
| `androidKit install <apk>` | Install an APK |
| `androidKit instrument <pkg>[/<class>] [-e key value]…` | Run `am instrument -w -r`, parse `INSTRUMENTATION_RESULT`/`CODE` (pass = `-1`) |
| `androidKit shot --out x.png [--annotate]` / `logcat --out x.txt` | Screenshot (`--annotate`: numbered boxes on UI elements) / logcat dump |
| `androidKit layout --out x.json` | UI tree of the current screen (text, ids, bounds, centers) |
| `androidKit tap <x> <y>` / `type <text>` / `key BACK\|HOME\|ENTER…` | Input. Coordinates are screenshot pixels; `layout` gives element centers |
| `androidKit test <csproj> [--shot]` | Build → install → run the project's `[Instrumentation]` → collect → uninstall |
| `androidKit probe [--iterations N] [--no-tls]` | `test` on `C:\Dev\YFex\_ignore\android-probe` |
| `androidKit snapshot-refresh` | Cold boot, wait for the home screen, save a new quick-boot snapshot |

Common options: `--serial <s>` (target; default the kit emulator), `--atd` (the ATD AVD on port 5586), `--avd <name>` (default `Android_Emulator_35`, or `ANDROID_KIT_AVD`), `--keep-running`, `--keep-app`, `--no-build`, `--timeout <s>`. `androidKit --help` lists everything.

## Writing a testable app

`test` runs an Android `Instrumentation`, which keeps the run synchronous and gives pass/fail without a test framework. Copy the pattern from the probe (`ProbeInstrumentation.cs`):

- `[Instrumentation(Name = "<appid>.<Class>", TargetPackage = "<appid>")]`, call `Start()` in `OnCreate`, do the work in `OnStart`, then `Finish(Result.Ok or Result.Canceled, bundle)`.
- Put one line of JSON in the bundle under `json`. The kit parses it into `instrumentation.results.json` and `probe.json`.
- Csproj: `<ApplicationId>`, `<EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>` (plain `adb install` must work), `<RuntimeIdentifiers>android-x64;android-arm64</RuntimeIdentifiers>` (emulator + handhelds), `INTERNET` permission if you open sockets.
- Also set `return-code` (`"0"` = pass) in the bundle: then the same app also runs under xharness.
- For real xUnit/NUnit suites use DeviceRunners + xharness (`dotnet test -f net10.0-android`); DeviceRunners is not set up yet.

## Other tools on this PC

**xharness** (Microsoft's .NET device test runner, `xharness` on PATH, version 10.0 prerelease; the 11.0 builds need the .NET 11 RC runtime). Use it when a test project is built for it (DeviceRunners), or when you want its standard output folder:

```bat
xharness android test --app=<apk> --package-name=<appid> --instrumentation=<appid>.<Class> --device-id=emulator-5584 --output-directory=<dir> --arg=key=value --timeout=00:10:00
```

Exit code 0 = pass. It installs, runs, saves `adb-logcat-*.log` in the output folder and uninstalls. It needs a running device: start one with `androidKit up` first. Exit 82 (`RETURN_CODE_NOT_SET`) means the instrumentation bundle has no `return-code`.

**Google Android CLI** (`android`, winget `Google.AndroidCLI`). `%USERPROFILE%\.androidrc` points it at the SDK and turns telemetry off. `androidKit layout` and `shot --annotate` call it for you. Direct use: `android layout --device=<serial>`, `android screen capture --annotate --device=<serial> --output=x.png`, `android docs search <query>`. On Windows its `android emulator` command is disabled; use `androidKit up`. The first `layout` installs a helper app (`com.android.cli.interact.instrumentation`) on the device. Do not run `android init` or `android skills add` without asking the user: they write agent skill files.

**Installing SDK packages.** cmdline-tools 23 made `sdkmanager` a wrapper around the Android CLI, and its `.bat` breaks on the `(x86)` in this SDK path. Use `android sdk list` to read. To install, ask the user first, then run elevated: `android sdk install <pkg>` (names like `platforms/android-36.1`) or the old `cmdline-tools\19.0\bin\sdkmanager.bat --install "platforms;android-36.1"`.

## Machine facts (2026-10-21)

- SDK: `C:\Program Files (x86)\Android\android-sdk` (`ANDROID_HOME` set for the user). It is admin-only: `sdkmanager` installs need an elevated prompt, so ask the user first.
- Emulator 37.2.12, platform-tools 37.0.1, cmdline-tools 23.0, JDK 21, WHPX acceleration. Platforms android-35, 36, 36.1.
- AVD `Android_Emulator_35` (API 35 Google APIs x86_64, port 5584): headless boot from snapshot in about 3 s. Full UI, screenshots work.
- AVD `yfex-atd-36` (API 36 Google ATD x86_64, port 5586, `--atd`): boot from snapshot in about 4 s. No launcher or normal apps, screenshots empty; probe about 3x faster. Use it for non-UI tests.
- **TFM:** `net11.0-android` (preview 4) builds and runs; the probe uses it. `net10.0-android` also works.
- USB handheld `8a236a37`: Qualcomm msm8953, Android 9 (API 28), Keymaster 4.0 in a real TEE, no StrongBox. Keep its screen unlocked and USB debugging authorized, or adb drops it.

## Pitfalls already solved in the kit

- **netsim hang.** Starting the emulator again while the previous one's `emulator.exe` wrapper or `netsimd` is still exiting hangs the boot after "netsim startup". `down` waits until they are gone. If you kill the emulator yourself, wait ~20 s before `up`.
- **Snapshot after an emulator update.** The old snapshot is rejected ("incompatible version") and the boot falls back to a ~30 s cold boot. A snapshot taken too early stays on "Pixel is starting…". Fix: `androidKit snapshot-refresh` (add `--atd` for the ATD AVD).
- **New AVD:** create it with `cmdline-tools\19.0\bin\avdmanager.bat create avd -n <name> -k "system-images;…"` (no admin), then `androidKit snapshot-refresh --avd <name> --port <free even port>` once.
- **Pipes.** The kit starts the emulator through ShellExecute, so a caller reading the kit's stdout is not blocked by the emulator.
- The emulator key store is AOSP **software** KeyMint (`securityLevel: SOFTWARE`, attestation root "Droid Unregistered Device CA"). Use it for API behavior, never for TEE timing or attestation claims. That needs a real device.

## R7 notes (for the probe)

- `new X509Certificate2(privateKeyEntry.Handle)` gives `SslStream` a client certificate whose key stays in AndroidKeyStore (.NET 9+, dotnet/runtime#103337). Mutual TLS 1.3 works on the emulator.
- A key used for TLS client auth must allow `KeyProperties.DigestNone` (plus SHA-256/384/512). Conscrypt signs the handshake digest with `NONEwithECDSA`; a SHA-256-only key fails with "Incompatible digest".
- The probe's handshake times include both TLS ends in one Debug-build process. They are not a product number.
- `probe.json` → `strongbox`: on a device with StrongBox it also has `signMs` (P-256 signing inside StrongBox). The emulator and handheld `8a236a37` have none (`StrongBoxUnavailableException`).

## Not covered here

- Avalonia apps on Android: the YFexAvaloniaMcp injector needs a small change (single-view `TopLevel`, `adb forward tcp:5199 tcp:5199`). Not done yet.
- Interactive control through an MCP (mobile-mcp) is not installed; `androidKit layout/shot/tap/type/key` cover the same basic loop.
- Whether `android layout` sees inside an Avalonia app (Avalonia draws on one surface; the tree depends on its accessibility support) is not checked yet. Until then use screenshots + coordinates for Avalonia screens.
