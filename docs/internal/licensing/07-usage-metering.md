# 07 — Usage metering (optional, ⚑LC15)

Off by default. A product declares meters in its catalog (01 §1: `fiscal.documents-issued` on the fiscal module); a License turns
reporting on with `MeteringEnabled`. Meters exist for **usage-based plans and capacity planning**, never for blocking work.

## 1. Counting on the installation

```csharp
// YFex.Licensing
public interface IUsageMeter
{
    void Add(MeterId meter, long amount = 1);     // lock-free increment of a pre-registered slot: < 20 ns, 0 bytes
}

// ✍ YFex Farma, after an NFC-e is authorized (fiscal module code)
usage.Add(YFexFarmaLicensing.Meters.FiscalDocumentsIssued);
```

- Slots are created at start-up from the catalog (an unknown meter id throws in `Add` during tests, is ignored in release).
- Counters are kept per **(meter, day, scope)** where the day comes from licensing's decision instant E (04 §2), so a wrong clock
  cannot move usage into another day.
- Flushed every minute to the app's `IKeyValueStore` (sealed with `AddYFexStorageEncryption`), so a crash loses at most a minute.
- With `MeteringEnabled = false`, `Add` returns at once (one branch); nothing is stored.

## 2. Reporting

| Hop | How | Idempotency |
|---|---|---|
| installation → central | `UsageReport` (installation id, day, totals per meter, applied License `Sequence`, seat state), signed with the installation's **evidence key** (Installations), sent once a day and at reconnect over the normal RPC connection, or through a friend's **relay** channel (⚑O26: signed, idempotent) | (installation, day, revision) — a later revision of the same day replaces the earlier one |
| central → vendor | SaaS: in-process, daily; on-premises: `MeterTotal` rows in the check-in (05 §3) | per (scope unit, meter, day) |

Central aggregates per tenant, scope unit and day, keeps 13 months, and shows them on the license screen. The vendor exposes them to
its billing system through `UsageForBilling` (05 §5).

## 3. Quotas are soft and vendor-side

There is **no quota in the License** and no code path that refuses work because a counter is high. Contract quotas ("5 000 fiscal
documents per month per store, overage billed") are evaluated by the vendor on the reported totals: a finding or an invoice line,
never a blocked sale. Reason: a counter is the least reliable number in the system (offline days, a deleted store, a patched client),
and a pharmacy refusing to issue a fiscal document because of it would be worse than any over-use.

## 4. Honest limit and privacy

- On-premises totals come from the customer's central; they can be under-reported. Plausibility checks (documents per checkout per
  day against the population of similar stores) are vendor analytics, not library code.
- Reports contain **totals only**: no user, customer, product or amount. A meter whose name or unit would describe a person is a
  design error in the app's catalog.
