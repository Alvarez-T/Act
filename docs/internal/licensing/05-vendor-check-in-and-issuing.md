# 05 — Vendor check-in and the issuing service

## 1. Hosting modes (⚑LC19)

| `LicenseHosting` | Who runs central | How central reaches the vendor | Who enforces seats | Freshness |
|---|---|---|---|---|
| `VendorHosted` | the vendor (SaaS) | in-process `IVendorLicensing` (or internal HTTPS when deployed apart) | central (the vendor's own code) **and** the activation lists | License re-issued when older than 1 day |
| `CustomerHosted` | the customer, on its server, with internet | HTTPS check-in, daily | central's policy (customer's server) **and** the vendor's activation lists | each check-in |
| `Disconnected` | the customer, no internet by policy | **files** carried by a person, every ≤ 90 days | as above, with activations arriving per file | each file check-in |

The mode is written in the License by the vendor. Central's `ReachVendor(...)` is only transport configuration; a License whose
hosting does not match how it is being used (a `VendorHosted` License checked in from the outside) is a vendor finding.

## 2. The licensee key and activating an on-premises central

An on-premises central needs an identity **towards the vendor**. It is not an installation key (those are certified by the tenant's
own CA, which the customer controls), and it is not the tenant CA. It is a separate **licensee key**:

```csharp
// YFex.Licensing.Central — key ring "licensing/licensee" (P-256, KeyPurpose.Signing), like central's other server rings:
// software keys under the root key, shared by every central instance; TPM-backed root on Windows, 0600 root-key file elsewhere (09)
central.UseLicenseeKey(rings => rings.GetSigningKeysAsync("licensing/licensee"));
```

Activation, once per central (admin with `licensing.activate` types the code from the vendor):

```
 central ──POST /licensing/activate──► vendor
   ActivationRequest (canonical bytes, SignedPayload domain "yfex.licensing.activation.v1", signed by the licensee key = proof of possession)
     activation code "LCN-4H7P-Q2MX" · licensee public key (SPKI) · tenant id "farmaviva" · tenant root certificate (SPKI)
     central installation id · app version · catalog hash · central trusted time · nonce
 vendor: code valid, unused, for tenant "farmaviva"? → binds licensee key + TenantRoot = SHA-256(root SPKI) to the License,
         issues the first License (Sequence 1) → response with the License bundle
```

- The code is **one use, 7 days**, tied to one customer account and tenant id.
- **Re-activation** (central reinstalled, licensee ring lost, tenant root replaced after a disaster) needs a new code from the vendor.
  The vendor sees the history ("third activation this year") — a finding, not a block.
- The licensee key **rotates** with the ring (yearly); the check-in after a rotation is signed by the old key and carries the new
  public key, as Installations' renewal does.

## 3. Online check-in (⚑LC13)

```csharp
// Wire payloads (canonical binary; shown as records)
public sealed record CheckInRequest(
    LicenseId License,
    long AcknowledgedSequence,                         // newest License central has applied
    long DeltaCursor,                                  // position in central's activation-delta outbox (03 §3)
    ImmutableArray<ActivationDelta> Deltas,            // since the vendor's last acknowledged cursor
    ImmutableArray<SeatCountRow> Counts,               // (scope unit, role, count) — reconciliation, not authority
    ImmutableArray<FindingCount> Findings,             // duplicate installation assertions, clock rollbacks, rejected bundles (counts)
    ImmutableArray<MeterTotal> Meters,                 // only when License.MeteringEnabled (07)
    InstallationId CentralInstallation,
    string AppVersion,
    TrustedInstant CentralTime,
    ReadOnlyMemory<byte> Nonce);                       // 16 bytes

public sealed record CheckInResponse(
    ReadOnlyMemory<byte> Nonce,                        // echo, bound by the vendor's signature on the response
    long AcceptedDeltaCursor,
    SignedBundle License,                              // re-issued, Sequence + 1 (freshness, 04 §4)
    ImmutableArray<SignedBundle> ActivationLists,      // only the scope units that changed
    SignedBundle? VendorKeys,                          // when the key list changed (09 §2)
    DateTimeOffset VendorTime);
```

- **Transport:** `POST /licensing/check-in`, HTTPS (public TLS), body = `SignedPayload` with domain `yfex.licensing.check-in.v1`
  signed by the licensee key; the response is a `SignedPayload` with domain `yfex.licensing.check-in-response.v1` signed by the vendor
  key (binds the nonce, so a captured old response cannot be replayed to central). Each bundle inside is also vendor-signed on its own
  and is what installations verify.
