# 07 — Analysis catalogue: filters, measures, analyses

What the store can answer, as CLI reports ([05](05-tool-cli-and-libraries.md)). Every report takes the same filters and prints a
table, CSV or JSON. SQL for the main ones is in [04](04-analysis-queries.md); the schema that makes them fast is in [06](06-sqlite-store.md).

## 1. Filters (every report)

| Filter | CLI | Column | Notes |
|---|---|---|---|
| period | `--since 2026-11-01 --until …`, `--last 14d` | `access.day` | indexed |
| session | `--session <id>`, `--title "*provisioning*"` | `session_id`, `session.title` | title glob over the latest title or any title change |
| question text | `--prompt "ecosystem fit"` | `prompt_fts` | full-text (FTS5) over scrubbed prompts |
| repository | `--repo YFex.Jobs` (repeatable, glob) | `file.repo` | |
| category / kind | `--category rule,skill`, `--kind doc` | `file.category`, `file.kind` | |
| path | `--path "docs/agent/rules/*"` | `file.path` | glob |
| initiator | `--initiator agent` (`user`, `user-directed`, `harness`, `session`) | `access.initiator` | [02 §6](02-events-classification-and-purpose.md) |
| actor | `--actor main`, `--agent-type Explore` | `agent.agent_type` | subagents by type |
| verb / channel | `--verb read,search`, `--channel shell` | `access.verb`, `access.channel` | |
| model / effort | `--model claude-opus-5-5`, `--effort high` | `session.model`, `prompt.effort` | |
| session kind | `--session-kind design` (`code`, `design`, `other`) | `session.kind` | derived at ingest: edited only `docs/internal` → design; edited `.cs` → code |
| advanced | `--where "category == 'rule' and initiator == 'agent' and range_pct < 50"` | any column | YFex.Expressions (⚑AT27) |

## 2. Measures

| Measure | Definition |
|---|---|
| `reads` | accesses with verb `read`/`search`/`load` |
| `sessions` | distinct sessions with ≥ 1 access (the fair count: one long session does not dominate) |
| `coverage` | share of the docs in a group read at least once in the period |
| `read_rate` | sessions that read the file ÷ sessions in the filter |
| `tokens` | estimated tokens the read put in context: result characters ÷ 4 (`access.result_chars`) |
| `range_pct` | lines read ÷ lines of the file at that time (partial reads) |
| `position` | index of the access in its session/agent (0 = first action); and seconds since the prompt |
| `reread` | same file read again in the same session (after a compaction, or because the first read was partial) |
| `follow` | after reading doc A, the agent opened a file A links to, in the same question |
| `miss` | a read that failed (file not found, empty grep) |

## 3. Analyses

| # | Analysis | Question it answers | Main measures |
|---|---|---|---|
| A1 | **Coverage** | which rules, skills, designs and public docs are never or rarely read | coverage, read_rate, last read |
| A2 | **Docs vs code** | do agents read docs or go straight to code; per agent type, session kind, repo | reads, tokens, ratio |
| A3 | **Routing funnel** | do agents follow the route `AGENTS.md` → `00-start-here` → kind rule → `ecosystem-fit` / map / atlas / skill | step conversion per session kind |
| A4 | **Time to first rule** | how early rules are read; does code get written before them | position, seconds, "edit before rule" count |
| A5 | **Expectation checks** | per session: did it read what the rules require for its kind (C# → `yfex-csharp`; design → `ecosystem-fit`, map, atlas) | verdicts per expectation ([05 §4](05-tool-cli-and-libraries.md), YFex.Rules) |
| A6 | **Sections read** | which headings of a rule/skill are read (line ranges mapped to Markdown headings of that version) | range_pct, hits per heading |
| A7 | **Link follow-through** | which links in routing docs agents actually follow; dead routes | follow rate per link |
| A8 | **Re-reads and cost** | which docs are read again and again, and how many tokens docs cost per session | reread, tokens per doc and per session |
| A9 | **Search misses** | `grep`/`Grep` patterns that found nothing and paths that did not exist: missing docs, stale paths in docs | miss count by pattern and path |
| A10 | **You vs the agent** | what you attached or named vs what the agent chose; how much the agent reads unasked | initiator mix |
| A11 | **Subagents** | what subagents read vs the main agent; do Explore/Plan agents read the rules | per agent type |
| A12 | **Doc change impact** | after a doc or rule changed (git commit time of the file), did reads and expectation verdicts change | before/after windows per commit |
| A13 | **Question → reads** | for one question: the ordered list of reads with purpose; questions with the most / fewest doc reads | sequence, counts |
| A14 | **Sequences** | the most common read paths (n-grams of categories or files) before the first edit | top sequences |
| A15 | **Purpose text** | the descriptions and plan steps next to each rule read (what agents say they look for) | grouped descriptions |
| A16 | **Model and effort** | does a model or effort level read more docs | per model / effort |
| A17 | **Size vs completeness** | are long docs read partially; does size predict partial reads | lines vs range_pct |

## 4. Output

- `--format table` (default, fixed-width), `csv`, `json`; `--top N`; `--by repo|category|day|week|session|agent-type` groups any count
  report.
- `agentReads sql "<SELECT …>"` runs read-only SQL for anything the reports do not cover (the connection is opened read-only).
- No UI (user, 2026-11-12). The atlas view stays a later option (⚑AT18).
