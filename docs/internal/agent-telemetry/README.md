# Agent read telemetry — Design

> **Status:** APPROVED 2026-11-12, docs only, no code. Decision prefix **⚑AT1–⚑AT31**, all answered by the user on 2026-11-12:
> ⚑AT1 (developer tool), ⚑AT6 changed (a project, release path from an environment variable), ⚑AT18 changed (no UI for now), ⚑AT24
> changed (plain Cqrs dispatch, no aggregate configuration), and every other decision approved as proposed. Next: AT-P1.
> Project kind: **developer tool**: [all-projects](../../agent/rules/all-projects.md) rules apply; infrastructure rules apply only to
> the YFex library code it calls.
> **Audience:** the user, then the session that builds it. Read this file, then [05](05-tool-cli-and-libraries.md) for the tool and
> [07](07-analysis-catalogue.md) for what it answers.

## The question

Which skills, rules and docs do AI coding agents read in `C:\Dev\YFex`, when, for which question and for what purpose, and how do
docs reads compare with code reads? The user wants data to analyse later. The measurement must not influence the agent: no tokens in its
context, no change to its answers.

## Answer in one picture

```
 Claude Code session (main + subagents)          nothing printed, nothing added to context
   │ writes its transcript as it works  ──►  ~/.claude/projects/C--Dev-YFex/<session>.jsonl (+ subagents/*.jsonl, custom-title)
   │ end of each turn: async Stop / SubagentStop / SessionEnd hook ─► $YFEX_AGENT_TELEMETRY_HOME/agentReads.exe hook  (exit 0, no output)
   ▼
 agentReads.exe  (project YFex.AgentTelemetry, this PC only; CLI commands are Cqrs messages)
   parse records → accesses (Read/Grep/Glob, shell cat/sed/grep, auto-loaded CLAUDE.md/AGENTS.md, skills, attached files)
   who: you / you named it / agent chose it / Claude Code loaded it / another session          (02 §6)
   classify path → repo + category (skill, rule, routing, design, public doc, other .md, code, build, config)
   attach question, plan step, call description, nearby text; scrub (YFex.Privacy TextScrubber)
   ─► %LOCALAPPDATA%\YFex\agent-telemetry\reads.db  (SQLite through YFex.Data: star schema, roll-ups, FTS)   (06)
   ▼
 agentReads report coverage | funnel | docs-vs-code | sections | links | … [filters]   ·   agentReads check (YFex.Rules)   (07)
```

- **Token cost for the agent: 0.** Transcripts are written anyway; the hooks print nothing (plain stdout of `UserPromptSubmit` and
  `SessionStart` would enter the context, so they are not used). Asking the agent for its purpose would cost 0.2–5 k tokens per session
  and change what is measured ([01 §2 C](01-sources-and-token-cost.md)).
- **Backfill on day one:** the 94 transcripts on disk (2026-11-12) are ingested first.
- **Purpose** comes from evidence that already exists: the question, the plan step, the Bash/Agent `description` (75 % of shell calls
  carry one) and the text before the call. What cannot be known is listed in [02 §5](02-events-classification-and-purpose.md).
- **You vs the agent:** each access gets an initiator, and your own prompts, approvals, answers and outside edits go to a separate
  `user_action` table ([02 §6](02-events-classification-and-purpose.md)).
- **Shell reads matter most:** about 95 % of reads go through Bash `cat`/`sed`/`grep`, not the `Read` tool, so command lines are parsed.

## How it runs: hooks, not a skill or a tool

Claude never calls anything. A skill or an MCP tool would put its description in every session's context (tokens) and record only what
Claude decides to call, which is the behaviour under study. Instead the Claude Code **harness** runs a command at the end of each turn;
Claude does not see it run.

```jsonc
// ~/.claude/settings.json (sketch) — the harness pipes the hook input JSON to stdin
"hooks": {
  "Stop":         [{ "hooks": [{ "type": "command", "command": "\"$YFEX_AGENT_TELEMETRY_HOME/agentReads.exe\" hook", "async": true }] }],
  "SubagentStop": [{ "hooks": [{ "type": "command", "command": "\"$YFEX_AGENT_TELEMETRY_HOME/agentReads.exe\" hook", "async": true }] }],
  "SessionEnd":   [{ "hooks": [{ "type": "command", "command": "\"$YFEX_AGENT_TELEMETRY_HOME/agentReads.exe\" hook", "async": true }] }]
}
```

