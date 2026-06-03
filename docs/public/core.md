# Core Utilities

**Libraries:** `YFex`, `YFex.Data`

## Overview

YFex core provides the foundational layer for the entire framework: high-performance collections with zero-allocation enumeration (`SpecializedDictionary`, `SpecializedConditionalWeakTable`), zero-allocation value types (`Percentual`, `Unit`, `TypeTuple`), a rich set of extension methods for enums, matching, numbers, and strings, and a fluent SQL query builder integrated with Dapper.

Every type in this layer is designed for hot-path performance. Collections use `ref struct` enumerators to avoid heap allocations. `Percentual` stores values as decimal fractions internally but exposes a percentage-based API. Enum extensions bypass boxing through `Unsafe` reinterpretation.

---

## Core Concepts

### Percentual: Fraction Storage, Percentage API

`Percentual` is a `record struct` that stores a decimal fraction internally (e.g., `0.475`) but presents a percentage-based API (e.g., `47.5%`). This design eliminates the divide-by-100 bugs that plague percentage arithmetic.

```csharp
// 47.5m.AsPercentual() stores 0.475 internally, displays as "47.5 %"
// 0.475m.ToPercentual() stores 0.475 internally, displays as "47.5 %"
// Both produce the same value — the factory method determines interpretation.
```

> **Warning:** `AsPercentual` treats the input as a display percentage (divides by 100). `ToPercentual` treats the input as a raw fraction. Mixing them up silently produces wrong results.

### SpecializedDictionary: Zero-Allocation Enumeration

`SpecializedDictionary<TKey, TValue>` provides `ref struct` enumerators, meaning `foreach` loops allocate nothing on the heap. It also exposes `GetOrAddValueRef` for in-place mutation without redundant lookups.

### EnumExtensions: Code Mapping and Zero-Boxing

Every enum in a business application needs string codes for database storage and display labels for UI. `EnumExtensions` provides both through `[EnumCode]` and `[Display]`/`[Description]` attributes, plus zero-boxing conversions via `Unsafe` reinterpretation.

---

## Step-by-Step Usage

### Percentual Arithmetic

```csharp
using Act.Utils;
using YFex.Extensions;

// Construction
var rate = 47.5m.AsPercentual();         // From display value: 47.5% -> stores 0.475
var fraction = 0.475m.ToPercentual();    // From fraction: 0.475 -> stores 0.475
var parsed = "47.5".ToPercentual();      // From string
var zero = Percentual.Zero;              // 0%
var full = Percentual.Full;              // 100%
var half = Percentual.Half;              // 50%

// Apply to values
decimal result = rate.ApplyTo(1000m);    // 475m (1000 * 0.475)
decimal increased = 1000m.IncreaseBy(rate);  // 1475m (1000 + 475)
decimal reduced = 1000m.ReduceBy(rate);      // 525m (1000 - 475)

// Reverse calculations
decimal original = 1475m.ReverseIncrease(rate); // 1000m — what was it before the increase?
```

### Enum Code Mapping

```csharp
using YFex.Extensions;

public enum OrderStatus
{
    [EnumCode("P")] Pending,
    [EnumCode("A")] Approved,
    [EnumCode("R")] Rejected,
}

// To/from database codes
string code = OrderStatus.Approved.GetCode();          // "A"
OrderStatus status = EnumExtensions.FromCode<OrderStatus>("A"); // Approved
bool found = EnumExtensions.TryFromCode<OrderStatus>("X", out var result); // false

// Display labels (resolution: [Display] -> [Description] -> member name)
string label = OrderStatus.Pending.GetLabel();         // "Pending"

// Build dropdowns
var options = EnumExtensions.GetSelectList<OrderStatus>();
// [(Pending, "Pending"), (Approved, "Approved"), (Rejected, "Rejected")]

// Zero-boxing conversions
int asInt = OrderStatus.Approved.ToInt();              // 1
OrderStatus fromInt = 1.ToEnum<OrderStatus>();         // Approved
```

### Match Extensions

