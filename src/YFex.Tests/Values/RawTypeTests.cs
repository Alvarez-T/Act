using System.Globalization;
using YFex.Helpers;

namespace YFex.Tests.Values;

/// <summary>App-declared types take part in RawValue through IRawValue — no library list of types.</summary>
public class RawTypeTests
{
    public readonly record struct Temperature(decimal Celsius) : IRawValue<Temperature>, IFormattable
    {
        public static RawType RawType { get; } = RawType.Define<Temperature>("test.temperature", ValueKind.Decimal);
        public RawValue ToRaw() => RawValue.Typed(RawType, Celsius);

        public static bool TryFromRaw(in RawValue raw, out Temperature value)
        {
            bool ok = raw.TryRead(RawType, out decimal c);
            value = new(c);
            return ok;
        }

        public string ToString(string? format, IFormatProvider? provider) => Celsius.ToString("0.#", provider) + " °C";
    }

    public readonly record struct Sku(string Value) : IRawValue<Sku>
    {
        public static RawType RawType { get; } = RawType.Define<Sku>("test.sku", ValueKind.Text);
        public RawValue ToRaw() => RawValue.Typed(RawType, Value);

        public static bool TryFromRaw(in RawValue raw, out Sku value)
        {
            bool ok = raw.TryRead(RawType, out string? s);
            value = new(s ?? "");
            return ok;
        }
    }

    [Fact]
    public void CustomType_RoundTrips_AndKeepsItsType()
    {
        var raw = RawValue.From(new Temperature(4.5m));

        raw.Kind.Should().Be(ValueKind.Decimal);
        raw.IsTyped.Should().BeTrue();
        raw.Type.Should().BeSameAs(Temperature.RawType);
        raw.As<Temperature>().Should().Be(new Temperature(4.5m));
        RawValueConvert.From(new Temperature(4.5m)).Should().Be(raw);
        RawValueConvert.To<Temperature>(raw).Should().Be(new Temperature(4.5m));
        RawValueConvert.TypeOf<Temperature>().Should().BeSameAs(Temperature.RawType);
    }

    [Fact]
    public void CustomType_FormatsWithItsOwnFormatter()
        => RawValue.From(new Temperature(4.5m)).ToString(null, CultureInfo.InvariantCulture).Should().Be("4.5 °C");

    [Fact]
    public void DifferentTypes_NeverEqualOrCompare_ButAcceptPlainNumbers()
    {
        var t = RawValue.From(new Temperature(5m));
        var m = RawValue.From(new Money(5m));

        t.Equals(m).Should().BeFalse();
        t.TryCompareTo(in m, out _).Should().BeFalse();
        (t < RawValue.From(8)).Should().BeTrue();                // plain number compares by amount
        m.TryGet(out Temperature _).Should().BeFalse();            // a Money is not a Temperature
        RawValue.From(7m).As<Temperature>().Should().Be(new Temperature(7m)); // plain number reads as one
    }

    [Fact]
    public void TextBackedType_IsDistinctFromPlainText()
    {
        var sku = RawValue.From(new Sku("COCA-2L"));

        sku.Kind.Should().Be(ValueKind.Text);
        sku.Equals(RawValue.From("COCA-2L")).Should().BeFalse();
        sku.As<Sku>().Value.Should().Be("COCA-2L");
        sku.ToString().Should().Be("COCA-2L");
    }

    [Fact]
    public void Parts_CarryTheType()
    {
        var raw = RawValue.From(Percentual.FromPercentage(12.5m, 1));
        raw.GetParts(out var kind, out ulong low, out ulong high, out string? text, out byte aux, out uint type);

        var back = RawValue.FromParts(kind, low, high, text, aux, type);
        back.Should().Be(raw);
        back.As<Percentual>().DecimalPlaces.Should().Be(1);
    }

    [Fact]
    public void Hash_DependsOnType()
    {
        var a = StableHash64.Create();
        RawValue.From(new Mass()).AppendTo(ref a);
        var b = StableHash64.Create();
        RawValue.From(new Length()).AppendTo(ref b);
        a.Finish().Should().NotBe(b.Finish());
    }

    [Fact]
    public void WithType_TagsPlainNumbers()
    {
        var tagged = RawValue.From(3).WithType(Money.RawType);
        tagged.Kind.Should().Be(ValueKind.Decimal);
        tagged.As<Money>().Should().Be(new Money(3m));
        tagged.Untyped.Should().Be(RawValue.From(3m));
        FluentActions.Invoking(() => tagged.WithType(Mass.RawType)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Registry_RejectsKeyCollisions()
        => FluentActions.Invoking(() => RawType.Define<Temperature>("money", ValueKind.Decimal))
            .Should().Throw<InvalidOperationException>();
}
