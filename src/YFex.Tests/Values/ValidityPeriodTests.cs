using System.Globalization;
using System.Text.Json;
using YFex.Diagnostics;

namespace YFex.Tests.Values;

public sealed class ValidityPeriodTests
{
    private static readonly DateTimeOffset Ten = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Twelve = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Contains_is_half_open()
    {
        var period = new ValidityPeriod(Ten, Twelve);
        period.Contains(Ten).Should().BeTrue();
        period.Contains(Ten.AddHours(1)).Should().BeTrue();
        period.Contains(Twelve).Should().BeFalse();
        period.Contains(Ten.AddTicks(-1)).Should().BeFalse();
        period.IsPending(Ten.AddTicks(-1)).Should().BeTrue();
        period.IsExpired(Twelve).Should().BeTrue();
        period.IsExpired(Twelve.AddTicks(-1)).Should().BeFalse();
    }

    [Fact]
    public void Open_ended_never_expires()
    {
        var period = ValidityPeriod.Starting(Ten);
        period.IsOpenEnded.Should().BeTrue();
        period.Duration.Should().BeNull();
        period.Remaining(Ten.AddYears(50)).Should().BeNull();
        period.IsExpired(DateTimeOffset.MaxValue).Should().BeFalse();
        period.Contains(Ten.AddYears(50)).Should().BeTrue();
    }

    [Fact]
    public void Remaining_and_duration()
    {
        var period = ValidityPeriod.For(Ten, TimeSpan.FromHours(2));
        period.Until.Should().Be(Twelve);
        period.Duration.Should().Be(TimeSpan.FromHours(2));
        period.Remaining(Ten.AddMinutes(30)).Should().Be(TimeSpan.FromMinutes(90));
        period.Remaining(Twelve.AddHours(5)).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void End_must_be_after_start()
    {
        var empty = () => new ValidityPeriod(Ten, Ten);
        var reversed = () => new ValidityPeriod(Twelve, Ten);
        empty.Should().Throw<ArgumentOutOfRangeException>();
        reversed.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Overlaps_and_cap_end()
    {
        var morning = new ValidityPeriod(Ten, Twelve);
        morning.Overlaps(new ValidityPeriod(Twelve, Twelve.AddHours(1))).Should().BeFalse();
        morning.Overlaps(new ValidityPeriod(Ten.AddHours(1), Twelve.AddHours(1))).Should().BeTrue();
        morning.Overlaps(ValidityPeriod.Starting(Ten.AddHours(-5))).Should().BeTrue();

        ValidityPeriod.Starting(Ten).CapEnd(Twelve).Should().Be(morning);
        morning.CapEnd(Twelve.AddDays(1)).Should().Be(morning);
    }

    [Fact]
    public void Equality_compares_instants_not_offsets()
    {
        var utc = new ValidityPeriod(Ten, Twelve);
        var brasilia = new ValidityPeriod(Ten.ToOffset(TimeSpan.FromHours(-3)), Twelve.ToOffset(TimeSpan.FromHours(-3)));
        brasilia.Should().Be(utc);
        brasilia.GetHashCode().Should().Be(utc.GetHashCode());
    }

    [Fact]
    public void Text_form_is_iso_interval_and_round_trips()
    {
        var closed = new ValidityPeriod(Ten, Twelve);
        closed.ToString().Should().Be("2026-10-02T10:00:00.0000000+00:00/2026-10-02T12:00:00.0000000+00:00");
        ValidityPeriod.Parse(closed.ToString()).Should().Be(closed);

        var open = ValidityPeriod.Starting(Ten);
        open.ToString().Should().Be("2026-10-02T10:00:00.0000000+00:00/..");
        ValidityPeriod.Parse(open.ToString()).Should().Be(open);

        ValidityPeriod.TryParse("2026-10-02T12:00:00Z/2026-10-02T10:00:00Z", CultureInfo.InvariantCulture, out _).Should().BeFalse();
        ValidityPeriod.TryParse("nonsense", CultureInfo.InvariantCulture, out _).Should().BeFalse();
    }

    [Fact]
    public void Binary_form_is_sixteen_bytes_and_round_trips()
    {
        Span<byte> bytes = stackalloc byte[ValidityPeriod.BinarySize];
        var closed = new ValidityPeriod(Ten, Twelve);
        closed.TryWriteBytes(bytes).Should().BeTrue();
        BitConverter.ToInt64(bytes[..8]).Should().Be(Ten.UtcTicks);
        ValidityPeriod.TryReadBytes(bytes, out var read).Should().BeTrue();
        read.Should().Be(closed);

        ValidityPeriod.Starting(Ten).TryWriteBytes(bytes).Should().BeTrue();
        BitConverter.ToInt64(bytes[8..]).Should().Be(0);
        ValidityPeriod.TryReadBytes(bytes, out read).Should().BeTrue();
        read.IsOpenEnded.Should().BeTrue();

        ValidityPeriod.TryReadBytes(bytes[..15], out _).Should().BeFalse();
    }

    [Fact]
    public void Json_uses_the_text_form()
    {
        var closed = new ValidityPeriod(Ten, Twelve);
        var json = JsonSerializer.Serialize(closed);
        JsonDocument.Parse(json).RootElement.GetString().Should().Be("2026-10-02T10:00:00.0000000+00:00/2026-10-02T12:00:00.0000000+00:00");
        JsonSerializer.Deserialize<ValidityPeriod>(json).Should().Be(closed);
    }

    [Fact]
    public void Logs_as_its_text_form()
    {
        Span<byte> buffer = stackalloc byte[256];
        var writer = new LogWriter(buffer);
        var period = new ValidityPeriod(Ten, Twelve);
        LogValueWriterFor<ValidityPeriod>.Write.Should().NotBeNull("core registers ValidityPeriod in its module initializer");
        LogValueWriterFor<ValidityPeriod>.Write!(in period, ref writer, 0);
        writer.FieldCount.Should().Be(1);
    }
}
