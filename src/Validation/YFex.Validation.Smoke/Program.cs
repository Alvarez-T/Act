using System.Collections.Immutable;
using System.Globalization;
using YFex;
using YFex.Localization;
using YFex.Validation;
using YFex.Validation.BuiltIn;

// AOT smoke: generated validation, slots, constraints, RawValue formatting, catalogs, regex rule.
var ok = new Order("Ana", "ana@x.com", 3, [new Line("SKU-1", 2m)], new Money(10m));
var bad = new Order("", "nope", 99, [new Line("", 0m)], new Money(-1m));

var catalog = new MessageCatalog(BuiltInMessages.Instance);
var pt = CultureInfo.GetCultureInfo("pt-BR");

int failures = 0;
void Expect(bool condition, string what)
{
    Console.WriteLine((condition ? "ok   " : "FAIL ") + what);
    if (!condition) failures++;
}

Expect(Validator.IsValid(in ok), "valid order passes");

var result = Validator.Validate(in bad);
foreach (ref readonly Violation v in result.Violations)
    Console.WriteLine($"     {v.Path,-18} {v.Code,-10} {catalog.Format(in v, pt, "Order." + v.Path)}");
Expect(result.Violations.Count == 6, "invalid order reports 6 violations");

Expect(ValidationSlot<Order>.IsSet, "module initializer filled ValidationSlot<Order>");
var scope = ViolationWriter.Begin(out var w);
using (scope) Expect(!ValidationSlot<Order>.Validate(in bad, ref w) && w.Count == 6, "slot dispatch through function pointer");

Expect(Order.TryGetConstraints("Lines[0].Quantity", out var c) && c.Min.ToString(null, CultureInfo.InvariantCulture) == "0.001",
    "element constraints via path");

return failures == 0 ? 0 : 1;

public sealed partial record Line([Required, Pattern(@"^SKU-\d+$")] string Sku, [Range("0.001", "999")] decimal Quantity);

public sealed partial record Order(
    [Required, Length(2, 40)] string Customer,
    [Email] string Email,
    [Range(1, 48)] int Installments,
    [Each] ImmutableArray<Line> Lines,
    [Range(0, 100000)] Money Total);