```csharp
using YFex.Extensions;

// Set membership (params uses ReadOnlySpan<T> — no array allocation)
bool valid = status.IsAnyOf(OrderStatus.Pending, OrderStatus.Approved);
bool excluded = status.IsNoneOf(OrderStatus.Rejected);

// With FrozenSet for repeated lookups (O(1), no allocation per call)
var validSet = new[] { OrderStatus.Pending, OrderStatus.Approved }.ToFrozenSet();
bool inSet = status.IsIn(validSet);

// Equality checks
bool allSame = MatchExtensions.AreAllEqual(a, b, c);
bool allDifferent = MatchExtensions.AreAllDistinct(a, b, c);

// Regex matching
bool isEmail = "user@example.com".MatchesPattern(@"\w+@\w+\.\w+");

// Conditional execution
status.WhenAnyOf([OrderStatus.Pending, OrderStatus.Approved], s => Process(s));
```

### QueryBuilder Fluent SQL

```csharp
using Hino.Integrador.Infra;

var query = QueryBuilder
    .Select("c.Id", "c.Name", "c.Email")
    .From("Customers c")
    .LeftJoin("Orders o", "o.CustomerId = c.Id")
    .Where("c.IsActive = @active")
    .OrderBy("c.Name")
    .Paginar(page: 2, pageSize: 20)
    .AddParameter(new { active = true });

string sql = query.Sql;
var parameters = query.GetParameters();

// Use with Dapper
var customers = await connection.QueryAsync<Customer>(sql, parameters);
```

---

## Deep Dive

### Percentual — Full API

`Percentual` implements `IComparable<Percentual>`, `IFormattable`, `ISpanFormattable`, `ISpanParsable<Percentual>`, `IParsable<Percentual>`, and full arithmetic operator interfaces (`+`, `-`, `*`, `/`, unary `-`, comparisons).

#### Construction

| Factory Method | Input | Example | Result |
|---|---|---|---|
| `decimal.AsPercentual()` | Display percentage | `47.5m.AsPercentual()` | 47.5% |
| `double.AsPercentual()` | Display percentage | `47.5.AsPercentual()` | 47.5% |
| `int.AsPercentual()` | Display percentage | `50.AsPercentual()` | 50% |
| `decimal.ToPercentual()` | Decimal fraction | `0.475m.ToPercentual()` | 47.5% |
| `double.ToPercentual()` | Decimal fraction | `0.475.ToPercentual()` | 47.5% |
| `string.ToPercentual()` | String parse | `"47.5".ToPercentual()` | 47.5% |
| `string.TryToPercentual()` | Safe string parse | `"bad".TryToPercentual(out var p)` | false |
| `Percentual.Zero` | Static | | 0% |
| `Percentual.Full` | Static | | 100% |
| `Percentual.Half` | Static | | 50% |

> **Tip:** Pass `decimalPlaces` to any factory method to control formatting precision: `47.5m.AsPercentual(decimalPlaces: 4)` formats as `"47.5000 %"` without affecting arithmetic.

#### Arithmetic

```csharp
var a = 30m.AsPercentual();
var b = 20m.AsPercentual();

var sum = a + b;            // 50%
var diff = a - b;           // 10%
var scaled = a * 2m;        // 60%
var halved = a / 2m;        // 15%
var negated = -a;           // -30%
var complement = a.Complement(); // 70% (100% - 30%)
var clamped = a.Clamp(0m.AsPercentual(), 25m.AsPercentual()); // 25%

// Comparisons
bool greater = a > b;       // true
bool equal = a == b;        // false
```

#### Application to Values

```csharp
var rate = 20m.AsPercentual();  // 20%

// Direct application
decimal portion = rate.ApplyTo(500m);          // 100m

// Increase / reduce
decimal after = 500m.IncreaseBy(rate);         // 600m
decimal reduced = 500m.ReduceBy(rate);         // 400m

// Reverse calculations (what was the original before the % was applied?)
decimal before = 600m.ReverseIncrease(rate);   // 500m
decimal beforeRed = 400m.ReverseReduction(rate); // 500m

// Compound growth
decimal balance = 1000m.CompoundGrowth(5m.AsPercentual(), periods: 12); // 1795.86m
```

