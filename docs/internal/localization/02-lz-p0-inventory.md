# LZ-P0 — Literal identities and UI texts inventory (2026-11-21)

> Phase LZ-P0 of [README](README.md) §6: YFX0100 in report-only mode over every repo, a count per repo and per kind, and
> whether stage codes and field names are persisted. Done. Findings feed LZ-P7 (enforcement, ⚑LZ17 severities) and the
> pending decisions ⚑LZ39–LZ44 at the end.

## 1. How it was counted

- **Analyzer** `LiteralAnalyzer` in `YFex.Localization.Generator` (Info, report-only; ids in `AnalyzerReleases.Unshipped.md`):
  - **YFX0100**: a string literal (or a concatenation / interpolation of literals) that becomes an identity: conversion to
    the identity type, its constructor, or one of its static factories. Identity types (by metadata name, no reference to
    their libraries): `RuleCode`, `ProfileId`, `MessageKey` (+ generic), `FieldPath`, `Permission`, `Stage`, `StageRef`,
    `RuleSetRef`, `TableRef` (+ generic), `JobKey`, `JobRef`, `TenancyPath`, `ScopeIdentity`; any type can opt in with an
    attribute named `YFexIdentityAttribute`.
  - **YFX0101**: a literal shown to a person: assigned to `Title`/`Header`/`Text`/`Content`/`Caption`/`Label`/`Description`/
    `PlaceholderText`/`ToolTip`/… (string or object), passed to a parameter of that name (`text`, `content`, `description`
    only as properties: too generic as parameters), or to `AutomationProperties.SetName` / `ToolTip.SetTip`. Needs a
    lower-case letter and words (so `" WHERE "`, `"PART_x"`, `"id"` do not count).
  - Exempt: the declaration (static field / static property initializer, static expression-bodied getter), attribute
    arguments, generated code, test assemblies (name ending in `Tests`/`Test`).
