# 06 — SQLite store for analysis

One local file, `%LOCALAPPDATA%\YFex\agent-telemetry\reads.db` (approved 2026-11-12, ⚑AT5), shaped for analysis: a narrow fact table of
integers, small dimension tables, a text dictionary, daily roll-ups and full-text search on prompts. This refines the logical model in
[02 §1](02-events-classification-and-purpose.md); column names there are kept as view columns.

## 1. Size

Today: ≈ 70 file accesses per transcript and ≈ 100 transcripts per month → ≈ 10 k access rows a month. A year of heavy use stays under
1 M rows (≈ 50–80 MB with indexes). AT-P5 adds, per transcript, ≈ 60–120 `tool_call` rows (fewer than accesses: one shell call can read
five files), ≈ 2–4 `tool_param` rows each, and ≈ 80 `turn` rows — the same order of magnitude, so the estimate above roughly doubles
and the conclusion does not change. ✔AT39 (every parameter in full, MCP arguments included) is the one thing that can move this:
an MCP call carrying a fetched page is a few KB of text per call, so the `text` dictionary grows faster than the fact tables. The
dictionary stores each distinct value once, `forget` and `rebuild` compact the file, and `status` already prints its size. At that size SQLite answers every report in [07](07-analysis-catalogue.md) in milliseconds; no
columnar engine (DuckDB) is needed. If it ever is, DuckDB can attach the SQLite file read-only without an export.

## 2. Tables

Star shape: `access` is the fact; everything repeated is an integer key.

| Table | Key columns | Notes |
|---|---|---|
| `access` | `access_id` INTEGER PK; `session_id`, `prompt_id`, `agent_id`, `file_id`, `purpose_call_id`, `purpose_step_id`, `purpose_text_id` (INTEGER FKs) | `at` (unix ms), `day` (days since 1970, for range scans), `seq` (position in its agent), `channel`, `verb`, `initiator`, `confidence` (tiny int codes), `partial`, `line_from`, `line_to`, `result_chars`, `ok`; ≈ 40–60 bytes a row |
| `file` | `file_id` PK, `path` UNIQUE | `repo`, `category`, `kind`, `ext`, `lines`, `bytes`, `exists_now`, `modified_at`; one row per distinct path ever seen (folders too) |
| `session` | `session_id` PK, `uuid` UNIQUE | `title` (latest), `kind` (code/design/other), `model`, `origin`, `started_at`, `last_at`, `cwd_repo` |
| `title_change` | (`session_id`, `at`) | every app title |
| `prompt` | `prompt_id` PK, `uuid` UNIQUE | `session_id`, `at`, `origin`, `effort`, `chars`, `text` (scrubbed) |
| `prompt_fts` | FTS5, external content = `prompt.text` | `--prompt "…"` filter; tokenizer `unicode61 remove_diacritics 2` (Portuguese and English) |
| `agent` | `agent_id` PK | `session_id`, `key` (`main` or the subagent id), `agent_type`, `description_id`, `parent_access_id` |
| `text` | `text_id` PK, `hash` UNIQUE | scrubbed purpose texts and command lines, stored once (Bash descriptions repeat a lot) |
| `user_action` | `action_id` PK | `session_id`, `prompt_id`, `at`, `kind`, `file_id`, `detail_id` |
| `code` | (`dimension`, `value`) PK | names of the tiny-int codes (`verb`, `initiator`, …), so SQL users can join for labels |
| `doc_heading` | (`file_id`, `version_id`, `line`) | Markdown headings per doc version (A6 sections read) |
| `doc_link` | (`from_file_id`, `to_file_id`, `line`) | links inside docs (A7 follow-through) |
| `doc_version` | `version_id` PK | `file_id`, `commit`, `committed_at` from `git log -- <path>` of the doc's repo (A12 change impact) |
| `session_check` | (`session_id`, `expectation`) | stored verdicts of the expectation checks (A5), recomputed when the rules change |
| `rollup_day` | (`day`, `file_id`, `initiator`, `agent_kind`) | `reads`, `sessions`, `tokens`, `partial_reads`; updated in the same transaction as the ingest, used by long-range reports |
| `ingest_state` | `transcript` PK | `bytes_read`, `format_version`, `last_at` (incremental ingest) |

