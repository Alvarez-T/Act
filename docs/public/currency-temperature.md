# Currency and temperature values

`Currency` is an open, normalized three-letter code. It identifies a currency without embedding exchange rates or a service dependency. `Currency.Parse("usd")` returns `USD`; the default value is `BRL` for existing fiscal callers. The built-in minor-unit table covers common zero-, three-, and four-digit exceptions; applications that require a complete, dated ISO 4217 catalogue should validate codes and metadata at their service boundary.

`Money` now contains both `Amount` and `Currency`:

```csharp
var price = new Money(89.50m, Currency.USD);
var tax = new Money(7.25m, Currency.USD);
var total = price + tax;
// Currency mismatch throws; obtain an explicit exchange quote before arithmetic.
```

The one-argument constructor, decimal conversion, numeric JSON, and `RawType` continue to represent BRL. Other currencies serialize as `{ "amount": 89.50, "currency": "USD" }`. `Money.WithCurrency` relabels a numeric amount; it never performs exchange conversion. `MinorUnits` follows the currency's scale; `Cents` applies only to two-decimal currencies. UI selectors can offer a caller-defined list of currencies without a framework enum.

`Temperature` stores an absolute value in kelvins and converts through `TemperatureUnit.Celsius`, `.Fahrenheit`, and `.Kelvin`. It parses and formats unit suffixes, has XAML `TypeConverter` and JSON converter support, and rejects values below absolute zero. Absolute temperatures have no addition operator; a later temperature-difference type can model deltas without confusing the two.
