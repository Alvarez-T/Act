# 02 — Model and core types

> Names in code blocks are normative unless marked `// (sketch)`. No type goes to YFex core (⚑H11).

## 1. Identities

```csharp
namespace YFex.Hosting;

/// <summary>A module of a host: lowercase, dot-separated, 3–64 chars ("offline.broker", "devices.serial-bridge").</summary>
public readonly record struct HostModuleId
{
    public string Value { get; }
    public static HostModuleId Parse(string value);            // validates, throws FormatException
    public static bool TryParse(string? value, out HostModuleId id);
}

/// <summary>What kind of host an executable is. App-declared open set ("yfex.generic", "farma.integrations").</summary>
public readonly record struct HostKindId { public string Value { get; } /* same rules */ }

/// <summary>One installed copy of a host kind on a machine ("default", "integrations"); names the service, folder and channel.</summary>
public readonly record struct HostInstanceName { public string Value { get; } /* [a-z0-9-]{1,32} */ }
```

- `HostModuleId` is not Licensing's `ModuleId` (⚑H20): a host module is *code that runs*, a license module is *something bought*.
- Host kinds are an **open set declared by whoever builds the executable** (AGENTS rule: app concepts are app declared). The generic
  host is `HostKinds.Generic = "yfex.generic"`.

## 2. Modules and the catalog (⚑H2, ⚑H18)

```csharp
public sealed class HostModule
{
    public required HostModuleId Id { get; init; }
    public required string DisplayName { get; init; }                // shown in tray/CLI; not localized by Hosting
    public HostModuleRole Role { get; init; } = HostModuleRole.Optional;   // Base modules are always on
    public ImmutableArray<HostModuleId> Requires { get; init; } = [];
    public HostPlatforms Platforms { get; init; } = HostPlatforms.All;    // flags: Windows | Linux | MacOS
    public ImmutableArray<HostListener> Listens { get; init; } = [];      // ports for the firewall/port checks (05 §3)
    public required Action<HostModuleContext> Register { get; init; }     // calls the library's Add… extension
}

public enum HostModuleRole : byte { Base = 1, Optional = 2 }   // closed: Hosting's own concept

public sealed class HostModuleContext
{
    public IServiceCollection Services { get; }
    public IConfiguration Settings { get; }          // this module's section of the effective profile ("YFex:Modules:{id}")
    public HostInstanceName Instance { get; }
    public HostKindId Kind { get; }
    public HostPaths Paths { get; }                  // data, logs, state folders of this instance (06 §2)
}

public sealed class HostModuleCatalog    // frozen after build; lookup by id is a FrozenDictionary read
{
    public static HostModuleCatalogBuilder Create(HostKindId kind);
    public bool TryGet(HostModuleId id, [NotNullWhen(true)] out HostModule? module);
    public ImmutableArray<HostModule> All { get; }
}
```

- **Compiled in.** The generic executable declares its built-in modules in `YFex.Hosting.Host/BuiltInModules.cs`, each an adapter
  over a library's `Add…` (H18). An app host declares its own in its `Program.cs`. Nothing is discovered by reflection or loaded at
  run time.
- **Ownership of hosted services.** While a module's `Register` runs, the host records the `IHostedService` descriptors it adds (before/after
  diff of the collection) and removes them from the Generic Host's list. The **supervisor** starts them per module, in `Requires`
  order. Shared services added with `TryAdd…` belong to whoever registered them first, so base modules register first.
- **Catalog checks at build time** (exceptions in `Build()`, caught by tests): duplicate id, unknown `Requires`, cycles, a `Base` module
  requiring an `Optional` one.

## 3. Host kinds and instances (⚑H4)

| | Generic host | App-built host |
|---|---|---|
| Executable | `yfex-host` (`YFex.Hosting.Host`) | the app's own exe referencing `YFex.Hosting` |
| Kind | `yfex.generic` | app-declared (`farma.integrations`) |
| Modules | YFex modules only; **architecture test: no app assembly** | YFex modules + the app's own |
| Profile | from central, stream per kind + instance (§5) | same mechanism |
| Control | same channel and tray | same |

Several instances can run on one machine (`default` generic + `integrations` app host). Each instance has its own service name, folders,
single-instance lock, control channel and profile. The tray lists every instance registered on the machine (04 §3).

## 4. Generations

```
 profile P1 ──► build generation 1 (ServiceCollection from base + enabled modules) ──► run
 profile P2 arrives
   ├─ same module set, settings differ → push to IConfiguration (reload token) → modules see IOptionsMonitor changes; no restart
   └─ module set differs → stop generation 1 (graceful, HostOptions.ShutdownTimeout, default 30 s) → dispose → build generation 2 → run
```

