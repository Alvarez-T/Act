using System.Globalization;
using YFex.Helpers;

namespace YFex.Tests.Values;

public class RawValueTests
{
    public enum Payment { Cash = 0, Pix = 7, [YFex.Extensions.EnumCode("CC")] Card = 9 }
    public enum Small : byte { A = 1, B = 200 }
    public enum Signed : sbyte { Neg = -5 }

    [Fact]
    public void Default_IsNone()
    {
        RawValue v = default;
        v.IsNone.Should().BeTrue();
        v.Kind.Should().Be(ValueKind.None);
        RawValue.From((string?)null).IsNone.Should().BeTrue();
    }

    [Fact]
    public void Size_IsCompact()
    {
        System.Runtime.CompilerServices.Unsafe.SizeOf<RawValue>().Should().BeLessThanOrEqualTo(32);
    }

    [Fact]
    public void RoundTrips_EveryKind()
    {
        var date = new DateOnly(2026, 9, 29);
        var time = new TimeOnly(13, 45, 10);
        var dt = new DateTime(2026, 9, 29, 13, 45, 10, DateTimeKind.Utc);
        var guid = Guid.NewGuid();

        RawValue.From(true).TryGet(out bool b).Should().BeTrue(); b.Should().BeTrue();
        RawValue.From(80).TryGet(out int i).Should().BeTrue(); i.Should().Be(80);
        RawValue.From(long.MaxValue).TryGet(out long l).Should().BeTrue(); l.Should().Be(long.MaxValue);
        RawValue.From(12.345m).TryGet(out decimal d).Should().BeTrue(); d.Should().Be(12.345m);
        RawValue.From(1.5).TryGet(out double db).Should().BeTrue(); db.Should().Be(1.5);
        RawValue.From(date).TryGet(out DateOnly da).Should().BeTrue(); da.Should().Be(date);
        RawValue.From(time).TryGet(out TimeOnly ti).Should().BeTrue(); ti.Should().Be(time);
        RawValue.From(dt).TryGet(out DateTime dtt).Should().BeTrue(); dtt.Should().Be(dt);
        dtt.Kind.Should().Be(DateTimeKind.Utc);
        RawValue.From(guid).TryGet(out Guid g).Should().BeTrue(); g.Should().Be(guid);
        RawValue.From("abc").TryGet(out string? s).Should().BeTrue(); s.Should().Be("abc");
        RawValue.From(new Money(1000m)).TryGet(out Money m).Should().BeTrue(); m.Should().Be(new Money(1000m));
        RawValue.From(new Quantity(2.5m)).TryGet(out Quantity q).Should().BeTrue(); q.Value.Should().Be(2.5m);
        RawValue.From(Mass.FromGrams(500m)).TryGet(out Mass ma).Should().BeTrue(); ma.Grams.Should().Be(500m);
        RawValue.From(Length.FromMetres(3m)).TryGet(out Length le).Should().BeTrue(); le.Metres.Should().Be(3m);

        var pct = new Percentual(12.5m, 1);
        RawValue.From(pct).TryGet(out Percentual p).Should().BeTrue();
        p.ToPercentageValue().Should().Be(12.5m);
        p.DecimalPlaces.Should().Be(1);
    }

    [Fact]
    public void TypedReads_RejectWrongKinds()
    {
        RawValue.From("x").TryGet(out decimal _).Should().BeFalse();
        RawValue.From(1).TryGet(out DateOnly _).Should().BeFalse();
        RawValue.From(1.5).TryGet(out long _).Should().BeFalse();
        RawValue.From(long.MaxValue).TryGet(out int _).Should().BeFalse("does not fit");
        RawValue.From(new Money(1m)).TryGet(out Percentual _).Should().BeFalse("units don't convert into each other");
    }

    [Fact]
    public void PlainNumbers_ReadAsUnits()
    {
        RawValue.From(100).TryGet(out Money m).Should().BeTrue();
        m.Amount.Should().Be(100m);
        RawValue.From(10m).TryGet(out Percentual p).Should().BeTrue();
        p.ToPercentageValue().Should().Be(10m);
    }

