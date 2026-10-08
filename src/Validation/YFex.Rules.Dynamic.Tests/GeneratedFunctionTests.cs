using static YFex.Rules.Dynamic.Tests.Shop;

namespace YFex.Rules.Dynamic.Tests;

/// <summary>App functions written in C# — the generator emits descriptors + adapters.</summary>
[ExprFunctions(Category = "shop")]
public static partial class ShopFunctions
{
    [ExprFunction("bulkPrice", Description = "Unit price after the bulk tier: 5% off from 10 units, 10% from 50.")]
    public static Money BulkPrice(Money unitPrice, int quantity) => unitPrice * (quantity >= 50 ? 0.9m : quantity >= 10 ? 0.95m : 1m);

    [ExprFunction(Description = "True for payment methods settled instantly.")]
    public static bool IsInstant(PayMethod method) => method is PayMethod.Pix or PayMethod.Card;
}

/// <summary>A contract a library declares; each domain app supplies the calendar.</summary>
[ExprFunctions(Category = "calendar")]
public partial interface ICalendarFunctions
{
    [ExprFunction("isHoliday", Description = "True when the date is a public holiday where the sale happens.")]
    static abstract bool IsHoliday(DateOnly date);
}

public sealed class TestCalendar : ICalendarFunctions
{
    public static bool IsHoliday(DateOnly date) => date is { Month: 12, Day: 25 } or { Month: 1, Day: 1 };
}

public class GeneratedFunctionTests
{
    [Fact]
    public void Generated_descriptors_carry_signatures_and_descriptions()
    {
        ShopFunctions.All.Select(f => f.Signature).Should().Equal(
            "bulkPrice(unitPrice: money, quantity: int32) → money",
            "isInstant(method: enum) → bool");
        ShopFunctions.All[1].Name.Should().Be("isInstant");                  // camelCase default
        ShopFunctions.All[0].Category.Should().Be("shop");
        ICalendarFunctions.Contracts.Single().IsContract.Should().BeTrue();
        ICalendarFunctions.Implement<TestCalendar>().Single().IsContract.Should().BeFalse();
    }

    [Fact]
    public void Rules_call_app_functions_and_implemented_contracts()
    {
        var functions = FunctionCatalog.BuiltIn.With(ShopFunctions.All.AsSpan()).With(ICalendarFunctions.Implement<TestCalendar>().AsSpan());
        var (host, _) = Host(functions);
        Publish(host, """
            { "code": "tenant.instant-holiday", "group": "Promo",
              "when": { "and": [ { "isInstant": [ { "var": "Payment" } ] }, { "isHoliday": [ { "var": "$effectiveDate" } ] } ] },
              "then": [ { "decrease": { "target": "total", "percent": 5 } } ] }
            """);
        Publish(host, """
            { "code": "tenant.bulk", "group": "LineDisc",
              "when": { "<": [ { "bulkPrice": [ { "line": "UnitPrice" }, { "line": "Quantity" } ] }, { "line": "UnitPrice" } ] },
              "then": [ { "decrease": { "target": "line", "percent": 5 } } ] }
            """);

        var christmas = Sale(PayMethod.Pix, lines: [new ShopLine("water", "drinks", 12, new Money(2))]) with { Date = new DateOnly(2026, 12, 25) };
        var d = host.Evaluate(christmas);
        d.Adjustments.AsSpan().ToArray().Select(a => a.Rule.Code).Should().BeEquivalentTo("tenant.bulk", "tenant.instant-holiday");

        host.Evaluate(christmas with { Payment = PayMethod.Cash }).Adjustments.AsSpan().ToArray()
            .Select(a => a.Rule.Code).Should().Equal("tenant.bulk");
    }

    [Fact]
    public void A_contract_without_implementation_is_rejected_where_it_would_run()
    {
        var (host, _) = Host(FunctionCatalog.BuiltIn.With(ICalendarFunctions.Contracts.AsSpan()));
        var spec = """{ "code": "x", "group": "Promo", "when": { "isHoliday": [ { "var": "$effectiveDate" } ] }, "then": [ { "warn": {} } ] }""";
        host.ValidateRule("x", Tenant, Json(spec)).Violations.AsSpan()[0].Code.Value.Should().Be(DynamicRuleCodes.Unimplemented);
    }
}
