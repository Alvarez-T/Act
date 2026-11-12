# 03 — Privacy, storage and retention

Prompts, titles and the agent's text are the user's personal data and may hold other people's data, keys or tokens pasted into a chat.
The rule is simple: **everything stays on this PC, nothing is sent anywhere, and free text is scrubbed before it is stored.**

## 1. Classification of each field

Levels are YFex core `YFex.Privacy.DataSensitivity` (`Public`, `Internal`, `Personal`, `PersonalIdentifier`, `SensitivePersonal`,
`Financial`, `Secret`), used here as a label in the design, not as a code reference.

| Field | Level | Handling |
|---|---|---|
| `prompt.text`, `session.title`, `title_change.title`, `purpose_text`, `purpose_step`, `purpose_call`, scrubbed command line | `Personal` (free text; may contain identifiers or secrets) | `<system-reminder>` blocks removed (they carry the account e-mail, linked-session lists, file contents); then scrubbed with YFex.Privacy `TextScrubber.Default` (e-mail, card, JWT, bearer, API keys, secret assignments, URL queries, IP); `purpose_text` cut to 300 characters; `prompt.text` cut to a limit (⚑AT11) |
| paths | `Internal`; the user folder is `Personal` | `C:\Users\<name>` written as `~`; nothing else changed |
| `session_id`, `agent_id`, `prompt_id`, `tool_use_id`, categories, counts, times | `Internal` | as is |
| file contents, tool results, thinking text beyond the 300-character excerpt | — | **never stored**; only sizes and line ranges |

`TextScrubber.ContainsPersonalData` sets a `scrubbed` flag on the row, so a query can show how often prompts carried such data.

## 2. Where it lives

| Option | Pros | Cons |
|---|---|---|
| **SQLite file** `%LOCALAPPDATA%\YFex\agent-telemetry\reads.db` through `Microsoft.Data.Sqlite` (already used by 16 YFex projects) — recommended (⚑AT5) | one file, SQL joins for every question in [04](04-analysis-queries.md), `sqlite3` CLI or any viewer, incremental upserts | a schema to keep (one `CREATE TABLE IF NOT EXISTS` script; rebuild from transcripts while they exist) |
| JSONL files + DuckDB queries | append-only, trivial writer | DuckDB is a new third-party tool; joins and upserts are clumsier |
| YFex.Logging `.jsonl`/`.ylog` rolling files, viewed in LoggY | ecosystem reuse | Logging is an operations log with lossy diagnostics; it has no query side; LoggY is a tail viewer prototype |
| YFex.Persistence.Sqlite | ecosystem reuse | app-state key-value, not tables for analysis |

Chosen 2026-11-12: SQLite (⚑AT5), written through YFex.Data + `YFex.Data.Sqlite` now that the tool is a project (⚑AT28, approved); the
physical schema is in [06](06-sqlite-store.md).

- Outside every repository and outside synced folders, so it is never committed or uploaded.
- Protected by the Windows profile ACL. Encrypting the file (YFex.Cryptography secret protection) is possible but not proposed: the
  text is scrubbed and the transcripts next to it are not encrypted either (⚑AT12).

## 3. Retention and deletion

- Claude Code deletes transcripts after `cleanupPeriodDays` (default 30). The store keeps its rows after that, so it becomes the only
  copy of the scrubbed prompts and titles. Proposed: keep 365 days, then drop `prompt.text` and `purpose_text` and keep the counts
  (⚑AT11).
- `agentReads forget --session <id>` and `--before <date>` delete rows; `agentReads rebuild` drops the store and re-ingests the
  transcripts still on disk.
- Raising `cleanupPeriodDays` to keep more raw transcripts is a separate settings decision (⚑AT3); it keeps unscrubbed text longer.

## 4. Nothing leaves the PC

- No network calls in the hook or the ingester. No telemetry endpoint, no YFex.Logging shipping, no YFex.System/Profiling transport.
- No language model reads the store unless the user chooses ⚑AT9.
- When an agent session later analyses the store at the user's request, the query results enter that session's context like any other
  file; that is the user's choice per session.
