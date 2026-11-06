---
name: yfex-csharp
description: How YFex uses current C# and .NET features — unions vs closed hierarchies vs enums, custom non-boxing unions, extension members, generic-math value objects, collection-expression arguments, memory-safety rules, and the .NET library APIs worth using. Use when writing, reviewing or modernizing C# in any C:\Dev\YFex repository, or when choosing how to model a result, state family or value object.
---

# Current C# and .NET in YFex

You already know the syntax of these features. This file covers only YFex's choices and the traps found while compiling them. Project-kind rules are in `YFex/docs/agent/rules/`.

## Modelling a closed set

| Situation | Use |
|---|---|
| Related cases in one assembly (states, results, commands of one family) | `closed` record hierarchy. JSON: `[JsonPolymorphic(InferClosedTypePolymorphism = true)]` on the base. |
| Cases are unrelated existing types, cold path | `union` declaration. JSON: `[JsonUnion]` (writes the active case untagged). |
| Hot path, or value-type cases in infrastructure | custom `[Union]` struct: `HasValue` plus one `TryGetValue(out T)` per case, or a nested `IUnionMembers` provider interface |
| Numeric codes or flags | `enum` (closed enums are not in the language yet) |

Switch exhaustively, with no discard arm, so a new case breaks the build.

Traps:
- `closed` is not transitive. Mark intermediate bases `closed` too. It implies `abstract` and can't be combined with `sealed`, `static` or `abstract`.
- A union declaration boxes value-type cases. Never read `Value` of a custom union on a hot path.
- A custom union that exposes `HasValue` needs a `null =>` arm (a default struct is empty), otherwise CS8655.
- A provider-based custom union still needs `[Union]` on the type, otherwise CS8121.
- In a union switch, `var x` and `_` capture the union itself, not the case.
- `UnionAttribute` and `IUnion` ship in the BCL (`System.Runtime.CompilerServices`). Delete local polyfills of them.

Minimal non-boxing union:

```csharp
[Union]
public readonly struct ParseOutcome
{
    readonly int _value; readonly ParseError? _error; readonly byte _tag;
    public ParseOutcome(int value) { _value = value; _tag = 1; }
    public ParseOutcome(ParseError error) { _error = error; _tag = 2; }
    public object? Value => _tag switch { 1 => _value, 2 => _error, _ => null };   // never on hot paths
    public bool HasValue => _tag != 0;
    public bool TryGetValue(out int value) { value = _value; return _tag == 1; }
    public bool TryGetValue([NotNullWhen(true)] out ParseError? error) { error = _error; return _tag == 2; }
}
```

## Extension members

- Use them for properties, static members, operators and indexers on types you don't own, and to replace static helper classes.
- Behaviour of your own types belongs on the type.
- Constraints go on the block: `extension<T>(ReadOnlySpan<T> span) where T : IEquatable<T>`, not on its members (CS0080).
- An instance member always beats an extension. An extension indexer on `string` taking `int` is never called.

## Value objects and generic math

- Implement the operator interfaces (`IAdditionOperators<,,>`, `IComparisonOperators<,,>`, `IAdditiveIdentity<,>`…) and the span parse/format interfaces. Algorithms are then written once over `INumber<T>`-style constraints.
- User-defined compound assignment (`public void operator +=(T)`) and `++` avoid copies on mutable or large structs in hot loops.
- Numeric types to know: `Complex<T>`, `Decimal32` / `Decimal64` / `Decimal128`, `BFloat16`, `Half`, `INumberBase<T>.TryParsePartial`, `Random.NextInteger<T>`.

## Small features: when they pay off

- **`field`:** validation or normalization in an accessor without a declared backing field.
- **`a?.B = x`:** replaces `if (a is not null)` guards.
- **Partial constructors and events:** for source generators that emit the implementation.
- **`[with(capacity: n), ..items]` and `[with(comparer), ...]`:** preallocate, or pass a comparer, in collection expressions.
- **Labeled `break outer;` / `continue outer;`:** replaces flags or `goto` in nested loops.
- **Implicit span conversions:** APIs take `ReadOnlySpan<T>`, and arrays convert at the call site.
- **Non-virtual static interface members:** helpers on a contract.

## Memory-safety rules

- Opt in per project: `<Features>$(Features);updated-memory-safety-rules</Features>`.
- What changes:
  - `&`, `fixed`, `stackalloc` to a pointer, and `sizeof` no longer need `unsafe`
  - dereference, `->`, pointer indexing and function-pointer calls still do
  - `unsafe` on a member means callers must be in an unsafe context
  - `unsafe(expr)` is a new expression form
- Infrastructure projects that use pointers should adopt it. Record the decision in the repository `AGENTS.md`.

## .NET library APIs worth reaching for

- **Text:** `Base64.EncodeToUtf8` / `DecodeFromUtf8` / `EncodeToChars`, `Utf8.IndexOfInvalidSubsequence`, `Utf16.IsValid`, `Rune` overloads on `string`, `StringBuilder.MoveChunks`.
- **Collections:** `EqualityComparer<T>.Create`, and `BitArray` span constructors plus `PopCount()`. `BitArray` is a heap class, so infrastructure hot paths use `ulong` masks or `BitOperations`.
- **I/O and compression:**
  - streams: `ReadOnlyMemoryStream`, `ReadOnlySequenceStream`
  - span-based encoders: `DeflateEncoder` / `ZLibEncoder` / `GZipEncoder` and their decoders
  - Zstandard: `ZstandardStream` / `ZstandardEncoder`
- **JSON:** `Utf8JsonWriter.Reset(...)` for writer reuse, `JsonSerializerOptions.GetTypeInfo<T>()`, and `SerializeAsyncEnumerable(..., topLevelValues: true)` for JSON Lines.
- **Process:** `Process.RunAsync`, `RunAndCaptureTextAsync`, `WaitForExitStatusAsync`, `ProcessStartInfo.KillOnParentExit` (Windows only).
- **Cryptography** (only inside YFex.Cryptography): `X25519DiffieHellman`, `Aes.EncryptKeyWrap` / `DecryptKeyWrap`.
- **Runtime async:** `<Features>runtime-async=on</Features>`. Benchmark before enabling it in a library.
