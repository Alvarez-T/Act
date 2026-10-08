# 09 — Pharmacy scenarios (YFex.Farma), end to end

Pharmacy **Farma Viva — Loja Centro** (`0=farmaviva/1=loja-centro`): `CAIXA-01`, `CAIXA-02`, `BALCAO-01` (counter, pharmacist),
`PC-GERENTE` (site broker, Windows service), `SiteTopology.SiteBroker`, `CentralFirst`. Business names are Portuguese and technical
names English (YFex convention). Every scenario lists what each piece of this design does.

## S1 — Saturday ISP outage, a normal afternoon

| Step | What happens | Design piece |
|---|---|---|
| 14:00 | modem dies | — |
| 14:00:15 | the three terminals enter `Site` mode. Status bar: "Modo loja — sem conexão com a central desde 14:00" | 04 §1, §4 |
| 14:03 | Ana sells a dipyrone + vitamin C basket on CAIXA-01 | CAIXA-01 decides with its own `OnOffline` handler; envelope signed by CAIXA-01; PC-GERENTE takes custody and returns receipt R1 |
| 14:03 | NFC-e | the fiscal module emits in contingency (no internet); transmission queued in the `Deadline` lane |
| 16:05 | modem back | revocations/bundles down, audit up, then 312 envelopes; review queue: 0 findings |

## S2 — Discount above the limit, with and without the site broker

```csharp
// The same command and rule; only the approval kind differs by mode
var result = await Venda.Commands.FinalizarVendaAsync(venda with { Desconto = 15m.Percent() }, ct);
if (result.IsApprovalRequired)
{
    var approval = await approvals.RequestAsync(result.ApprovalRequest, ct);   // dialog: supervisor types code + password
    result = await Venda.Commands.FinalizarVendaAsync(venda with { Approvals = [approval] }, ct);
}
```

| Mode | Who verifies the manager's password | Token | Limits |
|---|---|---|---|
| `Central` | identity provider | HMAC `Server` | rules |
| `Site` | PC-GERENTE, against the site verifier bundle (a PIN is allowed here) | `Site`, signed by PC-GERENTE | ≤ 20 % per sale; `desconto.aprovado.offline` budget R$ 500/day for the store |
| `Standalone` (PC-GERENTE off) | CAIXA-02, against its cached verifier of the manager (**password**, not PIN; she logged in there yesterday) | `Device`, flagged | `OfflineApprovalCap` ≤ 10 %, terminal slice R$ 100 |

At sync, approvals are checked **as of** the sale time (06 §6). Friday's approval replayed on Monday is valid (fixes F7).

## S3 — Dismissed cashier

Bruno is dismissed at 14:30. The admin disables him at central; `AuthorizationSignals.Revoked` fires and a revocation bundle is issued.

- **Store online:** PC-GERENTE gets the bundle within seconds and Bruno's open session ends. CAIXA-02 refuses his next sale.
- **Store offline since 14:00:** Bruno sells until his session's 12 h limit or his snapshot's 72 h, whichever comes first. At sync each
  sale is accepted (it happened) and grouped as one Low review item: "revogação não entregue: 37 vendas de Bruno após 14:30".
- **Store offline, but PC-GERENTE had received the bundle at 14:31 over a brief reconnection** and kept accepting him: Critical
  finding against the site broker (05 §6, 08 §5).

## S4 — The tampered discount table

A cashier copies an old `rules` file from a USB stick over the terminal's cache folder, hoping for the old 25 % cap.

- Before (F6): the POS applies it silently.
- After: the signed bundle has version 38, below the floor of 41 → `OlderThanFloor` → the version is not loaded. A `RollbackAttempt`
  finding is stored locally and shipped at sync. If the attacker restored a full disk image (floors included), central sees envelopes
  citing v38 from an installation that had acknowledged v41 → rollback finding plus an installation-sequence fork → Critical.

## S5 — Clock set back to stretch offline permissions

CAIXA-01 has been standalone since Friday. On Monday 13:30 someone sets Windows to Sunday 09:00 so Ana's snapshot (valid until Monday
14:00) keeps working longer.

- `TrustedTimeProvider`: wall clock < high-water − 2 min → `RollbackDetected`; `now` keeps counting from the high-water mark.
- The snapshot expires at 14:00 as planned. Sales 13:30–14:00 carry `ClockRollback` → High finding with the installation and user.

## S6 — Stolen terminal

CAIXA-02 is stolen at night.

