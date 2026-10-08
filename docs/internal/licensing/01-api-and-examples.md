# 01 — API and examples (dev side first)

The vendor (the user's company) sells **YFex Farma** (`yfex-farma`: the desktop PDV, the site broker and central). The customer is
the pharmacy chain **FarmaViva** (tenant `farmaviva`), store **Loja Centro** (`0=farmaviva/1=loja-centro`). Machines are those of
[installations 03](../installations/03-api-and-examples.md):

| Machine | Installation | Role | Counted as a seat? |
|---|---|---|---|
| `PC-GERENTE` | "YFex Site Broker" service + the manager's desktop app | `SiteBroker` + `Terminal` | yes, 1 broker + 1 terminal |
| `CAIXA-01`, `CAIXA-02` | YFex Farma PDV | `Terminal` | yes |
| `NOTEBOOK-REGIONAL` | desktop app, enrolled at `0=farmaviva/1=regiao-sul` | `Terminal` | yes, in `regiao-sul` (not a store) |
| central | the API host(s) | `Central` | never |

FarmaViva's contract (edition "completo"): 40 stores, 4 checkouts and 1 broker per store, modules fiscal + loyalty + delivery +
controlled medicines, from 2026-11-01 to 2027-10-31. Scenario A: FarmaViva uses the vendor's cloud (SaaS). Scenario B: FarmaViva runs
central on its own server (on-premises).

## 1. The catalog — written once by the vendor's developers

A small assembly **`YFexFarma.Licensing`** is referenced by the app (PDV, broker, central) **and** by the vendor's issuing service, so
both sides use the same ids (⚑LC4). Pharmacy meaning lives here, never in the library.

```csharp
// ✍ YFexFarma.Licensing/YFexFarmaLicensing.cs
using YFex.Licensing;
using YFex.Installations;

public static class YFexFarmaLicensing
{
    public static class Modules
    {
        public static ModuleId Fiscal { get; } = new("fiscal");                              // NFC-e, NF-e, SAT, contingency
        public static ModuleId Loyalty { get; } = new("loyalty");                            // points, campaigns, cashback
        public static ModuleId Delivery { get; } = new("delivery");                          // marketplace orders, couriers
        public static ModuleId ControlledMedicines { get; } = new("controlled-medicines");   // SNGPC reporting
    }

    public static class Limits
    {
        public static LimitId DeliveryIntegrations { get; } = new("delivery.integrations");  // how many marketplaces at once
    }

    public static class Meters
    {
        public static MeterId FiscalDocumentsIssued { get; } = new("fiscal.documents-issued");
    }

    public static LicensedProduct Product { get; } = LicensedProduct.Define(new ProductId("yfex-farma"), product => product
        .DisplayName("YFex Farma")
        .CountSeats(InstallationRole.Terminal, InstallationRole.SiteBroker)    // Central is never a seat
        .ScopeUnit(level: 1)                                                   // "40 stores" counts level-1 scopes
        .KeepWhenUnavailable(SalesPermissions.ViewHistory, SalesPermissions.CloseDay, ReportsPermissions.Export)   // product level
        .BaseContinuesAfterExpiry(TimeSpan.FromDays(30))                       // selling goes on 30 days after the modules stop (04 §3)
        .WithoutLicense(UnlicensedBehavior.ReadOnly)                           // never licensed / seat not activated → keep-list only (⚑LC9)
        .Module(Modules.Fiscal, module => module
            .Grants(FiscalPermissions.All)                                     // a whole Authorization catalog
            .KeepWhenUnavailable(FiscalPermissions.ViewDocuments, FiscalPermissions.ExportXml,
                                 FiscalPermissions.TransmitPending)            // legal duty for work already done (contingency NFC-e)
            .Meter(Meters.FiscalDocumentsIssued))
        .Module(Modules.Loyalty, module => module
            .Grants(LoyaltyPermissions.All)
            .KeepWhenUnavailable(LoyaltyPermissions.ViewBalances))
        .Module(Modules.Delivery, module => module
            .Grants(DeliveryPermissions.All)
            .Limit(Limits.DeliveryIntegrations))
        .Module(Modules.ControlledMedicines, module => module
            .Grants(ControlledMedicinesPermissions.All)
            .KeepWhenUnavailable(ControlledMedicinesPermissions.ExportBooks)));

    // Pinned in the binary, never read from a configuration file (⚑LC17). Active + standby public keys (SubjectPublicKeyInfo).
    public static VendorKeys VendorKeys { get; } = VendorKeys.Pin(
        active:  "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…",                   // (sketch) key "licensing/vendor" 2026
        standby: "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…");                  // (sketch) cold key in the safe
}
```

Every permission **not** listed in a module is **base**: selling, searching products, the cash drawer. Nothing to declare for them.
Base is part of what FarmaViva pays for, so YFex Farma declares `UnlicensedBehavior.ReadOnly`: without a valid license (never
licensed, a checkout over the seat limit, 30 days after the modules stopped) only the keep-lists work — history, closing the day,
exports, transmitting pending fiscal documents. The app always opens and the data is always reachable (⚑LC11). A product with a
free core declares `UnlicensedBehavior.BaseOnly` instead.
A test in the app solution writes `licensing-catalog.json` (modules, their permission ids, limits, meters) and fails when it changes
without the committed file being updated (the same snapshot idea as Audit's schema snapshot), so a PR shows "this permission just
moved into the delivery module".

## 2. Composition roots

```csharp
// ✍ CAIXA-01 / CAIXA-02 / the manager's desktop app — Program.cs (both scenarios: identical)
services.AddYFexInstallations(installations => installations.Terminal().UseWindowsKeyStore().EnrollAt(centralUri));
services.AddYFexTime(time => time.UseCentralAnchor().UseSiteAnchor().UseNetworkTime("a.ntp.br", "b.ntp.br").PersistHighWater());
services.AddYFexAuthorization(…);

services.AddYFexLicensing(licensing => licensing
    .Product(YFexFarmaLicensing.Product)
    .TrustVendorKeys(YFexFarmaLicensing.VendorKeys));   // registers the "vendor" bundle publisher (⚑I23) and the availability hook (⚑LC14)
```

That is all an installation needs. The License and the store's ActivationList arrive as vendor-signed bundles through Installations
(pushed by central while online, gossiped by the broker on the LAN); the library verifies them, evaluates them with trusted time and
masks unavailable modules out of every `PermissionSet`.

```csharp
// ✍ Central, scenario A (SaaS, the vendor's cloud) — the vendor's own API host
services.AddYFexInstallationsCentral(…);
services.AddYFexLicensingCentral(central => central
    .Product(YFexFarmaLicensing.Product)
    .TrustVendorKeys(YFexFarmaLicensing.VendorKeys)
    .ReachVendor(vendor => vendor.InProcess()));        // IVendorLicensing from AddYFexLicensingVendor in the same host (⚑LC16)
```

```csharp
// ✍ Central, scenario B (on-premises, FarmaViva's own server) — the same central app, different configuration
services.AddYFexLicensingCentral(central => central
    .Product(YFexFarmaLicensing.Product)
    .TrustVendorKeys(YFexFarmaLicensing.VendorKeys)
    .UseLicenseeKey(rings => rings.GetSigningKeysAsync("licensing/licensee"))         // central's identity towards the vendor (05 §2)
    .ReachVendor(vendor => vendor.Over(new Uri("https://licenciamento.yfex.com.br/"))  // (sketch) host name
        .CheckInEvery(TimeSpan.FromDays(1))));
// A disconnected customer uses .ReachVendor(vendor => vendor.ByFile()) — an admin exports/imports files (05 §4).
```

Whether a License is SaaS, on-premises or disconnected is written **in the License** by the vendor (⚑LC19). `ReachVendor` only says
how this central talks to the vendor; a mismatch (a `VendorHosted` license checked in from a customer's server) is a finding.

```csharp
// ✍ The vendor's issuing service — hosted only by the vendor (05 §5)
services.AddYFexLicensingVendor(vendor => vendor
    .Product(YFexFarmaLicensing.Product, editions => editions
        .Edition("essencial", edition => edition
            .Modules(YFexFarmaLicensing.Modules.Fiscal)
            .Seats(InstallationRole.Terminal, perScope: 2).Seats(InstallationRole.SiteBroker, perScope: 1))
        .Edition("completo", edition => edition
            .Modules(YFexFarmaLicensing.Modules.Fiscal, YFexFarmaLicensing.Modules.Loyalty,
                     YFexFarmaLicensing.Modules.Delivery, YFexFarmaLicensing.Modules.ControlledMedicines)
            .Seats(InstallationRole.Terminal, perScope: 4).Seats(InstallationRole.SiteBroker, perScope: 1)
            .Limit(YFexFarmaLicensing.Limits.DeliveryIntegrations, 3)))
    .SignWith(rings => rings.GetSigningKeysAsync("licensing/vendor"))           // YFex.Cryptography SigningKeyRing (TPM/HSM-backed root)
    .UseSqlStore(connectionFactory));
app.MapYFexLicensingVendor();                                                    // POST /licensing/activate, /licensing/check-in
```

## 3. Issuing FarmaViva's license (vendor back office)

```csharp
// ✍ Vendor admin screen (holds licensing.vendor.issue)
LicenseIssued issued = await LicensingVendor.Commands.IssueLicenseAsync(new LicensingVendor.Commands.IssueLicense(
    Customer: "farmaviva",                                       // the vendor's customer account
    Tenant: "farmaviva",                                         // tenant id (scope level 0)
    Product: YFexFarmaLicensing.Product.Id,
    Edition: "completo",                                         // template → modules, seats and limits copied INTO the license
    Hosting: LicenseHosting.CustomerHosted,                      // scenario B
    Subscription: new ValidityPeriod(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-3)),
                                     new DateTimeOffset(2027, 10, 31, 23, 59, 59, TimeSpan.FromHours(-3))),
    Scopes: new ScopeLimit(Level: 1, Count: 40),
    Seats: [new SeatLimit(InstallationRole.Terminal, PerScope: 4, Total: 120),          // overrides the edition's seats
            new SeatLimit(InstallationRole.SiteBroker, PerScope: 1, Total: 40)]),
    cancellationToken);

// issued.License.Id = "lic-0199b2…", issued.ActivationCode = "LCN-4H7P-Q2MX" (one use, 7 days) → handed to FarmaViva's IT
```

Scenario B: FarmaViva's IT pastes `LCN-4H7P-Q2MX` in central's admin screen once. Central creates its **licensee key**, sends the
code + the public key + the hash of the tenant root (05 §2), and receives the first License. Scenario A needs no code: the vendor's
central and issuing service share the host.

## 4. Gating features — nothing new in feature code

```csharp
// ✍ Delivery panel on CAIXA-01 — written as before licensing existed
public sealed partial class DeliveryPanelViewModel(IDeliveryOrders orders) : PageViewModel
{
    [StateCommand]
    [RequirePermission(DeliveryPermissions.Ids.Dispatch)]
    async Task DispatchAsync(DeliveryOrderId order, CancellationToken cancellationToken) => await orders.DispatchAsync(order, cancellationToken);
}
```

If FarmaViva's license does not include `delivery` (edition "essencial"), `delivery.dispatch` is **masked** out of every
`PermissionSet` on this installation and at central (⚑LC14). The command is disabled, `VisibleWith` hides the menu entry, the
universal search and the AI agent (Capabilities) no longer offer it, and a call that still arrives is denied:

```csharp
// ✍ A generic error presenter in the app
string text = error switch
{
    Forbidden { Denial.Kind: DenialKind.Unavailable } forbidden
        => $"O módulo \"{forbidden.Denial.Code}\" não faz parte do plano contratado. Fale com o administrador da rede.",
    Forbidden                    => "Você não tem permissão para esta ação.",
    _                            => error.ToString(),
};
```

Code that runs **without an actor** (background services, schedulers) asks the license directly:

```csharp
// ✍ Delivery marketplace poller on PC-GERENTE's broker
public sealed class DeliveryOrdersPoller(ILicense license, IMarketplaceClient marketplace) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await license.WaitUntilAvailableAsync(YFexFarmaLicensing.Modules.Delivery, stoppingToken);   // returns at once when available
            await marketplace.PollAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
```

Named limits are read where the app enforces them (the library never knows what a "delivery integration" is):

```csharp
// ✍ Server-side validation of "add a marketplace"
long allowed = license.GetLimit(YFexFarmaLicensing.Limits.DeliveryIntegrations) ?? 0;     // 3 in "completo"
if (await integrations.CountAsync(scope, cancellationToken) >= allowed)
    return Error.Rejected("delivery.integrations.limit", $"O plano permite {allowed} integrações de delivery.");
```

## 5. Seats: the 5th checkout at Loja Centro

Loja Centro uses `CAIXA-01`, `CAIXA-02`, `CAIXA-03` and the manager's desktop app on PC-GERENTE — four `Terminal` installations,
so its 4 terminal seats are all used (the broker service uses the separate `SiteBroker` seat). The admin tries to enroll `CAIXA-04`:

```csharp
// ✍ Central admin "Nova instalação" — central asks before creating the code (request to Installations, 10 §2)
SeatAvailability seats = await seatsQuery.GetAsync("0=farmaviva/1=loja-centro", InstallationRole.Terminal, cancellationToken);
// seats = SeatAvailability(Used: 4, PerScopeLimit: 4, TenantUsed: 97, TenantLimit: 120) → the screen offers "Substituir uma instalação"
```

If an enrollment is tried anyway, Installations calls `IEnrollmentPolicy` (⚑I23 hook 2) and the PDV's first-run screen shows:

```csharp
EnrollmentRefused { Reason: EnrollmentRefusal.PolicyRefused, PolicyCode: "licensing.seats" }
    => "Limite de caixas desta loja atingido no plano contratado. Substitua uma instalação ou amplie o plano.",
```

## 6. Moving a seat: CAIXA-02's disk died

```csharp
// ✍ Central admin — the hint says the new PC "looks like" nothing known; the admin picks CAIXA-02 by hand
EnrollmentCode code = await Installations.Commands.CreateEnrollmentCodeAsync(new Installations.Commands.CreateEnrollmentCode(
    Scope: "0=farmaviva/1=loja-centro",
    Role: InstallationRole.Terminal,
    DisplayName: "CAIXA-02",
    ValidFor: TimeSpan.FromMinutes(15),
    Replaces: caixa02.Id), cancellationToken);    // request to Installations (10 §2): the seat moves when the new one enrolls
```

When the new PC enrolls, Installations revokes the old `CAIXA-02` and Licensing moves the seat in the same step: no 5th seat is needed,
the new installation is **activation pending** until the vendor's ActivationList includes it (SaaS: seconds; on-premises: the next
check-in, which central starts at once when online). Until then all modules work (pending grace, 7 days, ⚑LC7).

## 7. The license screen ("Licença" in central admin and on every installation)

```csharp
// ✍ Read-only status page (permission licensing.view at central; on terminals the banner below)
public sealed partial class LicenseStatusViewModel(ILicense license) : PageViewModel
{
    [Computed] public string Headline => license.State switch
    {
        LicenseActive active      => $"Plano {active.License.Edition} — válido até {active.License.Subscription.Until:dd/MM/yyyy}",
        LicenseInGrace grace      => grace.Reason switch
        {
            LicenseGraceReason.SubscriptionEnded => $"Assinatura encerrada. Os módulos continuam até {grace.EndsAt:dd/MM}.",
            LicenseGraceReason.CheckInOverdue    => $"A central não fala com o fornecedor desde {grace.LastCheckIn:dd/MM}. Prazo: {grace.EndsAt:dd/MM}.",
            LicenseGraceReason.OfflineTooLong    => $"Sem contato com a central há muito tempo. Prazo: {grace.EndsAt:dd/MM}.",
            _                                    => $"Regularize a licença até {grace.EndsAt:dd/MM}.",
        },
        LicenseExpired expired    => expired.BaseUntil is { } baseUntil
            ? $"Licença expirada: módulos em modo consulta. As vendas continuam até {baseUntil:dd/MM}."
            : "Licença expirada: apenas consulta, fechamento e exportação.",
        LicenseMissing            => "Sem licença: apenas consulta, fechamento e exportação.",
        LicenseRejected rejected  => $"Licença inválida ({rejected.Reason}). Contate o suporte.",
    };

    [Computed] public string SeatLine => license.Seat switch
    {
        SeatActivated activated   => $"Esta máquina está ativada desde {activated.Since:dd/MM/yyyy}",
        SeatPending pending       => $"Ativação pendente — prazo {pending.Until:dd/MM HH:mm}",
        SeatNotActivated          => "Esta máquina não está na lista de ativação do fornecedor: apenas consulta.",
        SeatNotCounted            => "",                                  // central instances
    };
}
```

## 8. Tests

```csharp
// ✍ YFexFarma.Tests — the vendor, the central and three installations in memory
[Fact]
public async Task Delivery_stops_on_the_checkouts_fifteen_days_after_the_subscription_ends()
{
    await using LicensingHarness harness = await LicensingHarness.StartAsync(YFexFarmaLicensing.Product, setup => setup
        .Tenant("farmaviva")
        .Scope("0=farmaviva/1=loja-centro", scope => scope.Broker("PC-GERENTE").Terminals("CAIXA-01", "CAIXA-02"))
        .License(license => license
            .Modules(YFexFarmaLicensing.Modules.Fiscal, YFexFarmaLicensing.Modules.Delivery)
            .Seats(InstallationRole.Terminal, perScope: 2)
            .Subscription(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 10, 31, 0, 0, 0, TimeSpan.Zero))
            .Hosting(LicenseHosting.VendorHosted)));

    harness.Time.AdvanceTo(new DateTimeOffset(2027, 11, 10, 0, 0, 0, TimeSpan.Zero));   // trusted, authenticated anchor
    harness.Installation("CAIXA-01").License.State.ShouldBeOfType<LicenseInGrace>();
    harness.Installation("CAIXA-01").Can(DeliveryPermissions.Dispatch).ShouldBeTrue();

    harness.Time.AdvanceTo(new DateTimeOffset(2027, 11, 16, 0, 0, 0, TimeSpan.Zero));
    harness.Installation("CAIXA-01").Can(DeliveryPermissions.Dispatch).ShouldBeFalse();              // Unavailable
    harness.Installation("CAIXA-01").Can(FiscalPermissions.ViewDocuments).ShouldBeTrue();            // KeepWhenUnavailable
    harness.Installation("CAIXA-01").Can(SalesPermissions.Sell).ShouldBeTrue();                      // base never stops
}
```

`LicensingHarness` uses `TestVendor` with **test** vendor keys that exist only in `YFex.Licensing.Testing`; release builds pin only
the production keys (08 §5).
