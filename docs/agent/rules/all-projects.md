# Rules for every YFex project

## Types

- Prefer value objects over primitives for identities, codes, quantities and measures. Reuse an existing YFex type before creating one. A type that more than one library must name goes to YFex core.
- Use the BCL's numeric types before writing your own: `Complex<T>`, `Decimal32` / `Decimal64` / `Decimal128`, `Int128`, `Half`, `BFloat16`.
- A value object is a `readonly struct` (a record class only when reference semantics matter). It validates in its constructor or `TryCreate` and carries the methods its purpose needs (formatting, conversion, arithmetic, comparison).
- Implement what fits the value:
  - `IEquatable<T>`, plus `IComparable<T>` when ordering is meaningful
  - `ISpanParsable<T>`, `IUtf8SpanParsable<T>`, `ISpanFormattable`, `IUtf8SpanFormattable`
  - operators and generic-math interfaces (`IAdditionOperators<,,>`, `IComparisonOperators<,,>`…) when the arithmetic is meaningful
  - a `JsonConverter`, attached with a hand-written `[JsonConverter]` on the type (generators cannot see each other's output)
  - `TypeConverter` only when a consumer needs it (UI binding, configuration binding). It relies on `TypeDescriptor` and is not trim-safe.
- YFex classification goes once on the type, never per member: `[Sensitive]` for personal data, `ILogValue<T>` for log output, `ICanonicalValue<T>` for bytes that are signed or hashed, `IRawValue<T>` / `IAmount<T>` for rule values.
- Names are spelled out (`Operation`, not `Op`). Qualify generic words that clash with business terms (`IPAddressScope`, not `AddressScope`).
- Model closed sets so that a new case breaks the build: a union, a `closed` hierarchy, or an enum. Switch over them exhaustively, without a discard arm.

## String identities and texts (no magic strings)

A string that identifies something (a code, key, id, name or path that is stored, sent, looked up or matched) is an identity. A string shown to a person is a text. Neither is typed by hand at the places that use it.

- Declare each identity once: in its declaring attribute or catalog member (`[Permission("sales.close")]`, `[Rule("promo.l3p2")]`, `[Message("stock.out", …)]`). Every other use goes through a symbol: a generated property or handle (`SalePermissions.Close`, `SaleText.StockOut`, `CloseSale.Fields.CustomerId`, `SaleSituation.Fields.Installments`), a generated `Ids.X` constant in attribute arguments, or `nameof(...)`.
- Never write a string literal, interpolation or concatenation where an identity is expected: message keys and rule codes, rule set / table / situation names, permission ids, field paths and field names, cache, store, setting, route and event keys. When the API only accepts `string`, add the typed overload or the generated symbol in the owning library; don't write the literal and don't build a private constants class that repeats it.
- Identities in public APIs are value objects (`RuleCode`, `MessageKey`, `FieldPath`, `Permission`), not `string`. Only text that really arrives as text (database, wire, tenant edits) is parsed, with `TryCreate` / `Parse`, at the boundary.
- An identity that is stored, signed or sent keeps its explicit literal in the declaration, so renaming the C# symbol never changes it.
- Text shown to people is never a literal in running code. Declare it with its key (neutral-culture text on the declaration, translations in culture files), pass typed arguments, and format it through the message catalog at the edge. Logs carry code + arguments, not texts.
- Lists of names whose meaning the reader must guess (`string[] AmountFields => ["Total"]`) are not an API. Mark the member itself with an attribute the generator reads, or take the generated symbol.
- Exempt: tests of parsing and formatting, the declaration itself, and developer-only exception messages.
- Generators and analyzers enforce this: a library that introduces an identity ships its generated symbols and a diagnostic for literals. Design: [localization](../../internal/localization/README.md) (⚑LZ, pending).

## Encapsulation and YAGNI

- Use the most restrictive access modifier. Types default to `internal`. `public` is only for an intentionally supported API consumed across assemblies.
- Helpers, infrastructure components and implementation details stay `internal`, even when they support public features.
- Design for current requirements. No abstractions, layers, mediators, repositories or events for hypothetical needs or test indirection.

## Interfaces and packages

- Create an interface only for a concrete need: several implementations, an optional engine or provider seam, a contract crossing a module boundary, or a declared extension point. A single implementation gets no interface.
- Ship a library as one package. Extract a `.Public` contracts package only when another library must reference the contracts without the engine, for example to avoid a cycle or a heavy dependency.
- A `.Public` package holds contracts and passive data only: no logic, calculation or engine detail. The dependency runs engine → `.Public`, never the reverse.

## Time, secrets and personal data

- Inject `TimeProvider`. Never read the machine clock directly, and never use it alone for a security decision; trusted time comes from YFex.Time. Schedules use IANA zone ids and computed UTC.
- Cryptographic primitives (`AesGcm`, `HMACSHA*`, `ECDsa`, `RSA`, key derivation…) are used only inside YFex.Cryptography. Everyone else calls its APIs.
- Never log or return a secret, code or personal value in clear. Mask it through its type's classification.

## Dependency injection

- Use constructor injection for business dependencies and for services whose lifetime the consumer manages.
- Controlled, `internal` service resolution is allowed for infrastructure dependencies when injection would only add boilerplate. A typical case is a public base class that would otherwise force derived classes to forward dependencies only the base uses. It is the exception, not the default.
- Never use a service locator to hide a business dependency. Never expose `IServiceProvider` or container details in a public API.
- Respect lifetimes: never resolve scoped or disposable services from the root provider. Keep resolution points few and explicit.
- AOT: `GetService<T>()` and generated or static registration are fine. Never resolve open generics by reflection or scan assemblies.

## Module boundaries (modular monolith)

- Draw boundaries by business capability or cohesive responsibility, not by technical layer (Controllers, Services, Repositories, Helpers). Inside a module, group code by feature or use case (vertical slices).
- A module never touches another module's internals: repositories, entities, persistence models or services. Cross a boundary only through explicit public contracts (an API, a command, a query or an event).
- Share only reusable technical primitives and stable contracts, never business logic owned by one module.
- Enforce boundaries with assemblies, `internal`, explicit references and architecture tests.
- Modules run in-process. Split into separate services only for a demonstrated need to deploy or scale independently.

## API shape

- Keep public APIs ergonomic and stable; internal implementations may be optimized and change freely. Never expose an optimization (pool, buffer, specialized collection) through a public contract without a reason.
- Classification and behaviour come from types and conventions, not attributes repeated on every member.

## Comments

- XML `<summary>` docs on public types and members whose purpose isn't obvious from the name, especially generator-driven ones (attributes, partial members, generated helpers). Show usage with a short `<example><code>` in the style of CommunityToolkit.Mvvm. Skip them where the name already says everything (`CustomerId Id`).
- Inline comments only to explain *why*: a path chosen over an obvious alternative, code that can't be made clear, or a deliberate break from a rule.
- Banned: comments that restate the code, and separator or "region" comments (`// ---------`, `// ── Section ──`, `#region`).

## Language level

- Projects currently use the newest installed .NET SDK and `LangVersion` preview. The repository's `global.json` and `Directory.Build.props` are the source of truth. Use current C# and .NET features (see the `yfex-csharp` skill).
- When you work in code that still uses an older pattern, or a polyfill the SDK now ships (`UnionAttribute`, `IUnion`…), update it as part of the change, or report it when that is out of scope.
