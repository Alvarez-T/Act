# 11 — Performance/AOT checklist, pitfalls & tradeoffs, reference libraries

> Load for: **every phase** (skim §1 and the pitfalls relevant to your phase before coding).

---

## 1. AOT & performance checklist (review every PR against it)

- [ ] No reflection: no `GetCustomAttributes`, `Activator`, `MakeGenericType`, `Type.GetProperty`, `Enum.GetValues(Type)`.
- [ ] No `Expression<>` / `Expression.Compile` / `dynamic`. Member paths come from generators or `[CallerArgumentExpression]`.
- [ ] Rules are **static abstract** calls (inlinable) — no interfaces-as-objects on hot paths.
- [ ] Unions use the `[Union]` + `IUnion` tagged-struct pattern; library code never calls `.Value` (boxes).
- [ ] Success path of `Validate` allocates **0 bytes** (test it). Failure allocates the result array only.
- [ ] `ViolationWriter` (inline 4 + `ArrayPool` spill) is disposed on every path.
- [ ] Text rules use `ReadOnlySpan<char>`, `SearchValues<char>`, `Ascii.*`; regex only via `[GeneratedRegex]`.
- [ ] No LINQ in library code; struct enumerators; `for` over indexers/spans.
- [ ] Large structs (`Violation`, `RawValue`, situations) passed with `in`.
- [ ] `unsafe` only for `delegate*` slots and bitset scans; each use has a comment explaining why.
- [ ] Frozen/immutable lookup structures built once (`FrozenDictionary`, sorted arrays, bitsets) — never rebuilt per call.
- [ ] Decisions are deterministic (stable ordering, no hash-set iteration, decimal money, explicit rounding).
- [ ] Trim/AOT smoke app (`PublishAot=true`) builds with zero IL2xxx/IL3xxx warnings.

## 2. Pitfalls & tradeoffs

### 2.1 Attributes only accept constants
Attributes cover the common ~80% (required, lengths, ranges, patterns, simple `When` on a bool member).
Anything conditional/complex → `[Invariant]` (cross-field shape) or YFex.Rules (business). **Do not**
grow attributes into a mini-language (`[When("A > 3 && B")]` is forbidden). `decimal`/`DateOnly`/`Money`
bounds use the **string** overloads parsed at compile time.

### 2.2 Name clashes
`Required`/`Range`/`ValidationResult` also exist in `System.ComponentModel.DataAnnotations`. Our
generator reads DataAnnotations from symbols, so app code rarely needs to import both namespaces;
stacking both on one member → YFV0010. Inside YFex, `IRule<T>`/`[Rule]` belong to YFex.Rules; validation
uses `IValueRule<T>`/`[ValidateWith<T>]` (⚑P1).

### 2.3 Client/server drift — three kinds

**A) Code drift — the rule is declared twice.** A developer puts `Length(1,100)` on the VM while the
contract allows 80 → the client accepts 95 chars, the server rejects after Save.
*Fix:* declare shape rules once on the shared contract (attributes or **baseline** fluent config);
VMs borrow with `[ValidatesAs]`; analyzer YFV0023 flags looser VM rules.

**B) Version drift — an old app talks to a newer server.** App v1.2 ships `Name ≤ 80`; server v1.3
has `Name ≤ 60`. The old POS lets 70 chars through.
*Fix:* the server is always authoritative: it re-validates and returns structured violations;
`ApplyServer` shows them on the right field. Nothing breaks; the user just sees it later.

**C) Data drift — rule tables change while a device is offline** (the important one for sales):

```
09:00  POS goes offline with discount table v41 (cap 15%)
10:00  manager publishes v42 (cap 10%), effective 10:00
09:30  sale A 15% → POS evaluates with v41
10:30  sale B 15% → POS still has v41 (stale!)
11:00  POS reconnects; outbox replays A and B (each carries RulesetVersion + DecisionFingerprint)
server: Evaluate(situation, asOf: BusinessDate)
  A: 09:30 → v41 in force → same fingerprint → accepted
  B: 10:30 → v42 in force → fingerprint differs → DecisionMismatchPolicy
     (retail default AcceptAndFlag: honour what the customer was charged, audit entry)
```

