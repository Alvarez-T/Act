# 05 — The tool: project, CLI and YFex libraries

User direction 2026-11-12: a real project (not a file-based script), since it uses other YFex libraries; the release folder comes from
an environment variable and may be the Scripts folder; CLI only, no UI.

## 1. Project and release

```
C:\Dev\YFex\YFex.AgentTelemetry\            own Git root, like the other siblings (name ⚑AT6)
  agent-telemetry.slnx
  src/YFex.AgentTelemetry/                  ingest, parsers, classifier, store, reports, expectations (Cqrs messages + handlers)
  src/YFex.AgentTelemetry.Cli/              composition root; the executable agentReads.exe
  tests/YFex.AgentTelemetry.Tests/          parser and classifier tests over trimmed sample transcripts (no real prompts)
  config/categories.json, config/expectations.*   defaults copied next to the exe
```

- Kind: developer tool (⚑AT1). It references YFex libraries; nothing references it.
- **Release path:** environment variable `YFEX_AGENT_TELEMETRY_HOME` (user scope), e.g. `C:\Dev\Scripts\Exe\agent-telemetry`. The
  Cli project's publish target writes there (`dotnet publish -c Release` with `PublishDir=$(YFEX_AGENT_TELEMETRY_HOME)`); when the
  variable is missing the publish fails with a message instead of guessing.
- **Launcher on PATH:** `C:\Dev\Scripts\Exe\agentReads.bat` → `"%YFEX_AGENT_TELEMETRY_HOME%\agentReads.exe" %*`, so you type
  `agentReads …` anywhere.
- **Hooks** call the exe through the variable: `"$YFEX_AGENT_TELEMETRY_HOME/agentReads.exe" hook` (async; which shell Claude Code uses
  for hook commands on Windows is checked in AT-P2, and the `.bat` is the fallback).
- Native AOT if the referenced libraries publish clean; otherwise framework-dependent single file. Start-up time only matters for the
  hook, and the hook is async.
- The data folder is separate from the release folder: `%LOCALAPPDATA%\YFex\agent-telemetry\` (`reads.db`, `logs\`). Overridable
  with `YFEX_AGENT_TELEMETRY_DATA`.

## 2. CLI

| Command | What it does |
|---|---|
| `agentReads hook` | reads the hook JSON on stdin, ingests the new part of `transcript_path` / `agent_transcript_path`; prints nothing, exit 0 always |
| `agentReads ingest --all` · `ingest <transcript>` | backfill or one file; prints counts |
| `agentReads docs refresh` | doc inventory, Markdown headings, links, `git log` versions of every doc ([06](06-sqlite-store.md)) |
| `agentReads report <name> [filters]` | the analyses of [07](07-analysis-catalogue.md) (`coverage`, `docs-vs-code`, `funnel`, `first-rule`, `sections`, `links`, `rereads`, `misses`, `initiators`, `subagents`, `change-impact`, `question`, `sequences`, `purposes`, `models`, `size`) |
| `agentReads check [filters]` | expectation verdicts per session (A5) |
| `agentReads sql "<SELECT …>"` | read-only SQL |
| `agentReads forget --session <id>` · `--before <date>` | delete rows |
| `agentReads rebuild` | drop and re-ingest the transcripts still on disk (warns what will be lost: sessions whose transcripts are gone) |
| `agentReads status` | store size, last ingest, transcripts pending, the last hook errors from the log |

Filters, measures and output formats are listed in [07 §1, §4](07-analysis-catalogue.md).

## 3. YFex libraries and what each does here

| Library | Use | ⚑ |
|---|---|---|
| **YFex.Cqrs** | each CLI command is a Cqrs message: `IngestTranscript`, `RefreshDocs`, `Forget`, `Rebuild` as `ICommand`; each report as `IQuery<TRows>` (e.g. `CoverageReport(ReportFilter)`). The CLI only parses arguments and dispatches; a later UI or capability surface dispatches the same messages. Clean dispatch only: no `IAggregateConfiguration` classes (the registry is built from an empty list, `CompiledMessagingRegistry.Build([])`), handlers registered through the generated handler registry; `LocalDispatcher` comes from YFex.Messaging.Rpc with in-memory cache/outbox defaults | AT24 |
| **YFex.Validation** | `[Validatable]` on `ReportFilter` (period order, known categories/initiators, `top > 0`), on the loaded `categories.json` and expectation files; errors print as one list before anything runs | AT25 |
| **YFex.Rules** (+ `.Dynamic`, `.Data`) | expectation checks (§4): a situation per session, verdicts `Allowed` / `Warned` with a violation per broken expectation; with Rules.Dynamic the expectations are edited without a rebuild and kept in `reads.db` through Rules.Data | AT26 |
| **YFex.Expressions** | the `--where` filter: compiled with `ExprCompiler` against an `ExprSchema` of the `reads` row and evaluated over rows the SQL filters already narrowed. It has no SQL translation (request in the README) | AT27 |
| **YFex.Data** + `YFex.Data.Sqlite` | the store: versioned schema modules (`[DataTable]`, `[DataIndex]`, `SchemaInstaller`), generated row mapping for report rows, `IBulkWriter` for access rows | AT28 |
| **YFex.Logging** | the tool's own log (`AddYFexLogging`, rolling file under the data folder): hooks are silent, so a failed ingest is only visible here and in `agentReads status`. Not the store (⚑AT17 approved) | AT29 |
| **YFex.Privacy** | `TextScrubber.Default` + the profile-path detector (⚑AT15, ⚑AT16 approved) | — |
| YFex.Actors | names only (⚑AT13 approved) | — |

## 4. Expectation checks with YFex.Rules

The rules files say what an agent should read for each kind of task. The checks turn them into verdicts, so a report can say "7 of 9
code sessions loaded `yfex-csharp` before the first `.cs` edit" and show how that changes after a rule edit (A12).

```csharp
// (sketch) one situation per session (or per question), built from the store
public sealed partial record SessionReads(
    SessionKind Kind,                 // code | design | other
    bool EditedCSharp, bool EditedDesignDocs,
    bool ReadStartHere, bool ReadKindRule, bool ReadEcosystemFit, bool ReadInfrastructureMap, bool ReadAtlas,
    bool LoadedCSharpSkill, int FirstRulePosition, int FirstEditPosition) : ISituation<SessionReads>;
```

| Expectation (initial set, from `docs/agent/rules`) | Applies when | Verdict when missed |
|---|---|---|
| `start-here` read before the first edit | any session that edits | Warned |
| kind rule read (`infrastructure` / `domain` / `all-projects`) | any session that edits | Warned |
| `yfex-csharp` skill loaded before the first `.cs` edit | `EditedCSharp` | Warned |
| `ecosystem-fit`, infrastructure map **and** atlas read | `EditedDesignDocs` | Warned |
| `build-and-test` read before a build | session ran `dotnet build/test` | Warned |

Only `Allowed` and `Warned` are used: the checks describe, they never block anything. With Rules.Dynamic the user adds or changes an
expectation as an expression over `SessionReads` (`EditedCSharp and not LoadedCSharpSkill`) and runs `agentReads check` again; stored
verdicts in `session_check` are recomputed.

## 5. Not used

Jobs and Hosting (no periodic pass or service, ⚑AT19 approved), Time (timestamps come from the transcripts), Persistence (the store
is SQL through Data), Messaging, Audit, Cryptography (⚑AT12 approved: no encryption), UI and the atlas view (no UI for now, ⚑AT18).
Capabilities could later expose the report queries to an AI session (⚑AT30).
