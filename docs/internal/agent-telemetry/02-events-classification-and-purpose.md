# 02 — Events, classification and purpose

## 1. Event model

One row per **access**: a file or folder the agent read, searched, listed, loaded or wrote. Everything else is a dimension of it.

```
session ─┬─ prompt ─┬─ access (tool call, shell call or auto-load)
         │          └─ access …
         └─ agent (main | subagent: type, description, parent tool call) ─ access …
doc (inventory snapshot of every file that could be read; finds the never-read ones)
```

| Table | Columns | Source (transcript / hook) |
|---|---|---|
| `session` | `session_id`, `title` (latest), `started_at`, `last_at`, `cwd`, `entrypoint`, `model`, `origin` (`user` / `side-session` / `sdk`), `transcript` | `custom-title`, first `user` record / `SessionStart` |
| `title_change` | `session_id`, `at`, `title` | each `custom-title` record (the app renames as the work moves) |
| `prompt` | `prompt_id`, `session_id`, `at`, `origin` (§6: `human`, `brief`, `peer`, `notification`, `coordinator`, `system`), `text` (scrubbed, [03](03-privacy-storage-and-retention.md)), `chars`, `mentions` (paths and file names named in the text) | `user` records with text content / `UserPromptSubmit` |
| `user_action` | `session_id`, `prompt_id`, `at`, `kind` (`prompt`, `attach`, `approve`, `deny`, `answer`, `outside-edit`), `path`, `detail` (scrubbed) | §6 |
| `agent` | `agent_id` (`main` for the session itself), `session_id`, `agent_type`, `description`, `parent_tool_use_id`, `model` | `subagents/*.meta.json` / `SubagentStart` |
| `access` | `access_id`, `session_id`, `agent_id`, `prompt_id`, `tool_use_id`, `at`, `channel`, `initiator`, `verb`, `path`, `repo`, `category`, `ext`, `partial`, `range`, `pattern`, `confidence`, `purpose_call`, `purpose_step`, `purpose_text` | §2–§5 |
| `doc` | `path`, `repo`, `category`, `bytes`, `lines`, `modified_at`, `snapshot_at` | file system scan at ingest |
| `ingest_state` | `transcript`, `bytes_read`, `format_version`, `at` | the ingester itself (incremental reads) |

- `channel`: `tool` (Read, Grep, Glob, Edit, Write, Skill, MCP), `shell` (Bash, PowerShell), `auto` (attachments: instructions,
  nested memory, attached file, invoked skill, outside edit).
- `initiator`: who caused the access — `agent`, `user-directed`, `user`, `harness`, `session` (§6).
- `verb`: `read`, `search`, `list`, `load` (auto), `write`, `edit`. Builds and test runs are not accesses.
- `partial`: the call read part of the file (`offset`/`limit`, `sed -n 1,80p`, `head`, `Select-Object -First`); `range` keeps it.
- `confidence`: `exact` (tool input, attachment) or `parsed` (shell command line, §4).
- One shell call that reads five files gives five rows with the same `tool_use_id`.

The actor vocabulary matches YFex.Actors (`ActorKind.Agent`, "an AI agent"; the user is `ActorKind.User`), as names only: the tool does
not reference the library (⚑AT13).

## 2. Repository

