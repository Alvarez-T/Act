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
  config/tools.json                         AT-P5: the tool table — surface and kind per tool name (parameters are stored in full, AT39)
```

- Kind: developer tool (⚑AT1). It references YFex libraries; nothing references it.
- **Release path:** environment variable `YFEX_AGENT_TELEMETRY_HOME` (user scope), e.g. `C:\Dev\Scripts\Exe\agent-telemetry`. The
  Cli project's publish target writes there (`dotnet publish -c Release` with `PublishDir=$(YFEX_AGENT_TELEMETRY_HOME)`); when the
  variable is missing the publish fails with a message instead of guessing.
- **Launcher on PATH:** `C:\Dev\Scripts\Exe\agentReads.bat` → `"%YFEX_AGENT_TELEMETRY_HOME%\agentReads.exe" %*`, so you type
  `agentReads …` anywhere.
- **Hooks** call the exe through the variable: `"${YFEX_AGENT_TELEMETRY_HOME:-C:/Dev/Scripts/Exe/agent-telemetry}/agentReads.exe" hook` (async). On Windows Claude Code
  runs hook commands in Git Bash (PowerShell only when Git Bash is missing), so `$VAR` works; the default path covers a desktop app
  started before the variable was set (checked in AT-P2).
- Native AOT if the referenced libraries publish clean; otherwise framework-dependent single file. Start-up time only matters for the
  hook, and the hook is async.
- The data folder is separate from the release folder: `%LOCALAPPDATA%\YFex\agent-telemetry\` (`reads.db`, `logs\`). Overridable
  with `YFEX_AGENT_TELEMETRY_DATA`. The files the user edits live there, not next to the exe, so a republish never overwrites them:
  `workspaces.json` (AT-P4, ✔AT32), `prices.json` (AT-P5, optional, ✔AT45) and any `tools.json` / `categories.json` overrides.

```jsonc
// %LOCALAPPDATA%\YFex\agent-telemetry\workspaces.json   (AT-P4; validated: unique names, existing roots)
[ { "name": "YFex",   "root": "C:/Dev/YFex",   "enabled": true },
  { "name": "Verion", "root": "C:/Dev/Verion", "enabled": true } ]