- One process, one generation at a time; generation numbers are logged and reported in the status.
- If generation N+1 fails to **build** (catalog/profile error) the host keeps running N and reports `hosting.profile.rejected`. If it
  fails to **start**, the host goes back to the last-good profile once (no loop) and reports the failure.
- The control channel, the single-instance lock and the profile tracker live **outside** the generations (the "shell"), so the tray
  never loses the connection during a switch. The shell needs Installations to read bundles, so base modules are rebuilt in each
  generation while the tracker keeps its own small read-only bundle store view (05 §5).

## 5. Profiles (⚑H3)

### 5.1 Content

```csharp
public sealed record HostProfile(
    long Version,                                   // central's version; local first-run profile = 0
    HostKindId Kind,
    ImmutableArray<HostModuleSetting> Modules,      // only Optional modules; Base modules cannot be switched off
    HostProfileFlags Flags,                         // AllowLocalOverride (H17)
    TimeSpan StatusInterval);                       // H19, default 15 min

public sealed record HostModuleSetting(HostModuleId Module, bool Enabled, ImmutableArray<KeyValuePair<string, string>> Settings);
```

Settings are flat `key → string` pairs under the module's section, so modules bind them with the configuration-binding **source
generator** (no reflection) exactly as they would bind `appsettings.json`.

### 5.2 Resolution at central (Tenancy chain)

Central stores profile **layers** per scope and resolves them along the installation's `ScopeIdentity` rungs, most specific last:

```
 tenant "0=farmaviva"                        offline.broker: on · files.exchange: off
 site   "0=farmaviva/1=loja-centro"          files.exchange: on, Settings: inbox=…
 installation "PC-GERENTE" (kind yfex.generic, instance default)   devices.serial-bridge: on, Settings: port=COM3, tcp=4001
 ───────────────────────────────────────────────────────────────────────────────────────
 effective profile v42 for PC-GERENTE/yfex.generic/default
```

- `Enabled`: the most specific layer that mentions the module wins. `Settings`: merged key by key, most specific wins.
- Central validates against the **kind's catalog manifest** (the module ids, platforms and setting keys each kind declares, uploaded
  with a release as `hosting-catalog.json`, like Licensing's catalog test) and refuses an unknown module or key when the layer is saved
  (`Error.Validation`), never when the host applies it.
- The resolved profile is published as an Installations **signed bundle**: kind `hosting.profile`, audience = the installation,
  stream = `{kind}/{instance}`, version = monotonic per stream. Republished when any layer on the chain changes.

### 5.3 On the machine

| Source | When used | Kept |
|---|---|---|
| Signed `hosting.profile` bundle | always when present and verified | the bundle store (Installations) |
| Last-good profile | central unreachable at start | `{state}/profile.last-good` (encrypted `IKeyValueStore`) |
| Local first-run profile (`hosting.json` → `FirstRun`) | before enrollment, or never reached central | file |
| **Local override** (H17) | only if the effective profile has `AllowLocalOverride`; expires (default 4 h); applied by `yfex-host override …` or the tray | `{state}/override`, audited |

`hosting.json` (bootstrap, written by `install`): instance name, central URL, first-run profile, log folder. It never holds secrets.

## 6. Status

```csharp
public sealed record HostStatus(
    HostInstanceName Instance, HostKindId Kind, string Version, long Generation,
    HostProfileSource ProfileSource, long ProfileVersion, DateTimeOffset StartedAt,
    HostState State,                                  // Running, Degraded, Starting, Stopping
    ImmutableArray<HostModuleStatus> Modules);

public sealed record HostModuleStatus(HostModuleId Module, HostModuleState State, string? Reason, DateTimeOffset Since, int RecentFailures);

public enum HostModuleState : byte { Disabled, Starting, Running, Dormant, Degraded, Stopping, Stopped, Faulted }
```

A module that knows more than "my hosted services run" registers an `IHostModuleHealth` (e.g. the broker: `Dormant` while the roster
does not say `Active`; Networking: `Degraded` with `NoNetwork`). Reasons are **stable keys** (`offline.broker.dormant`), translated by
the tray, never free text with personal data.

## 7. Failure policy (⚑H16)

- `HostOptions.BackgroundServiceExceptionBehavior = Ignore`; the supervisor observes each module's hosted services (`ExecuteTask`
  of `BackgroundService`, exceptions from `StartAsync`).
- Restart of a failed module's hosted services with backoff **5 s, 30 s, 2 min, 10 min** (a YFex.Resilience retry policy).
- **Crash-loop breaker:** 5 failures in 10 min → `Faulted`, finding `hosting.module.faulted`, tray toast, status to central. Manual
  restart (tray/CLI) or a new generation resets it.
- A `Base` module that is `Faulted` makes the host `Degraded`, never stops it. The process exits only on unrecoverable shell errors
  (lock lost, state folder unreadable); the OS service manager then restarts it (06 §3).
