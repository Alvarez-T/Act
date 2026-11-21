# YFex agent rules: start here

These rules apply to every repository under `C:\Dev\YFex`. Repository `AGENTS.md` files add only what is specific to that repository.

## 1. Classify the project before applying rules

- **Infrastructure / cross-cutting:** a reusable library with no business meaning, usable by any app (core types, data access, messaging, security, logging, time, UI controls…). Read [all-projects](all-projects.md) + [infrastructure](infrastructure.md).
- **Domain:** business modules and the apps built on them. The code names business concepts (customer, sale, invoice, drug, payment) or a country's rules. Read [all-projects](all-projects.md) + [domain](domain.md).
- **App / tool / sample:** executables, samples, smoke tests, benchmarks, galleries, developer tools. Read [all-projects](all-projects.md). Infrastructure rules apply only to the library code they call.

**If the kind is unclear, a project mixes kinds, or a change would move code between kinds, ask the user before writing code.**

Designing a library, a phase or a feature, or changing what a library does, in any kind of project: also read [ecosystem-fit](ecosystem-fit.md). Every integration with another library and every better placement becomes a pending ⚑ decision for the user.

Writing C#: load the `yfex-csharp` skill (`YFex/docs/agent/skills/yfex-csharp/SKILL.md`). Building or testing: [build-and-test](build-and-test.md).

## 2. Dependency direction

- Framework layers, no cycles: core → Localization → Validation → Persistence → Expressions / State → Rules → Cqrs → Messaging → NavigatR → Mvvm.
- Core references no sibling repository. Infrastructure never references a domain project. Domain may reference any infrastructure.
- Ownership and boundaries between libraries: [infrastructure-map](../infrastructure-map.md).

## 3. Working method

- Current source wins over design docs and phase notes. Find symbols with the RoslynMcp tools (`get_names`, `get_feature_slice`, `get_minimal_context_for`). Never infer an API from a design example.
- Validate nontrivial C# with `dry_run_compile`, then build the affected projects.
- In a multi-step feature, each step only has to build. Write tests, AOT smoke calls and benchmarks as the final step, unless the user asks for them earlier. Before calling the feature done, run the gates of its project kind. Report a targeted check as targeted, not as a full build.
- Before changing a public API, show the caller's code, the dependency direction and the effect on consumers.
- When proposing a design or explaining a change, show concrete before/after code with realistic examples, including the cons, and get agreement before building.
- Leave unrelated in-progress edits alone. Stage only your own files.
- Git: commit directly on `main`, no feature branches, only when the user asks. This overrides any tool default to branch first. Push only on an explicit request.
