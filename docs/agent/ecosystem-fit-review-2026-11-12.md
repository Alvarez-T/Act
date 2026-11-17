# Ecosystem fit review — 2026-11-12

> **Status:** analysis, docs only. Decision prefix **⚑EG1–⚑EG22**, **all pending** until the user answers.
> The rule changes in §5 are applied but not committed.

Question: why do agents that design or change YFex libraries miss the libraries that already exist, and what do the rules need to say so that every integration reaches the user as a decision?

## 1. Are the rules read?

Source: the 92 session transcripts under `~/.claude/projects/C--Dev-YFex/` (74 main sessions, 18 subagents; 2026-09-29 → 2026-11-12). The rule files exist since 2026-11-06 (`docs(agent): ecosystem rule files…`), so the counts below cover the 33 transcripts from that date. A read is a Read, Grep or shell `cat`/`sed`/`grep` on the file. A session is "design" when it edited only files under `docs/internal/`, "code" when it edited `.cs` files. This review session is left out.

| Sessions since 2026-11-06 | Opened ≥ 1 rule file | 00-start-here | all-projects | infrastructure | domain | build-and-test | infrastructure-map | atlas | yfex-csharp skill |
|---|---|---|---|---|---|---|---|---|---|
| main, design (4) | 4 | 2 | 2 | 3 | 0 | 0 | **4** | **0** | 2 |
| main, code (7) | 7 | 5 | 4 | 4 | 1 | 6 | 4 | 2 | 2 (+2 via Skill) |
| subagents, code (9) | 7 | 7 | 5 | 2 | 3 | 5 | 6 | 0 | 4 |
| main, other (4) | 2 | 1 | 1 | 1 | 1 | 1 | 3 | 1 | 1 |

Per design session (session id prefix → design folder edited, rule files opened):

| Session | Designs edited | Rule files opened |
|---|---|---|
| `7312bfad` (2026-11-11/12) | provisioning (53 edits), cluster (4) | 00-start-here, all-projects, infrastructure ×3, infrastructure-map ×9, yfex-csharp |
| `68457730` (2026-11-11) | databases-and-backups (48), tenancy (16), cluster (2) | infrastructure ×1, infrastructure-map ×2; **not** 00-start-here or all-projects |
| `5f7fa8f2` (2026-11-10) | cluster (24) | 00-start-here, all-projects, infrastructure, infrastructure-map ×5 |
| `e60441ff` (2026-11-11) | cluster (13) | yfex-csharp, infrastructure-map ×2; **not** 00-start-here |

Earlier designs (files 2026-10-29, notifications 2026-11-04, licensing, offline, hosting) were written before the rule files existed.

**Reading.** The rules are read: every design session since 2026-11-06 opened the infrastructure map, and code sessions open `00-start-here` and `build-and-test` most of the time. Skipped: `domain.md` in design sessions (expected for infrastructure), `00-start-here`/`all-projects` in two of four design sessions, the **atlas in every design session** (it holds the open duplication findings F09, F10 and F27 that apply to the new designs), and the `yfex-jobs` skill (one session).

**Following.** The provisioning session read every rule file and the map nine times, and looked at the source of Cqrs, Rules, Validation and Orchestration, yet its first draft still had its own run engine, tier table, spec checks and background services. Reading was not the gap: **the rules had no step that asks for an ecosystem-fit check, and nothing that turns a fit into a decision.**

## 2. Per-design findings

"Mentions" = lines naming the library across the folder's files (grep, 2026-11-12). Folders with zero mentions of a library that fits are the signal.

