# YFex Atlas

A local, self-updating map of the YFex infrastructure ecosystem: what each library does, its status, who depends on whom, what is planned, and where things are inconsistent. It needs no server or internet connection.

## Use it

```bash
yfexAtlas --open      # scan C:\Dev\YFex once and open the atlas
yfexAtlas --watch     # keep it current while you work (page reloads itself every few seconds)
yfexAtlas --check     # print open findings; exit 1 when a high one is open
yfexAtlas --bundle    # also write atlas.standalone.html: ONE file with the data inside
```

**On a phone or another PC** send only `atlas.standalone.html`. Phones won't load `atlas.data.js` next to a local page, so `index.html` + data does not work there. The standalone file is a snapshot (no live reload; `file:///C:/…` links only work on this PC). Combine with `--watch` to keep it fresh, and `--out` to write into a synced folder (OneDrive/Dropbox).

`yfexAtlas` is `C:\Dev\Scripts\yfexAtlas.cs` (launcher `C:\Dev\Scripts\Exe\yfexAtlas.bat`). Open `index.html` directly from disk (`file://`).

## Files

| File | Who writes it | What it holds |
|---|---|---|
| `atlas.json` | **you / agents** | component catalogue (summary, group, status, progress, next step, open decisions, design docs), planned dependencies, curated findings, `findingStatus` overrides |
| `atlas.data.js` | scanner | everything below, loaded by `index.html` |
| `atlas.generated.json` | scanner | same data for agents and scripts |
| `index.html` | — | the viewer: Map, Mind map, Board, Findings, Projects, Docs, Activity |
| `atlas.standalone.html` | scanner (`--bundle`) | viewer + data in one file, for phones and other devices |

## What the scanner reads from disk

- every `YFex` / `YFex.*` repo: `.csproj` (target frameworks, AOT flag, project and package references with `Directory.Build.props` properties expanded), `.slnx`/`.sln` membership, C# file and line counts, newest change;
- git: branch, last commit, commits in the last 14 days, uncommitted files;
- design docs: `YFex/docs/internal/*` plus each component's `docs` list: title, `Status:` line, newest date, ⚑ flag count.

Projects map to **components** (logical libraries): the core repo by name (`YFex.Cqrs.Tenancy` → `YFex.Cqrs`), other repos by repo name, overridden by `componentRules` in `atlas.json`. Tests, samples and benchmarks never create dependency edges.

## Automatic checks (findings marked `auto`)

Component and repository cycles · core repo referencing satellite repos · libraries referencing an engine when a `.Public` package exists · OS-neutral projects referencing platform packages · broken or unresolved project references · duplicate project names · projects outside their repo's solutions · empty/obj-only folders · agent worktree copies · repos without git or with many uncommitted files · design `Status:` lines that say "not built" for code that exists · components that have code but no description in `atlas.json`.

Domain repos (status `domain`: Payment, Farma, Fiscal, Finance, Domain, Marketplace) are out of scope: hidden by default and excluded from findings.

## Keeping it true

- **Status and progress are curated**: update the component in `atlas.json` when a phase lands (agents can do this at the end of a phase).
- **Accept or close a finding**: set `"status"` on a curated finding, or add `"findingStatus": { "AUTO-NOSLN-YFex.EventSourcing": "accepted" }` for an automatic one. Values: `open`, `accepted`, `fixed`, `wontfix`.
- **New library or plan**: add a component (with `docs`) and, for plans, `plannedEdges`; it appears as a dashed node until projects exist.