- **When:** daily at a random minute inside a configured window (spreads the vendor's load), **immediately** (debounced 1 minute)
  when there are new activation deltas and central is online, and on demand (`licensing.check-in`). Failures retry with exponential
  back-off up to 1 hour; the outbox of deltas is durable, so nothing is lost.
- **Idempotent:** the vendor keys the request by (licensee key, nonce) and applies deltas by cursor; a retried request gets the same
  answer.
- **After success** central hands the bundles to Installations' `IBundleStore.AcceptAsync` (verified like on any installation) and
  they are pushed to connected installations and gossiped on the LAN.
- **Seat counts are reconciliation only:** the activation lists are built from deltas; a count that differs from the vendor's
  view is a finding (a patched central reporting fewer checkouts than its deltas showed).

## 4. File check-in (disconnected sites, or a fallback during a long outage)

```
 central admin "Licença → Exportar solicitação"  →  farmaviva-2027-01-15.yfex-license-request   (the SAME signed CheckInRequest bytes)
 a person uploads it in the vendor's customer portal (any machine with internet, a phone)
 portal returns                                   →  farmaviva-2027-01-15.yfex-license-response  (the SAME signed CheckInResponse bytes)
 central admin "Licença → Importar resposta"      →  applied exactly like an online response (nonce must match an open request)
```

- An open request file is valid for **30 days** (a trip to the portal can take time).
- The vendor's portal is the vendor's own web application; it calls the same handler as the HTTPS endpoint, so there is one code path.
- A `Disconnected` License has `CheckInInterval = 90 days` and `OfflineAllowance = 180 days` (04 §4); its activation lists arrive by
  file too, so a new checkout in a disconnected site is pending until the next file trip — the admin is told so when creating the code.

## 5. The issuing service (⚑LC16)

```
 YFex.Licensing.Vendor (hosted only by the vendor; YFex.Data store; every command audited with YFex.Audit)
   CustomerAccount ─┬─ Tenant binding (tenant id, TenantRoot hash, licensee public keys, activation history)
                    └─ License history (every Sequence kept forever) ── ActivationState per scope unit ── lineage (03 §4)
   ActivationCode (one use)   CheckInLog (every request/response hash)   Findings   MeterTotals (07)
   signing ring "licensing/vendor" (active key; standby key OFFLINE)       editions from the shared catalog assembly
```

| Command / query (`LicensingVendor.Commands` / `.Queries`) | Permission (`LicensingVendorPermissions`) |
|---|---|
| `IssueLicense`, `IssueTrial`, `ChangeLicense` (modules, seats, limits, add-ons), `RenewSubscription` | `licensing.vendor.issue` |
| `CreateActivationCode`, `RevokeLicensee` (a stolen server) | `licensing.vendor.activation` |
| `ActivateInstallations` / `RefuseActivation` (manual override of the automatic decision) | `licensing.vendor.activation` |
| `RotateVendorKey` (adds a standby, 09 §2) — requires **two** different people (approval, Authorization 4b) | `licensing.vendor.keys` |
| `Usage`, `CheckIns`, `Findings`, `UsageForBilling(period)` (export to the vendor's billing system) | `licensing.vendor.view` |

- **One protocol for everyone.** `IVendorLicensing` (activate, check in, apply deltas) is what the HTTPS endpoint and the SaaS
  in-process path both call. A SaaS central in the same host calls it directly; a SaaS central deployed apart uses HTTPS with a
  licensee key like an on-premises one.
- **SaaS activation is immediate:** central forwards each registry event as a delta in-process; the vendor answers with the updated
  list in the same call, so `SeatPending` lasts milliseconds.
- **Automatic decisions** (activate or refuse a delta) follow the License limits; humans only override.
- **Prices are not here.** `UsageForBilling` gives the vendor's billing system seat-days and meter totals per tenant; invoicing,
  dunning and "stop renewing when unpaid" are the vendor's business processes that end in `RenewSubscription` or not.

## 6. What the vendor receives (privacy)

| Sent | Not sent |
|---|---|
| installation ids, roles, scope texts (store identifiers), evidence key ids, keyed hardware hints (tenant-keyed MAC, not reversible, not linkable across tenants — ⚑I16) | user names, CPFs, customers, sales, prices, products |
| seat counts, finding counts, meter totals (07), app versions, central's trusted time | display names (`CAIXA-02`) — only with `.ShareDisplayNames()` for support |

Installation ids are pseudonymous technical identifiers (installations 02 §4: "not personal data by itself"). On-premises licensing
terms must state that the check-in exists and what it carries; that text is the vendor's, not the library's.