| Design | Reuses well | Own engine or missed fit | Likely cause (from the text) |
|---|---|---|---|
| **provisioning** | after the user's corrections: Orchestration (⚑PV19), Rules + Cqrs `IDecisionBound` (⚑PV14, ⚑PV24), Validation (⚑PV26), Jobs (⚑PV27); Audit, Authorization | first draft: own run engine, hard-coded tier table, hand-written spec checks, a `BackgroundService` per pass (rejected alternatives in README rows PV14, PV19, PV24, PV26, PV27). Still 0 mentions of Messaging, Notifications (approval notices), Offline | no fit step; Rules routed as "pure business-rule evaluation" (map row), so an operational policy did not look like Rules; when a library lacked a feature (Cluster capacity history, Cqrs conditional `RequireApproval`) the draft built around it until the user asked |
| **cluster** | Jobs (36), Resilience (29), Audit (27), Messaging (30), Notifications (19), Data, Installations, Offline | **Rules 0**: the "work policy" (11-work-classes-priority-and-control.md:20, 93–107) is its own versioned policy row with runtime changes and preview, which is what `YFex.Rules.Dynamic` and Rules `SimulationReport` (`YFex.Rules/src/YFex.Rules/Hosting/Simulation.cs:29`) do; Orchestration 3 | policy treated as configuration; no trigger "runtime-changed policy → Rules.Dynamic" |
| **files** (2026-10-29) | Resilience (11), Jobs (7), Audit (8), Capabilities (15), Cryptography (17) | own "ledger lease row" for single writer (05-exchange.md:53) and own ledger (05-exchange.md:58–62); **Orchestration 0**, Validation 1, Notifications 0 | written before Cluster (2026-11-10) and Notifications; no rule to revisit an earlier design when a library lands |
| **notifications** | Jobs (39), Resilience (22), Privacy (18), Authorization (16), Orchestration `WaitFor` (04 §2 scenario 8) | own outbox (`YFex.Notifications/src/YFex.Notifications/Outbox.cs:24,39`) beside Messaging's; Rules 1 for routing, quiet hours, escalation (README:53) | the shared durable primitive (atlas F09) does not exist, so it built its own; atlas not read |
| **licensing** | Installations (75), Authorization (43), Time (17), Cryptography (18), Capabilities (14) | **Jobs 0, Resilience 0, Validation 0, Rules 0, Orchestration 0**: the vendor check-in is "daily at 03:00" (README:54), i.e. a cron job, but runs as a hosted service inside Hosting (⚑LC19, README:109); an activation-delta outbox of its own | check-in routed to Hosting, not Jobs; Licensing is also missing from the map (fixed, §5) |
| **tenancy** | Orchestration "when present" (05-lifecycle-and-provisioning.md:30), Cluster lease, Backup | **own provisioning runner** with step states, resume and reverse rollback (05 §2, ⚑TN10) and an own region-move sequence (05 §3) — the same pattern provisioning had before ⚑PV19; **Rules 0** although "every library checks residency" (06-places-platforms-security-aot.md:21); Validation 0, Cqrs 0 | map rule "optional engines stay optional" read as "build a fallback engine"; no trigger table |
| **databases-and-backups** | Jobs for schedules (⚑DB22 rejects an own scheduler), Audit, Privacy, Cryptography, Data | **Orchestration 0**: tenant move between databases (05-moves-replicas-failover.md §1) and restore are multi-step runs with pauses; Backup "runner" (README:53). Approvals as "Authorization inline approval" (⚑DB25) while provisioning uses Rules `NeedsApproval` + Cqrs `IDecisionBound` | same as tenancy; no single approval pattern in the rules |
| **offline** | Rules (60), Messaging (58), Installations (157), Cryptography, Time, Persistence | built code has eight periodic `BackgroundService`/`PeriodicTimer` loops (§3, ⚑EG17); Orchestration 0 for central reconciliation | site broker may run without Jobs; never recorded as a decision |
| **hosting** | every library in a dedicated table, [05-ecosystem-integration.md](../internal/hosting/05-ecosystem-integration.md) | — | the agent made a fit table on its own initiative; it is now the model in the rules |

### Causes, ranked

