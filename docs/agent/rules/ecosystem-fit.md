# Ecosystem fit: reuse, integrations and placement

These rules apply to every project kind, whenever you design a library, a phase or a feature, or change what an existing library does. YFex projects are boundaries with their cross-cutting concerns separated. The user decides where each piece lives, so every fit you find becomes a decision for them.

## 1. Check every library before designing

- Read the [infrastructure map](../infrastructure-map.md) in full, the open findings in the [atlas](../../atlas/README.md) (`atlas.json`, `findings`), and the folders under `docs/internal/` that are still designs.
- Go through each library and design in the map, not only the ones you expect to need. Give each one an outcome:
  - **used**: what it does for this design;
  - **fits, the library lacks a feature**: what that library would have to add;
  - **better placement**: a piece of this design belongs in that library or design (or one of its pieces belongs here);
  - **not a fit**: one line saying why.
- Consider useful outside libraries too. A third-party package is a decision under the dependency rule of [infrastructure](infrastructure.md).

### Owners to check before you write it yourself

Building any of these yourself is a decision for the user, never a default. "(design)" marks an owner that is only designed: a fit with it is still a decision, recorded in both designs. Status and documents per owner are in the map.

**Core types and contracts** (`YFex/src/YFex`)

| You are about to write | Owner to check |
|---|---|
| a value type or contract that several libraries name; money, quantities, units, percentages, validity periods | YFex core |
| consent levels or a consent store | core `Consent` (`IConsentStore`), YFex.System |
| e-mail or phone value types | core `Contacts` |
| user-facing message text, message catalogs | core `Localization` (atlas F12) |
| a canonical byte form for signing or hashing | core `ICanonicalValue<T>`; the writer and hashing in YFex.Cryptography |
| field paths for errors | core `Paths` |

**Observability and machine knowledge**

| You are about to write | Owner to check |
|---|---|
| log lines, operation timing, traces, metrics | `YFex.Diagnostics` probes in core; the engine, `Meter` and `Activity` bridges in YFex.Logging |
| alerts, incidents, on-call routing | YFex.Logging alerts (design) with YFex.Notifications (atlas F07) |
| durable, tamper-evident facts of who changed what | YFex.Audit |
| machine, session, hardware or display telemetry; consent-gated batches | YFex.System |
| user or device profile and behaviour signals | YFex.Profiling |
| security inspection, capture, threat intelligence | YFex.Security |
| classification, masking, scrubbing text, erasure of personal data | YFex.Privacy |
| health, readiness, drain of a service | YFex.Cluster and YFex.Hosting (both design; atlas F22) |
| who acted; actor stamps on records | YFex.Actors |

**Identity, access and trust**

| You are about to write | Owner to check |
|---|---|
| accounts, sign-in, sessions, tokens, one-time codes, passkeys | YFex.Authentication |
| permissions, roles, approval tokens, impersonation | YFex.Authorization |
| allow / deny / needs-approval decisions, tiers, limits, policies an operator changes at run time | YFex.Rules (verdicts, `YFex.Rules.Dynamic`) and a Cqrs `IDecisionBound` command |
| approvals, two-person sign-off | Rules `NeedsApproval`, Cqrs `IDecisionBound`, Authorization approval tokens |
| a feature limited by license, seats, activation | YFex.Licensing |
| identity of an installed copy, enrollment, trusted peers, signed bundles to sites | YFex.Installations |
| tenant or scope identity, per-scope settings and overrides | YFex.Tenancy (atlas F27); tenant registry, home region and residency in YFex.Tenancy.Registry (design) |
| a different implementation for one tenant or scope | YFex.CustomLogic |
| hashing, signatures, keys, certificates, sealed bytes, secrets at rest, key derivation | YFex.Cryptography |
| expiry, or any security decision that depends on time | YFex.Time (contracts in core `Time`) |

**Commands, decisions and data shape**

| You are about to write | Owner to check |
|---|---|
| an admin API, commands and queries, idempotency, caching of query results | YFex.Cqrs |
| field and range checks on specs, settings or input | YFex.Validation (`[Validatable]`) |
| a formula or expression language | YFex.Expressions |
| screens or logic that end users compose | YFex.Composer (design) |
| discoverable actions for search, UI or AI | YFex.Capabilities |
| a search box, search providers, ranking | YFex.Search |

**Workflows, time and failure**

