# Agent read telemetry — Design

> **Status:** APPROVED 2026-11-12, docs only, no code. Decision prefix **⚑AT1–⚑AT31**, all answered by the user on 2026-11-12:
> ⚑AT1 (developer tool), ⚑AT6 changed (a project, release path from an environment variable), ⚑AT18 changed (no UI for now), ⚑AT24
> changed (plain Cqrs dispatch, no aggregate configuration), and every other decision approved as proposed. **AT-P1 BUILT 2026-11-12**
> (repo `YFex.AgentTelemetry`, [AT-P1 log](#at-p1-log-2026-11-12)); **AT-P2 BUILT 2026-11-12** (hooks live in the user settings, tool
> log, `forget` / `rebuild`, [AT-P2 log](#at-p2-log-2026-11-12)).
> **2026-11-13, user requests:** more than one workspace, the workspace separated in the store so reports can filter by it, and
> measurement of tool use (which tool, how often, which commands and parameters), MCP calls and tokens (reported by the transcripts,
> estimated when absent). Designed in this revision as phases **AT-P4** (workspaces) and **AT-P5** (tool, MCP and token metering);
> decisions **✔AT32–✔AT49, all answered on 2026-11-13** (list below), so both phases are **APPROVED**. Order: AT-P4 → AT-P5 → AT-P3,
> because both change the schema that AT-P3's checks and reports read. Neither new phase is built; AT-P1 and AT-P2 are unchanged and
> keep working.
> **Answered 2026-11-13:** ✔AT33 (keep dropping sessions outside every configured root; make the drop visible instead),
> ✔AT39 (store every tool call's parameters in full, MCP included, scrubbed), ✔AT44 (proportional, calibrated token attribution),
> ✔AT32 (`workspaces.json`), ✔AT34 (longest root wins), ✔AT35 (workspace on sessions and files), ✔AT36 (full rebuild, no in-place
> migration), ✔AT37 changed (call rows **plus** a simplified per-session view and a daily roll-up, with failure filters), ✔AT38
> (MCP server split), ✔AT40 (`ok` and `denied` apart), ✔AT41 (`access.tool_call_id`), ✔AT42 (real tokens from the `usage` records),
> ✔AT43 (reported and estimated labelled and never summed), ✔AT45 (optional `prices.json`, core money type, no prices shipped),
> ✔AT46 (roll-ups filled in the ingest transaction), ✔AT47 (all seven new reports in AT-P5), ✔AT48 (request the name-based detector
> from Privacy), ✔AT49 (the model stays here; contracts move when Automation needs them).
> **All of ✔AT32–✔AT49 are answered. AT-P4 BUILT 2026-11-13** ([AT-P4 log](#at-p4-log-2026-11-13));
> **AT-P5 BUILT 2026-11-13** ([AT-P5 log](#at-p5-log-2026-11-13)): tool calls, MCP, parameters in full and real token usage.
> Only **AT-P3** is left (expectation checks, reports A5–A7/A12/A14–A17, `--where`).
> Project kind: **developer tool**: [all-projects](../../agent/rules/all-projects.md) rules apply; infrastructure rules apply only to
> the YFex library code it calls.
> **Audience:** the user, then the session that builds it. Read this file, then [05](05-tool-cli-and-libraries.md) for the tool and
> [07](07-analysis-catalogue.md) for what it answers.

## The question

Which skills, rules and docs do AI coding agents read in `C:\Dev\YFex`, when, for which question and for what purpose, and how do
docs reads compare with code reads? The user wants data to analyse later. The measurement must not influence the agent: no tokens in its
context, no change to its answers.

Added 2026-11-13, same constraint: **in which workspace** (more than one root, separated in the store so every report can filter by
it), **which tools and MCP servers** the agents use, with which commands and parameters, and **how many tokens** that costs — reported
by the transcripts where they carry the numbers, estimated from sizes where they do not.

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
// ~/.claude/settings.json (registered 2026-11-12) — the harness pipes the hook input JSON to stdin; on Windows Git Bash runs the command
"hooks": {
  "Stop":         [{ "hooks": [{ "type": "command", "command": "\"${YFEX_AGENT_TELEMETRY_HOME:-C:/Dev/Scripts/Exe/agent-telemetry}/agentReads.exe\" hook", "async": true }] }],
  "SubagentStop": [ … same … ],
  "SessionEnd":   [ … same … ]
}
```

`agentReads hook` reads `transcript_path` (or `agent_transcript_path`) from stdin, re-parses that transcript when it changed (AT-P1 log),
writes nothing to stdout or stderr and exits 0; failures go to the tool log, which `agentReads status` shows. The user runs `agentReads ingest --all` once for the backlog and `agentReads report …` to analyse.
`YFEX_AGENT_TELEMETRY_HOME` is the release folder (e.g. `C:\Dev\Scripts\Exe\agent-telemetry`); `C:\Dev\Scripts\Exe\agentReads.bat`
puts the command on PATH ([05 §1](05-tool-cli-and-libraries.md)).

## Files

| File | Content |
|---|---|
| [01-sources-and-token-cost.md](01-sources-and-token-cost.md) | how agents read today (numbers), options A hooks / B transcripts / C self-report, token cost and comparison; what else the transcripts carry (tool calls, MCP names, `usage`) |
| [02-events-classification-and-purpose.md](02-events-classification-and-purpose.md) | logical model (session, turn, tool call, access), workspace, repository and category rules, shell parsing, purpose levels, you vs the agent, what cannot be known |
| [03-privacy-storage-and-retention.md](03-privacy-storage-and-retention.md) | field classification, scrubbing, retention, nothing leaves the PC |
| [04-analysis-queries.md](04-analysis-queries.md) | SQL for the main reports |
| [05-tool-cli-and-libraries.md](05-tool-cli-and-libraries.md) | project, release path, config files, CLI, how Cqrs, Validation, Rules, Expressions, Data, Logging and Privacy are used; expectation checks; §5 tool, MCP and token metering |
| [06-sqlite-store.md](06-sqlite-store.md) | physical schema for analysis: fact + dimensions, indexes, roll-ups, FTS, concurrent hooks, schema versions |
| [07-analysis-catalogue.md](07-analysis-catalogue.md) | filters, measures and the 24 analyses (A18–A24 are tools, MCP, tokens, cost, failures, commands, estimates) |

## Phases

| Phase | Content | Done when |
|---|---|---|
| AT-P1 ✅ | repo `YFex.AgentTelemetry`; store (Data schema module), transcript and shell parsers, classifier, initiators, scrubbing, `ingest --all`, `docs refresh`, reports A1–A4, A8–A11, A13, `sql`, `status`; publish to `YFEX_AGENT_TELEMETRY_HOME` | the 94 transcripts load; the 2026-11-12 review's rule-read counts ([§1](../../agent/ecosystem-fit-review-2026-11-12.md)) are reproduced by a report |
| AT-P2 ✅ | async `Stop` / `SubagentStop` / `SessionEnd` hooks; lock-file writers; tool log + `status` errors; `forget`, `rebuild` | a new session appears in the store a turn later; the session's context shows no hook text; two sessions ending together both ingest |
| AT-P4 ✅ | **workspaces** (✔AT32–✔AT36): `workspaces.json`, `workspace` dimension on `session` and `file`, longest-root match, sessions outside every root kept as `(outside)`, `--workspace` filter, inventory and coverage per workspace, schema v3 + `rebuild` backfill, `agentReads workspace list/add/remove/rename` | two roots configured; a session in each appears with its own workspace; `report coverage --workspace YFex` matches today's numbers; no session is silently dropped |
| AT-P5 ✅ | **tool, MCP and token metering** (✔AT37–✔AT47), schema v4: **5a** `tool_call` fact + `tool` dimension (surface, MCP server, kind), the `tool_use_by_session` view and `rollup_tool_day` roll-up (✔AT37), parameters in full (✔AT39), failures and denials apart (✔AT40), `access.tool_call_id`; **5b** `turn` table from the transcripts' `usage` records (input, output, cache write / read, thinking, service tier, model, effort), `token_source` reported vs estimated, proportional attribution of a turn's real tokens to its tool calls and reads, optional `prices.json` cost, roll-ups; reports A18–A24 | `report tools --last 30d` lists every tool and MCP server with calls, failures and tokens; `report tokens --by session` totals match the transcripts' own usage for five sessions; `report estimates` shows the estimate error against reported tokens |
| AT-P3 | expectation checks (Rules, Rules.Dynamic) and the remaining reports A5–A7, A12, A14–A17; `--where` (Expressions) | `check` reproduces by hand-checked verdicts on five sessions |
| later | atlas view or another UI (⚑AT18); per-tool hooks if the transcript format breaks; capabilities (⚑AT30) | — |

Tests and AOT checks come at the end of each phase (all-projects: tests last unless asked).

AT-P4 and AT-P5 are each one schema version and one ingest change; AT-P3's expectation situations and `--where` schema then see the new
columns once, instead of being written twice.

### AT-P1 log (2026-11-12)

- Built: repo `C:\Dev\YFex\YFex.AgentTelemetry` (library, CLI `agentReads`, 32 tests on synthetic transcripts); schema module
  `yfex.agent-telemetry` v1; reports `sessions`, A1 `coverage`/`never-read`, A2, A3 `funnel`, A4, A8, A9, A10 `initiators`/`user-actions`,
  A11, A13 `question`; `sql`, `status`; `hook` command (registration in Claude Code settings is AT-P2). Published to
  `YFEX_AGENT_TELEMETRY_HOME = C:\Dev\Scripts\Exegent-telemetry` (user variable set), launcher `C:\Dev\Scripts\ExegentReads.bat`.
- Real run: 274 transcripts (207 main + subagents in every project folder whose session started under the workspace; 33 outside) in about
  11 s → 52.7 k accesses, 1 551 prompts; inventory 875 docs, 7 914 headings, 1 241 links.
- Acceptance: `report funnel --since 2026-11-06` gives the review's main design row exactly for the four design sessions (map 4,
  infrastructure 3, start-here 2, all-projects 2, yfex-csharp 2, domain 0, build-and-test 0). Differences: "any rule" 3 vs 4 (the review
  counted a session that opened only the map and the skill), atlas 1 vs 0 (a design session read the atlas after the review), and more
  code/other sessions because the tool reads every project folder and the review only `C--Dev-YFex`.
- Changes from the design, found while building:
  - a changed transcript is parsed whole and replaces its rows (not a byte-offset tail): no parser state to keep between runs, and a full
    ingest takes seconds; `ingest_state.bytes_read` only says whether a file changed;
  - the writer lock is an exclusive lock file `reads.db.lock`, not a named mutex: async code resumes on other threads, which a mutex
    does not allow (06 §4);
  - schema v1 leaves out `doc_version`, `session_check` and `rollup_day` until the phases that fill them (A12, A5, long ranges);
  - "design" kind = edited something under `docs/internal` and no non-doc file in the workspace (design sessions also update the map
    and `index.md`); edits outside the workspace (memory, notes) do not count;
  - coverage counts files the agent opened; files only matched by a grep over a glob show as `searched_only`;
  - the release is a framework-dependent single file: YFex.Cqrs does not publish AOT-clean (requests below).
- YFex.Cqrs: the handler interfaces and a default in-process dispatcher (`CqrsDispatcher`, `AddYFexCqrs`) moved from Messaging.Rpc into
  Cqrs first (user decision 2026-11-12, commits Cqrs `b04641b`, Messaging `e5c9885`, EventSourcing `4b2ad0d`).

### AT-P2 log (2026-11-12)

- Hooks: async `Stop`, `SubagentStop`, `SessionEnd` command hooks in `~/.claude/settings.json` (user's OK 2026-11-12). On Windows Claude
  Code runs hook commands in Git Bash (PowerShell only without Git Bash), so `$VAR` expands. The command is
  `"${YFEX_AGENT_TELEMETRY_HOME:-C:/Dev/Scripts/Exe/agent-telemetry}/agentReads.exe" hook`: the desktop app keeps the environment it started
  with, so the user variable set during AT-P1 is only seen after a restart; until then the release folder is the fallback. The `.bat`
  launcher is not needed for hooks.
- Tool log (⚑AT29): YFex.Logging JSON Lines under `%LOCALAPPDATA%\YFex\agent-telemetry\logs\<date>.jsonl`, 30 days. Only the CLI
  (composition root) references YFex.Logging; the library reads the files back for `status` (hook runs, median / max time, the last five
  warnings and errors). Each hook run writes one record with `elapsedMs`; a failed ingest or an exception is an error record. The hook sets
  `SelfLog.Output = null`, so the pipeline's own diagnostics cannot reach stderr.
- Several processes append to the same log: YFex.Logging's `RollingFileSink` keeps its file open with `FileShare.Read` for the life of the
  process, so a second hook would drop its records. The CLI wraps it (`SharedFileSink`: open per batch, retry for up to 1 s); request
  below.
- `forget --session <id or unique prefix>` / `--before yyyy-MM-dd` (last activity before local midnight): deletes the session's
  accesses, prompts, titles, user actions and agents, then texts and paths nothing refers to, and compacts the file (FTS optimize,
  `vacuum`, WAL checkpoint) so the text is gone from disk. Schema v2 adds `forgotten_session`: later ingests and `rebuild` skip those
  sessions, so a forgotten session does not come back while its transcript exists.
- `rebuild [--yes]`: lists stored sessions whose main transcript is gone (session id = transcript file name, checked on all 204 stored
  sessions) and changes nothing unless `--yes`; with none lost it runs at once. It keeps a copy `reads.db.before-rebuild`
  (`vacuum into`), deletes the derived rows, re-ingests, refreshes the inventory and compacts.
- Cost (item 4): the first hook took 3.2–5.4 s for one changed 2 MB transcript because `UpdateKindsAsync` recomputed every agent's kind
  with sub-queries on `agent_id`, which no index leads with. It now updates only the sessions written in the run and also matches
  `session_id` (index `ix_access_session_seq`): 0.3–0.6 s inside the process, about 1 s wall clock with start-up; an unchanged transcript
  0.2 s. Whole-transcript re-parse stays.
- Tests: 42 (10 new: forget by id, prefix and date, forgotten sessions stay out, rebuild with and without lost sessions, two writers at
  once, log round trip, log file held by another process).
- Verified by hand on the real store: the hook prints nothing and exits 0 with and without `YFEX_AGENT_TELEMETRY_HOME`; this session
  appeared with its title and accesses; a malformed input became an error row in `status`.

### AT-P4 log (2026-11-13)

- Built: `Workspaces/WorkspaceSet.cs` (the list, its validation and longest-root matching), `Workspaces/WorkspaceAdmin.cs`
  (`add` / `remove` / `rename`), schema **v3** (`workspace`, `skipped_session`, `workspace_id` on `session` and `file`, two indexes,
  the `reads` view rebuilt with `workspace` and `session_workspace`), `--workspace` / `--session-workspace` filters, the `workspace`
  command, and the workspace rows in `status`. 60 tests (18 new), build and tests clean in Release.
- `CanonicalPath` now takes the whole `WorkspaceSet` instead of one root: `RepoOf` resolves against the matched root, `WorkspaceOf`
  is new, and `InWorkspace` means "under any enabled root". The design-session heuristic in `UpdateKindsAsync` and the skill lookup in
  `TranscriptParser` became an OR over the roots.
- Changes from the design, found while building:
  - **the drop needed a row.** ⚑AT33 keeps sessions outside every root out of the store, but `status` must still report them, and a
    status run only reads the database. So `skipped_session` stores the session id, the folder cut to drive + two levels
    (`C:/Dev/Verion`) and the time — nothing else of such a session. `workspace add` reads it to say what a rebuild would reclaim.
  - `workspaces.json` is validated in its loader (unique names, unique roots, absolute roots, at least one enabled) with
    `FormatException`, like `categories.json`. YFex.Validation is still not referenced by the project, as in AT-P1 (⚑AT25 stands).
  - `WorkspaceSet` and `WorkspaceEntry` are public, because `AgentTelemetryOptions` is public and a `required` member cannot be less
    visible than its type.
  - the v3 view SQL is a **method**, not a static property: a static property declared after `Module` is still null while `Module`'s
    initializer runs, and `SqlStep.For` rejected the empty statement (11 tests failed until this was found).
  - read-only commands (`status`, `workspace list`) check `sqlite_master` for the v3 tables, so they keep working on a store that no
    writer has upgraded yet, and `workspace list` says the counts fill in after a rebuild.
- Verified on the real store: schema v3 applied by `rebuild --yes` (⚑AT36), 276 transcripts, 205 sessions and 6 883 files carrying
  workspace `YFex`; `status` now lists **31 dropped sessions** by folder — 21 under `C:/Dev/Verion`, 4 under `~/AppData/Local`, 2 under
  `C:/Dev`, 2 `C:/Dev/Avalonia`, 1 `C:/Dev/AI`, 1 `C:/Dev/Scripts` — each with the `workspace add` line that would measure it. This is
  the question the user asked on 2026-11-13 ("why is my last session not in the report?"), now answered by the tool itself.
  `workspace add Verion C:/Dev/Verion` reported the 21 reclaimable sessions and was removed again, so the configuration still holds
  only `YFex`.
- Cost: that rebuild dropped one stored session whose transcript had disappeared since the morning (`7d79c56f`, "Outlook access
  issue", 2 accesses). The previous store is kept as `reads.db.before-rebuild`. ⚑AT36 accepted exactly this risk, and the command
  reported it before doing it.

### AT-P5 log (2026-11-13)

- Built, both steps in one pass: `Classification/ToolRules.cs` + embedded `tools.json` (surface, MCP server and kind, ✔AT38),
  `ToolCallFact` / `ToolParamFact` / `TurnFact` in the parser, `Store/ReadsStore.Metering.cs` (calls, parameters, turns, roll-up),
  schema **v4** (`tool`, `tool_call`, `tool_param`, `turn`, `rollup_tool_day`, `access.tool_call_id`, `access.est_share_tokens`, the
  `reads` view rebuilt plus the `tool_calls`, `tool_use_by_session` and `turns` views) and **v5** (one index fix, below),
  `Reports/ReportRunner.Metering.cs` with A18–A24, and the filters `--tool`, `--server`, `--surface`, `--failed`, `--denied`,
  `--tokens`, `--by`, `--detail`. 81 tests (21 new), Release build and tests clean.
- The parser now records **every** tool call, not only the ones that touch a file: `ToolUse` writes a call row before the file logic,
  and the MCP / `Agent` / plan branches that used to `return` now register the call so its result still lands on it.
- Token usage comes straight from the records Claude Code already writes (✔AT42): `message.usage` per `assistant` record
  (input, output, cache write, cache read, `thinking_tokens`, `service_tier`, `server_tool_use`) plus `model` and `effort` from the
  `api-request` record beside it. Nothing is asked of the agent, so the cost to it is still zero.
- Attribution (✔AT44) runs inside the parser: a result is billed in the **next** request, so turn *i*'s reported
  `input + cache_creation` tokens are split across the calls of turn *i‑1* by result size, and each call's share is divided again over
  the files it read. Stored as `est_share_tokens`, never mixed with a reported number.
- Changes from the design, found while building:
  - **`rollup_day` never existed** (schema v1 left it out), so ✔AT46 landed as `rollup_tool_day` (day × tool × agent kind) only. The
    token roll-up on `rollup_day` is still unbuilt; `turns` answers today's token questions directly and A20 is fast without it.
  - a response id is **not** unique per store: two transcripts of one session (a subagent, or a fork) repeat it, which broke two
    transcripts on the first real rebuild. Schema v5 replaces the unique index with `(agent_id, message_uuid)`.
  - `sum(x = 0)` is NULL when every `x` is NULL, which made `rollup_tool_day.failed` fail its NOT NULL constraint; the counts are
    `sum(case when … then 1 else 0 end)` now.
  - two more static-initializer traps like AT-P4's: `MeteringNames` and the v4 view had to become methods, because a static member
    declared in another part of the class is still null while `Names` / `Module` initialize.
  - A24's first version joined attribution to reported tokens by day alone, which counted a day's attribution once per model that ran
    that day (haiku showed 220 % of its input tokens). Both sides now group by day **and** model, through `tool_call.turn_id`.
- Verified on the real store (rebuild: 277 transcripts, 0 failed, about 90 s; one changed transcript re-ingests in 0.6 s):
  **30 047 tool calls, 65 792 parameters, 24 535 turns, 67 distinct tools, 1 202 roll-up rows**. `report tools --last 30d` ranks
  Bash (8 620 calls, 194 failed, 6 denied), Write (1 769), Edit (1 139), `mcp__ccd_turn__update_plan` (784), Read (595); `report mcp`
  splits ccd_turn, ccd_session, RoslynMcp (57 `get_csharp_context` calls, 5 failed) and Claude_Browser; `report tokens --by day`
  shows 2026-11-09 at 280 M total tokens with a **98.5 % cache-read share**; `report failures`, `report commands` and `report cost`
  (no `prices.json`, so it says what to write) all answer.
- A24 on real data: characters ÷ 4 — the AT-P1 estimate — accounts for only **7.8–27 %** of the reported input tokens per model, so
  the old number understated what reads cost; the calibrated attribution gives 29.0 M of opus-5-5's 44.0 M reported input tokens.
- Cost of ✔AT39 (parameters in full, MCP included), measured: the store went from **18.6 MB to 96 MB** for the same 206 sessions.
  `forget` and `rebuild` compact it, and `status` prints the size.

## Ecosystem fit

Every library in the [infrastructure map](../../agent/infrastructure-map.md), outcome per [ecosystem-fit §1](../../agent/rules/ecosystem-fit.md).

| Library | Outcome | Role or reason | ⚑ |
|---|---|---|---|
| YFex core (`YFex.Privacy` types) | used (names) | `DataSensitivity` levels label each stored field ([03 §1](03-privacy-storage-and-retention.md)) | AT15 ✔ |
| YFex core (`Money`) | fits (AT-P5) | the optional cost estimate is money: core's money value type and currency, not a bare `decimal`, if the cost columns are kept | AT45 ✔ |
| YFex.Privacy | used; lacks two features | `TextScrubber.Default` scrubs prompts, titles and command lines; no built-in detector for a user-profile path, and none for a secret-looking **parameter value** (tool parameters and MCP arguments are new free text in AT-P5) | AT15 ✔, AT16 ✔, AT48 ✔ |
| **YFex.Cqrs** | used | CLI commands and reports as `ICommand` / `IQuery<T>` messages; a later UI or capability surface reuses them | AT24 ✔ |
| **YFex.Validation** | used | `[Validatable]` filters and config files (categories, expectations; AT-P4/AT-P5 add `workspaces.json`, `tools.json`, `prices.json`: unique workspace names, existing roots, known tool names, non-negative prices) | AT25 ✔, AT32 ✔, AT39 ✔, AT45 ✔ |
| **YFex.Rules** (+ `.Dynamic`, `.Data`) | used | expectation checks per session as verdicts; expectations editable without rebuild, stored in `reads.db` | AT26 ✔ |
| **YFex.Expressions** | used; lacks a feature | `--where` filters compiled against the row schema; no translation to SQL, so evaluation is in memory after the SQL prefilter | AT27 ✔, AT31 ✔ |
| **YFex.Data** + `YFex.Data.Sqlite` | used; may lack a feature | versioned schema modules, row mapping, `IBulkWriter`; FTS5 virtual tables and views may need plain SQL steps. AT-P4 = schema v3 (`workspace`), AT-P5 = schema v4 (`tool`, `tool_call`, `tool_param`, `turn`). No data migration for either: the columns are added empty and `rebuild` re-ingests (✔AT36) | AT28 ✔, AT36 ✔ |
| YFex.Logging | used (tool's own log); not the store; lacks a feature | hooks are silent, so failures go to a rolling log shown by `status`; its rolling file cannot be shared by several processes | AT29 ✔, AT17 ✔ |
| YFex.Actors | used (names only) | `ActorKind.Agent` / `User` vocabulary | AT13 ✔ |
| Atlas (`docs/atlas`, `yfexAtlas`) | better placement (later) | a "never read" view could live there; no UI for now | AT18 ✔ (deferred) |
| YFex.LoggY | not a fit | tail viewer for operation logs | AT17 ✔ |
| YFex.Capabilities | fits later | report queries as capabilities so an AI session can ask the store | AT30 ✔ |
| YFex.Persistence | not a fit | app-state key-value store; the store is SQL through Data | — |
| YFex.Backup | not a fit | the data folder is covered by the profile backup | — |
| YFex.Jobs | not a fit | no periodic pass: hooks trigger ingest; manual `--all` sweep | AT19 ✔ |
| YFex.Hosting (design) | not a fit | no background service | AT19 ✔ |
| YFex.Resilience | not a fit | local I/O; SQLite busy timeout + writer lock file | — |
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
| **YFex.Automation** | better placement (later) | it owns AI agent steps, and its own agents call tools and spend tokens. Once AT-P5 exists, this tool-and-token model is the one an Automation run would report into (or read back for a budget), instead of each growing its own | AT49 ✔ |
| YFex.Search | not a fit | FTS5 over commands and parameters is the same SQLite index as prompts | AT-P5 |
| YFex.Licensing / Jobs (quota) | not a fit | a token budget here is analysis after the fact, not an enforced quota | AT47 ✔ |
| YFex.Security | not a fit | not a security monitor | — |
| Outside: `Microsoft.Data.Sqlite` | used (through YFex.Data.Sqlite) | already used by 16 YFex projects | AT5 ✔ |

## Requests to other libraries

| Library | Change | Breaking | ⚑ | Needed by |
|---|---|---|---|---|
| YFex.Privacy | built-in `TextScrubber` detector for local user-profile paths (`C:\Users\<name>`, `/home/<name>`), opt-in; also useful for Logging and support bundles | no | AT16 ✔ | AT-P1 (stop-gap: a custom `ITextDetector` in the tool) |
| YFex.Privacy | a detector for **secret-looking values by parameter name** (`token`, `password`, `secret`, `apiKey`, `authorization`, `connectionString`), so a key/value payload can be scrubbed by name as well as by value shape; also useful for Logging scopes and support bundles | no (additive) | AT48 ✔ | AT-P5 (stop-gap: the tool's own name list; now the only guard, since ✔AT39 stores parameters in full) |
| YFex.Expressions (or YFex.Data) | translate a compiled expression over a flat row schema into a SQL `WHERE` with parameters, for a dialect | no (additive) | AT31 ✔ | AT-P3 (stop-gap: in-memory evaluation after the SQL prefilter; fine below ≈ 1 M rows) |
| YFex.Data | schema modules that can declare SQLite FTS5 virtual tables and views, or a documented plain-SQL step inside a module | no | AT28 ✔ | AT-P1 (stop-gap: plain SQL step) |
| Atlas (`yfexAtlas.cs`) | read `reads.db` when present and add an "Agent reads" view | no | AT18 ✔ (deferred) | later |
| YFex.Cqrs | publish AOT-clean: `CompiledMessagingRegistry` uses `MakeGenericMethod` (IL3050/IL2060, line 224) and `GetConstructors` on an unannotated type (IL2070, line 280) | no | pending | Native AOT release of `agentReads` (stop-gap: framework-dependent single file) |
| YFex.Cqrs | `[NotNullWhen(true)]` on `CacheableQueryResult<T>.TryGetValue` / `QueueableResult<T>.TryGetValue` | no | pending | nothing blocked (callers add `is null` checks) |
| YFex.Logging | `RollingFileSink` usable by several processes at once (open with `FileShare.ReadWrite` and append per batch, or a `{pid}` path token); today a second process cannot open the file and its records are dropped with only a `SelfLog` line. Also useful for CLIs, hooks and any multi-process host writing one log | no | pending | nothing blocked (stop-gap: `SharedFileSink` in the CLI opens the file per batch and retries) |
| YFex.Data | a public way to keep a `YFexConnection` open across units of work (`KeepOpen` is internal; each `StartScopedTransaction` closes the connection, so per-connection pragmas are lost) | no | pending | nothing blocked (pragmas moved to the connection string; WAL is stored in the file) |

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
| **AT24** ✔ changed | Cqrs | CLI commands and reports as plain Cqrs `ICommand` / `IQuery<T>` messages with handlers, dispatched by the CLI. No aggregate configuration and no Messaging reference: YFex.Cqrs's default `CqrsDispatcher` (`AddYFexCqrs`), moved from Messaging.Rpc into Cqrs on 2026-11-12; handlers listed in a `LocalHandlerInvokerRegistryBuilder`, reflection-free for AOT | plain methods behind the CLI parser | 05 §3 |
| **AT25** ✔ | Validation | `[Validatable]` on `ReportFilter` and on the loaded config files | hand-written checks | 05 §3 |
| **AT26** ✔ | Rules | expectation checks with YFex.Rules (`Allowed` / `Warned` only), Rules.Dynamic for editing them without a rebuild, stored in `reads.db` through Rules.Data | expectations as fixed SQL queries; no checks | 05 §4 |
| **AT27** ✔ | Expressions | `--where` filter compiled with `ExprCompiler` against the row schema, evaluated after the SQL prefilter | only fixed CLI flags; raw SQL `WHERE` text from the user | 05 §3, 07 §1 |
| **AT28** ✔ | Data | the store through YFex.Data + `YFex.Data.Sqlite` (schema modules and migrations, row mapping, `IBulkWriter`); plain SQL steps for FTS5 and views | raw `Microsoft.Data.Sqlite` with a hand-written migration table | 06 §5 |
| **AT29** ✔ | Tool log | YFex.Logging rolling file in the data folder; `status` shows recent errors | no log (hook failures invisible) | 05 §3 |
| **AT30** ✔ | Capabilities | later: report queries as capabilities for an AI session; not in AT-P1–P3 | an MCP server now | 05 §5 |
| **AT31** ✔ | Expressions/Data request: expression → SQL | request the translator; in-memory evaluation until then | private translator in the tool | requests |

### Workspaces and metering (proposed and answered 2026-11-13)

✔ = answered by the user on 2026-11-13; AT37 is the one the user changed (a simplified layer beside the call rows).

| ⚑ | Topic | Decision / proposal | Rejected alternatives | Where |
|---|---|---|---|---|
| **AT32** ✔ 2026-11-13 | How workspaces are configured | `workspaces.json` in the data folder: `[{ "name": "YFex", "root": "C:\\Dev\\YFex", "enabled": true }, ...]`, validated (unique names, root exists). `YFEX_AGENT_TELEMETRY_WORKSPACE` keeps working as a single-root override for tests and one-off runs; with neither, the default stays `C:\Dev\YFex`. `agentReads workspace list / add <name> <root> / remove / rename` edits the file. With ✔AT33 (sessions outside every root are dropped) this file is the only thing deciding what is measured, so `workspace add` reports how many transcripts on disk it would now bring in, and `status` reports what is being dropped | a repeatable `--workspace-root` flag only (nothing stored, every command repeats it); roots inferred from the transcript folders (no names, no control) | [05 §1](05-tool-cli-and-libraries.md), [02 §2](02-events-classification-and-purpose.md) |
| **AT33** ✔ 2026-11-13 | Sessions outside every workspace | **keep dropping them** (user): only a session whose first `cwd` is under a configured root is stored, so nothing from an unlisted project enters the store. There is no `(outside)` workspace. The remedy for a missing session is to configure its root (✔AT32), so the loss must be **visible**: `ingest` already counts the dropped transcripts, and `status` adds how many sessions were dropped and from which top-level folders, with the hint to add it as a workspace | store them as `(outside)` and filter (proposed; rejected: other projects' prompts would enter the store); store them without text (a third storage mode) | 02 §2, [03](03-privacy-storage-and-retention.md) |
| **AT34** ✔ 2026-11-13 | Which root a path belongs to | longest matching root wins, so a nested root (`C:\Dev\YFex\YFex.AgentTelemetry` inside `C:\Dev\YFex`) is its own workspace; `repo` stays the first folder under **its** workspace root; `~claude`, `scripts` and `other` stay as they are today for paths under no root | first match in file order (order becomes meaningful); forbid nesting (the user's own layout nests) | 02 §2 |
| **AT35** ✔ 2026-11-13 | Workspace on sessions **and** files | both: `session.workspace_id` (from the session's first `cwd`) and `file.workspace_id` (from the path). An agent in a YFex session that reads another root is then visible as a cross-workspace read, and `--workspace` means "files in it" while `--session-workspace` means "sessions started in it" | one column on `session` only (a cross-workspace read is attributed to the session's workspace and cannot be found) | 02 §1, [06 §2](06-sqlite-store.md) |
| **AT36** ✔ 2026-11-13 | Migration of the existing store | **force a full rebuild** (user): schema v3 adds the columns empty and `rebuild` fills them by re-ingesting every transcript still on disk, so the ingester is the only code that ever assigns a workspace. The `workspace` column still prints in a report only when the filter spans more than one. Cost: a stored session whose transcript has aged out is lost — checked before deciding, a rebuild on 2026-11-13 lost nothing (277 transcripts, 206 sessions, 52 104 accesses before and after), and `rebuild` keeps `reads.db.before-rebuild` and refuses without `--yes` when sessions would be lost | an in-place migration by path (proposed; rejected: a second code path assigning workspaces); a new database (loses the history) | 06 §5 |
| **AT37** ✔ changed 2026-11-13 | Tool-call granularity | one `tool_call` row per `tool_use` block (tool, `at`, `seq`, `ok`, `denied`, `duration_ms`, `input_chars`, `result_chars`, `result_lines`, `error`), **and** a simplified layer on top (user): a view `tool_use_by_session` (one row per session × tool: calls, failures, denials, median duration, tokens) and a roll-up `rollup_tool_day` (day × tool × agent kind) so "which tools did I use, how often" is one cheap row per tool without touching the fact table. `report tools` prints the simplified shape by default and `--detail` drops to the call rows; `--failed` and `--denied` filter either level, and both carry through to the other reports ([07 §1](07-analysis-catalogue.md)) | counts per tool per session **only** (no parameters, failures or sequence); one row per tool **result** (loses calls whose result never arrived) | 02 §1, 06 §2 |
| **AT38** ✔ 2026-11-13 | Tool dimension, MCP split | a `tool` dimension row per distinct name with `surface` (`builtin`, `mcp`, `skill`, `agent`, `hook`), `server` and `tool` parsed from `mcp__<server>__<tool>`, and `kind` (`read`, `search`, `edit`, `exec`, `web`, `plan`, `ask`, `other`) from the same JSON config as the categories. So "how much do I use RoslynMcp vs YFexAvaloniaMcp" is a group-by, not a `LIKE` | the raw name only (every report parses the prefix itself); a fixed enum in code (a new MCP server needs a rebuild) | 02 §1, [07 §3](07-analysis-catalogue.md) |
| **AT39** ✔ 2026-11-13 | Which parameters are stored | **everything in full** (user): the whole input object of every tool call, MCP included, stored as scrubbed JSON with no per-tool keep/redact list and no character cap, plus one `tool_param` row per top-level parameter so queries can group by name. Scrubbing still runs (✔AT15: `<system-reminder>` blocks removed, `TextScrubber.Default`, profile paths as `~`), and ✔AT48's name-based detector still matters because it is now the only thing standing between a `--password` argument and the store. Consequences the user accepted: MCP arguments are unbounded text from outside this PC (a fetched page, a mail body, a connection string), and the store grows faster — `forget` and `rebuild` already compact it | the per-tool keep/redact config (proposed); names and sizes only (loses "which commands") | 03 §1, 05 §3 |
| **AT40** ✔ 2026-11-13 | Failures, denials and retries | `ok=false` from `is_error` on the result; `denied` when the result is a permission refusal (already a `user_action` of kind `deny`, now linked by `tool_call_id`); `error` keeps the first 200 scrubbed characters. A retry is not inferred: the sequence shows it | a single `ok` flag (a refusal by you and a crash in the tool are not the same thing) | 02 §1 |
| **AT41** ✔ 2026-11-13 | Linking accesses to tool calls | `access.tool_call_id` (indexed) replaces the loose join on `tool_use_id`; `access.tool_use_id` stays for readability | join on `tool_use_id` text in every query | 06 §2-S3 |
| **AT42** ✔ 2026-11-13 | Where real tokens come from | the transcripts already carry them: each `assistant` record's `message.usage` has `input_tokens`, `output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `output_tokens_details.thinking_tokens`, `service_tier` and `server_tool_use` counts, and the `api-request` record beside it has `model`, `effort` and `max_tokens`. One `turn` row per response, deduplicated by message id (as [01 §1](01-sources-and-token-cost.md) already does for the output-token figures). Still zero tokens for the agent: nothing is asked of it | ask the agent to report its usage (the observer effect, AT9/AT22); estimate everything from characters when the real numbers are in the file | 01 §1, 02 §1 |
| **AT43** ✔ 2026-11-13 | Reported vs estimated | every token number carries `token_source`: `reported` (from `usage`) or `estimated` (characters / 4, the existing A8 measure). Reports never add the two silently: they show the split, and `report estimates` gives the error of the estimate against the reported totals per model, so the character divisor can be corrected | one `tokens` column mixing both (a total nobody can trust); estimates only (the real numbers are already there) | 07 §2 |
| **AT44** ✔ 2026-11-13 | Attributing a turn's tokens to tool calls and reads | a tool result's tokens are not billed separately, so exact attribution does not exist. Proportional allocation: inside one turn, split the reported `input + cache_creation` tokens across that turn's tool results by their `result_chars`, stored as `est_share_tokens` on `tool_call` and summed onto that call's accesses. Calibrated by the turn's real total, labelled an estimate everywhere | characters / 4 per read with no calibration (today's A8; drifts from the real bill); no attribution at all (the "which doc costs me most" question has no answer) | 07 §2 |
| **AT45** ✔ 2026-11-13 | Money cost | optional `prices.json` (per model: input, cache write, cache read, output, per million tokens). With no file the cost columns stay empty; no prices are shipped or guessed. If kept, the amount is core's money value type with its currency, not a bare `decimal` | hard-coded prices (wrong the day they change); a cost computed in SQL from magic numbers | 05 §1, 07 §2 |
| **AT46** ✔ 2026-11-13 | Roll-ups | `rollup_day` gains `tool_calls`, `tokens_reported`, `tokens_estimated` (the `tokens` column designed in [06 §2](06-sqlite-store.md) and never filled), written in the ingest transaction. Long-range token reports read the roll-up, not the fact table | compute from `turn` and `tool_call` every time (fine today, slow at a few million rows) | 06 §2 |
| **AT47** ✔ 2026-11-13 | New reports | A18 `tools` (calls, failures, denials, duration, tokens per tool), A19 `mcp` (per server and tool), A20 `tokens` (per session, day, model, agent type; reported vs estimated), A21 `cost` (per question and session, only with `prices.json`), A22 `failures` (what fails or is refused, and after which command), A23 `commands` (top shell commands and MCP arguments by frequency and cost), A24 `estimates` (estimate error). Filters `--tool`, `--server`, `--surface`, `--workspace` | only `sql` for all of this (the user asked for reports); a dashboard (no UI, AT18) | 07 §3 |
| **AT48** ✔ 2026-11-13 | Privacy request: secret-value detector | request a Privacy detector keyed on parameter **names**; until it exists, the `redact` list in `tools.json` does it. Tool parameters and MCP arguments are classified `Personal`, and `Secret` when a name matches | trust `TextScrubber.Default` alone (it matches value shapes, not `--password hunter2`) | 03 §1, requests |
| **AT49** ✔ 2026-11-13 | YFex.Automation placement | record the fit: Automation owns AI agent steps, so once this model exists its runs are the second producer of the same tool-and-token facts. Proposal: keep the model here for now (a developer tool on this PC), and when Automation needs it, move the **contracts** (tool, tool_call, turn) to the owner the user picks then, rather than growing a second private copy | build an Automation-side copy later (two models for the same facts); move it now (Automation does not need it yet, and this tool is not a library) | ecosystem fit |
