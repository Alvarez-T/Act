# 07 — Scenarios per place

## 1. Vendor cloud

| Scenario | What happens |
|---|---|
| New customer signs up | tenant registry chooses the home region (residency), then Placement picks the database there, Files creates the prefix, Backup adds the policies ([tenancy 03 §2](../tenancy/03-api-and-examples.md)) |
| Second Postgres server needed | operator adds `br-sp-04` to `placement.databases`; the default policy places new tenants there; existing tenants move one by one (05 §1) |
| Big tenant slows a shared server | move it to a `dedicated` database during the night; seconds of 503 for that tenant only |
| Customer requires EU-only data | `ResidencyRule(eu-west)`; placement, replicas, backups and failover all refuse other regions |
| Tenant deleted rows by mistake yesterday | platform PITR to scratch → tenant export → in-place tenant restore (03 §5); others unaffected |
| Region outage | runbook 05 §4; tenants without an allowed standby region stay paused and are told so |
| Daily evidence that backups work | weekly platform drill + monthly sample of tenant exports restored into scratch; dashboard shows last verified age |
| Tenant leaves | portability export handed to them; after the contract period, rows deleted, tenant ring destroyed (old exports unreadable) |

One cluster per region (agreed); each region's backup and drill jobs run as Jobs `[Singleton]` on that region's cluster.

## 2. On-premises central

| Scenario | What happens |
|---|---|
| Typical install (one server, SQL Server or Postgres) | static placement (no change); nightly native backup to `D:\YFexBackups` + off-site copy (vendor bucket in the customer's allowed region, or the customer's NAS/S3), sealed to the customer's ring (+ vendor recovery ring if opted in) |
| Server disk dies | new server, install, import key ring (Cryptography procedure), `restore --new-database` from the off-site copy, erasure ledger re-applied, epoch bumped, sites resync from their outboxes |
| Ransomware encrypts the server | immutable off-site copy; restore as above; site outboxes fill the gap since the last backup (Offline reconciliation) |
| Two-server central | backup job on one node via `[Singleton]`; nothing else changes |
| No IT staff | defaults on by install (YFex.Installer): local + vendor off-site, weekly drill into SQLite/scratch, admin view warns in red when a backup is older than 2 days |

## 3. Shop site (⚑DB24)

| Scenario | What happens |
|---|---|
| Nightly | broker host module: SQLite online backup of the broker store → sealed to **central's** ring → uploaded through the Offline/Installations channel (resumable) → central records it in its catalog; optional USB copy |
| Broker PC replaced | new broker enrolls (Installations); central sends the latest site set; broker restores, then resyncs newer data from central and terminals' outboxes |
| Terminal replaced | no backup needed: terminal resyncs; terminals with unsent sales are drained first or, opt-in, back up their outbox store to the broker |
| Store offline for days | backups stay local (USB/broker disk, retention 7) and upload later |
| Privacy | sets are sealed before leaving the device; the site cannot read other sites' sets |

Upload size is bounded (SQLite file of the broker, compressed); bandwidth limits come from Networking conditions.
