# 03 — YFex.Validation.Generator

> Load for: **Phase 1** (sections 1–6, 8, 9) and **Phase 2** (section 7 + file 07).
> Project: `YFex.Validation/src/YFex.Validation.Generator` — `netstandard2.0`, copy
> `YFex.State.Generator.csproj` (Roslyn 4.11.0 pinned, `EnforceExtendedAnalyzerRules`,
> `EmitCompilerGeneratedFiles` to `Generated/`). Reuse the `EquatableArray<T>` pattern from
> `YFex.State.Generator/EquatableArray.cs`. Bundled as analyzer of `YFex.Validation`.

---

## 1. Inputs (what the generator discovers)

| # | Input | Discovery |
|---|---|---|
| 1 | Types with `[Validatable]` | `ForAttributeWithMetadataName("YFex.Validation.ValidatableAttribute")` |
| 2 | Types whose members carry any YFex.Validation rule attribute (or DataAnnotations equivalents) | syntax provider on attribute lists → containing type; dedupe with #1 |
| 3 | `[Invariant]` methods | `ForAttributeWithMetadataName` → containing type |
| 4 | Fluent `.Validation(v => …)` inside `IAggregateConfiguration<T>.Configure` bodies | syntax provider: classes implementing `YFex.Cqrs.IAggregateConfiguration<>`; walk invocations (09 §2) |
| 5 | `[ValidatesAs<T>]` on StateObject VMs | `ForAttributeWithMetadataName` (07) |
| 6 | Obsolete `[ValidateWith(typeof(X))]` / `[ValidateAsync(typeof(X))]` from YFex.State | same as #2 (migration) |

All transforms produce **equatable models** (strings, `EquatableArray`, primitive records) — never
`ISymbol`/`SyntaxNode` in the model — so incremental caching works (verify with a caching test).

## 2. Output per validatable type — attribute example

Input (a DTO or UI model — for CQRS messages the fluent form in §4 is the convention, D14):

```csharp
[Validatable]
public sealed partial record CustomerDto
{
    [Required, Length(2, 80)]           public string Name { get; init; } = "";
    [Required, Email]                   public string Email { get; init; } = "";
    [Range(0, 150)]                     public int Age { get; init; }
    [Required, ValidateWith<CpfOrCnpj>] public string Document { get; init; } = "";
    [Each]                              public ImmutableArray<AddressDto> Addresses { get; init; }  // AddressDto is [Validatable]

    [Invariant]
    static void MinorCannotHaveCnpj(in CustomerDto c, ref ViolationWriter w)
    {
        if (c.Age < 18 && c.Document.Length == 14)
            w.Add(Fields.Document, "customer.minor-cnpj");
    }
}
```

Generated `CustomerDto.Validation.g.cs` (shape is normative; formatting free):