| You are about to write | Owner to check |
|---|---|
| a multi-step run, a step ledger, resume after a crash, waits, timeouts, compensation | YFex.Orchestration (`ProcessManager<TState>`, sagas, durable waits) |
| a periodic pass, a timer loop, a `BackgroundService` with `PeriodicTimer`, a cron time | YFex.Jobs (`[Interval]`, `[Cron]`, `[Retry]`) |
| retries, backoff, per-attempt timeouts, a breaker | YFex.Resilience |
| an event-sourced write model, projections, snapshots | YFex.EventSourcing |
| offline modes, signed offline envelopes, budgets, offline sign-in, a site broker | YFex.Offline |
| leases, one worker per key or tenant, node list | YFex.Cluster (design) |

**Storage, files and communication**

| You are about to write | Owner to check |
|---|---|
| app-state storage, a key-value store, a durable queue, a cache | YFex.Persistence |
| an outbox, inbox, journal, ledger or duplicate filter | the existing ones (atlas F09): YFex.Persistence, YFex.Messaging |
| SQL, row mapping, bulk writes, schema migrations | YFex.Data |
| which database holds a tenant, replicas, failover | YFex.Data.Placement (design) |
| backup, restore, retention of backups | YFex.Backup |
| remote file stores, partner file exchange | YFex.Files (design) |
| XML documents from typed models | YFex.Xml (`YFex/src/XML`) |
| an outbound HTTP API client, per-scope endpoints and credentials, OAuth tokens | YFex.Web |
| events between modules or nodes, RPC, offline command queue | YFex.Messaging |
| a message to a person (e-mail, SMS, WhatsApp, chat, push, in-app) | YFex.Notifications |

**Hosting, operations and devices**

| You are about to write | Owner to check |
|---|---|
| a background service host, module profiles, tray or CLI control | YFex.Hosting (design) |
| creating or scaling servers, databases and regions from the vendor's control plane | YFex.Provisioning (design) |
| installing products, firewall rules, self-update | YFex.Installer (design) |
| network conditions, probes, discovery, DNS, SNMP, Telnet | YFex.Networking |
| serial ports, POS peripherals, scales, GPS, Bluetooth, device drivers | YFex.Devices |
| desktop or AI automation, task graphs, AI agent steps | YFex.Automation |

**UI**

| You are about to write | Owner to check |
|---|---|
| reactive state, undo history | YFex.State |
| a ViewModel base, page lifecycle | YFex.Mvvm |
| navigation between screens | YFex.NavigatR |
| UI controls or UI contracts for several frameworks | YFex.UI (atlas F11) |

## 2. Record each fit as an open decision

- Every integration you found goes into the design's decision table as a ⚑ row, pending, with its options and costs. Never skip one silently. "Optional", "not needed yet", YAGNI or "that project is concluded" are reasons you may give in the row; they are not reasons to leave the row out. YAGNI ([all-projects](all-projects.md)) limits new abstractions. It never justifies skipping an existing library.
- **The library lacks the feature:** propose the change to that library anyway. Name what it must add, whether the change is breaking, and which of its phases it fits. List it in the design's "requests to other libraries" table. Don't build a private substitute while the request waits. If a stop-gap is needed, it is an option inside the same decision.
- **A piece fits better elsewhere:** record a decision that names where it would fit, even when that project is already concluded and the move needs an adjustment or a breaking change.
- **An optional library:** when your design keeps a library optional, the fallback is a decision too. A private fallback engine (a runner "for when Orchestration is absent") duplicates that library.
- Until the user answers, the decision stays pending and you design around the recommended option. Don't build the alternative.

## 3. Existing libraries

- The same applies to code you read or change. When a function belongs in another library, or the same functionality exists in two or more libraries and could become one shared library or move to its owner, report it to the user with file:line evidence. Add or update an atlas finding, and record a ⚑ decision in the owning design when one exists. Don't refactor outside the task.
- When a library or design lands, check the earlier designs and atlas findings that wanted it ("library missing", "when X exists"), and report each new fit as a decision.

## 4. Design documents

A design folder's `README.md` holds:

- a status line naming its decision prefix and range. Search `docs/` for `⚑<prefix>` first, so the prefix is unused;
- an **ecosystem fit** table covering every library in the map: library, outcome (§1), role or reason, decision ([hosting 05](../../internal/hosting/05-ecosystem-integration.md) is the model);
- a **requests to other libraries** table: library, change, breaking or not, decision, phase that needs it;
- the decision table: ⚑ id, topic, proposal, rejected alternatives, where it is explained. Each decision is pending until the user answers.

When a design is proposed, the summary to the user lists the pending integration and placement decisions first.
