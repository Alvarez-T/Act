using YFex.Canonical;
using YFex.Time;

namespace YFex.Tests.Time;

public class TrustedInstantTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Plain_time_provider_is_flagged_unanchored_and_unauthenticated()
    {
        var now = TimeProvider.System.GetTrustedNow();

        now.Flags.Should().Be(TimeFlags.Unanchored | TimeFlags.Unauthenticated);
        now.Source.Should().Be(TimeSourceKind.System);
        now.Uncertainty.Should().Be(TrustedInstant.UnknownUncertainty);
        now.IsAnchored.Should().BeFalse();
        now.IsAuthenticated.Should().BeFalse();
        now.IsUsableForSecurity(TimeSpan.FromMinutes(10)).Should().BeFalse();
    }

    [Fact]
    public void Trusted_clock_answers_for_itself()
    {
        var expected = new TrustedInstant(Monday, TimeSpan.FromMilliseconds(20), TimeSourceKind.CentralAnchor, TimeSpan.FromMinutes(3),
            TimeFlags.None);
        TimeProvider time = new StubTrustedClock(expected);

        time.GetTrustedNow().Should().Be(expected);
    }

    [Theory]
    [InlineData(TimeFlags.None, 20, true)]                                                    // authenticated, small uncertainty
    [InlineData(TimeFlags.None, 20 * 60_000, false)]                                          // authenticated, too uncertain
    [InlineData(TimeFlags.RollbackDetected, 20, true)]                                        // anchored: the device clock does not matter
    [InlineData(TimeFlags.Unauthenticated, 20, false)]                                        // NTP only (⚑T4)
    [InlineData(TimeFlags.Unanchored | TimeFlags.Unauthenticated, 20, false)]                 // offline start, no floor
    [InlineData(TimeFlags.Unanchored | TimeFlags.Unauthenticated | TimeFlags.AuthenticatedFloor, 20, true)]  // offline start (⚑T11)
    [InlineData(TimeFlags.Unauthenticated | TimeFlags.AuthenticatedFloor, 20, true)]          // NTP answered later: floor still holds
    [InlineData(TimeFlags.Unanchored | TimeFlags.AuthenticatedFloor | TimeFlags.RollbackDetected, 20, false)]
    public void Usable_for_security_follows_T11(TimeFlags flags, int uncertaintyMilliseconds, bool usable)
    {
        var now = new TrustedInstant(Monday, TimeSpan.FromMilliseconds(uncertaintyMilliseconds), TimeSourceKind.CentralAnchor,
            TimeSpan.Zero, flags);

        now.IsUsableForSecurity(TimeSpan.FromMinutes(10)).Should().Be(usable);
    }

    [Fact]
    public void Binary_form_round_trips_and_is_pinned()
    {
        var now = new TrustedInstant(Monday.ToOffset(TimeSpan.FromHours(-3)), TimeSpan.FromMilliseconds(400), TimeSourceKind.SiteAnchor,
            TimeSpan.FromMinutes(90), TimeFlags.RollbackDetected | TimeFlags.SourcesDisagree);
        Span<byte> bytes = stackalloc byte[TrustedInstant.BinarySize];

        now.TryWriteBytes(bytes).Should().BeTrue();
        Convert.ToHexString(bytes).Should().Be("003C40C1E422DF08" + "00093D0000000000" + "009CA6920C000000" + "02" + "06");
        TrustedInstant.Read(bytes).Should().Be(now with { Value = Monday });
        now.TryWriteBytes(bytes[..^1]).Should().BeFalse();
    }

    [Fact]
    public void Canonical_form_equals_binary_form()
    {
        var now = new TrustedInstant(Monday, TimeSpan.FromSeconds(1), TimeSourceKind.NetworkTime, TimeSpan.Zero, TimeFlags.Unauthenticated);
        Span<byte> canonical = stackalloc byte[TrustedInstant.BinarySize];
        Span<byte> binary = stackalloc byte[TrustedInstant.BinarySize];

        Canonical(now, canonical).Should().Be(TrustedInstant.BinarySize);
        now.TryWriteBytes(binary);
        canonical.SequenceEqual(binary).Should().BeTrue();
    }

    [Fact]
    public void Text_form_shows_value_uncertainty_source_and_flags()
    {
        new TrustedInstant(Monday, TimeSpan.FromMilliseconds(400), TimeSourceKind.CentralAnchor, TimeSpan.Zero, TimeFlags.None)
            .ToString().Should().Be("2026-10-05T13:30:00.0000000+00:00 ±0.400s CentralAnchor");
        TrustedInstant.FromUntrustedClock(Monday).ToString()
            .Should().Be("2026-10-05T13:30:00.0000000+00:00 ±? System [Unanchored, Unauthenticated]");
    }

    [Fact]
    public void Time_reply_binary_form_round_trips()
    {
        var reply = new TimeReply(Monday, Monday.AddMilliseconds(2), TimeSpan.FromMilliseconds(15), TimeSourceKind.CentralAnchor,
            TimeFlags.None, 3);
        Span<byte> bytes = stackalloc byte[TimeReply.BinarySize];

        reply.TryWriteBytes(bytes).Should().BeTrue();
        TimeReply.Read(bytes).Should().Be(reply);
        reply.TryWriteBytes(bytes[..^1]).Should().BeFalse();
        FluentActions.Invoking(() => TimeReply.Read(new byte[3])).Should().Throw<ArgumentException>();
    }

    private static int Canonical<T>(in T value, Span<byte> destination) where T : ICanonicalValue<T>
        => T.TryWriteCanonical(in value, destination, out int written) ? written : -1;

    [Fact]
    public async Task Waiting_for_an_anchor_on_a_plain_provider_returns_at_once()
    {
        var now = await TimeProvider.System.WaitForAnchorAsync(TimeSpan.FromHours(1));

        now.IsAnchored.Should().BeFalse();
    }

    private sealed class StubTrustedClock(TrustedInstant now) : TimeProvider, ITrustedClock
    {
        public TrustedInstant GetTrustedNow() => now;
        public ValueTask<TrustedInstant> WaitForAnchorAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(now);
        public override DateTimeOffset GetUtcNow() => now.Value;
    }
}
