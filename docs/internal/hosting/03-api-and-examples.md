# 03 — API and examples

> Dev-side code first. Names are normative unless marked `// (sketch)`. Store examples use the pharmacy sample (FarmaViva, loja-centro).

## 1. The generic host (`YFex.Hosting.Host`, executable `yfex-host`)

This is the whole program. It contains no app code (architecture test, ⚑O20).

```csharp
// YFex.Hosting.Host/Program.cs
return await YFexServiceHost.RunAsync(args, HostKinds.Generic, BuiltInModules.Declare);

// YFex.Hosting.Host/BuiltInModules.cs — adapters over each library's Add… (⚑H18); libraries reference nothing of Hosting
internal static class BuiltInModules
{
    public static void Declare(HostModuleCatalogBuilder catalog) => catalog
        // base: always on, registered first (they own shared services)
        .Base("installations", "Installation identity", m => m.Services.AddYFexInstallations(i => i
            .Application("yfex-host", HostVersion.Current)
            .Storage(InstallationStorageScope.Machine, m.Paths.Installations)
            .UseHostKeyStore(m)                       // (sketch) UseWindowsKeyStore() on Windows, file/TPM store on Linux (06 §2)
            .EnrollAt(m.Settings.GetCentralUri())
            .Peers(p => p.OfflineBundles(otherPublishers: HostingBundles.Publisher))))   // G4: one publisher slot
        .Base("time", "Trusted time", m => m.Services.AddYFexTime(t => t.UseCentralAnchor().UseNetworkTime().PersistHighWater()))
        .Base("networking", "Network", m => m.Services.AddYFexNetworking(n => n.UseWindowsConditions()
            .WatchEndpoint("central", m.Settings.GetCentralHealthUri(), essential: true)))
        .Base("logging", "Logs", m => m.Services.AddYFexHostLogging(m))                  // 05 §2
        .Base("jobs", "Scheduled jobs", m => m.Services.AddYFexJobs())                      // ⚑H10
        .Base("hosting.status", "Status to central", m => m.Services.AddHostStatusRelay())  // ⚑H19
        // optional: switched on by the profile
        .Module("offline.broker", "Site broker", m => m.Services
                .AddYFexOfflineBroker().AddSiteSessions().AddSiteCustody().AddSiteCoordination().AddSiteFeed()
                .AddKeyValueBrokerStores().AddSiteJournalUplink(),
            requires: ["installations", "time"], platforms: HostPlatforms.Windows | HostPlatforms.Linux,  // ⚑H8: no macOS broker
            listens: [HostListener.Tcp(7443, "YFex Installations peer")])
        .Module("devices.serial-bridge", "Serial port bridge", m => m.Services.AddSerialBridges(m.Settings))   // H3
        .Module("profiling.machine", "Machine telemetry", m => m.Services.AddHostProfiling(m))                 // ⚑H15, H3
        .Module("data.schema", "Local schema", m => m.Services.AddYFexSchemaMigration(o => o.ApplyOnStartup = true));
}
```

`YFexServiceHost.RunAsync` parses the verbs (§4), picks the platform lifetime (`.Windows` / `.Linux`), takes the single-instance lock,
starts the control channel and the profile tracker (the shell, 02 §4), then runs generations until stopped. Exit codes: 0 stopped,
1 configuration error, 2 lock held by another process, 3 unrecoverable shell error (service manager restarts).

## 2. An app's own host (⚑H4)

FarmaViva needs partner file exchanges and a SNGPC report job whose handlers are app code, so they cannot be in `yfex-host`. The app
builds a second host kind from the same library; it installs next to `yfex-host` as instance `integrations`.

