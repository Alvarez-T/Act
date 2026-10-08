using YFex.Validation;
using static YFex.Expressions.Tests.Order;

namespace YFex.Expressions.Tests;

[ExprFunctions(Category = "shop")]
public static partial class ShopFunctions
{
    [ExprFunction("memberDiscount", Description = "Percent of discount for members (5) or others (0).")]
    public static decimal MemberDiscount(bool member) => member ? 5m : 0m;

    [ExprFunction(Description = "Money rounded to tens.")]
    public static Money Tens(Money value) => new(Math.Round(value.Amount / 10m) * 10m);

    [ExprFunction("isInstant", Description = "True for instant payment methods.")]
    public static bool IsInstant(PayMethod method) => method == PayMethod.Pix;

    [ExprFunction("clock", Description = "A fixed 'now' for tests.", Deterministic = false)]
    public static DateOnly Clock() => new(2026, 10, 1);
}

[ExprFunctions]
public partial interface IShopCalendar
{
    [ExprFunction("isHoliday", Description = "Public holiday?")]
    static abstract bool IsHoliday(DateOnly date);
}

public sealed class NoHolidays : IShopCalendar
{
    public static bool IsHoliday(DateOnly date) => false;
}

/// <summary>Generated descriptors bind and run like hand-written functions (port of Rules.Dynamic GeneratedFunctionTests).</summary>
public class GeneratedFunctionTests
{
    private static readonly FunctionCatalog Catalog = FunctionCatalog.BuiltIn.With(ShopFunctions.All.AsSpan());

    [Fact]
    public void Generated_class_functions_run_with_typed_arguments()
    {
        Run(Compile("""{ "memberDiscount": [ { "var": "Member" } ] }""", Options(Catalog)), Sale(member: true)).TryGet(out decimal p).Should().BeTrue();
        p.Should().Be(5m);
        Run(Compile("""{ "tens": [ { "var": "Total" } ] }""", Options(Catalog)), Sale()).As<Money>().Should().Be(new Money(960));
        Run(Compile("""{ "isInstant": [ { "var": "Payment" } ] }""", Options(Catalog)), Sale(PayMethod.Pix)).TryGet(out bool instant).Should().BeTrue();
        instant.Should().BeTrue();
    }

    [Fact]
    public void Generated_non_deterministic_flag_reaches_the_binder()
    {
        Catalog.TryGet("clock", out var clock).Should().BeTrue();
        clock.IsDeterministic.Should().BeFalse();
        Errors("""{ "clock": [] }""", Options(Catalog) with { RequireDeterministic = true }).Should().Equal("e.clock:expr.non-deterministic");
    }

    [Fact]
    public void Generated_contracts_bind_once_implemented()
    {
        var declared = FunctionCatalog.BuiltIn.With(IShopCalendar.Contracts.AsSpan());
        Errors("""{ "isHoliday": [ { "var": "Date" } ] }""", Options(declared)).Should().Equal("e.isHoliday:expr.unimplemented");

        var implemented = FunctionCatalog.BuiltIn.With(IShopCalendar.Implement<NoHolidays>().AsSpan());
        Run(Compile("""{ "isHoliday": [ { "var": "Date" } ] }""", Options(implemented)), Sale()).TryGet(out bool h).Should().BeTrue();
        h.Should().BeFalse();
    }
}