Schema v3 (AT-P4, workspaces ✔AT32–✔AT36):

| Table | Key columns | Notes |
|---|---|---|
| `workspace` | `workspace_id` INTEGER PK, `name` UNIQUE | `root` (canonical, forward slashes), `enabled`, `added_at`. No `(outside)` row: a session under no root is not stored (✔AT33) |
| `session` | + `workspace_id` | from the session's first `cwd`, longest root match; `cwd_repo` keeps its meaning inside that workspace |
| `file` | + `workspace_id` (nullable) | from the path, so a cross-workspace read is visible (✔AT35); null for a path under no root (`~claude`, `scripts`, `other`), which is still stored because agents inside a workspace read those |

Schema v4 (AT-P5, tool, MCP and token metering ✔AT37–✔AT47):

| Table | Key columns | Notes |
|---|---|---|
| `tool` | `tool_id` INTEGER PK, `name` UNIQUE | `surface` (`builtin` / `mcp` / `skill` / `agent` / `hook`), `server` (MCP server, null otherwise), `tool` (the part after the server), `kind` (`read` / `search` / `edit` / `exec` / `web` / `plan` / `ask` / `other`), `first_seen_at`; one row per distinct tool name ever seen, so a new MCP server needs no code change (✔AT38) |
| `tool_call` | `tool_call_id` INTEGER PK | `session_id`, `agent_id`, `prompt_id`, `turn_id`, `tool_id` (INTEGER FKs); `at`, `day`, `seq` (position in its agent, shared with `access`), `ok`, `denied`, `duration_ms`, `input_chars`, `result_chars`, `result_lines`, `accesses` (count), `est_share_tokens` (✔AT44), `error_id`, `command_id`, `params_id` (text dictionary); ≈ 60 bytes a row |
| `tool_param` | (`tool_call_id`, `name`) | `value_id` (the scrubbed value in the `text` dictionary), `chars`, `type` (`string` / `number` / `bool` / `object` / `array`); one row per top-level parameter, stored in full (✔AT39). `tool_call.params_id` keeps the whole scrubbed input JSON, so a nested MCP argument is queryable too |
| `turn` | `turn_id` INTEGER PK, `message_uuid` UNIQUE | `session_id`, `agent_id`, `prompt_id`; `at`, `day`, `model`, `effort`, `service_tier`, `input_tokens`, `cache_creation_tokens`, `cache_read_tokens`, `output_tokens`, `thinking_tokens`, `iterations`, `web_search_requests`, `web_fetch_requests`, `token_source` (`reported` / `estimated`, ✔AT43), `request_id`, `stop_reason`, `duration_ms`. `message_uuid` is what deduplicates a re-parsed transcript (✔AT42) |
| `access` | + `tool_call_id`, + `est_share_tokens` | the loose `tool_use_id` join becomes a key (✔AT41) |
| `rollup_day` | + `tool_calls`, `tokens_reported`, `tokens_estimated` | the `tokens` column designed below and never filled (✔AT46) |
| `rollup_tool_day` | (`day`, `tool_id`, `agent_kind`) | `calls`, `failed`, `denied`, `duration_ms_total`, `tokens`: the simplified layer ✔AT37 asks for, written in the ingest transaction, so "which tools, how often" over months is a roll-up scan and never touches `tool_call` |

Views: `tool_calls` (call rows with readable tool, server and surface names), `tool_use_by_session` (one row per session × tool with
calls, failures, denials, median duration and tokens) and `turns`. `tool_use_by_session` is the shape `report tools` prints by default
(✔AT37); `--detail` reads the fact table.

Views with readable names (`reads`, `sessions_summary`, `docs_never_read`) keep [04](04-analysis-queries.md)'s SQL short.

## 3. Indexes