#### Analysis

```csharp
// Percentage change
var change = 120m.PercentChangeFrom(100m);     // +20%
var drop = 80m.PercentChangeFrom(100m);        // -20%

// Share of total
var share = 30m.AsPercentageOf(120m);          // 25%

// Reverse: portion from percentage
decimal portion = 25m.AsPercentual().PortionOf(120m); // 30m

// Absolute difference (unsigned, order-independent)
var diff = 100m.AbsolutePercentDifference(120m); // 20%
```

#### Aggregation

```csharp
var rates = new[] { 10m.AsPercentual(), 20m.AsPercentual(), 30m.AsPercentual() };

var avg = rates.Average();                     // 20%
var sum = rates.Sum();                         // 60%
var max = rates.MaxPercentual();               // 30%
var min = rates.MinPercentual();               // 10%

// Weighted average
var weights = new[] { 1m, 2m, 3m };
var weighted = rates.WeightedAverage(weights); // (10*1 + 20*2 + 30*3) / 6 = 23.33%

// Distribution: each value as % of total
var values = new[] { 100m, 200m, 300m };
var dist = values.ToPercentualDistribution();
// [16.67%, 33.33%, 50.00%]
```

#### Formatting

```csharp
var rate = 47.5m.AsPercentual();

rate.ToString();                    // "47.50"
rate.ToStringWithSymbol();          // "47.50 %"
rate.ToSignedString();              // "+47.50 %"
rate.ToDisplay(decimalPlaces: 4);   // "47.5000 %"
rate.ToFractionString();            // "0.475" (for JSON/CSV export)
rate.SignPrefix();                  // "+" (or "-" or "" for zero)

// Implements IFormattable — works with string interpolation
string formatted = $"Rate: {rate:N4}";

// Boolean helpers
rate.IsZero;      // false
rate.IsFull;      // false
rate.IsPositive;  // true
rate.IsNegative;  // false
```

> **Note:** `Percentual` has built-in `TypeConverter` and `System.Text.Json` converter support. It serializes/deserializes automatically in ASP.NET model binding and JSON payloads.

---

### EnumExtensions — Full API

| Method | Description | Allocates? |
|---|---|---|
| `GetCode<T>()` | Returns `[EnumCode]` value or member name | No (cached) |
| `HasCode<T>()` | Whether member has `[EnumCode]` | No |
| `FromCode<T>(string)` | Parse code to enum (throws if not found) | No |
| `TryFromCode<T>(string?)` | Safe parse, returns `null` on failure | No |
| `GetCodeMap<T>()` | All (value, code) pairs | One-time |
| `GetLabel<T>()` | `[Display]` > `[Description]` > name | No (cached) |
| `GetSelectList<T>()` | All (value, label) pairs for dropdowns | One-time |
| `ToInt<T>()` | Zero-boxing int conversion | No |
| `ToLong<T>()` | Zero-boxing long conversion | No |
| `ToByte<T>()` | Zero-boxing byte conversion | No |
| `ToEnum<T>(int)` | Int to enum (throws if undefined) | No |
| `ToEnumOrNull<T>(int)` | Safe int to enum | No |
| `IsDefined<T>()` | Whether value is a defined member | No |
| `HasAllFlags<T>()` | All bits of flag are set | No |
| `HasAnyFlag<T>()` | Any bit of flags is set | No |
| `WithFlag<T>()` | Returns value with flag set | No |
| `WithoutFlag<T>()` | Returns value with flag cleared | No |
| `ToggleFlag<T>()` | Returns value with flag toggled | No |
| `GetFlags<T>()` | Decompose `[Flags]` into individual bits | Lazy |
| `GetValues<T>()` | All defined values (cached) | One-time |
| `When<T>()` | Execute action on match, return value | No |

