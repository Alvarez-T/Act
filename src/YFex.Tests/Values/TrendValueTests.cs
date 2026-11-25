using System.Globalization;
using System.Text;
using System.Text.Json;

namespace YFex.Tests.Values;

public sealed class TrendValueTests
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    [Fact]
    public void Of_gives_absolute_relative_and_direction()
    {
        var sales = TrendValue.Of(current: 84_210m, previous: 74_920m);
        sales.Absolute.Should().Be(9_290m);
        sales.Relative!.Value.ToPercentageValue().Should().Be(12.40m);
        sales.Direction.Should().Be(TrendDirection.Up);

        TrendValue.Of(90m, 100m).Direction.Should().Be(TrendDirection.Down);
        TrendValue.Of(100m, 100m).Direction.Should().Be(TrendDirection.Flat);
    }

    [Fact]
    public void Relative_is_null_when_the_previous_value_is_zero()
    {
        var first = TrendValue.Of(current: 5m, previous: 0m);
        first.Relative.Should().BeNull();
        first.Direction.Should().Be(TrendDirection.Up);
        first.ToString().Should().Be("+5", "without a relative change it shows the absolute one");
    }

    [Fact]
    public void A_rise_from_a_negative_value_reads_as_a_positive_change()
    {
        TrendValue.Of(current: -50m, previous: -100m).Relative!.Value.ToPercentageValue().Should().Be(50m);
    }

    [Fact]
    public void Formats_relative_and_absolute_with_the_sign()
    {
        var sales = TrendValue.Of(84_210m, 74_920m);
        sales.ToString().Should().Be("+12.4 %");
        sales.ToString("R2", PtBr).Should().Be("+12,40 %");
        sales.ToString("A0", PtBr).Should().Be("+9.290");
        TrendValue.Of(96.9m, 100m).ToString().Should().Be("−3.1 %");
        TrendValue.Of(100m, 100m).ToString().Should().Be("0.0 %");
        $"{sales:A}".Should().Be("+9290");

        Span<byte> utf8 = stackalloc byte[32];
        sales.TryFormat(utf8, out var written, "R1", CultureInfo.InvariantCulture).Should().BeTrue();
        Encoding.UTF8.GetString(utf8[..written]).Should().Be("+12.4 %");
    }

    [Fact]
    public void Money_needs_the_same_currency()
    {
        TrendValue.TryOf(new Money(120m), new Money(100m), out var brl).Should().BeTrue();
        brl.Direction.Should().Be(TrendDirection.Up);
        TrendValue.TryOf(new Money(120m, Currency.USD), new Money(100m), out var mixed).Should().BeFalse();
        mixed.Should().Be(default(TrendValue));
        TrendValue.TryOf(new Quantity(3m), new Quantity(4m), out var units).Should().BeTrue();
        units.Direction.Should().Be(TrendDirection.Down);
    }

    [Fact]
    public void Small_changes_read_as_flat_under_a_threshold()
    {
        var tiny = TrendValue.Of(1001m, 1000m).WithFlatBelow(Percentual.FromPercentage(0.5m));
        tiny.Direction.Should().Be(TrendDirection.Flat);
        TrendValue.Of(1100m, 1000m).WithFlatBelow(Percentual.FromPercentage(0.5m)).Direction.Should().Be(TrendDirection.Up);
        TrendValue.Of(5m, 0m).WithFlatBelow(Percentual.FromPercentage(50m)).Direction.Should().Be(TrendDirection.Up, "no relative change: only no change is flat");
    }

    [Fact]
    public void Json_round_trips_including_a_flattened_trend()
    {
        var sales = TrendValue.Of(84_210m, 74_920m);
        var json = JsonSerializer.Serialize(sales);
        json.Should().Be("{\"previous\":74920,\"current\":84210}");
        JsonSerializer.Deserialize<TrendValue>(json).Should().Be(sales);

        var flat = TrendValue.Of(1001m, 1000m).WithFlatBelow(Percentual.FromPercentage(0.5m));
        var back = JsonSerializer.Deserialize<TrendValue>(JsonSerializer.Serialize(flat));
        back.Direction.Should().Be(TrendDirection.Flat);
        back.Should().Be(flat);
    }
}
