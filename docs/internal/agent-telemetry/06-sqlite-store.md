# 06 — SQLite store for analysis

One local file, `%LOCALAPPDATA%\YFex\agent-telemetry\reads.db` (approved 2026-11-12, ⚑AT5), shaped for analysis: a narrow fact table of
integers, small dimension tables, a text dictionary, daily roll-ups and full-text search on prompts. This refines the logical model in
[02 §1](02-events-classification-and-purpose.md); column names there are kept as view columns.

## 1. Size

Today: ≈ 70 file accesses per transcript and ≈ 100 transcripts per month → ≈ 10 k access rows a month. A year of heavy use stays under
1 M rows (≈ 50–80 MB with indexes). At that size SQLite answers every report in [07](07-analysis-catalogue.md) in milliseconds; no
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

`ANALYZE` after the first backfill and `PRAGMA optimize` after each ingest keep the planner's statistics current.

## 4. Writing

- Pragmas: `journal_mode=WAL` (reads during ingest), `synchronous=NORMAL`, `foreign_keys=ON`, `busy_timeout=5000`, `temp_store=MEMORY`.
- Several sessions can end a turn at once, so several hooks can ingest at once. Writers serialize on a named mutex per database file
  (`Global\YFex.AgentTelemetry.<hash>`); a hook that cannot get it within a few seconds leaves the work to the next run (the byte
  offset in `ingest_state` makes that safe).
- One transaction per transcript chunk: dimension upserts through in-memory caches (`path → file_id`, `text hash → text_id`), access
  rows through YFex.Data's `IBulkWriter` (prepared statements on SQLite), roll-ups updated in the same transaction.
- Reports open the file read-only.

## 5. Schema changes

Transcripts disappear after 30 days, so the store can't always be rebuilt from them. The schema is versioned: YFex.Data schema modules
(`[DataTable]`, `[DataIndex]`, `SchemaInstaller`, `SchemaHistory`) create and migrate the tables (⚑AT28). FTS5 tables, views and
triggers are plain SQL steps in the same module if the schema model does not describe virtual tables (request in the README).