- **Runner** `YFex.Localization/tools/LiteralInventory/Invoke-LiteralInventory.ps1`: builds each repo's solution with the
  analyzer injected through `CustomAfterMicrosoftCommonTargets` (no repo file changes), a per-run stamp so every project
  compiles once, one SARIF per project; a finding belongs to the repo of its file, once. Falls back to per-project builds
  when a solution does not build. Also counts XAML/AXAML literal texts (outside `Design.PreviewWith`). Areas: `test` (path
  under `tests\` or a `*.Tests` folder; AOT smokes under `tests\` count here), `sample` (app, samples, gallery, smoke,
  spike, `_ignore`), `src` (the rest). Rerun: `./Invoke-LiteralInventory.ps1 -Root C:\Dev\YFex -Out <folder>`.
- **Coverage:** 50 repos (YFex.Farma excluded: reference only), 376 project builds. Not compiled, so their C# is not counted:
  `YFex.Vsix` (Axamly; needs the VS SDK), `YFex.Finance.Desktop`, `YFex.LoggY` (its XAML is counted), `YFex.Payment` +
  tests (out of scope, to be rewritten). Pre-existing build problems worked around: NU1510/NU1902 as errors (Networking,
  Installations, Messaging), missing projects in `yfex.slnx` (`YFex.System.Tests`) and `payment.slnx`.

## 2. Counts

### 2.1 YFX0100 — literal identities (100)

| Repo | src | sample | test | Kinds |
|---|---:|---:|---:|---|
| YFex.Rules | 23 | | | FieldPath 21, RuleCode 2 |
| YFex.Validation | 6 | | | RuleCode 6 |
| YFex.Offline | 5 | | 1 | FieldPath 5; Permission 1 |
| YFex.Messaging (YFex.Rules.Admin) | 3 | | | FieldPath 3 |
| YFex.Jobs | 2 | | 40 | FieldPath 2; JobKey 16, ScopeIdentity 12, JobRef 10, FieldPath 1, RuleCode 1 (AOT smoke) |
| YFex.Cqrs | 1 | | | RuleCode 1 |
| YFex.Actors | | 9 | | TenancyPath 9 |
| YFex.Licensing | | 1 | 1 | TenancyPath |
| YFex.Localization | | 1 | | MessageKey |
| YFex.Privacy | | 1 | | TenancyPath |
| YFex.Authentication | | | 3 | TenancyPath |
| YFex.Backup, YFex.Installations, YFex.Notifications | | | 1 each | TenancyPath |
| **Total** | **40** | **12** | **48** | src: FieldPath 31, RuleCode 9 |

Every other repo: 0. No literal `MessageKey`, `Permission`, `Stage`, `RuleSetRef` or `TableRef` in src: LZ-P3/P4 removed them.

### 2.2 YFX0101 — literal UI texts in C# (179)

| Repo | src | sample | test |
|---|---:|---:|---:|
| YFex.UI | 4 | 110 (gallery) | |
| YFex.Devices | | 41 (`YFex.Devices.Manager` console menus) | |
| YFex.Security | 12 | | |
| YFex.AgentTelemetry | 4 | | |
| YFex.Authorization | 3 | | |
| YFex.Cryptography | 1 | | 3 (AOT smoke) |
| YFex.Networking | 1 (`YFex.Networking.Testing` fake) | | |
| **Total** | **25** | **151** | **3** |

### 2.3 XAML / AXAML literal texts (1 074)

| Repo | src | sample |
|---|---:|---:|
| YFex.UI | 0 (⚑LZ21 done) | 970 (gallery pages) |
| YFex.LoggY | 100 (app views; app, not a library) | |
| YFex.Axamly | | 3 |
| YFex.Finance | | 1 |

(22 hits in a restored NuGet `packages\` folder of YFex.Devices were false positives; the scan now skips `packages\`.)

## 3. The src findings, by cause

| Cause | Where | Count | Fix |
|---|---|---:|---|
| Built-in validation codes written at each use | `YFex.Validation/Fluent.cs` (`required`, `notempty`, `range`, `pattern`, `with` ×2) | 6 | declared once (⚑LZ39) |
| Engine codes as literals | `YFex.Rules` `FrozenTable` (`rules.table.unreachable`, `rules.table.overlap`), `YFex.Cqrs` `RulesStage` (`rules.decision.mismatch`) | 3 | `[Messages]` keys of the engine (⚑LZ40) |
| `FieldPath` of a DTO member as a literal | `RuleSetHostAdmin` (14), `FrozenTable`/`TableGaps` (`Rows[]`, 6), `DynamicRules` (`code`, `group`), `Rules.Admin/RuleAdminHandlers` (3), `Offline.Central/Sites` (5), `Jobs/JobSettingsProposal` (`Signals[]`, 2) | 31 | `nameof` / generated `Fields` (⚑LZ41) |
| Operator / developer texts | Security anomaly and auth-flow descriptions (12), AgentTelemetry report titles (4), `ViewLocator` "view not found" (2, UI) | 18 | decide if they are UI texts (⚑LZ42) |
| Texts a user sees | Authorization ProblemDetails titles (`Unauthorized`, `Forbidden`, `Reauthentication required`), `YComboChart` default series titles (`Bars`, `Line`), libsecret key label | 6 | keys (⚑LZ42) |
| Test helper in src | `YFex.Networking.Testing` fake adapter description | 1 | false positive: `*.Testing` projects count as test in LZ-P7 |

## 4. Are stage codes and field names persisted?

Yes, both. Dynamic rule specs (`YFex.Rules.Dynamic/Model/RuleSpec.cs`) store `RuleSpec.Group` (the stage/group code)
and `DirectiveSpec.Field` (a situation field name) in the signed rule data
of `IRuleStore`; violation codes are stored in audit records (⚑LZ3). So stage codes, situation field names and rule codes
are persisted identities: explicit once, never derived from a member name without the shipped-identities check (⚑LZ27).
⚑LZ11 stays withdrawn; output names are not stored (⚑LZ29).

## 5. What the analyzer does not see yet (input for LZ-P7)

- **String parameters that are identities** (`IRuleStore.LoadAsync(string ruleSet, …)`, `[When("Payment")]`-style attribute
  references): no identity type is involved, so YFX0100 cannot tell. Attributes that reference (not declare) an identity
  need their own rule or a typed argument.
- **UI texts through other APIs** (string-format helpers, view-model properties bound to the view): only direct
  assignments and named parameters are seen.
- **Areas:** `*.Testing` projects in `src` and AOT smokes in `tests` need an explicit rule for ⚑LZ17's severities
  (MSBuild property per project rather than path guessing).

## 6. Decisions raised (pending)

| ⚑ | Question | Recommended | Alternative |
|---|---|---|---|
| LZ39 | Built-in validation codes | one `BuiltInCodes` class in YFex.Validation (`RuleCode` statics, `[Messages]` keys for their texts), `Fluent.cs` uses them | keep the literals, exempt `Fluent.cs` |
| LZ40 | Engine rule codes (`rules.table.*`, `rules.decision.mismatch`) | `[Messages]` keys in YFex.Rules / YFex.Cqrs with texts in their language files | literals with an exemption |
| LZ41 | `FieldPath` literals naming DTO members | `nameof(...)` (or the generated `Fields`) everywhere; `Rows[]` / `Signals[]` via `FieldPath.Of(nameof(Rows)).Each()`-style helper if missing | keep literals |
| LZ42 | Which literal texts are UI texts | ProblemDetails titles, chart default titles and the libsecret label become keys; operator/developer texts (Security anomaly descriptions, AgentTelemetry reports, `ViewLocator` errors) stay English and are exempt through an attribute such as `[DeveloperText]` | localize all of them; or exempt by namespace |
| LZ43 | Apps outside libraries (YFex.LoggY views, YFex.Devices.Manager menus) | samples/apps: warning (⚑LZ17); LoggY moves to keys when it is next worked on | out of scope for LZ-P7 |
| LZ44 | Gallery texts (970 XAML + 110 C#) | warning only (sample); the gallery shows English demo content | move to keys (large, little value) |
