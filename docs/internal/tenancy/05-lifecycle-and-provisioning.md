# 05 — Lifecycle and provisioning

## 1. States (⚑TN9, ⚑TN12)

```
Registering ──► Provisioning ──► Active ◄──► Suspended
     │               │             │
     ▼               ▼             ├──► Moving ──► Active          (region move, §3)
 NeedsRegion       Failed          └──► Offboarding ──► Deleted   (tombstone)
     │  (region recorded)
     └──► Provisioning
```

| State | Requests | Background work (Cluster/Jobs) | Data |
|---|---|---|---|
| Registering / Provisioning / NeedsRegion | refused (`TenantNotReady`) | none | being created |
| Active | served | runs | normal |
| Suspended | refused with `TenantSuspended` (reason shown to admins only) | tenant workers and jobs paused | kept |
| Moving | served except during the short pause of each library's switch | paused per library during its switch | copied |
| Offboarding | admin and export only | none, except export and retention jobs | kept until the retention period |
| Deleted | `null` from the store | none | deleted; tombstone (key, dates) kept |
| Failed | refused | none | partial; operator retries or rolls back |

## 2. The provisioning runner (⚑TN10)

- Runs steps in `Order`; for each step: mark `Running` → call → mark `Done`; on transient failure, Resilience retry; on permanent failure,
  `Failed` with the step name.
- **Resumes** after a crash: steps marked `Done` are skipped; a step left `Running` is called again (steps are idempotent).
- One runner per tenant at a time: Cluster lease `tenancy.provision:<tenant>` when Cluster is present (single node otherwise).
- With YFex.Orchestration present, the runner is a `ProcessManager<TState>` (durable waits, e.g. "wait for the admin to accept the
  invitation" is **not** part of provisioning — `Active` does not wait for people).
- Rollback: deprovision the steps already `Done`, in reverse order.

## 3. Region move (⚑TN8)

1. Command `tenancy.region.move` (permission, approval): target must be an open `Home` region allowed by the residency rule.
2. State `Moving`; each step's `MoveAsync` runs in order — Data.Placement cross-region move (copy, catch up, short pause, switch),
   Files copy of the tenant prefix, Backup policies re-pointed to the new region's destinations, Cluster stops the tenant's workers in the
   old region.
3. Home region switched (version + 1, notice); edge/DNS sends the tenant's traffic to the new region (platform; the old region answers
   with a redirect/`TenantInOtherRegion` meanwhile).
4. Old copies retired after the retention window (each library's own rule).

## 4. Offboarding and deletion (⚑TN13)

| Step | Owner |
|---|---|
| Portability export offered (database rows + files) | Backup (tenant export) |
| Access closed (sessions revoked, tokens refused) | Authentication / Authorization through the state |
| Retention clock (contract period, legal minimums from Audit/Fiscal) | Registry stores the dates; owners declare minimums |
| Delete: each step's `DeprovisionAsync` in reverse order (rows, prefixes, policies, license) | each library |
| **Destroy the tenant key ring** → every backup and export of the tenant becomes unreadable | Cryptography |
| Tombstone: key, created/deleted dates; the key is **never reused** | Registry |