```csharp
[Flags]
public enum Permission
{
    [EnumCode("R")] Read = 1,
    [EnumCode("W")] Write = 2,
    [EnumCode("X")] Execute = 4,
}

var perms = Permission.Read | Permission.Write;

perms.HasAnyFlag(Permission.Execute);      // false
perms.HasAllFlags(Permission.Read | Permission.Write); // true
var withExec = perms.WithFlag(Permission.Execute);     // Read | Write | Execute
var noWrite = perms.WithoutFlag(Permission.Write);     // Read

// Decompose
foreach (var flag in perms.GetFlags())
    Console.WriteLine(flag); // Read, Write

// Zero-boxing: no object allocation
int bits = perms.ToInt();                  // 3
var restored = 3.ToEnum<Permission>();     // Read | Write
```

---

### MatchExtensions — Full API

| Method | Description |
|---|---|
| `IsAnyOf(T, params T[])` | True if value equals any option |
| `IsAnyOf(T, IEqualityComparer, params T[])` | With custom comparer |
| `IsNoneOf(T, params T[])` | True if value equals none |
| `IsIn(T, FrozenSet<T>)` | O(1) lookup in pre-built set |
| `IsIn(T, IReadOnlySet<T>)` | Generic set overload |
| `AreAllEqual(params T[])` | All elements equal |
| `AreAllDistinct(params T[])` | No duplicates (stack-alloc for <= 8) |
| `EqualsTo(T?, T)` | Null-safe equality |
| `MatchesPattern(string?, string)` | Regex match (case-insensitive default) |
| `MatchesPattern(string?, Regex)` | Pre-compiled regex |
| `WhenAnyOf(T, T[], Action)` | Execute action on match |
| `MatchAnyOf(T, T[], Func, T)` | Project on match, fallback otherwise |

> **Tip:** `IsAnyOf` and `IsNoneOf` accept `ReadOnlySpan<T>` via `params`, so the compiler stack-allocates the array when the values are constants. For hot paths with a fixed set, prefer `IsIn(frozenSet)`.

---

### NumberExtensions

```csharp
using YFex.Extensions;

42.IsBetween(1, 100);             // true (inclusive: 1 <= 42 <= 100)
3.14.IsStrictlyBetween(3, 4);     // true (exclusive: 3 < 3.14 < 4)
amount.IsPositive();              // true if > 0
count.IsZero();                   // true if == 0
```

---

### StringExtensions

```csharp
using YFex.Extensions;

"".IsEmpty();            // true (handles null, empty, whitespace)
"hello".IsNotEmpty();    // true
"2024-01-01".ToDateTime(); // DateTime(2024, 1, 1)
```

---

### Collections

#### SpecializedDictionary\<TKey, TValue\>

High-performance dictionary optimized for the framework's internal dispatch tables. The `ref struct` enumerator means `foreach` produces zero heap allocations.

```csharp
using YFex.Collections;

var dict = new SpecializedDictionary<TypeTuple, Handler>();

// In-place mutation: get a ref to the slot, assign directly
ref var slot = ref dict.GetOrAddValueRef(key);
slot = handler;

// Zero-allocation enumeration
foreach (var entry in dict) // Enumerator is a ref struct
{
    TypeTuple key = entry.GetKey();
    Handler value = entry.GetValue();
}
```

> **Note:** Because the enumerator is a `ref struct`, you cannot use LINQ on `SpecializedDictionary`. Use `foreach` loops for iteration.

#### SpecializedConditionalWeakTable\<TKey, TValue\>

Weak-reference table with zero-allocation enumeration and lock-free reads. The event bus uses this internally for weak subscriptions that don't prevent GC of subscribers.

#### ArrayPoolBufferWriter\<T\>

`IBufferWriter<T>` + `IMemoryOwner<T>` backed by `ArrayPool<T>`. Returns the rented array on `Dispose`.

```csharp
using YFex.Collections;

using var writer = new ArrayPoolBufferWriter<byte>(initialCapacity: 256);
var span = writer.GetSpan(10);
// write to span...
writer.Advance(10);
ReadOnlySpan<byte> data = writer.WrittenSpan;
```