`repo` = the first folder under `C:\Dev\YFex\` (`YFex`, `YFex.Jobs`, …). Outside it: `~claude` (`C:\Users\thiag\.claude\…`, skills,
memory, settings), `scripts` (`C:\Dev\Scripts`), `other`. Relative paths resolve against the record's `cwd` plus any `cd` earlier in the
same command line.

## 3. Category

First match wins. The table is data (a JSON file next to the tool), so a new folder needs no code change (⚑AT7).

| Category | Matches | Kind |
|---|---|---|
| `skill` | `**/skills/*/SKILL.md` (YFex `docs/agent/skills`, `~/.claude/skills`, a repo's `.claude/skills`) and the `invoked_skills` attachment | doc |
| `skill-ref` | any other file under a `skills/<name>/` folder | doc |
| `rule` | `YFex/docs/agent/rules/*.md` | doc |
| `routing` | any `AGENTS.md` or `CLAUDE.md`; `YFex/docs/agent/infrastructure-map.md`; `YFex/docs/index.md` | doc |
| `atlas` | `YFex/docs/atlas/**` | doc |
| `agent-doc` | other `YFex/docs/agent/**` (testing guides, reviews, archive) | doc |
| `memory` | `~/.claude/projects/*/memory/*.md` | doc |
| `design` | `YFex/docs/internal/**` | doc |
| `public-doc` | `YFex/docs/public/**`; a repo's `README.md` or `docs/**` | doc |
| `other-md` | any other `.md` | doc |
| `code` | `.cs`, `.razor`, `.axaml`, `.xaml`, `.ps1`, `.py`, `.sh` | code |
| `build` | `.csproj`, `.props`, `.targets`, `.slnx`, `.sln`, `Directory.Packages.props` | code |
| `config` | `.json`, `.jsonc`, `.yml`, `.yaml`, `.xml`, `.editorconfig`, `.runsettings` | other |
| `transcript` | `~/.claude/projects/**/*.jsonl` (agents reading other sessions) | other |
| `other` | the rest | other |

`kind` (`doc` / `code` / `other`) is what the docs-vs-code ratio counts. Folders (`ls`, `Glob` base path, `grep -r dir`) keep their
category from the same table and `ext = "/"`.

## 4. Shell commands

Shell calls are about 95 % of the reads ([01 §1](01-sources-and-token-cost.md)), so they are parsed, not skipped.

- Split on `;`, `&&`, `||`, `|` and newlines; follow `cd`.
- Verb table: `cat`, `type`, `Get-Content`, `head`, `tail`, `sed -n`, `awk`, `less`, `more`, `git show <rev>:<path>` → `read`;
  `grep`, `rg`, `Select-String`, `findstr` → `search` (keep the pattern); `ls`, `dir`, `find`, `Get-ChildItem`, `tree` → `list`;
  `sed -i`, `>`/`>>` redirects, `Set-Content`, `Out-File` → `write`. Unknown verbs and builds (`dotnet`, `git commit`, …) give no row.
- Globs (`docs/agent/rules/*.md`) are expanded against the file system at ingest time; `for f in a b c` lists are expanded.
- `python -c` / here-docs: paths inside `open('…')` are taken best effort.
- Rows from parsing are `confidence = parsed`. The scrubbed command line is kept (⚑AT10) so a wrong parse can be checked and the
  parser fixed and re-run over the same transcripts.

## 5. Purpose without asking the agent

"For what purpose" is built from evidence that already exists, in four levels. None costs the agent a token.

| Level | Column | Evidence | Coverage today |
|---|---|---|---|
| **Question** | via `prompt_id` | the user prompt that started the turn; in a side session the brief from the parent session (`turnOrigin = host_synthetic`); in a subagent the parent's prompt plus the subagent's `description` | every access |
| **Task step** | `purpose_step` | the plan step `in_progress` at that moment, rebuilt from `mcp__ccd_turn__update_plan` calls (`items`, `advance`, `done`/`start`); `TodoWrite` in CLI sessions | sessions that keep a plan (372 plan calls in 94 transcripts) |
| **Call** | `purpose_call` | Bash/PowerShell `description` (75 % of shell calls); `Agent` `description`; `Grep`/`grep` pattern; `Skill` name; for `nested_memory` "auto: touched `<trigger file>`" | per call; `Read` has no description field |
| **Narrative** | `purpose_text` | the last visible assistant text before the call, and the thinking text when the transcript has it; scrubbed, ≤ 300 characters | partial: most thinking blocks are empty in the transcript |

What **cannot** be known:

- The real intent of a `Read`/`Glob` call with no text before it: only the question and the step are known.
- Whether the content **influenced** the answer. Proxies only: an edit in the same repo or file later in the turn, the doc's path or a
  distinctive term (a ⚑ id, a rule heading) appearing in the agent's later text or files. Queries label them as proxies.
- Auto-loaded files (`instructions`, `nested_memory`) were put in context by the harness; "loaded" is not "read with attention".
- Anything the agent recalled from training or from earlier context instead of reading again (a skipped read is invisible except as
  absence; [04](04-analysis-queries.md) compares sessions to find it).
- Reasoning the transcript does not store (empty thinking blocks).

Optional later: label purposes into a small set (`route`, `rule-check`, `api-lookup`, `example`, `verify`, `edit-target`) with a
language model after the fact. It costs tokens outside the agent's session, never influences the session, but sends prompts to a model
— a privacy decision (⚑AT9, default no; rule-based labels from the call purpose first).

## 6. Who: the user, the agent, Claude Code or another session

Every prompt and every access gets an initiator, so the user's own actions are never counted as the agent's behaviour. The transcript
marks the origin of each message (field `origin.kind` on `user` records; counts from the 94 transcripts):

| Transcript marker | Meaning | `prompt.origin` | Count |
|---|---|---|---|
| `origin.kind = "human"` | the user typed it | `human` | 360 |
| `turnOrigin = "host_synthetic"` | the brief another session sent when it started this one | `brief` | 11 |
| `origin.kind = "peer"` | a message from another session (`SendMessage`) | `peer` | 24 |
| `origin.kind = "task-notification"`, `<side-sessions-event>` | a background task or a side session reported back | `notification` | ≈ 50 |
| `origin.kind = "coordinator"` (in `subagents/*.jsonl`) | the parent agent's prompt to a subagent | `coordinator` | 4 |
| no origin, `promptSource = "sdk"`/`"system"` | app-generated turns | `system` | ≈ 70 |

Accesses then get their initiator:

| `initiator` | Rule | Example |
|---|---|---|
| `user` | the user put the file in front of the agent: an `@path` attachment on a `human` prompt, or a path pasted in it that the harness attached | "@YFex/docs/agent/infrastructure-map.md look at this" |
| `user-directed` | the agent read it with a tool, and the current `human` prompt names that path or file name (`prompt.mentions`) | "read ecosystem-fit.md and …" → Read `ecosystem-fit.md` |
| `agent` | the agent read it with a tool and nobody named it: **the behaviour under study** | the agent opens `00-start-here.md` on its own |
| `harness` | Claude Code loaded it: `instructions`, `nested_memory`, skill listing, files re-attached after compaction | `YFex/CLAUDE.md` loaded when the agent touched `YFex/` |
| `session` | another session named it in a brief or message (`brief`, `peer`), and the agent then read it | the parent's brief lists the files to read |

In a subagent the same rules apply with the parent's `coordinator` prompt in place of the user's: `agent` when the subagent chose the
file, `user-directed` when the parent's prompt named it. `agent_id` says which subagent, and the parent's own initiator for that turn
says whether the chain started with the user.

**The user's own actions** go to `user_action`, never to `access`:

| `kind` | Source |
|---|---|
| `prompt` | a `human` prompt (text scrubbed) |
| `attach` | an `@path` attachment |
| `approve` / `deny` | `permissionDecision` with `source = user_temporary` or `user_permanent` (approvals by settings or hooks are not the user's) |
| `answer` | the result of an `AskUserQuestion` call (the option chosen) |
| `outside-edit` | an `edited_text_file` attachment: a file the agent had read changed outside the agent. If another ingested session wrote that file in the same minutes it is labelled `session`, otherwise `user` |

What stays invisible: what the user reads or runs **outside** Claude Code (an editor, the Terminal panel, a browser). Only edits to
files the agent had already read show up, and only while that session is open.