// a session whose first cwd is under none of these roots is not stored at all (AT33); `status` reports how many were dropped
```

```jsonc
// prices.json (AT-P5, optional; with no file the cost columns stay empty — nothing is guessed)
{ "claude-opus-5": { "input": 15, "cacheWrite": 18.75, "cacheRead": 1.5, "output": 75, "per": 1000000, "currency": "USD" } }
```

## 2. CLI

| Command | What it does |
|---|---|
| `agentReads hook` | reads the hook JSON on stdin, ingests the new part of `transcript_path` / `agent_transcript_path`; prints nothing, exit 0 always |
| `agentReads ingest --all` · `ingest <transcript>` | backfill or one file; prints counts |
| `agentReads docs refresh` | doc inventory, Markdown headings, links, `git log` versions of every doc ([06](06-sqlite-store.md)); from AT-P4 it scans every enabled workspace root, so coverage has a denominator per workspace |
| `agentReads workspace list` · `add <name> <root>` · `remove <name>` · `rename <old> <new>` | AT-P4: edit `workspaces.json` with validation; `list` shows roots, enabled state, sessions and files stored per workspace. `add` says how many stored sessions it would reclaim from `(outside)` and whether a `rebuild` is needed |
| `agentReads report <name> [filters]` | the analyses of [07](07-analysis-catalogue.md) (`coverage`, `docs-vs-code`, `funnel`, `first-rule`, `sections`, `links`, `rereads`, `misses`, `initiators`, `subagents`, `change-impact`, `question`, `sequences`, `purposes`, `models`, `size`; AT-P5 adds `tools`, `mcp`, `tokens`, `cost`, `failures`, `commands`, `estimates`) |
| `agentReads check [filters]` | expectation verdicts per session (A5) |
| `agentReads sql "<SELECT …>"` | read-only SQL |
| `agentReads forget --session <id>` · `--before <date>` | delete a session's rows (id or unique prefix) or those last active before the date; forgotten sessions are not ingested again; the file is compacted |
| `agentReads rebuild [--yes]` | drop and re-ingest the transcripts still on disk; lists the sessions whose transcripts are gone and needs `--yes` then; keeps `reads.db.before-rebuild` |
| `agentReads status` | store size, last ingest, transcripts pending, hook runs and their median / max time, the last warnings and errors from the log; from AT-P4 also the workspaces with their counts and, because an unlisted root is dropped (✔AT33), the dropped sessions with the top-level folders they came from — so a missing session is visible instead of silent |

Filters, measures and output formats are listed in [07 §1, §4](07-analysis-catalogue.md).


When the user asks an agent to add, rename or remove a root, the procedure it follows lives in the tool repository's
`YFex.AgentTelemetry/AGENTS.md` (“When the user asks about workspaces”) and **nowhere else**: ✔AT22 keeps the tool out of the
ecosystem rules and skills, so an agent in another repository only learns of it from the user's own words. The procedure is
list first, add through the CLI (never by editing `workspaces.json`), report the reclaimable count, and ask before a `rebuild`,
since the list of roots is the privacy boundary (✔AT33).

## 3. YFex libraries and what each does here

| Library | Use | ⚑ |
|---|---|---|
| **YFex.Cqrs** | each CLI command is a Cqrs message: `IngestTranscript`, `RefreshDocs`, `Forget`, `Rebuild` as `ICommand`; each report as `IQuery<TRows>` (e.g. `CoverageReport(ReportFilter)`). The CLI only parses arguments and dispatches; a later UI or capability surface dispatches the same messages. Clean dispatch only: YFex.Cqrs's default `CqrsDispatcher` through `AddYFexCqrs(handlers)` (moved out of Messaging.Rpc into Cqrs on 2026-11-12 for this), no `IAggregateConfiguration` classes, handlers listed in a `LocalHandlerInvokerRegistryBuilder`; no Messaging reference | AT24 |
| **YFex.Validation** | `[Validatable]` on `ReportFilter` (period order, known categories/initiators, `top > 0`), on the loaded `categories.json` and expectation files; errors print as one list before anything runs. AT-P4/AT-P5: `workspaces.json` (unique names, root exists, no duplicate root), `tools.json` (known surfaces and kinds), `prices.json` (non-negative, currency present) | AT25, ✔AT32, ✔AT39, ✔AT45 |
| **YFex.Rules** (+ `.Dynamic`, `.Data`) | expectation checks (§4): a situation per session, verdicts `Allowed` / `Warned` with a violation per broken expectation; with Rules.Dynamic the expectations are edited without a rebuild and kept in `reads.db` through Rules.Data | AT26 |
| **YFex.Expressions** | the `--where` filter: compiled with `ExprCompiler` against an `ExprSchema` of the `reads` row and evaluated over rows the SQL filters already narrowed. It has no SQL translation (request in the README) | AT27 |
| **YFex.Data** + `YFex.Data.Sqlite` | the store: versioned schema modules (`[DataTable]`, `[DataIndex]`, `SchemaInstaller`), generated row mapping for report rows, `IBulkWriter` for access rows | AT28 |
| **YFex.Logging** | the tool's own log (`AddYFexLogging`, rolling file under the data folder): hooks are silent, so a failed ingest is only visible here and in `agentReads status`. Not the store (⚑AT17 approved) | AT29 |
| **YFex.Privacy** | `TextScrubber.Default` + the profile-path detector (✔AT15, ✔AT16). AT-P5 adds tool parameters and MCP arguments as a new free-text source, stored in full (✔AT39) and scrubbed the same way; the name-based detector for secret-looking values is then the main remaining protection (✔AT48) | ✔AT48 |
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

## 5. Measuring tool use, MCP calls and tokens (AT-P5 ✅ built 2026-11-13)

The user asked for three things: how often each tool is used and with which commands and parameters, which MCP servers and tools are
called, and the tokens — reported where the transcript gives them, estimated where it does not.

**All three are already in the transcripts**, so the token cost for the agent stays zero (✔AT2, ✔AT42):

| Wanted | Record in the transcript | Stored as |
|---|---|---|
| which tool, how often | every `tool_use` content block of an `assistant` record: `name`, `id`, `input` | `tool_call` + `tool` dimension (✔AT37, ✔AT38) |
| MCP server and tool | the same `name`, shaped `mcp__<server>__<tool>` | `tool.server`, `tool.tool`, `tool.surface = mcp` |
| commands and parameters | the `input` object (`Bash.command`, `Read.file_path`, an MCP argument object) | `tool_call.params_id` holds the whole scrubbed JSON and `tool_param` one row per top-level parameter (✔AT39: in full, MCP included) |
| failures and refusals | the matching `tool_result`: `is_error`, a permission refusal | `ok`, `denied`, `error_id` (✔AT40) |
| how long a call took | result timestamp − call timestamp | `duration_ms` (wall clock, includes the user's own approval time: not a benchmark) |
| real tokens | `message.usage` on each `assistant` record: `input_tokens`, `output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `output_tokens_details.thinking_tokens`, `service_tier`, `server_tool_use` | one `turn` row, `token_source = reported` (✔AT42) |
| model and effort | the `api-request` record: `params.model`, `params.output_config.effort`, `params.max_tokens` | `turn.model`, `turn.effort` |
| tokens where usage is missing | characters of the result | `token_source = estimated`, characters ÷ 4 (✔AT43) |

Observed in a real 1 MB transcript on 2026-11-13 (session `8f8a18d6`): 80 `assistant` records each with `usage`
(`cache_creation_input_tokens` 30 383, `cache_read_input_tokens` 45 840, `output_tokens` 200, `thinking_tokens` 47 in the sample),
`model: claude-opus-5`, `effort: low`, and 57 tool calls — 45 `Bash`, 12 MCP (`mcp__ccd_turn__update_plan`, `mcp__ccd_turn__wrap_up`).
That is the whole input the metering needs.

**What cannot be known** (stated here so no report implies it):

- a tool result's own token cost. The bill is per request, not per result, so attribution is proportional and labelled an estimate
  (✔AT44). The share uses `result_chars` inside the same turn, calibrated by that turn's reported total.
- the cost in money, unless the user writes `prices.json` (✔AT45). Prices are not shipped.
- tokens of a session whose transcript was deleted before the first ingest: nothing to read.
- a tool call whose result never arrived (the session was interrupted): the call is stored with `ok` unknown and no duration.

## 6. Not used

Jobs and Hosting (no periodic pass or service, ⚑AT19 approved), Time (timestamps come from the transcripts), Persistence (the store
is SQL through Data), Messaging, Audit, Cryptography (⚑AT12 approved: no encryption), UI and the atlas view (no UI for now, ⚑AT18).
Capabilities could later expose the report queries to an AI session (⚑AT30).