`agentReads hook` reads `transcript_path` (or `agent_transcript_path`) from stdin, ingests the bytes added since its last run, writes
nothing to stdout and exits 0. The user runs `agentReads ingest --all` once for the backlog and `agentReads report …` to analyse.
`YFEX_AGENT_TELEMETRY_HOME` is the release folder (e.g. `C:\Dev\Scripts\Exe\agent-telemetry`); `C:\Dev\Scripts\Exe\agentReads.bat`
puts the command on PATH ([05 §1](05-tool-cli-and-libraries.md)).

## Files

| File | Content |
|---|---|
| [01-sources-and-token-cost.md](01-sources-and-token-cost.md) | how agents read today (numbers), options A hooks / B transcripts / C self-report, token cost and comparison |
| [02-events-classification-and-purpose.md](02-events-classification-and-purpose.md) | logical model, repository and category rules, shell parsing, purpose levels, you vs the agent, what cannot be known |
| [03-privacy-storage-and-retention.md](03-privacy-storage-and-retention.md) | field classification, scrubbing, retention, nothing leaves the PC |
| [04-analysis-queries.md](04-analysis-queries.md) | SQL for the main reports |
| [05-tool-cli-and-libraries.md](05-tool-cli-and-libraries.md) | project, release path, CLI, how Cqrs, Validation, Rules, Expressions, Data, Logging and Privacy are used; expectation checks |
| [06-sqlite-store.md](06-sqlite-store.md) | physical schema for analysis: fact + dimensions, indexes, roll-ups, FTS, concurrent hooks, schema versions |
| [07-analysis-catalogue.md](07-analysis-catalogue.md) | filters, measures and the 17 analyses |

## Phases

| Phase | Content | Done when |
|---|---|---|
| AT-P1 | repo `YFex.AgentTelemetry`; store (Data schema module), transcript and shell parsers, classifier, initiators, scrubbing, `ingest --all`, `docs refresh`, reports A1–A4, A8–A11, A13, `sql`, `status`; publish to `YFEX_AGENT_TELEMETRY_HOME` | the 94 transcripts load; the 2026-11-12 review's rule-read counts ([§1](../../agent/ecosystem-fit-review-2026-11-12.md)) are reproduced by a report |
| AT-P2 | async `Stop` / `SubagentStop` / `SessionEnd` hooks; named-mutex writers; tool log + `status` errors | a new session appears in the store a turn later; the session's context shows no hook text; two sessions ending together both ingest |
| AT-P3 | expectation checks (Rules, Rules.Dynamic) and the remaining reports A5–A7, A12, A14–A17; `--where` (Expressions) | `check` reproduces by hand-checked verdicts on five sessions |
| later | atlas view or another UI (⚑AT18); per-tool hooks if the transcript format breaks; capabilities (⚑AT30) | — |

Tests and AOT checks come at the end of each phase (all-projects: tests last unless asked).

## Ecosystem fit

Every library in the [infrastructure map](../../agent/infrastructure-map.md), outcome per [ecosystem-fit §1](../../agent/rules/ecosystem-fit.md).