1. **No fit step and no trigger table** in the rules. Agents check the libraries they already think of; none of the rules listed "multi-step run → Orchestration", "policy/approval → Rules + `IDecisionBound`", "periodic → Jobs".
2. **Wording that steers away from reuse:** `infrastructure.md` said "Reference other YFex libraries only when needed"; YAGNI in `all-projects.md` was read as "don't integrate yet"; the map said "use this file only when a task crosses package boundaries", and YFex `AGENTS.md` repeated it.
3. **Missing feature → silence.** Nothing told the agent to propose a change to the other library, so it built a private substitute (provisioning's first draft; `YFex.Data` has its own canonical writer because it does not reference Cryptography, §3).
4. **Optional engine → private fallback.** "Optional engines stay optional" produced fallback runners (tenancy ⚑TN10).
5. **Earlier designs are never revisited** when a library lands (files before Cluster and Notifications; licensing before Jobs had its Cluster integration).
6. **Routing gaps:** Licensing missing from the map; the Rules row read as business-only; the atlas (open duplication findings) is never opened in design sessions.
7. **No design template:** no "ecosystem fit" or "requests to other libraries" section, so nothing made the omissions visible.

## 3. Existing libraries: duplication and misplacement

Checked against source on 2026-11-12. Already tracked by the atlas and not repeated here: F06, F07, F09 (outbox/journal/ledger primitive), F10 (three schedulers), F12, F19, F20, F22, F27. Below are new findings, or residue of findings marked done.

| # | Finding | Evidence |
|---|---|---|
| X1 | Jobs has its own approval and change-policy vocabulary parallel to Rules verdicts | `YFex.Jobs/src/YFex.Jobs.Abstractions/Control/JobChangePolicy.cs:9` (`IJobChangePolicy`), `:70` (`JobChangeApproval`), `:102` (`JobApprovalRequest(string permission, in Violation reason, string? rule)`), `:118` (`JobChangeVerdict` Allowed/NeedsApproval…) vs `YFex.Rules/src/YFex.Rules/Decisions/Verdict.cs:7` (`ApprovalRequest(Permission, in Violation, RuleRef)`), `:44`, `:49`; `YFex.Jobs/src/YFex.Jobs/Configuration/JobSettingsProposal.cs:177` |
| X2 | Offline uses HMAC, SHA-256 and PBKDF2 directly (rule: primitives only inside Cryptography) | `YFex.Offline/src/YFex.Offline/SignIn/OfflineVerifierWrap.cs:147,168,200` |
| X3 | Hashing over canonical bytes is hand-written in several libraries, and Data has a second canonical writer, while core says "Hash256 here, hashing in Cryptography" | `YFex.Data/src/YFex.Data/Schema/SchemaCanonical.cs:16,65`; `YFex.Authorization/src/YFex.Authorization.Public/Approvals.cs:133`; `YFex.Rules/src/YFex.Rules/Engine/Evaluator.cs:767`; `YFex.Audit/src/YFex.Audit.Public/AuditValueEncoding.cs:261`; `YFex.Web/src/Web/YFex.Web.Refit/Tokens/OAuthTokenSource.cs:80`; vs `YFex.Cryptography/src/YFex.Cryptography/Canonical/CanonicalWriter.cs:28`, `YFex/src/YFex/Cryptography/Hash256.cs:18` |
| X4 | F04 (retry, "done") residue: hand-written backoff | `YFex.Messaging/src/YFex.Messaging.Rpc/OutboxReplayer.cs:281-282` (the project already references Resilience); `YFex.Automation/src/YFex.Automation.Core/Engine/RetryPolicy.cs:4,23,57-61`, `YFex.Automation/src/YFex.Automation.Core/Steps/CallAiStep.cs:106`; `YFex.Installations/src/YFex.Installations/Enrollment/EnrollmentClient.cs:170-185` |
| X5 | Periodic loops outside Jobs | `YFex.Offline/src/YFex.Offline.Data/OfflineDataPurge.cs:22`, `YFex.Offline/src/YFex.Offline.Broker/SiteFeed.cs:179`, `YFex.Offline/src/YFex.Offline/Receipts/DeliveryReceiptSender.cs:130`, `YFex.Offline/src/YFex.Offline/Broker/OfflineBrokerLink.cs:360`, `YFex.Offline/src/YFex.Offline/DependencyInjection/OfflineServiceCollectionExtensions.cs:304`, `YFex.Offline/src/YFex.Offline.Rpc/OfflineTerminal.cs:184`; `YFex.Installations/src/YFex.Installations/Diagnostics/InstallationDiagnosticsBuilderExtensions.cs:42`, `YFex.Installations/src/YFex.Installations.Central/DependencyInjection/CentralInstallationsServiceCollectionExtensions.cs:259`, `EnrollmentClient.cs:170` (renewal loop) |
| X6 | Machine clock read in libraries (rule: inject `TimeProvider`) | `YFex.Orchestration/src/YFex.Orchestration/Scheduler/KeyValueDelayScheduler.cs:67`, `.../Scheduler/SchedulerHostedService.cs:43`; `YFex.Persistence/src/YFex.Persistence/InMemoryCache.cs:21,36`; `YFex.Messaging/src/YFex.Messaging.Rpc/CallContext/CallIdentitySources.cs:45,82`; `YFex.EventSourcing/src/YFex.EventSourcing/Store/InHouseAggregateSession.cs:70`, `.../Sync/EventIngest.cs:49`; `YFex.Jobs/src/YFex.Jobs.Hub.Worker/HubConnection/BufferedReports.cs:42,47` |
| X7 | Automation keeps its own secret-store contract and platform stores next to Cryptography's (F08 follow-up only names DPAPI) | `YFex.Automation/src/YFex.Automation.Abstractions/Secrets/ISecretStore.cs:7`, `YFex.Automation/src/YFex.Automation.Platform.Windows/Secrets/DpapiSecretStore.cs:35,43`, `YFex.Automation/src/YFex.Automation.Platform.Linux/LinuxSecretStore.cs:14` vs `YFex.Cryptography/src/YFex.Cryptography/Secrets/SecretStores.cs:18` |
| X8 | Two public records named `OutboxEntry` | `YFex.Messaging/src/YFex.Messaging.Rpc/IOutbox.cs:23`, `YFex.Notifications/src/YFex.Notifications/Outbox.cs:24` (part of F09; timing matters before Notifications N1) |
| X9 | Runtime-changeable, versioned policy is built or designed four times | Rules.Dynamic (`YFex.Rules/src/YFex.Rules.Dynamic`, simulation `Hosting/Simulation.cs:29`); Jobs settings proposals (`JobSettingsProposal.cs`, `JobChangePolicy.cs`); Cluster work policy (cluster 11:93–107, design); Provisioning limits and freeze (⚑PV24, design) |

## 4. Decisions (all pending)

The design fixes are also atlas findings for later resolution: ⚑EG2 → F39, ⚑EG3 → F30, ⚑EG4 → F31, ⚑EG5 → F32, ⚑EG6 → F33, ⚑EG7 and ⚑EG8 → F34, ⚑EG9 → F35, ⚑EG10 → F36, ⚑EG11 → F37, ⚑EG12 → F38. The code findings are atlas findings too: ⚑EG13 → F40, ⚑EG14 → F41, ⚑EG15 → F42, ⚑EG16 → F43, ⚑EG17 → F44, ⚑EG18 → F45, ⚑EG19 → F46, ⚑EG20 → F47 (`docs/atlas/atlas.json`; run `yfexAtlas` to show them in the viewer).

| ⚑ | Topic | Proposal | Alternatives | Ref |
|---|---|---|---|---|
| **EG1** | Rule change | Keep `rules/ecosystem-fit.md` and the edits of §5 as applied | edit wording; revert | §5 |
| **EG2** | Retrofit pending designs | Add an "ecosystem fit" and a "requests to other libraries" table to the README of cluster, files, tenancy, databases-and-backups, licensing and notifications (provisioning has a requests table in 09 §3), each fit as a ⚑ row of that design | only for new designs; one table here for all | §2 |
| **EG3** | Tenancy runner and region move | A tenancy provisioning run and a region move are always YFex.Orchestration process managers (as ⚑PV19); no fallback runner | keep the own runner when Orchestration is absent (⚑TN10 today) | tenancy 05 §2–3 |
| **EG4** | Tenant move between databases, restore | Data.Placement move and Backup restore run as Orchestration process managers (durable pauses, resume, step ledger) | own runner in Backup and Placement | databases-and-backups 05 §1, README:53 |
| **EG5** | One approval pattern for admin commands | Admin commands that need approval (restore, failover, residency change, vendor key rotation, provisioning) use a Rules verdict `NeedsApproval` + a Cqrs `IDecisionBound` command + Authorization approval tokens, as ⚑PV14; put the pattern in `ecosystem-fit.md` | each design keeps "Authorization inline approval" (⚑DB25, ⚑TN16, licensing 05 §5) | §2 |
| **EG6** | Licensing check-in | The check-in is a YFex.Jobs `[Cron]` job with Resilience retries; the activation-delta outbox waits for the F09 primitive. ⚑LC19 changes from "inside Hosting" to "a Jobs job, hosted wherever Jobs runs". Licensing was concluded through LC-P2, so this adjusts an approved decision | keep the hosted service (⚑LC19) | licensing README:54, :109 |
| **EG7** | Cluster work policy on Rules | Express the work policy (levels → actions, class → roles) as a Rules situation and rule set, changed at run time through `YFex.Rules.Dynamic`, with Rules simulation as the preview | own versioned policy row and preview (cluster 11) | cluster 11:20, 93–107 |
| **EG8** | One runtime-policy mechanism | Decide whether Rules.Dynamic is the one versioned, previewable, approvable runtime-policy store (Jobs settings, Cluster work policy, Provisioning limits move onto it), or a new shared library | keep four mechanisms | X9 |
| **EG9** | Files exchange single writer and ledger | Files single-writer moves to Cluster leases (⚑CL8) where Cluster exists; the exchange ledger waits for the F09 primitive; multi-step exchanges are considered for Orchestration | keep the ledger lease row (files 05:53) | files 05 §4–5 |
| **EG10** | Notifications routing policy | Recipient routing, quiet hours and escalation as a Rules situation (operator-changeable through Rules.Dynamic) | code in the dispatcher | notifications README:53 |
| **EG11** | Residency checks | Residency is evaluated in one place (a `ResidencyRule` evaluation in Tenancy, optionally a Rules situation) that Data, Files and Backup call, instead of "every library checks residency" | per-library checks | tenancy 06:21 |
| **EG12** | Spec validation in designs | Specs and policies in tenancy, backup and licensing (`BackupPolicy`, `ResidencyRule`, license specs) are `[Validatable]` with YFex.Validation, as ⚑PV26 | hand-written checks | §2 |
| **EG13** | Jobs approval vocabulary (breaking for Jobs) | Jobs uses the Rules decision types (`Verdict`, `ApprovalRequest`) or both move to a shared contract below them; `JobApprovalRequest`/`JobChangeVerdict` go away | keep the parallel types | X1 |
| **EG14** | Offline crypto primitives | Move the verifier-wrap key derivation and MAC of `OfflineVerifierWrap` into a YFex.Cryptography API | keep (rule exception, documented) | X2 |
| **EG15** | Hashing and canonical bytes | Choose one: (a) YFex.Cryptography offers `Hash256` hashing over its `CanonicalWriter` and every library uses it (Data then references Cryptography, a new dependency); (b) the canonical writer and a SHA-256 helper move to core as a stated exception; (c) the rule allows `SHA256.HashData` everywhere and reserves keyed primitives to Cryptography. Data's `SchemaCanonicalWriter` then goes away or stays by decision | — | X3 |
| **EG16** | Retry residue (F04) | Messaging `OutboxReplayer`, Automation `RetryPolicy`/`CallAiStep` and Installations `EnrollmentClient` use Resilience `RetryDelay`; reopen F04 in the atlas | leave as is | X4 |
| **EG17** | Periodic loops outside Jobs | Offline and Installations periodic passes become Jobs jobs where Jobs runs; where a site broker runs without Jobs, keep the loop and record why | keep all loops | X5, atlas F10/F22 |
| **EG18** | Machine clock in libraries | Inject `TimeProvider` in Orchestration's scheduler (it backs the durable timeouts provisioning ⚑PV19 relies on), Persistence, Messaging.Rpc, EventSourcing and the Jobs hub worker | leave; fix only when touched | X6 |
| **EG19** | Automation secret stores (breaking for Automation) | Automation uses Cryptography's `ISecretStore`; its DPAPI and Linux stores move to Cryptography platform packages | keep Automation's own contract | X7, atlas F08 |
| **EG20** | `OutboxEntry` name clash | Rename Notifications' record (or merge through F09) before Notifications N1 ships more callers | keep both names | X8 |
| **EG21** | Design skill | Add a `yfex-design` skill holding the design README template (fit table, requests table, decision table, prefix check) | the rule file is enough | §6 |
| **EG22** | Missing agent files | Add `AGENTS.md` to YFex.Automation and YFex.Brain (both lack one; Automation is in the map) | leave | §6 |

## 5. Rule changes applied

| File | Change |
|---|---|
| [`rules/ecosystem-fit.md`](rules/ecosystem-fit.md) (new) | Fit check of every library before designing (map, atlas findings, open designs), four outcomes per library, owner tables "you are about to write X → check owner Y" covering every infrastructure and cross-cutting library and design (core types, observability and machine knowledge — Logging, System, Profiling, Security, Privacy, Audit — identity and trust, commands and data shape, workflows, storage and communication, hosting and devices, UI; designs marked "(design)"), every fit as a pending ⚑ decision (never skipped for "optional", YAGNI or "concluded"), propose the change when the other library lacks the feature, record better placements even when breaking, optional library ≠ private fallback engine, the same for existing code (duplication → atlas finding + decision), design README shape (fit table, requests table, decision table, unused prefix) |
| [`rules/00-start-here.md`](rules/00-start-here.md) | §1 routes every design or capability change, in any project kind, to `ecosystem-fit.md` |
| [`rules/infrastructure.md`](rules/infrastructure.md) | "Reference other YFex libraries only when needed" replaced by "reuse YFex libraries… each integration is a pending decision; keep the reference optional when only some users need it" |
| [`rules/domain.md`](rules/domain.md) | Framework reuse points to the owner table; logic without business meaning is a placement decision naming the library |
| [`infrastructure-map.md`](infrastructure-map.md) | Read in full before any design; Rules row now names operational policies, verdicts and `Rules.Dynamic`; new rows for YFex.Licensing, YFex.Composer (proposal) and YFex.Xml |
| [`docs/atlas/atlas.json`](../atlas/atlas.json) | Findings F30–F39 for the design fixes and F40–F47 for the code findings (§4) |
| [`YFex/AGENTS.md`](../../AGENTS.md) | The map is read in full before any design |
| `C:\Dev\YFex\AGENTS.md` (always loaded) | New bullet routing design work to `ecosystem-fit.md` |

## 6. Further rule changes proposed, not applied

- **One owner table.** `YFex/AGENTS.md` "Where work belongs" repeats the map's owner table in short form; they already disagree (no Actors, Notifications, Licensing, Tenancy in the short one). Replace it with a pointer to the map.
- **Approval pattern** (if ⚑EG5 is approved): one line in `ecosystem-fit.md` §1 naming the admin-approval pattern.
- **Atlas update on landing.** `ecosystem-fit.md` §3 asks agents to add atlas findings; the atlas README could list the finding fields and severity scale so agents write them consistently.
- **Design skill** (⚑EG21) and **missing AGENTS.md** (⚑EG22).
- **"Optional engines stay optional"** in the map's boundary list: add "the fallback is a decision, not a private engine" there too, or drop the duplicate wording from `ecosystem-fit.md`; it is in `ecosystem-fit.md` only, to keep one place.
- **Measure again** after two weeks with the same transcript counts, plus whether each new design README has a fit table.
