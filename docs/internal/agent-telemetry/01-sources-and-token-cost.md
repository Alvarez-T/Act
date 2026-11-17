# 01 — Sources and token cost

How each option gets the data, what it costs the agent, and what it misses. Numbers come from the 94 transcripts in
`C:\Users\thiag\.claude\projects\C--Dev-YFex\` (main sessions + `<session>/subagents/*.jsonl`) on 2026-11-12. Hook fields come from the
Claude Code hooks reference (`https://code.claude.com/docs/en/hooks`) read on 2026-11-12.

## 1. How agents read today

| Measure (94 transcripts) | Value |
|---|---|
| Tool calls by name | Bash 7 852, Write 2 132, Edit 734, Read **356**, PowerShell 172, Grep 77, Skill 10 |
| Bash/PowerShell calls that look like a read (`cat`, `sed`, `head`, `grep`, `rg`, `Get-Content`, …) | 7 785; 1 721 name a `.md` file |
| Shell calls that carry a `description` | 5 985 of 8 024 (75 %) |
| File-touching calls per transcript | mean 70, median 63, max ≈ 300 |
| User prompts per transcript | mean 5.2 |
| Output tokens per transcript (deduplicated by message id) | mean ≈ 159 k, median ≈ 136 k |
| Thinking blocks with text | 1 750 of 8 233 (most are empty in the transcript) |

**Consequence:** a telemetry that only watches the `Read` tool sees about 5 % of the reads. The shell command line must be parsed
(§4 of [02](02-events-classification-and-purpose.md)).

Files also reach the agent **without any tool call**. The transcript records them as `attachment` entries:

| Attachment type | What it is | Count |
|---|---|---|
| `instructions` | user and project `CLAUDE.md` loaded at start (with `@AGENTS.md` imports) | 133 |
| `nested_memory` | a `CLAUDE.md`/`AGENTS.md` of a subfolder, loaded when the agent touches that folder | 162 |
| `file` | a file attached to a prompt: by the user with `@path` (the prompt text has the `@`), or re-attached by Claude Code after a compaction (no `@`) | 16 |
| `edited_text_file` | a file the agent had read was changed **outside the agent** (by the user in an editor, or by another session) | 1 062 |
| `queued_command` | a message queued while the agent worked (user prompt, task notification) | 101 |
| `invoked_skills` | `SKILL.md` content injected by the `Skill` tool | 2 |
| `skill_listing`, `dynamic_skill` | the skill names and descriptions offered to the agent (not the skill body) | 120, 1 |

### What else the transcripts already carry (AT-P5, ✔AT37–✔AT42)

The figures above were produced by reading the same records the metering phase stores, so the source is proven, not assumed. Per
`assistant` record:

```jsonc
"message": {
  "model": "claude-opus-5",
  "usage": {
    "input_tokens": 2, "output_tokens": 200,
    "cache_creation_input_tokens": 30383, "cache_read_input_tokens": 45840,
    "output_tokens_details": { "thinking_tokens": 47 },
    "server_tool_use": { "web_search_requests": 0, "web_fetch_requests": 0 },
    "service_tier": "standard"
  },
  "content": [ { "type": "tool_use", "id": "toolu_…", "name": "mcp__RoslynMcp__get_names", "input": { … } } ]
}
```

and beside it an `api-request` record with `params.model`, `params.output_config.effort` and `params.max_tokens`, plus `tool_result`
blocks carrying `is_error` and the result text. So tool use, MCP calls, parameters and real token usage need **no new hook, no new
permission and no question to the agent**: they are a parser change plus two tables (✔AT42). The three transcript-side limits stay:
a deleted transcript cannot be read, a tool result has no token count of its own (attribution is proportional, ✔AT44), and prices are
not in the file (✔AT45).

## 2. Options

### A. Claude Code hooks → local store

`type: "command"` hooks in `~/.claude/settings.json` (user level, so every repository under `C:\Dev\YFex` and every subagent is covered).
The hook reads the JSON on stdin, appends one event, prints nothing, exits 0.

| Event | Fields the hook gets (verified) | Use |
|---|---|---|
| every event | `session_id`, `prompt_id`, `transcript_path`, `cwd`, `permission_mode`, `effort`, `hook_event_name`; `agent_id` + `agent_type` inside a subagent | keys; main vs subagent |
| `SessionStart` | `source` (`startup`/`resume`/`clear`/`compact`/`fork`), `model`, `session_title` (only when a custom title is set) | session row |
| `UserPromptSubmit` | `prompt`, `session_title` when set | the question |
| `PostToolUse` | `tool_name`, `tool_input` (incl. Bash `description`), `tool_response`, `tool_use_id`, `duration_ms` | the read |
| `InstructionsLoaded` | `file_path`, `memory_type`, `load_reason`, `trigger_file_path`, `parent_file_path` | auto-loaded `CLAUDE.md`/`AGENTS.md` |
| `SubagentStart` / `SubagentStop` | `agent_id`, `agent_type`; Stop adds `agent_transcript_path`, `last_assistant_message` | subagent rows |
| `SessionEnd` | `reason` | close the session; trigger ingest (option B) |

**Token cost: 0.** For `PostToolUse`, plain stdout never reaches the model. For `UserPromptSubmit` and `SessionStart` plain stdout **is
added to the context**, so the hook must print nothing (and never return `additionalContext`, `systemMessage` or exit code 2).

**Latency:** a synchronous hook blocks the tool until the process exits. At ≈ 85 tool calls per transcript, a 100 ms process start is
≈ 8–9 s per session. `"async": true` runs it in the background: 0 wait. A `dotnet run` script starts in seconds, so the hook must be
a published (Native AOT) executable or a very small script.

**Misses:** recalled memory files and `invoked_skills` content (no hook event names them; `Skill` appears only as a `PostToolUse` with the
skill name), the agent's visible text and thinking before the call, the desktop app's generated title (`session_title` is documented
for custom titles only — to verify whether the app's title counts, ⚑AT4).

**Risks:** the hook runs on every tool call of every session. A crash shows a hook error to the user (never to the model if it exits
non-2), so it must catch everything and exit 0. Hooks are a documented, stable contract.

### B. Offline parsing of the transcripts

A command reads `*.jsonl` and `*/subagents/*.jsonl` and writes the same events. Nothing runs during the session.

**Token cost: 0. Latency: 0. No change to the agent or its settings.** Works backwards: the 94 existing transcripts can be loaded on
day one (the 2026-11-12 review already counted rule reads this way, [ecosystem-fit review §1](../../agent/ecosystem-fit-review-2026-11-12.md)).

What the transcript has that hooks do not:

| Record | Field | Use |
|---|---|---|
| `custom-title` (and `<session>/custom-title.json`) | `customTitle` — the title shown in the desktop app, updated as it changes | session title |
| `agent-name` | `agentName` | session title (same text today) |
| `user` | `promptId` on prompts **and** on tool results; `turnOrigin` (`host_synthetic` = the brief from another session), `promptSource` | the question each read belongs to; side-session briefs |
| `assistant` | text and thinking blocks before the `tool_use` | purpose evidence |
| `attachment` | the table in §1 | reads without a tool |
| `<session>/subagents/agent-*.meta.json` | `agentType`, `description`, `toolUseId` (the parent's `Agent` call) | subagent purpose and parent link |

**Misses:** nothing that hooks see, except live data (the store is filled when the ingest runs).

**Risks:** the transcript format is **internal and undocumented**; a Claude Code update can rename records. Transcripts are deleted
after `cleanupPeriodDays` (Claude Code default 30 days; not set in `~/.claude/settings.json` today), so ingest must run more often than
that (⚑AT3).

### C. The agent reports its own purpose

A rule or a hook asks the agent to state why it reads each file (or each turn).

| Variant | Tokens per session (median transcript) | Behaviour change |
|---|---|---|
| C1. One line per read ("reading X to check Y") | ≈ 63 reads × 15–25 output tokens ≈ **0.9–1.6 k output tokens**, ≈ 1 % of the session's output; output tokens cost 5× input | high: the agent reads differently when it must justify each read |
| C2. One line per turn ("docs I relied on: …") | ≈ 5 × 40 ≈ **0.2 k output tokens** | medium: it reports what it thinks it used, after the fact |
| C3. Hook injects a reminder (`additionalContext`) | ≈ 30–60 **input** tokens per tool call ≈ 2–5 k per session, re-read on every later turn | high, and it also costs every subagent |
| Rule text that asks for C1/C2 | ≈ 100–150 input tokens loaded in every session and subagent | — |

The token cost is small; the problem is the **observer effect**. The user's goal is to measure how agents react to the docs, and C
changes the reaction it measures. It is also a rule the agent may skip, which is the behaviour under study. Self-report is also not
proof: the agent can name a file it never opened.

## 3. Comparison

| | A. Hooks | B. Transcripts | C. Self-report |
|---|---|---|---|
| Tokens for the agent | 0 (prints nothing) | 0 | 0.2–5 k per session |
| Influences the answer | no | no | yes |
| Latency | 0 with `async: true` | 0 | output time |
| Shell reads | yes (command line) | yes | — |
| Auto-loaded files | `CLAUDE.md`/`AGENTS.md` only | all (§1 table) | — |
| Session title | custom titles only (⚑AT4) | yes (`custom-title`) | — |
| Purpose evidence | Bash/Agent `description`, prompt | + visible text, thinking, plan step, subagent meta | stated purpose |
| History before go-live | none | all transcripts still on disk | none |
| Contract | documented | internal format | rule text |
| Setup | settings change + an executable | none | rule change |

**Recommendation (⚑AT2):** B as the engine. One async `Stop` hook (end of each turn, ≈ 5 per session) passes `transcript_path` to the
ingester, which re-parses the transcript when it changed (AT-P1 log: no byte-offset tail); `SubagentStop` does the same with `agent_transcript_path`. A desktop
session can stay open for days, so `SessionEnd` alone would ingest too late; it is still worth adding for the last turn. `ingest --all`
loads the backlog and catches anything a hook missed. The hooks print nothing and always exit 0: 0 tokens. Per-tool hooks (A) stay a
later option if the transcript format breaks or live data is wanted; the event model ([02](02-events-classification-and-purpose.md)) is
the same, so the store does not change. C is not used.