```csharp
partial record CustomerDto : global::YFex.Validation.IValidatable<CustomerDto>
{
    /// Member ids — dense, declaration order. Used by ValidationBag and ValidateMember.
    public static class FieldIds
    {
        public const ushort Name = 0, Email = 1, Age = 2, Document = 3, Addresses = 4;
    }

    /// Ready-made paths (no allocation when adding violations).
    public static class Fields
    {
        public static readonly global::YFex.FieldPath Name = global::YFex.FieldPath.Of("Name");
        public static readonly global::YFex.FieldPath Email = global::YFex.FieldPath.Of("Email");
        public static readonly global::YFex.FieldPath Age = global::YFex.FieldPath.Of("Age");
        public static readonly global::YFex.FieldPath Document = global::YFex.FieldPath.Of("Document");
        public static readonly global::YFex.FieldPath Addresses = global::YFex.FieldPath.Of("Addresses[]");
    }

    /// Per-member metadata + checks. EVERY public settable/init member gets a class here, even with
    /// no rules (empty Check) — other generators (State) call these by name convention and cannot
    /// see this output, so the members must always exist.
    public static class Rules
    {
        public static class Name
        {
            public const bool Required = true;
            public const int MinLength = 2;
            public const int MaxLength = 80;
            public static global::YFex.Validation.FieldConstraints Constraints { get; } =
                global::YFex.Validation.FieldConstraints.Create(required: true, minLength: 2, maxLength: 80);

            /// Checks one value of this member. `path` lets callers re-target (VM field, indexed element).
            public static bool Check(string? value, ref global::YFex.Validation.ViolationWriter w, in global::YFex.FieldPath path)
            {
                if (string.IsNullOrWhiteSpace(value)) { w.Add(in path, "required"); return false; }
                if ((uint)(value.Length - 2) > 78u)
                { w.Add(in path, "len", global::YFex.RawValue.From(2), global::YFex.RawValue.From(80)); return false; }
                return true;
            }
        }
        public static class Age
        {
            public const int Min = 0, Max = 150;
            public static global::YFex.Validation.FieldConstraints Constraints { get; } = …;
            public static bool Check(int value, ref global::YFex.Validation.ViolationWriter w, in global::YFex.FieldPath path)
            {
                if ((uint)value > 150u) { w.Add(in path, "range", global::YFex.RawValue.From(0), global::YFex.RawValue.From(150)); return false; }
                return true;
            }
        }
        public static class Document
        {
            public const bool Required = true;
            public static global::YFex.Validation.FieldConstraints Constraints { get; } = …(required: true, format: "br.document");
            public static bool Check(string? value, ref global::YFex.Validation.ViolationWriter w, in global::YFex.FieldPath path)
            {
                if (string.IsNullOrWhiteSpace(value)) { w.Add(in path, "required"); return false; }
                if (!global::YFex.Entity.CpfOrCnpj.IsValid(value.AsSpan())) { w.Add(in path, global::YFex.Entity.CpfOrCnpj.Code); return false; }
                return true;
            }
        }
        // Email, Addresses …
    }

    /// Invariant ids (OwnerId in ViolationOrigin) and the members each one reads (inferred, §3).
    public static class InvariantIds { public const ushort MinorCannotHaveCnpj = 1; }
    internal static global::System.ReadOnlySpan<ushort> __Reads_MinorCannotHaveCnpj => [FieldIds.Age, FieldIds.Document];

    public static bool Validate(in CustomerDto v, ref global::YFex.Validation.ViolationWriter w)
    {
        int errorsBefore = w.ErrorCount;
        Rules.Name.Check(v.Name, ref w, in Fields.Name);
        Rules.Email.Check(v.Email, ref w, in Fields.Email);
        Rules.Age.Check(v.Age, ref w, in Fields.Age);
        Rules.Document.Check(v.Document, ref w, in Fields.Document);
        // [Each] over a validatable element type: indexed, prefixed paths "Addresses[i].City"
        var addresses = v.Addresses;
        for (int i = 0; i < addresses.Length; i++)
            global::My.AddressDto.ValidateAt(in addresses.ItemRef(i), ref w, Fields.Addresses.WithIndex(i));

        // Invariants run last and ONLY when none of their inputs already failed (avoids noise).
        if (!w.HasErrorFor(in Fields.Age) && !w.HasErrorFor(in Fields.Document))
        {
            var prev = w.CurrentOrigin;
            w.CurrentOrigin = new(global::YFex.Validation.ViolationSource.Invariant, InvariantIds.MinorCannotHaveCnpj);
            MinorCannotHaveCnpj(in v, ref w);
            w.CurrentOrigin = prev;
        }
        // Runtime-only rules registered by fluent config with non-constant args (§4.3) — null check when none.
        global::YFex.Validation.RuntimeRules<CustomerDto>.Run(in v, ref w);
        return w.ErrorCount == errorsBefore;
    }

    /// Same as Validate but every path is prefixed (used when this type is nested/element of another).
    public static bool ValidateAt(in CustomerDto v, ref global::YFex.Validation.ViolationWriter w, in global::YFex.FieldPath prefix) { … }

    public static bool ValidateMember(global::YFex.Validation.FieldId member, in CustomerDto v, ref global::YFex.Validation.ViolationWriter w)
        => member.Value switch
        {
            FieldIds.Name => Rules.Name.Check(v.Name, ref w, in Fields.Name),
            FieldIds.Age => Rules.Age.Check(v.Age, ref w, in Fields.Age),
            // …
            _ => true,
        };

    public static bool TryGetConstraints(global::System.ReadOnlySpan<char> path, out global::YFex.Validation.FieldConstraints c)
    {
        switch (path)
        {
            case "Name": c = Rules.Name.Constraints; return true;
            case "Age": c = Rules.Age.Constraints; return true;
            case "Document": c = Rules.Document.Constraints; return true;
        }
        // nested: "Addresses[3].City" → strip "Addresses[n]." and delegate
        if (path.StartsWith("Addresses[")) return global::My.AddressDto.TryGetConstraints(__AfterIndexer(path), out c);
        c = default; return false;
    }

    public static bool TryGetFieldId(global::System.ReadOnlySpan<char> path, out global::YFex.Validation.FieldId id) => …; // same switch → ids
}
```