| Library | Outcome | Role or reason | ⚑ |
|---|---|---|---|
| YFex core (`YFex.Privacy` types) | used (names) | `DataSensitivity` levels label each stored field ([03 §1](03-privacy-storage-and-retention.md)) | AT15 ✔ |
| YFex.Privacy | used; lacks a feature | `TextScrubber.Default` scrubs prompts, titles and command lines; no built-in detector for a user-profile path | AT15 ✔, AT16 ✔ |
| **YFex.Cqrs** | used | CLI commands and reports as `ICommand` / `IQuery<T>` messages; a later UI or capability surface reuses them | AT24 ✔ |
| **YFex.Validation** | used | `[Validatable]` filters and config files (categories, expectations) | AT25 ✔ |
| **YFex.Rules** (+ `.Dynamic`, `.Data`) | used | expectation checks per session as verdicts; expectations editable without rebuild, stored in `reads.db` | AT26 ✔ |
| **YFex.Expressions** | used; lacks a feature | `--where` filters compiled against the row schema; no translation to SQL, so evaluation is in memory after the SQL prefilter | AT27 ✔, AT31 ✔ |
| **YFex.Data** + `YFex.Data.Sqlite` | used; may lack a feature | versioned schema modules, row mapping, `IBulkWriter`; FTS5 virtual tables and views may need plain SQL steps | AT28 ✔ |
| YFex.Logging | used (tool's own log); not the store | hooks are silent, so failures go to a rolling log shown by `status` | AT29 ✔, AT17 ✔ |
| YFex.Actors | used (names only) | `ActorKind.Agent` / `User` vocabulary | AT13 ✔ |
| Atlas (`docs/atlas`, `yfexAtlas`) | better placement (later) | a "never read" view could live there; no UI for now | AT18 ✔ (deferred) |
| YFex.LoggY | not a fit | tail viewer for operation logs | AT17 ✔ |
| YFex.Capabilities | fits later | report queries as capabilities so an AI session can ask the store | AT30 ✔ |
| YFex.Persistence | not a fit | app-state key-value store; the store is SQL through Data | — |
| YFex.Backup | not a fit | the data folder is covered by the profile backup | — |
| YFex.Jobs | not a fit | no periodic pass: hooks trigger ingest; manual `--all` sweep | AT19 ✔ |
| YFex.Hosting (design) | not a fit | no background service | AT19 ✔ |
| YFex.Resilience | not a fit | local I/O; SQLite busy timeout + named mutex | — |
| YFex.Orchestration | not a fit | single-step ingest | — |
| YFex.System / YFex.Profiling | not a fit | consent-gated telemetry shipped to a server; here nothing leaves the PC | AT20 ✔ |
| YFex.Time | not a fit | timestamps come from the transcripts | — |
| YFex.Cryptography | not a fit | no encryption at rest | AT12 ✔ |
| YFex.Audit | not a fit | no tamper-evidence needed | — |
| YFex.Messaging / NavigatR / State / Mvvm / UI | not a fit | single process CLI, no UI | — |
| YFex.Authentication / Authorization | not a fit | one local user; profile ACL | — |
| YFex.Search | not a fit | prompt search is SQLite FTS5 | — |
| YFex.Tenancy / CustomLogic | not a fit | no tenants or scopes | — |
| YFex.Files / Web / Notifications | not a fit | no remote stores, HTTP clients or messages to people | — |
| YFex.Networking / Installations / Offline / Devices | not a fit | no network, identity or device concerns | — |
| YFex.Cluster / Provisioning (designs) | not a fit | single PC | — |
| YFex.EventSourcing | not a fit | rows are derived facts | — |
| YFex.Licensing / Installer | not a fit | not a product | — |
| YFex.Automation | not a fit (now) | its AI agents could emit the same events later | — |
| YFex.Security | not a fit | not a security monitor | — |
| Outside: `Microsoft.Data.Sqlite` | used (through YFex.Data.Sqlite) | already used by 16 YFex projects | AT5 ✔ |

## Requests to other libraries

| Library | Change | Breaking | ⚑ | Needed by |
|---|---|---|---|---|
| YFex.Privacy | built-in `TextScrubber` detector for local user-profile paths (`C:\Users\<name>`, `/home/<name>`), opt-in; also useful for Logging and support bundles | no | AT16 ✔ | AT-P1 (stop-gap: a custom `ITextDetector` in the tool) |
| YFex.Expressions (or YFex.Data) | translate a compiled expression over a flat row schema into a SQL `WHERE` with parameters, for a dialect | no (additive) | AT31 ✔ | AT-P3 (stop-gap: in-memory evaluation after the SQL prefilter; fine below ≈ 1 M rows) |
| YFex.Data | schema modules that can declare SQLite FTS5 virtual tables and views, or a documented plain-SQL step inside a module | no | AT28 ✔ | AT-P1 (stop-gap: plain SQL step) |
| Atlas (`yfexAtlas.cs`) | read `reads.db` when present and add an "Agent reads" view | no | AT18 ✔ (deferred) | later |

## Decisions

✔ = answered by the user on 2026-11-12.

| ⚑ | Topic | Decision / proposal | Rejected alternatives | Where |
|---|---|---|---|---|
| **AT1** ✔ | Project kind | developer tool | infrastructure library; part of YFex.Logging | this file |
| **AT2** ✔ | Data source | transcripts (option B) ingested by async `Stop`/`SubagentStop`/`SessionEnd` hooks that print nothing; `ingest --all` for backfill | per-tool hooks as the main source; self-report | [01](01-sources-and-token-cost.md) §2–3 |
| **AT3** ✔ | Transcript retention | keep Claude Code's default `cleanupPeriodDays` (30); the store keeps the scrubbed rows | raise it | 01 §2 B, [03](03-privacy-storage-and-retention.md) §3 |
| **AT4** ✔ | Session title | latest `custom-title` + every change; first prompt as fallback; check hook `session_title` in AT-P2 | hook `session_title` only; first prompt only | 01 §2 |
| **AT5** ✔ | Store | SQLite file under `%LOCALAPPDATA%\YFex\agent-telemetry\`, shaped for analysis ([06](06-sqlite-store.md)) | JSONL + DuckDB; Logging files | 06 |
| **AT6** ✔ changed | Tool form and place | **a project** (user): repo `C:\Dev\YFex\YFex.AgentTelemetry` with `YFex.AgentTelemetry` + `YFex.AgentTelemetry.Cli` (`agentReads.exe`); release folder from `YFEX_AGENT_TELEMETRY_HOME` (may be under `C:\Dev\Scripts\Exe`); `.bat` launcher on PATH. The repo and exe names are the proposal inside this decision | file-based script (user rejected); Python | [05 §1](05-tool-cli-and-libraries.md) |
| **AT7** ✔ | Classification rules | first-match path table in a JSON file | rules in code; YFex.Rules | [02 §3](02-events-classification-and-purpose.md) |
| **AT8** ✔ | Query surface | named reports in the CLI + read-only `sql` | a UI; capabilities | [07](07-analysis-catalogue.md) |
| **AT9** ✔ | Purpose labelling by a model | no | cloud or local model | 02 §5 |
| **AT10** ✔ | Keep the scrubbed command line | yes | paths only | 02 §4 |
| **AT11** ✔ | Prompt text | scrubbed, no `<system-reminder>` blocks, ≤ 4 000 characters; after 365 days drop the text, keep counts | hash only; first 200 characters; forever | 03 |
| **AT12** ✔ | Encryption at rest | no | Cryptography secret protection | 03 §2 |
| **AT13** ✔ | Actor vocabulary | YFex.Actors names, no reference | own words; reference | 02 §1 |
| **AT14** ✔ | Hook scope | user-level settings; keep sessions with `cwd` under `C:\Dev\YFex`; Claude Code only | project-level settings; all projects | 01 §2 A |
| **AT15** ✔ | Privacy integration | reference YFex.Privacy, `TextScrubber.Default`; `DataSensitivity` labels | copied regexes; no scrubbing | 03 §1 |
| **AT16** ✔ | Privacy request: profile-path detector | request it; custom `ITextDetector` until then | tool-only detector forever | requests |
| **AT17** ✔ | Logging as store | no; optional `.jsonl` export later | Logging rolling files as store | 03 §2 |
| **AT18** ✔ changed | UI / atlas | **no UI for now** (user); the atlas view stays a later option | atlas view in AT-P3 | requests |
| **AT19** ✔ | Jobs / Hosting | no periodic pass, no service | Task Scheduler; Jobs in Hosting | ecosystem fit |
| **AT20** ✔ | System / Profiling | not used | a shipped telemetry signal | ecosystem fit |
| **AT21** ✔ | Routing | one line in the infrastructure map's documentation routes (added 2026-11-12); an atlas component once the repo exists | no route | — |
| **AT22** ✔ | Telling agents | agents are not told in rules, `AGENTS.md` or skills | a rule line announcing it | 01 §2 C |
| **AT23** ✔ | You vs the agent | `initiator` on every access; your actions in `user_action` | everything counted as agent behaviour | 02 §6 |
| **AT24** ✔ changed | Cqrs | CLI commands and reports as plain Cqrs `ICommand` / `IQuery<T>` messages with handlers, dispatched by the CLI. No aggregate configuration: the registry is built from an empty list (`CompiledMessagingRegistry.Build([])`, as Messaging.Rpc does); the generated handler registry keeps dispatch reflection-free for AOT | plain methods behind the CLI parser | 05 §3 |
| **AT25** ✔ | Validation | `[Validatable]` on `ReportFilter` and on the loaded config files | hand-written checks | 05 §3 |
| **AT26** ✔ | Rules | expectation checks with YFex.Rules (`Allowed` / `Warned` only), Rules.Dynamic for editing them without a rebuild, stored in `reads.db` through Rules.Data | expectations as fixed SQL queries; no checks | 05 §4 |
| **AT27** ✔ | Expressions | `--where` filter compiled with `ExprCompiler` against the row schema, evaluated after the SQL prefilter | only fixed CLI flags; raw SQL `WHERE` text from the user | 05 §3, 07 §1 |
| **AT28** ✔ | Data | the store through YFex.Data + `YFex.Data.Sqlite` (schema modules and migrations, row mapping, `IBulkWriter`); plain SQL steps for FTS5 and views | raw `Microsoft.Data.Sqlite` with a hand-written migration table | 06 §5 |
| **AT29** ✔ | Tool log | YFex.Logging rolling file in the data folder; `status` shows recent errors | no log (hook failures invisible) | 05 §3 |
| **AT30** ✔ | Capabilities | later: report queries as capabilities for an AI session; not in AT-P1–P3 | an MCP server now | 05 §5 |
| **AT31** ✔ | Expressions/Data request: expression → SQL | request the translator; in-memory evaluation until then | private translator in the tool | requests |