    [Fact]
    public void Enum_StoresValueAndCode()
    {
        var pix = RawValue.FromEnum(Payment.Pix);
        pix.Kind.Should().Be(ValueKind.EnumCode);
        pix.TryGetEnum(out Payment back).Should().BeTrue();
        back.Should().Be(Payment.Pix);
        pix.TryGet(out long raw).Should().BeTrue();
        raw.Should().Be(7);
        pix.ToString().Should().Be("Pix");
        RawValue.FromEnum(Payment.Card).ToString().Should().Be("CC", "the [EnumCode] is the stable text");

        RawValue.FromEnum(Small.B).TryGet(out long bl).Should().BeTrue();
        bl.Should().Be(200, "unsigned underlying values are not sign-extended");
        RawValue.FromEnum(Signed.Neg).TryGet(out long sl).Should().BeTrue();
        sl.Should().Be(-5);
        RawValue.FromEnum(Signed.Neg).TryGetEnum(out Signed sb).Should().BeTrue();
        sb.Should().Be(Signed.Neg);
    }

    [Fact]
    public void Compare_NumericKindsByValue()
    {
        RawValue.From(5).CompareTo(RawValue.From(5L)).Should().Be(0);
        RawValue.From(5).CompareTo(RawValue.From(5.5m)).Should().BeNegative();
        RawValue.From(6.0).CompareTo(RawValue.From(5)).Should().BePositive();
        (RawValue.From(new Money(1000m)) >= RawValue.From(1000)).Should().BeTrue();
        RawValue.FromEnum(Payment.Pix).CompareTo(RawValue.From(7)).Should().Be(0);
        RawValue.None.CompareTo(RawValue.From(-100)).Should().BeNegative();
    }

    [Fact]
    public void Compare_IncompatibleKinds_Throws_AndTryCompareFails()
    {
        var act = () => RawValue.From("a").CompareTo(RawValue.From(1));
        act.Should().Throw<InvalidOperationException>();
        RawValue.From(new Money(1m)).TryCompareTo(RawValue.From(Percentual.FromPercentage(1m)), out _).Should().BeFalse();
    }

    [Fact]
    public void Equality_IsKindStrict_AndScaleInsensitive()
    {
        RawValue.From(1.0m).Should().Be(RawValue.From(1.00m));
        RawValue.From(1.0m).GetHashCode().Should().Be(RawValue.From(1.00m).GetHashCode());
        RawValue.From(1).Should().NotBe(RawValue.From(1L), "equality is kind-strict; use CompareTo for numeric equivalence");
        RawValue.From("a").Should().Be(RawValue.From("a"));
        RawValue.FromEnum(Payment.Pix).Should().Be(RawValue.FromEnum(Payment.Pix));
    }

    [Fact]
    public void Format_IsCultureAware()
    {
        var br = CultureInfo.GetCultureInfo("pt-BR");
        RawValue.From(1234.5m).ToString("N2", br).Should().Be("1.234,50");
        RawValue.From(80).ToString(null, br).Should().Be("80");
        RawValue.From(new DateOnly(2026, 9, 29)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).Should().Be("2026-09-29");

        Span<char> small = stackalloc char[2];
        RawValue.From("abc").TryFormat(small, out _).Should().BeFalse();
    }

    [Fact]
    public void Hash_IsCanonical()
    {
        Hash(RawValue.From(2.50m)).Should().Be(Hash(RawValue.From(2.5m)));
        Hash(RawValue.From(1)).Should().NotBe(Hash(RawValue.From(1L)), "kind is part of the encoding");
        Hash(RawValue.From("x")).Should().Be(Hash(RawValue.From("x")));

        static ulong Hash(RawValue v)
        {
            var h = StableHash64.Create();
            v.AppendTo(ref h);
            return h.Finish();
        }
    }
}
