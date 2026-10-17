# 03 — API and examples (dev side first)

The pharmacy: **FarmaViva**, store **Loja Centro** (`0=farmaviva/1=loja-centro`). Machines:

| Machine | Installations on it | Role | Key store |
|---|---|---|---|
| `PC-GERENTE` (manager's office) | "YFex Site Broker" service + the manager's FarmaViva desktop app | `SiteBroker` + `Terminal` | TPM, machine scope + user scope |
| `CAIXA-01`, `CAIXA-02` | FarmaViva PDV | `Terminal` | TPM (or software KSP on old PCs) |
| `NOTEBOOK-REGIONAL` (Marcos, regional manager) | FarmaViva desktop app | `Terminal`, enrolled at `0=farmaviva/1=regiao-sul` | TPM |
| a customer's laptop on the shop Wi-Fi | — | — | — |

## 1. Composition roots

> **As built (2026-10-13, 08 §5):**
> - **Installation side:** `services.AddYFexInstallations(i => i.Application("farma-pdv", version).Terminal().UseWindowsKeyStore()
>   .UseWindowsHardwareHints().EnrollAt(uri))`.
> - **RPC client:** `rpc.UseInstallationAssertion()` + `services.AddYFexInstallationRpcClient()`.
> - **Central:** `services.AddYFexInstallationsCentral(c => c.Tenant("farmaviva", (sp, options) => new InstallationAuthority(...))
>   .RequireHardwareKeys(InstallationRole.SiteBroker))`, `services.AddYFexInstallationRpcServer()`, and
>   `app.MapYFexInstallationsEnrollment()` (package `.Central.AspNetCore`).
> - **Admin codes:** `CentralEnrollmentService.CreateCodeAsync(new EnrollmentCodeRequest(scope, role, applicationName, displayName))`.
>   Permission checks are the host's command handler's job.
> - **SQL registry:** not built yet (in-memory registry only).

```csharp
// ✍ CAIXA-01 / CAIXA-02 — Program.cs of the PDV
services.AddYFexInstallations(installations => installations
    .Terminal()                                                    // configured role; scope comes from enrollment
    .UseWindowsKeyStore()                                          // YFex.Installations.Windows: CNG, TPM first, user scope
    .EnrollAt(new Uri("https://api.farmaviva.com.br/")));          // central's enrollment endpoint (public TLS)

services.AddYFexMessagingRpcClient(rpc => rpc.UseInstallationAssertion());   // YFex.Installations.Rpc (⚑I11)

services.AddYFexTime(time => time
    .UseCentralAnchor()                                            // Messaging.Rpc (built, T2)
    .UseSiteAnchor()                                               // Installations: the broker over the peer channel (⚑I19)
    .UsePeerTime()                                                 // Installations: friends as unauthenticated fill-in (⚑T13)
    .UseNetworkTime("a.ntp.br", "b.ntp.br")
    .PersistHighWater());

services.AddYFexOffline(offline => offline.Terminal());            // YFex.Offline reads identity, keys, trust and channels from here
services.AddYFexLogging(log => log.Shipping(s => s.ThroughFriends()));   // relay channel when central is unreachable (⚑O26)
```

```csharp
// ✍ Central — the API host
services.AddYFexInstallationsCentral(central => central
    .Tenant("farmaviva", ca => ca.UseRootKey(rootKeyStore, "tenant/root-ca"))   // P-256, TPM or a SigningKeyRing key
    .UseBundleSigning(rings => rings.GetSigningKeysAsync("installations/bundles"))
    .UseSqlRegistry(connectionFactory));                           // installations, public keys forever, codes, rosters (later: .Data)
services.AddYFexInstallations(installations => installations.Central());      // central instances are installations too
services.AddYFexMessagingRpcServer(...);
app.UseYFexMessagingRpcServer(rpc => rpc.CallContext(ctx => ctx.FromConnection().WithTenant().WithInstallation()));  // ⚑I11
app.MapYFexInstallationsEnrollment();                              // POST /installations/enroll, /renew (public TLS)
```

The site broker has **no developer code** (offline ⚑O20). The generic host composes Installations and Offline and, until the future
**YFex.Installer** exists (user, 2026-10-07), takes the code as a start-up argument and calls the same `IInstallationEnrollment`:

```bash
YFex.Offline.Broker.exe --enroll 7Q4K-M2XD --display-name PC-GERENTE      # (sketch) Offline's host; machine-scope keys
```

## 2. Enrolling CAIXA-02

**Admin at central** (holds `installations.enroll`):

```csharp
EnrollmentCode code = await Installations.Commands.CreateEnrollmentCodeAsync(new Installations.Commands.CreateEnrollmentCode(
    Scope: "0=farmaviva/1=loja-centro",
    Role: InstallationRole.Terminal,
    DisplayName: "CAIXA-02",
    ValidFor: TimeSpan.FromMinutes(15)), cancellationToken);
// code.Text = "7Q4K-M2XD" — one use, bound to scope + role, shown to the admin or printed on the installation sheet
```

**On CAIXA-02** (first-run screen of the PDV):

```csharp
public sealed partial class FirstRunViewModel(IInstallationEnrollment enrollment) : PageViewModel
{
    [Observable] public partial string Code { get; set; } = "";
    [Observable] public partial string Message { get; set; } = "";

    [StateCommand]
    async Task EnrollAsync(CancellationToken cancellationToken)
    {
        EnrollmentResult result = await enrollment.EnrollAsync(new EnrollmentCode(Code), displayName: "CAIXA-02", cancellationToken);
        Message = result switch
        {
            EnrollmentSucceeded enrolled  => $"Instalado como {enrolled.Identity.DisplayName} em {enrolled.Identity.Scope}",
            EnrollmentRefused refused     => refused.Reason switch
            {
                EnrollmentRefusal.CodeExpired  => "Código expirado — peça outro ao administrador",
                EnrollmentRefusal.CodeUsed     => "Código já usado",
                EnrollmentRefusal.RoleMismatch => "Este código é para outro tipo de instalação",
                _                              => "Instalação recusada",
            },
            CentralUnreachable              => "Sem conexão com a central. Tente de novo ou peça pareamento ao gerente",
        };
    }
}
```

```csharp
// Library types (native union per ⚑K9/⚑N27 style: cold path, record cases)
public sealed record EnrollmentSucceeded(InstallationIdentity Identity, KeyProtection TransportProtection, KeyProtection EvidenceProtection);
public sealed record EnrollmentRefused(EnrollmentRefusal Reason);
public sealed record CentralUnreachable(string Detail);
public union EnrollmentResult(EnrollmentSucceeded, EnrollmentRefused, CentralUnreachable);
```

**Result:** CAIXA-02 kept the id it minted at first run (its first-run logs already carry it), its TPM holds the two keys, central
recorded both public keys forever, and `LocalInstallation.Current` changes from `InstallationNotEnrolled` to
`CAIXA-02 · Terminal · 0=farmaviva/1=loja-centro · InstallationEnrolled(certificate valid until 2027-01-03)`. Renewal happens alone every ~60 days.

## 3. Reading "who am I" from any library (no reference to the repo)

```csharp
// Audit engine, Logging, Privacy — YFex core only
InstallationIdentity? me = LocalInstallation.Current;
// me.Id = 0199a3c4-…, me.Role = Terminal, me.Scope = "0=farmaviva/1=loja-centro", me.DisplayName = "CAIXA-02", me.Status is InstallationEnrolled
```

## 4. Friends

```csharp
public interface IInstallationFriends
{
    FriendSet Current { get; }                                         // immutable snapshot, refreshed by roster + discovery
    event Action<FriendSet>? Changed;
    ValueTask<FriendConnection?> ConnectAsync(InstallationId friend, PeerChannel channel, CancellationToken cancellationToken);
}

public sealed record Friend(InstallationId Id, string DisplayName, InstallationRole Role, string Scope, FriendRelation Relation,
    FriendReachability Reachability, ImmutableArray<PeerChannel> Offers, bool ReachesCentral);

public enum FriendRelation : byte { SameSite = 1, Visitor = 2, OtherSite = 3, Central = 4 }
```

```csharp
// ✍ Manager screen "Máquinas da loja" (desktop app on PC-GERENTE)
foreach (Friend friend in friends.Current)
    rows.Add(new MachineRow(friend.DisplayName, friend.Role, friend.Reachability, friend.ReachesCentral));
```

| DisplayName | Role | Relation | Reachability | Reaches central |
|---|---|---|---|---|
| PC-GERENTE | SiteBroker | SameSite | Connected | yes |
| CAIXA-01 | Terminal | SameSite | Connected | no (its cable is fine, its route to the internet is not) |
| NOTEBOOK-REGIONAL | Terminal | Visitor | Seen (beacon verified) | yes (4G) |
| *(192.168.0.57 — no certificate)* | — | — | **not listed**: a candidate, never a friend | — |

The last row never appears in `FriendSet`: the customer's laptop answered nothing, or sent a beacon without a valid signature
(dropped and counted), or connected without a certificate (handshake refused, `installations.peer.refused` metric +1, a site
finding after 10 refusals in a minute).

## 5. Using a channel (a library's own traffic over the mesh)

```csharp
// ✍ YFex.Logging.Shipping (sketch) — ship through a friend that reaches central when this installation does not
public sealed class FriendRelayShipper(IInstallationRelay relay) : IBatchTransport
{
    public ValueTask<RelayReceipt> SendAsync(ReadOnlyMemory<byte> signedBatch, RelayItemId id, CancellationToken cancellationToken)
        => relay.ForwardToCentralAsync(new RelayItem(LoggingRelayKinds.Batch, id, signedBatch), cancellationToken);
}
```

```csharp
// Installations — the relay channel contract (⚑O26: only signed or idempotent items)
public readonly record struct RelayItemKind(string Name);              // "logging.batch", "audit.batch", "offline.envelope", …
public readonly record struct RelayItemId(Hash256 Value);              // the owner's idempotency id; duplicates are dropped
public sealed record RelayItem(RelayItemKind Kind, RelayItemId Id, ReadOnlyMemory<byte> SignedBytes);
public enum RelayReceipt : byte { DeliveredToCentral, HeldByFriend, NoFriendAvailable, Refused }

public interface IInstallationRelay
{
    ValueTask<RelayReceipt> ForwardToCentralAsync(RelayItem item, CancellationToken cancellationToken);
}
```

> **As built (I4, 2026-10-14):** exactly this contract (plus `RelayItemKind.IsValid`). The producer's side is `InstallationRelay`
> (also registered as `IInstallationRelay` by `.Peers()`); at central the owner implements
> `IRelayItemConsumer { RelayItemKind Kind; ValueTask<bool> ConsumeAsync(RelayedItem item, InstallationKeyRecord producer, CancellationToken) }`
> and registers it as a service; `AddYFexInstallationsCentral` + `AddYFexInstallationCentralLink()` serve it over RPC, and installations add
> `peers.RelayThroughCentralRpc()`. Bundles of an owner's kind are gossiped with `peers.GossipBundles(kind)` (needs an `IBundleStorage`).

The friend **never opens** `SignedBytes`; central verifies the producer's signature. `HeldByFriend` is not custody in the offline
sense: Offline adds its signed receipts (R1/R2) on top for envelopes (06 §2).

## 6. Declaring a new channel (Devices remoting)

```csharp
// ✍ YFex.Devices.Remoting (sketch) — share COM3 of BALCAO-01 with friends of the same site that hold the permission
services.AddYFexInstallations(installations => installations.Mesh(mesh => mesh
    .Channel(DevicePeerChannels.Serial, channel => channel
        .SharedWith(FriendScope.SameSite)                              // ⚑O29: who may open it
        .Handler<SerialShareChannelHandler>())));
```

Before (Devices plan §12): a PSK in a config file on every PC + an IP allow-list. After: the installation certificate, the roster and
`FriendScope.SameSite`; the per-port allow-list names `InstallationId`s (07 §5).

## 7. The regional manager's laptop

`NOTEBOOK-REGIONAL` is enrolled at `0=farmaviva/1=regiao-sul`. Marcos opens it at Loja Centro.

- Central listed it as a **visitor** in Loja Centro's roster (`Visitors: [notebook-regional: channels time, relay]`).
- PC-GERENTE sees its verified beacon and accepts its mTLS connection: relation `Visitor`.
- Channels allowed: **time** and **relay** (tenant-wide by default, ⚑O29). Its log batches go to central through PC-GERENTE when the
  4G drops. **Bundles and facts of Loja Centro are not shared with it** unless the roster grants them, and Offline never gives it a
  budget slice of the store.
- At a store whose roster does not list it, it is a friend for nothing at the site level: `OtherSite` is computed only for tenant-wide
  channels when the roster allows "any installation of the tenant" for that channel.

## 8. Replacing a dead checkout (hint, not trust)

Saturday, `CAIXA-02`'s disk dies. The technician reinstalls Windows and the PDV on the same PC and enrolls it with a new code.

```
central admin screen, "Instalações de Loja Centro":
  ⚠ CAIXA-02 (novo, 0199b0e1-…) parece ser o mesmo computador que CAIXA-02 (0199a3c4-…, sem contato desde 14:02).
     [Revogar o antigo]  [São computadores diferentes]
```

The suggestion comes from the keyed hardware hint (⚑I16). It changes nothing by itself: the old installation stays enrolled until the
admin revokes it, and its envelopes signed before the revocation are still accepted.

## 9. The customer's laptop on the shop Wi-Fi

| Attempt | What happens |
|---|---|
| listens to beacons | hears `yfex.installation` datagrams: an HMAC site tag, an installation id, a port. Learns no tenant name, no store name |
| replays a captured beacon | dropped: issued-at older than 30 s, or same sequence (Networking duplicate filter) |
| forges a beacon "I am PC-GERENTE" | dropped: the signature does not verify with PC-GERENTE's evidence key from the roster |
| connects to 192.168.0.10:7443 | TLS 1.3 requires a client certificate chaining to FarmaViva's root: refused before any frame |
| ARP-spoofs PC-GERENTE's IP | terminals connect to it and refuse **its** certificate (no tenant chain): the store falls to Standalone at worst; nothing leaks |
| copies CAIXA-01's state file | the CNG keys stay in CAIXA-01's TPM; the copy cannot complete a handshake |
</content>
</invoke>
