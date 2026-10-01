namespace YFex.Tests.Values;

public class OptionSetTests
{
    [Fact]
    public void Default_IsUnrestricted()
    {
        OptionSet s = default;
        s.IsEmpty.Should().BeTrue();
        s.AllowsNothing.Should().BeFalse();
        s.Contains(999).Should().BeTrue();
    }

    [Fact]
    public void Range_UsesBits_AndEnumeratesInOrder()
    {
        var s = OptionSet.Range(1, 6);
        s.Count.Should().Be(6);
        s.Contains(0).Should().BeFalse();
        s.Contains(6).Should().BeTrue();
        s.Contains(7).Should().BeFalse();

        var seen = new List<long>();
        foreach (RawValue v in s) { v.TryGet(out long l); seen.Add(l); }
        seen.Should().Equal(1, 2, 3, 4, 5, 6);

        OptionSet.Range(0, 63).Count.Should().Be(64);
    }

    [Fact]
    public void LargeValues_UseSortedArray()
    {
        var s = OptionSet.Of([1000, 10, 1000, 70]);
        s.Count.Should().Be(3);
        s.Contains(70).Should().BeTrue();
        s.Contains(71).Should().BeFalse();
        s.Contains(RawValue.From("x")).Should().BeFalse("incompatible kinds are simply not members");
    }

    [Fact]
    public void Intersect_IsMostRestrictive()
    {
        OptionSet.Range(1, 12).Intersect(OptionSet.Range(1, 6)).Should().Be(OptionSet.Range(1, 6));
        OptionSet.Unrestricted.Intersect(OptionSet.Range(2, 3)).Should().Be(OptionSet.Range(2, 3));

        var none = OptionSet.Range(1, 2).Intersect(OptionSet.Range(5, 6));
        none.IsEmpty.Should().BeFalse();
        none.AllowsNothing.Should().BeTrue();
        none.Contains(1).Should().BeFalse();

        var mixed = OptionSet.Of([3, 100, 200]).Intersect(OptionSet.Range(0, 10));
        mixed.Count.Should().Be(1);
        mixed.Contains(3).Should().BeTrue();
    }

    public enum Pay { Cash = 1, Pix = 2, Card = 3 }

    [Fact]
    public void Enums_KeepCodes_AndMatchUnderlyingValues()
    {
        var s = OptionSet.OfEnum<Pay>([Pay.Pix, Pay.Cash]);
        s.Contains(RawValue.FromEnum(Pay.Pix)).Should().BeTrue();
        s.Contains(RawValue.FromEnum(Pay.Card)).Should().BeFalse();
        s.Contains(2).Should().BeTrue();
    }
}

public class RateTests
{
    [Fact]
    public void Percent_AppliesWithRounding()
    {
        Rate r = Rate.Percent(Percentual.FromPercentage(5));
        r.Kind.Should().Be(RateKind.Percent);
        r.Apply(new Money(200m), RoundingPolicy.Currency()).Should().Be(new Money(10m));
        Rate.Percent(Percentual.FromPercentage(3.33m)).Apply(new Money(10.15m), RoundingPolicy.Currency())
            .Should().Be(new Money(0.34m)); // 0.337995 → 0.34
    }

    [Fact]
    public void Fixed_IgnoresBase()
    {
        Rate.Fixed(new Money(7m)).Apply(new Money(1000m), RoundingPolicy.Currency()).Should().Be(new Money(7m));
        default(Rate).Apply(new Money(1000m), RoundingPolicy.Currency()).Should().Be(Money.Zero);
    }

    [Fact]
    public void PatternMatching_DoesNotNeedValue()
    {
        Rate r = Rate.Fixed(new Money(3m));
        string text = r switch
        {
            PercentRate p => "p" + p.Percent,
            FixedRate f when f.Amount == new Money(3m) => "fixed 3",
            _ => "none",
        };
        text.Should().Be("fixed 3");
    }

    [Fact]
    public void RoundingPolicy_Modes()
    {
        RoundingPolicy.Currency().Round(2.345m).Should().Be(2.35m);
        RoundingPolicy.Currency(2, MidpointRounding.ToEven).Round(2.345m).Should().Be(2.34m);
        var act = () => new RoundingPolicy(-1, MidpointRounding.ToEven);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public class BoundsTests
{
    [Fact]
    public void ContainsClampIntersect()
    {
        var b = new Bounds<int>(1, 12);
        b.Contains(12).Should().BeTrue();
        b.Contains(13).Should().BeFalse();
        b.Clamp(20).Should().Be(12);
        b.Intersect(new Bounds<int>(6, 48)).Should().Be(new Bounds<int>(6, 12));
        new Bounds<int>(5, 1).IsValid().Should().BeFalse();
    }
}