#### RefArrayPoolBufferWriter\<T\>

Stack-allocated (`ref struct`) version for short-lived temporary buffers:

```csharp
using var buf = RefArrayPoolBufferWriter<int>.Create();
buf.Add(1);
buf.Add(2);
ReadOnlySpan<int> items = buf.Span;
// buf is Disposed here, returning the array to the pool
```

#### PagedList\<T\> / ObservablePagedCollection\<T\>

```csharp
using YFex.Primitives.Collections;

// Static pagination over an existing collection
var page = PagedList<Order>.Create(orders, page: 1, pageSize: 20);
page.HasNextPage;   // bool
page.TotalPages;    // int

// Observable variant with async loading, navigation, and loading state
var collection = new ObservablePagedCollection<Order>(fetchPage);
await collection.LoadAsync();
await collection.NextPageAsync();
collection.IsLoading;    // bool — bind to a spinner
collection.PageSummary;  // "2 / 5 (47 items)" — bind to a label
```

---

## Common Patterns

### Tax Calculation with Percentual

```csharp
var taxRate = 18m.AsPercentual();       // 18%
var price = 100m;

var tax = taxRate.ApplyTo(price);       // 18m
var total = price.IncreaseBy(taxRate);  // 118m

// Customer shows you a receipt for 118 — what was the pre-tax price?
var original = total.ReverseIncrease(taxRate); // 100m

// Compound tax scenario: apply multiple rates
var stateTax = 10m.AsPercentual();
var federalTax = 5m.AsPercentual();
var combined = stateTax + federalTax;   // 15%
var totalTax = combined.ApplyTo(price); // 15m
```

### Enum-to-Database Mapping

```csharp
public enum PaymentMethod
{
    [EnumCode("CC")]  [Display(Name = "Credit Card")]  CreditCard,
    [EnumCode("DC")]  [Display(Name = "Debit Card")]   DebitCard,
    [EnumCode("PIX")] [Display(Name = "PIX Transfer")] Pix,
    [EnumCode("BOL")] [Display(Name = "Bank Slip")]    BankSlip,
}

// Store in DB
string dbValue = payment.GetCode();           // "CC"

// Read from DB
var method = EnumExtensions.FromCode<PaymentMethod>(row["PaymentCode"]);

// Build dropdown
var options = EnumExtensions.GetSelectList<PaymentMethod>();
// [(CreditCard, "Credit Card"), (DebitCard, "Debit Card"), ...]

// Map all codes at startup for batch lookups
IReadOnlyDictionary<PaymentMethod, string> map =
    EnumExtensions.GetCodeMap<PaymentMethod>();
```

### Fluent SQL Query Building

```csharp
// Build a base query
var baseQuery = QueryBuilder
    .Select("c.Id", "c.Name", "c.Email", "c.CreatedAt")
    .From("Customers c")
    .Where("c.IsActive = @active")
    .AddParameter(new { active = true });

// Copy and extend for different use cases
var withOrders = baseQuery.Copy()
    .LeftJoin("Orders o", "o.CustomerId = c.Id")
    .Select("COUNT(o.Id) AS OrderCount")
    .GroupBy("c.Id", "c.Name", "c.Email", "c.CreatedAt")
    .OrderBy("OrderCount DESC");

// Pagination
var paged = withOrders.Copy()
    .Paginar(page: 3, pageSize: 25);

// Subquery
var vipQuery = QueryBuilder
    .Select("c.Id", "c.Name")
    .From("Customers c")
    .Where("c.TotalSpent > @threshold")
    .AddParameter(new { threshold = 10000m });

// Union
var combined = baseQuery.Copy()
    .UnionAll(vipQuery);
```

### Zero-Allocation Dictionary Iteration