Rules for emission:
- Members are validated in **declaration order**; within a member, rules in attribute order; a
  member stops at its first Error (one message per rule chain, like the UI expects) — `Severity.Warning`
  rules don't stop the chain.
- `When = nameof(IsCompany)` wraps that rule: `if (v.IsCompany) { … }`.
- `Profile = "final"` rules are emitted into `Validate(in v, ref w, ProfileId profile)` overload
  (`ProfileId` = generated `byte` id per profile name used in the compilation; `ProfileId.All` = 0)
  branches; `Validate(in v, ref w)` = all profiles (server always validates everything the command declares).
- `[Pattern]` → emits a cached `internal static readonly Regex __Pattern_<n>` (CultureInvariant, 1 s timeout).
  *(Implementation note: `[GeneratedRegex]` cannot be used — the Regex generator never sees another
  generator's output. See 13 "Implementation log".)*
- Struct types: `in` parameters everywhere; records/classes: `in` still used (no copy of the reference, harmless).
- Collections: pick the fastest indexer shape available (`ImmutableArray<T>.ItemRef`, `T[]`,
  `List<T>` via `CollectionsMarshal.AsSpan`, `IReadOnlyList<T>` indexer, `StateList<T>` indexer).

## 3. Invariants — how inputs are inferred

The generator walks the invariant method body and collects member accesses:
- static invariant `(in CustomerDto c, …)`: every `c.X` member access → `X`;
- instance invariant on a StateObject: every implicit/explicit `this.X` property access → `X`.

If the body calls other methods of the type / passes `c` or `this` to another method, the generator
cannot know what they read → marks the invariant **reads-all** (re-run on any change) and reports
info `YFV0009`. `[Invariant(Reads = [...])]` overrides inference; if the body reads members not in
the list → warning `YFV0009`.

Supported signatures:

```csharp
static void X(in T v, ref ViolationWriter w)          // records/structs/DTOs/commands
void X(ref ViolationWriter w)                          // StateObject instance (VM)
static bool X(in T v, ref ViolationWriter w)           // bool return allowed (ignored)
```

Anything else → error `YFV0008`.

## 4. Fluent configuration as a source (D14, ⚑P2)

### 4.1 What is parsed

```csharp
public sealed class CustomerBaseline : IAggregateConfiguration<Customer>
{
    public void Configure(AggregateConfigurationBuilder<Customer> b)
        => b.Command<Customer.Commands.CreateCustomer, CustomerId>()
            .Validation(v =>
            {
                v.Field(c => c.Name).Required().Length(2, 80);
                v.Field(c => c.Document).Required().With<CpfOrCnpj>();
                v.Field(c => c.Age).Range(0, 150);
                v.Each(c => c.Addresses);                          // element type validated with its own rules
                v.Invariant(static (in Customer.Commands.CreateCustomer c, ref ViolationWriter w) =>
                {
                    if (c.Age < 18 && c.Document.Length == 14) w.Add(Customer.Commands.CreateCustomer.Fields.Document, "customer.minor-cnpj");
                });
            });
}
```

The generator finds `.Validation(<lambda>)` whose receiver chain contains
`.Command<TCmd…>()` / `.Query<TQuery, …>()`, then reads each statement of the lambda:
`v.Field(<simple member lambda>)` followed by a chain of known rule calls
(`Required, NotEmpty, Length, Range, Pattern, Email, With<T>, Each, When, Severity, Code, Profile`).

### 4.2 What is emitted

Exactly the same members as §2, emitted **into the message type's partial declaration**
(`CreateCustomer.Rules.Name.MaxLength`, `Validate`, `TryGetConstraints`…). Therefore
`[ValidatesAs<CreateCustomer>]`, the UI projection and the typed Cqrs slot work identically whether
rules came from attributes or fluent config.

### 4.3 What stays runtime-only

- Non-constant arguments (`Length(2, options.MaxName)`) → the rule is still enforced (the builder
  registers it into `RuntimeRules<T>` at registry build), but no compile-time const/constraint is
  emitted → info `YFV0004`. The UI still receives it through the runtime part of `TryGetConstraints`
  (generated method falls back to `RuntimeRules<T>.TryGetConstraints`).
- Fluent `Invariant(...)` lambdas → registered at runtime into `RuntimeRules<T>` (the generator cannot
  inline a lambda from another method). The generator still infers their reads for the UI.
- Message type not `partial` → everything runtime-only + warning `YFV0003`.

`RuntimeRules<T>` is a static generic holder (`static InvariantFn<T>[]? s_rules`) filled once by the
compiled registry; `Run` is a null check when empty.

### 4.4 Merging

Attribute + fluent on the same member → rules are **unioned**; the same rule with different
arguments → error `YFV0011`. Config layers (09 §3): baseline rules are shared; a client/server
override may only **add** rules or **tighten** (analyzer `YFV0023` when loosening).

## 5. `Required` inference (⚑P5)

Off by default. MSBuild `<YFexValidationInferRequired>true</YFexValidationInferRequired>` makes
non-nullable reference members (`string`, not `string?`) implicitly `[Required]` (info `YFV0030`
listing inferred members). Value types are never inferred.

## 6. Diagnostics

| Id | Severity | Message |
|---|---|---|
| YFV0001 | Error | Type '{0}' declares validation but is not `partial` |
| YFV0002 | Error | Rule '{0}' is not applicable to member type '{1}' (e.g. `Length` on `int`) |
| YFV0003 | Warning | Message '{0}' has fluent validation but is not `partial`; metadata is runtime-only |
| YFV0004 | Info | Fluent rule on '{0}' has non-constant arguments; enforced at runtime only (no compile-time constraints) |
| YFV0005 | Error | '{0}' does not implement a supported rule interface for '{1}' (`IValueRule<T>`, `IValueRule<T,TArg>`, `ITextRule`, `ITextRule<TArg>`, `IAsyncValueRule<T>`) |
| YFV0006 | Error | Range bounds invalid / unparsable for type '{0}' / min > max |
| YFV0007 | Error | Pattern is not a valid regular expression |
| YFV0008 | Error | Invariant '{0}' has an unsupported signature |
| YFV0009 | Warning/Info | Invariant '{0}': reads could not be inferred / explicit `Reads` disagrees with body |
| YFV0010 | Warning | Member '{0}' has both DataAnnotations and YFex.Validation attributes for the same rule |
| YFV0011 | Error | Member '{0}' declares rule '{1}' twice with different arguments (attribute vs fluent) |
| YFV0012 | Error | Cycle in nested validatable graph: {0} |
| YFV0013 | Warning | Member named '{0}' clashes with generated nested class; generated class renamed to `Validation{0}` |
| YFV0014 | Error | More than 4 nested collection levels in path '{0}' |
| YFV0015 | Error | `When` member '{0}' missing or not `bool` |
| YFV0020–YFV0024 | — | `[ValidatesAs]` diagnostics (07 §4.6) |
| YFV0025 | Warning | view-model rules that cannot run on change (not a writable `[Observable]` of the class; inherited contract match) |
| YFV0030 | Info | Required inferred from nullability for: {0} |

Keep the existing YFex.State `YFEX0301` until the obsolete `[ValidateWith(typeof)]` path is removed.

## 7. Coordination with YFex.State.Generator (⚑P6 → D22, implemented in Phase 2)

**Roslyn generators cannot see each other's output.** Coordination uses `partial void` hooks and
naming conventions only:

1. **YFex.State.Generator**: for every writable, non-Task-notifier `[Observable]` member of a `StateObject` subclass it
   always emits, after `NotifyChanged(...)`:

   ```csharp
   __OnValidate_Name(in value);                               // call in the setter
   partial void __OnValidate_Name(in string value);           // declaration (no body); `in` avoids copying large structs
   ```

   plus, per class, the batch hook reached through a virtual (a private partial method cannot be called by the runtime):

   ```csharp
   protected override void OnUpdateCompleted() { base.OnUpdateCompleted(); __OnBatchValidated(); }
   partial void __OnBatchValidated();
   ```

   `StateObject.OnUpdateCompleted()` runs when the outermost `BeginUpdate()` scope ends, before the batched
   notifications. Unimplemented partial void methods are **removed by the compiler** → zero cost for VMs without
   validation. The old `Validate_X` emission is deleted.
2. **YFex.Validation.Generator** implements those hooks for VMs that have rules (own attributes, `[Invariant]`,
   `[ValidatesAs]`, obsolete typeof validators) — the body writes into the State `ValidationBag` (07 §2) and calls
   `CreateCustomer.Rules.Name.Check(value, ref w, in path, profile)` by convention (every member has both `Check`
   overloads, §2). It may only implement hooks the State generator declares (CS0759 otherwise), so
   `Parser.HasStateHook` mirrors the State rule exactly (writable `[Observable]`, top-level partial class, not a Task
   notifier). The exact type spelling is the same in both generators (fully qualified + nullable annotations).
3. Hooks are private to the declaring class: rules for an inherited property live on the class that declares it
   (YFV0025 when a derived VM's contract matches an inherited property). Derived VMs chain submit validation,
   constraints and Touch through protected virtual `__vValidateAll` / `__vTryGetConstraints` / `__vTouch`.
4. Detecting "is a StateObject": check the base-type chain for `YFex.State.StateObject` by metadata name (string) — no
   project reference from the generator to YFex.State is needed.

## 8. Tests (Phase 1)

Follow the conventions of `YFex.Cqrs.SourceGenerator.Test` / `YFex.State.Tests`:
1. **Emission snapshots** for: attributes, DataAnnotations, fluent (constant), fluent (non-constant),
   nested + `[Each]`, invariants (inferred / explicit / reads-all), `When`, `Profile`, struct type.
2. **Compile + run**: generated code compiles with zero warnings; behaviour tests per built-in rule.
3. **Diagnostics**: one test per YFV id.
4. **Incremental caching**: second run with an unrelated edit → steps report `Cached`/`Unchanged`.
5. **Allocation test**: `Validate` of a valid 5-member object allocates 0 bytes
   (`GC.GetAllocatedBytesForCurrentThread`), same approach as `YFex.State.Tests/Performance/AllocationTests.cs`.
6. **AOT smoke**: a tiny console published with `PublishAot=true` validating a type (no trim warnings).

## 9. Performance targets (guidance, measure with BenchmarkDotNet in a separate bench project)

- Valid 5-field object: no allocation; tens of ns.
- Invalid: one allocation for the result array (plus pooled spill beyond 4 violations).
- `TryGetConstraints`: compiled `switch` on span; no allocation.