- The admin revokes the installation at central (or the manager at PC-GERENTE if central is down). The site broker refuses its certificate
  from then on.
- The thief cannot read the stores: the storage key is wrapped by the TPM-bound CNG key (05 §3) and the key cannot leave the TPM. On
  a software-key installation, the Windows account password is the remaining barrier.
- If the thief starts the app elsewhere and "sells", the envelopes are signed after the revocation time → `Rejected` + Critical
  finding (08 §3.1 step 1).

## S7 — Replacing a dead terminal during the outage

CAIXA-03 dies on Saturday at 15:00. The site manifest has `OfflinePairing = Enabled`.

1. The spare PC (YFex installed, not enrolled) finds PC-GERENTE through the beacon.
2. Both screens show "Confirme: 482 913"; the manager signs in on PC-GERENTE and confirms.
3. The spare gets a provisional certificate (72 h). Its sales carry `ProvisionalInstallation`; it cannot issue device approvals.
4. Monday: central confirms the installation (normal certificate), and the review item closes as "accepted".

## S8 — Agreement customer (convênio) buying in two places

Empresa Alfa's employees buy on account, with a monthly limit of R$ 3 000 for the company.

- Central grants Loja Centro a 24 h budget of R$ 600 for `convenio.credito / Alfa` (the agreement is active at this store). Loja Norte
  gets R$ 400. Both are subtracted from Alfa's available credit at central.
- During the outage PC-GERENTE gives CAIXA-01 and CAIXA-02 R$ 100 each and keeps R$ 400.
- A R$ 450 purchase on account in `Site` mode is refused ("acima do limite offline; pague de outra forma ou aguarde a conexão"),
  because only R$ 400 + slices are left.
- Nothing can overspend Alfa's credit, even if both stores are offline at the same time.

## S9 — Prescription-only medicine at the counter

The pharmacist on BALCAO-01 sells an antibiotic, which requires prescription retention. `RegistrarReceitaRetida` is a **fact**:

```csharp
b.Command<RegistrarReceitaRetida, Unit>()
    .RequirePermission(FarmaPermissions.Values.ReceitaReter)                 // pharmacist role
    .OnOffline<RegistrarReceitaOffline>()
    .Offline(offline => offline.RecordsFact().ClaimsUnique(command => command.NumeroReceita));   // broker refuses a second use in the store
```

The same prescription number presented at a second counter is refused by the broker's unique claim (in standalone the claim is
provisional and a duplicate becomes a High finding at sync). The patient's personal data entered offline is sealed for central with
the tenant's intake key (privacy 05 §5.5). The site broker cannot read it. Only the store's own display copy lives in the device's encrypted store with a TTL. Any regulatory reporting stays a
central job after sync.

## S10 — Government program sale while central is down but internet is up (situation 2b)

Farmácia Popular-style sales need an online authorization from the program's own system per sale. With central down and internet up,
the integration on BALCAO-01 still reaches the program, so the sale proceeds and is recorded as a fact for central. With the internet
down, the message returns `Error.Unavailable("requires internet")`. The counter tells the customer that the program is unavailable,
and no offline workaround exists.

## S11 — Store price override during the outage

At 14:30, during the S1 outage, the manager sees that the shelf label says R$ 9,90 for dipyrone while the system says R$ 12,90. She
fixes it on CAIXA-01 with `AlterarPrecoNaLoja` (store tier, within the PMC guardrail signed by central, [06](06-offline-authority.md) §5).

| Time | Where | What happens |
|---|---|---|
| 14:30:00 | CAIXA-01 | session from PC-GERENTE holds `preco.alterar.loja`; guardrail ok (≤ PMC R$ 12,90); override applied locally |
| 14:30:01 | PC-GERENTE | takes custody, orders the entry in the site sequence, pushes it on the site feed. It does not interpret the price |
| 14:30:02 | CAIXA-02, BALCAO-01 | `PrecoNaLojaProjection` verifies installation signature, session, sequence and guardrail, then applies it; next sale charges R$ 9,90 |
| 15:10–15:30 | CAIXA-03 (cable out) | sells at R$ 12,90 citing the old version; catches up when reconnected |
| 16:05 | central | confirms the override as an official store-tier price version. The 3 sales at R$ 12,90 on CAIXA-03 after 14:30 get one Low finding ("override não recebido") |

Variant: a cashier tries to inject a cheaper price with a forged feed entry → no valid broker session holding the permission, no site
sequence signature → every terminal refuses it, and a security finding is stored and shipped.