```csharp
// When you need to iterate a hot-path dictionary without GC pressure
var handlers = new SpecializedDictionary<TypeTuple, Delegate>();

// Populate
ref var slot = ref handlers.GetOrAddValueRef(new TypeTuple(typeof(MyEvent), typeof(string)));
slot = myHandler;

// Iterate — the foreach loop allocates nothing
foreach (var entry in handlers)
{
    var messageType = entry.GetKey().TMessage;
    var handler = entry.GetValue();
    // dispatch...
}
```

---

## Reference Summary

### Percentual Factory Methods

| Method | Input Type | Interpretation |
|---|---|---|
| `decimal.AsPercentual()` | `decimal` | Display percentage (47.5 -> 47.5%) |
| `double.AsPercentual()` | `double` | Display percentage |
| `int.AsPercentual()` | `int` | Display percentage |
| `decimal.ToPercentual()` | `decimal` | Decimal fraction (0.475 -> 47.5%) |
| `double.ToPercentual()` | `double` | Decimal fraction |
| `string.ToPercentual()` | `string` | Parse ("47.5" -> 47.5%) |
| `string.TryToPercentual()` | `string?` | Safe parse |
| `Percentual.Zero` | static | 0% |
| `Percentual.Full` | static | 100% |
| `Percentual.Half` | static | 50% |

### Percentual Application Methods

| Method | Description | Example |
|---|---|---|
| `ApplyTo(decimal)` | Multiply value by percentage | `20%.ApplyTo(500)` = 100 |
| `IncreaseBy(decimal, Percentual)` | Add percentage of value | `500.IncreaseBy(20%)` = 600 |
| `ReduceBy(decimal, Percentual)` | Subtract percentage of value | `500.ReduceBy(20%)` = 400 |
| `ReverseIncrease(decimal, Percentual)` | Original before increase | `600.ReverseIncrease(20%)` = 500 |
| `ReverseReduction(decimal, Percentual)` | Original before reduction | `400.ReverseReduction(20%)` = 500 |
| `PercentChangeFrom(decimal, decimal)` | Signed % change | `120.PercentChangeFrom(100)` = 20% |
| `AsPercentageOf(decimal, decimal)` | Part as % of total | `30.AsPercentageOf(120)` = 25% |
| `PortionOf(Percentual, decimal)` | Amount for % of total | `25%.PortionOf(120)` = 30 |
| `CompoundGrowth(decimal, Percentual, int)` | Compound interest | `1000.CompoundGrowth(5%, 12)` |

### EnumExtensions Methods

| Method | Returns | Allocates? |
|---|---|---|
| `GetCode<T>()` | `string` | No |
| `FromCode<T>(string)` | `TEnum` | No |
| `TryFromCode<T>(string?)` | `TEnum?` | No |
| `GetLabel<T>()` | `string` | No |
| `GetSelectList<T>()` | `IReadOnlyList<(T, string)>` | One-time |
| `GetCodeMap<T>()` | `IReadOnlyDictionary<T, string>` | One-time |
| `ToInt<T>()` / `ToLong<T>()` / `ToByte<T>()` | `int` / `long` / `byte` | No |
| `ToEnum<T>(int)` | `TEnum` | No |
| `HasAnyFlag<T>()` / `HasAllFlags<T>()` | `bool` | No |
| `WithFlag<T>()` / `WithoutFlag<T>()` / `ToggleFlag<T>()` | `TEnum` | No |
| `GetFlags<T>()` | `IEnumerable<TEnum>` | Lazy |

### MatchExtensions Methods

| Method | Description |
|---|---|
| `IsAnyOf(T, params T[])` | Value equals any option (stack-allocated span) |
| `IsNoneOf(T, params T[])` | Value equals none of the options |
| `IsIn(T, FrozenSet<T>)` | O(1) frozen set lookup |
| `AreAllEqual(params T[])` | All elements are equal |
| `AreAllDistinct(params T[])` | All elements are unique |
| `EqualsTo(T?, T)` | Null-safe equality |
| `MatchesPattern(string?, string)` | Regex match |
| `WhenAnyOf(T, T[], Action<T>)` | Conditional execution |
| `MatchAnyOf(T, T[], Func, T)` | Conditional projection |
