# 04 — Analysis queries

SQLite over the tables in [02 §1](02-events-classification-and-purpose.md). Run with `sqlite3 "%LOCALAPPDATA%\YFex\agent-telemetry\reads.db"`
or `agentReads query <name>` (the tool ships these as named queries, ⚑AT8). `doc` is refreshed at each ingest, so "never read" means
"in the current tree and never read".

A helper view keeps the queries short:

```sql
CREATE VIEW IF NOT EXISTS reads AS
SELECT a.*, s.title, p.text AS question, p.origin AS prompt_origin,
       CASE WHEN a.agent_id = 'main' THEN 'main' ELSE 'subagent' END AS actor,
       CASE WHEN a.category IN ('skill','skill-ref','rule','routing','atlas','agent-doc','memory','design','public-doc','other-md')
            THEN 'doc' WHEN a.category IN ('code','build') THEN 'code' ELSE 'other' END AS kind
FROM access a
JOIN session s ON s.session_id = a.session_id
LEFT JOIN prompt p ON p.prompt_id = a.prompt_id
WHERE a.verb IN ('read','search','load');
```

## 1. Docs never read

```sql
-- every rule, skill, routing and design file with no read in the period
SELECT d.category, d.repo, d.path, d.lines, d.modified_at
FROM doc d
WHERE d.category IN ('skill','skill-ref','rule','routing','atlas','agent-doc','design','public-doc')
  AND NOT EXISTS (SELECT 1 FROM reads r WHERE r.path = d.path AND r.at >= :since)
ORDER BY d.category, d.path;
```

## 2. Reads per session and per question

```sql
-- one line per session: title, questions, doc/code reads, rule and skill reads
SELECT s.title, s.started_at,
       COUNT(DISTINCT r.prompt_id)                        AS questions,
       SUM(r.kind = 'doc')                                 AS doc_reads,
       SUM(r.kind = 'code')                                AS code_reads,
       SUM(r.category = 'rule')                            AS rule_reads,
       SUM(r.category IN ('skill','skill-ref'))            AS skill_reads,
       SUM(r.actor = 'subagent')                           AS by_subagents
FROM session s LEFT JOIN reads r ON r.session_id = s.session_id
GROUP BY s.session_id ORDER BY s.started_at DESC;

-- for one question: what was read, in order, and why
SELECT r.at, r.actor, r.category, r.path, r.range, r.purpose_step, r.purpose_call, r.purpose_text
FROM reads r WHERE r.prompt_id = :prompt_id ORDER BY r.at;
```

## 3. Docs vs code

```sql
-- ratio per session and per agent type (main vs Explore, general-purpose, …)
SELECT COALESCE(g.agent_type, 'main') AS agent_type,
       SUM(r.kind = 'doc') AS doc_reads, SUM(r.kind = 'code') AS code_reads,
       ROUND(1.0 * SUM(r.kind = 'doc') / NULLIF(SUM(r.kind = 'code'), 0), 2) AS doc_per_code
FROM reads r LEFT JOIN agent g ON g.agent_id = r.agent_id
GROUP BY 1;
```

## 4. When in the session docs are read

```sql
-- position of the first rule/routing read among the session's accesses (0 = first thing the agent did)
WITH ordered AS (
  SELECT r.*, ROW_NUMBER() OVER (PARTITION BY r.session_id, r.agent_id ORDER BY r.at) - 1 AS n
  FROM reads r)
SELECT session_id, agent_id, MIN(n) AS first_rule_at_access
FROM ordered WHERE category IN ('rule','routing') GROUP BY session_id, agent_id;

-- sessions that wrote C# without opening the yfex-csharp skill
SELECT s.title FROM session s
WHERE EXISTS (SELECT 1 FROM access a WHERE a.session_id = s.session_id AND a.verb IN ('write','edit') AND a.ext = '.cs')
  AND NOT EXISTS (SELECT 1 FROM reads r WHERE r.session_id = s.session_id AND r.path LIKE '%skills/yfex-csharp/%');
```

## 5. How much of a doc is read

```sql
-- partial vs full reads per rule file, and the most read line ranges
SELECT path, SUM(partial = 0) AS full_reads, SUM(partial = 1) AS partial_reads,
       GROUP_CONCAT(DISTINCT range) AS ranges
FROM reads WHERE category IN ('rule','skill','routing') GROUP BY path ORDER BY full_reads + partial_reads DESC;
```

## 6. Purpose

```sql
-- why each rule file is opened: the call descriptions and plan steps next to it
SELECT path, purpose_call, purpose_step, COUNT(*) AS n
FROM reads WHERE category = 'rule' AND (purpose_call IS NOT NULL OR purpose_step IS NOT NULL)
GROUP BY path, purpose_call, purpose_step ORDER BY path, n DESC;

-- coverage: how many reads have each purpose level
SELECT SUM(purpose_call IS NOT NULL) AS with_call, SUM(purpose_step IS NOT NULL) AS with_step,
       SUM(purpose_text IS NOT NULL) AS with_text, COUNT(*) AS total
FROM reads;
```

## 7. Influence proxies (labelled as proxies)

```sql
-- a design doc read, then an edit in the same repo within the same question
SELECT r.path AS read_path, a.path AS edited_path, r.question
FROM reads r JOIN access a
  ON a.session_id = r.session_id AND a.prompt_id = r.prompt_id AND a.verb IN ('write','edit')
 AND a.repo = r.repo AND a.at > r.at
WHERE r.category = 'design';
```

## 8. You vs the agent

```sql
-- per session: what you put in front of the agent, what you named, what it chose on its own, what Claude Code loaded
SELECT s.title,
       SUM(r.initiator = 'user')          AS you_attached,
       SUM(r.initiator = 'user-directed') AS you_named,
       SUM(r.initiator = 'agent')         AS agent_chose,
       SUM(r.initiator = 'harness')       AS auto_loaded,
       SUM(r.initiator = 'session')       AS other_session
FROM session s JOIN reads r ON r.session_id = s.session_id
GROUP BY s.session_id ORDER BY s.started_at DESC;

-- rule and skill reads the agent chose on its own (the behaviour under study)
SELECT path, COUNT(*) AS reads, COUNT(DISTINCT session_id) AS sessions
FROM reads WHERE initiator = 'agent' AND category IN ('rule','skill','routing')
GROUP BY path ORDER BY sessions DESC;

-- your actions per session: prompts, approvals, answers, edits made outside the agent
SELECT s.title, u.kind, COUNT(*) AS n
FROM user_action u JOIN session s ON s.session_id = u.session_id
GROUP BY s.session_id, u.kind;
```

## 9. Trends

```sql
-- rule reads per week, to see whether a rule change (e.g. ecosystem-fit.md, 2026-11-12) changed behaviour
SELECT strftime('%Y-%W', at) AS week, path, COUNT(DISTINCT session_id) AS sessions
FROM reads WHERE category = 'rule' GROUP BY week, path ORDER BY week, path;
```