```csharp
// FarmaViva.Integrations.Host/Program.cs — app code is allowed here (this is not the generic host)
return await YFexServiceHost.RunAsync(args, new HostKindId("farma.integrations"), catalog => catalog
    .UseYFexBaseModules()                                                     // installations, time, networking, logging, jobs, status
    .Module("files.exchange", "Partner files", m => m.Services
        .AddYFexFilesExchange(x => x.FromSettings(m.Settings))
        .AddExchangeHandler<NotasDeEntradaHandler>())                          // app handler
    .Module("farma.sngpc", "SNGPC report", m => m.Services.AddSingleton<SngpcJobs>(), requires: ["jobs"]));
```

```bash
farma-integrations install --instance integrations      # service "YFex Host (integrations)", same account rules
```

## 3. Central side (⚑H3, ⚑H5)

Central stays an ASP.NET Core app; it authors profiles and reads status.

```csharp
// central composition root
services.AddYFexHostingCentral(hosting => hosting
    .Catalogs("hosting-catalog.json")                  // module ids, platforms, setting keys per host kind (02 §5.2)
    .UseSqlStore(connectionFactory));                  // layers per scope, status per installation (YFex.Data, later)

// admin: switch the broker on for the tenant, the file exchange for one store, the bridge for one PC
await Hosting.Commands.SetLayerAsync(new Hosting.Commands.SetLayer(
    Scope: "0=farmaviva",
    Kind: HostKinds.Generic,
    Modules: [new("offline.broker", Enabled: true, Settings: [])]), cancellationToken);

await Hosting.Commands.SetLayerAsync(new Hosting.Commands.SetLayer(
    Scope: "0=farmaviva/1=loja-centro",
    Installation: pcGerente,                             // optional: an installation-level layer
    Kind: HostKinds.Generic, Instance: "default",
    Modules: [new("devices.serial-bridge", true, [new("port", "COM3"), new("tcp", "4001")])]), cancellationToken);

// support: what does this store's broker PC run right now?
HostStatus? status = await Hosting.Queries.GetStatusAsync(new(pcGerente, "default"), cancellationToken);
```

Permissions (`HostingPermissions`, Authorization catalog): `hosting.profiles.read`, `hosting.profiles.write`,
`hosting.status.read`, `hosting.override.allow` (who may set `AllowLocalOverride`).

## 4. Command line (`yfex-host`, and any app host)

| Verb | Rights | What it does |
|---|---|---|
| `run` (default) | — | runs as console or under the service manager (detected) |
| `install [--instance n] [--tray]` | admin/root | service + account + folders + ACLs + operators group (+ tray auto-start for all users); idempotent (06 §3) |
| `uninstall [--instance n] [--keep-data]` | admin/root | removes service; keeps `{data}` unless told otherwise |
| `--enroll CODE --display-name NAME` | admin/root | one-time enrollment through `IInstallationEnrollment` until YFex.Installer exists (offline 02 §5.1) |
| `status` | interactive user | `HostStatus` over the control channel; `--json` for scripts |
| `modules`, `modules restart <id>` | operators | list / restart a module |
| `jobs`, `jobs run <key>`, `jobs pause <key>` | operators | YFex.Jobs list/run now/pause |
| `logs [--follow] [--module id] [--level w]` | operators | live tap / recent records |
| `diagnose [--out file]` | interactive user | Networking diagnosis + status + recent warnings, masked (Privacy), for a support ticket |
| `override set <module> on|off [k=v…] [--for 4h]`, `override clear` | operators, profile must allow | technician mode (H17), audited |

```bash
yfex-host install --instance default --tray
yfex-host --enroll 7Q4K-M2XD --display-name PC-GERENTE
yfex-host status
```

## 5. Testing

```csharp
await using HostHarness harness = await HostHarness.StartAsync(HostKinds.Generic, BuiltInModules.Declare,
    profile: HostProfiles.With("offline.broker"));
HostStatus status = await harness.Control.GetStatusAsync();
status.Module("offline.broker").State.ShouldBe(HostModuleState.Dormant);     // roster does not say Active

await harness.PublishProfileAsync(HostProfiles.With("offline.broker", "data.schema"));   // new generation
harness.Generation.ShouldBe(2);
```