Requirements this creates: published versions are **immutable and effective-dated**; clients keep the
last N versions; the server **never trusts** the client's decision — it recomputes and compares.

### 2.4 Time
Rules never read clocks. The business date is a situation field (`[RuleEffectiveDate]`) captured once when the
document is opened (client, `TimeProvider`), carried in the command, and plausibility-checked by the
server. Tests inject a fake `TimeProvider`. See 04 §2.

### 2.5 Evaluating/validating too often in the UI
- Sync field rules: cheap, run per setter (or per commit with `ValidateOn.Commit`).
- Async rules: `[Debounce]` on the property + cancellation + stale-ticket check (07 §6.3).
- Fact gathering (async): `[ReactsTo(CancelPrevious = true), Debounce]` on the loader method only.
- Decisions: sync, microseconds → no debounce; coalesce with `BeginUpdate()`; `Decision` equality by
  fingerprint avoids UI churn when nothing changed.
- Show policy separately from validate policy (`ShowErrors.AfterTouched` default) to avoid red forms on open.

### 2.6 Rule explosion
Without groups and resolution strategies, rules fight in `if` chains. Every group must declare a
strategy; tables get overlap/gap/unreachable checks at publish; the trace shows who won and why.

### 2.7 Money
Rules express **rates**; the engine computes amounts from the stage base, applies caps, rounding and
allocation to lines in one place (04 §7). Discount order is explicit via stages (items → subtotal →
payment); it affects the fiscal base (`vDesc` feeds the ICMS base), so it can never be implicit.
The **installment schedule** (splitting the final total into N installments, remainder cents in the
first installment, due dates) is a domain calculation done *after* the decision — rules only decide
the allowed N and the minimum installment value.

### 2.8 Generators
- Generators cannot see each other's output → `partial void` hooks + name conventions (03 §7).
- Nested types, collections and union-typed members are recursive work: detect cycles (YFV0012),
  cap collection nesting (YFV0014).
- Keep models equatable (no symbols) or incremental caching silently breaks → caching tests.
- Fluent parsing only understands constant, inline chains; everything else becomes runtime-only
  (YFV0004) — still enforced, just without compile-time constraints.

### 2.9 Explanations cost allocations
`TraceLevel.Off` on hot server paths; `Full` on interactive UI (per user action) and in tests. Applied
adjustments are always recorded (business data), which is cheap (exact-size array).

### 2.10 Offline approvals
A manager approving on a disconnected POS needs a device-signed token verified on replay — security
design still open (09 §6).

### 2.11 Tenants editing rules
Runtime editing (D11) adds real product surface: storage, history, permissions, publish checks,
distribution, rollback, sealed guardrails. The typed-row design keeps it additive (same evaluator,
different source), but budget the editor and publish flow as first-class features (05 §4, 08 §7).

## 3. Reference libraries (ideas only — no dependencies, D18)

| Library | Take from it | Why not depend on it |
|---|---|---|
| **Immediate.Validations** | source-generated, attribute-based, AOT-friendly design | different result model; no UI metadata, no rules engine |
| **Microsoft.Extensions.Validation** (.NET 10+) and `[OptionsValidator]` | generator-driven discovery of DataAnnotations | still evaluates through `ValidationAttribute.IsValid(object)` → boxing |
| **FluentValidation** | fluent vocabulary, rule sets/`When` ergonomics | reflection/expressions, allocations → adapter only (`YFex.Cqrs.FluentValidation`) |
| **Validot** | fast runtime specification model, low allocation | specs built at runtime; no compile-time UI constraints |
| **DMN** (standard) | decision tables & hit policies vocabulary | a standard, not a library to embed |
| **NRules / Microsoft RulesEngine** | rule/fact concepts, workflow of rule sets | expression trees + reflection; not AOT-friendly |
| **Vogen** | value-object generation ("parse, don't validate") | you already hand-write value objects in YFex.Entity.Public |
