# Rules for infrastructure and cross-cutting projects

These rules add to [all-projects](all-projects.md).

## Domain neutrality

- These libraries serve very different apps. No retail, pharmacy, fiscal or payment concept in a contract. `Money`, `Tenant`, `Store` and sales appear only in tests and samples.
- App value types enter through an extension point, such as a static-abstract interface in core. Never use hard-coded type lists or `typeof` chains.
- Use an enum only when the library owns the complete set (Severity, HitPolicy). When an app could add a member (scope levels, tiers), the app declares the names.
- Design for many scenarios, never for one deployment. Each library detects per host at run time and always offers a manual override in configuration.

## Core holds types, libraries hold behaviour

- YFex core gets types and contracts other boundaries must name, never engines. For example, `Hash256` is in core, while hashing is in Cryptography.

## Cross-platform

- Code works on Windows, Linux and macOS. Core has no OS code. A main package uses only BCL behaviour available on all three. OS integration goes in `.Windows`, `.Linux` and `.MacOS` packages.
- In each design, list the per-OS differences that matter: key storage, TLS support, firewall and local-network permissions, service host, file modes and atomic writes. Treat unverified platform behaviour as a spike.

## AOT, trimming and generators

- Library code is AOT- and trim-clean: zero IL warnings. New projects set `IsAotCompatible` and get an AOT smoke app. JSON uses System.Text.Json source generation; P/Invoke uses `LibraryImport`.
- Use source generators and precomputed metadata instead of reflection, dynamic dispatch and runtime discovery.
- Also use a generator wherever it removes repetition or boilerplate and leaves the caller a short, readable declaration. Prefer one attribute or marker on a type over hand-written glue in every consumer. When the type also has an aggregate configuration, offer the same declaration as a builder verb the generator reads ([ecosystem-fit](ecosystem-fit.md) §1).
- Generators are separate `netstandard2.0` projects, follow [source-generators](../../internal/source-generators.md), and report misuse as diagnostics, not silence.

## Dependencies and naming

- Avoid third-party packages. Accept one only when it is free, narrowly scoped and clearly cheaper than building it, and keep it behind an optional package.
- Reuse YFex libraries and core instead of rebuilding what they do. Each possible integration is a pending decision for the user ([ecosystem-fit](ecosystem-fit.md)); keep the reference optional when only some users need it.
- Libraries do not reference the logging engine; they probe `YFex.Diagnostics` in core. Libraries ship hosted services through `Add…` extensions, never their own executable: one service host runs per machine.
- `YFex.<Engine>` names are reserved for YFex.Data database providers. A bridge between libraries is named for the bridge (`YFex.Persistence.Data`), not the engine.
- New repositories use `src/`, `tests/`, `app/`, `benchmark/`, `docs/`, a `<short>.slnx` and an `AGENTS.md`.
- Security and network libraries never include offensive features (spoofing, brute force, evading security tools).

## Performance

- Find the hot path first and measure with BenchmarkDotNet and allocation tests. Don't optimize what isn't hot, and keep it readable unless a measured requirement says otherwise.
- On hot paths:
  - no LINQ, boxing, closures, delegates, intermediate collections or repeated materialization
  - prefer `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>` and arrays
  - preallocate known capacities
- Pool (`ArrayPool<T>`, object pools, buffer reuse) only when reuse shows a measured gain.
- Use `readonly struct` and `ref struct` where value semantics fit. Avoid large structs and needless copies.
- Prefer compact representations (bit flags, `ulong` masks, `BitOperations`) and zero-copy processing. Think about memory layout, cache locality and algorithmic complexity.
- `unsafe`, `MemoryMarshal`, `CollectionsMarshal`, `Unsafe` and `stackalloc` are allowed with a concrete, measured justification, and only after the safe optimized APIs. You remain responsible for bounds, buffer ownership, lifetimes and thread safety.
- Discriminated results on hot paths use a custom `[Union]` struct read through `TryGetValue` / `HasValue`. Never read its `Value`. The `union` declaration boxes value-type cases, so it suits only cold paths.
- Specialized helpers, pools and collections stay `internal`.

## Stored and wire formats

- A byte format that is persisted, signed, hashed or sent over the wire changes only with a new version tag and a golden test.

## Gates before claiming success

- Release build, tests, AOT smoke publish with zero IL warnings, and the allocation tests where the repository has them.