| Index | Serves |
|---|---|
| `access(file_id, day)` | per-file counts in a period (A1, A8, A12) |
| `access(session_id, agent_id, seq)` | sequences, positions, funnels (A3, A4, A13, A14) |
| `access(day, initiator)` | period + initiator filters (A10) |
| `access(prompt_id)` | question → reads (A13) |
| `file(category, repo)` | category/repo filters, coverage denominators |
| `prompt(session_id, at)`, `user_action(session_id, at)` | joins by time |
| `session(workspace_id)`, `file(workspace_id, category)` | workspace filters and per-workspace coverage denominators (AT-P4) |
| `tool_call(tool_id, day)` | calls, failures and cost per tool or MCP server in a period (A18, A19, A22) |
| `tool_call(session_id, tool_id)` | the `tool_use_by_session` view (✔AT37) |
| `tool_call(day, ok, denied)` | `--failed` / `--denied` over a period (✔AT40) |
| `tool_call(session_id, agent_id, seq)` | the call sequence beside the access sequence (A22, A23) |
| `tool_call(turn_id)`, `access(tool_call_id)` | token attribution inside a turn (A20, A21, ✔AT44) |
| `turn(session_id, at)`, `turn(day, model)` | token totals per session, day and model (A20, A24) |

`ANALYZE` after the first backfill and `PRAGMA optimize` after each ingest keep the planner's statistics current.

## 4. Writing

- Pragmas: `journal_mode=WAL` (reads during ingest), `synchronous=NORMAL`, `foreign_keys=ON`, `busy_timeout=5000`, `temp_store=MEMORY`.
- Several sessions can end a turn at once, so several hooks can ingest at once. Writers serialize on an exclusive lock file next to the
  database (`reads.db.lock`; a named mutex was the first plan, but async code resumes on other threads, which a mutex does not allow); a
  writer that cannot get it within 30 s leaves the work to the next run (`ingest_state` records which transcripts changed). A hook
  holds it for about 0.3–0.6 s per changed transcript (AT-P2 log).
- One transaction per transcript chunk: dimension upserts through in-memory caches (`path → file_id`, `text hash → text_id`,
  `tool name → tool_id`), access, `tool_call`, `tool_param` and `turn` rows through YFex.Data's `IBulkWriter` (prepared statements on
  SQLite), roll-ups updated in the same transaction. Token attribution (✔AT44) is computed per turn inside that transaction, after its
  tool calls are known, so no second pass over the file is needed.
- Reports open the file read-only.

## 5. Schema changes

Transcripts disappear after 30 days, so the store can't always be rebuilt from them. The schema is versioned: YFex.Data schema modules
(`[DataTable]`, `[DataIndex]`, `SchemaInstaller`, `SchemaHistory`) create and migrate the tables (✔AT28). FTS5 tables, views and
triggers are plain SQL steps in the same module if the schema model does not describe virtual tables (request in the README).

| Version | Adds | Backfill |
|---|---|---|
| v1 (AT-P1) | the tables above | — |
| v2 (AT-P2) | `forgotten_session` | — |
| v3 (AT-P4) | `workspace`; `workspace_id` on `session` and `file` | **no in-place migration** (✔AT36, user): the columns are added empty and filled by `rebuild`, which re-ingests every transcript still on disk, so the ingester is the only code that assigns a workspace. A session whose transcript has aged out is lost; `rebuild` lists those before doing anything and refuses without `--yes`. Measured 2026-11-13: a rebuild lost nothing |
| v4 (AT-P5) | `tool`, `tool_call`, `tool_param`, `turn`; `tool_call_id` and `est_share_tokens` on `access`; three columns on `rollup_day` | no backfill from the store is possible (tool calls and usage were never stored). `rebuild` re-reads the transcripts still on disk; older sessions keep accesses with no tool calls, which the reports show as `unknown` rather than zero |
| v5 (AT-P5) | the `turn` uniqueness index becomes `(agent_id, message_uuid)` | —: a response id repeats across the agents of one session (a subagent, a fork), which broke two transcripts on the first real rebuild |
| v6 (AT-P5) | recreates `reads`, `tool_calls`, `tool_use_by_session` and `turns` from their current text | —. A view is created inside the version that introduced it, so a store that applied v3 or v4 **before** that view's text last changed kept the old columns and every report over `reads` failed with `no such column: r.session_workspace` (seen 2026-11-13). Views hold no data, so recreating them is safe to repeat; the rule is that any later change to a view adds a version doing the same, rather than editing the applied one |

The `reads` view keeps its columns, so every query in [04](04-analysis-queries.md) and every report built in AT-P1 keeps working; new
views (`tool_calls`, `turns`) carry the readable names for the new tables.
