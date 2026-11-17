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
| `tool_param.value`, the input JSON of every tool call (AT-P5) | `Personal`, and `Secret` when a value is one | **stored in full**, scrubbed (✔AT39, user 2026-11-13): `<system-reminder>` blocks removed, `TextScrubber.Default`, profile paths as `~`. No keep/redact list and no cap, MCP arguments included — so this is the most sensitive table in the store: an MCP argument can hold a fetched page, a mail body or a credential, and a shell flag can hold a password that value-shape scrubbing does not match. Mitigations: the name-based Privacy detector is requested (✔AT48), the store never leaves the PC (✔AT12 none of it is encrypted either), and `forget --session` / `--before` removes it and compacts the file |
| `turn` token counts, `model`, `effort`, `service_tier`, `request_id` (AT-P5) | `Internal` | numbers and identifiers only; no message content is read from the `usage` records |
| file contents, tool results, thinking text beyond the 300-character excerpt | — | **never stored**; only sizes and line ranges |
| sessions of other workspaces (AT-P4) | same levels as the YFex ones | a session is stored only if its root is configured (✔AT33), so the workspace list is also the privacy boundary: an unlisted project is never read. Adding a root is therefore a deliberate act, and `workspace add` says how many transcripts it would bring in before it is saved |

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
- `agentReads forget --session <id>` and `--before <date>` delete rows, record the session as forgotten (later ingests and rebuilds
  skip it) and compact the file so the text is not left in free pages; `agentReads rebuild` drops the store and re-ingests the
  transcripts still on disk.
- Raising `cleanupPeriodDays` to keep more raw transcripts is a separate settings decision (⚑AT3); it keeps unscrubbed text longer.

## 4. Nothing leaves the PC

- No network calls in the hook or the ingester. No telemetry endpoint, no YFex.Logging shipping, no YFex.System/Profiling transport.
- No language model reads the store unless the user chooses ⚑AT9.
- When an agent session later analyses the store at the user's request, the query results enter that session's context like any other
  file; that is the user's choice per session.
