using System.Buffers.Binary;
using System.Globalization;
using YFex.Canonical;

namespace YFex.Time;

/// <summary>
/// "What time is it, really?" — the best estimate of UTC now plus how far it can be trusted (time README §2.1). Produced by the
/// YFex.Time library's <c>TrustedTimeProvider</c>; read through <see cref="TrustedClockExtensions.GetTrustedNow"/> on any
/// <see cref="TimeProvider"/>.
/// <code>
/// TrustedInstant now = time.GetTrustedNow();
/// if (!now.IsUsableForSecurity(TimeSpan.FromMinutes(10))) return SnapshotUse.Refused;   // time ⚑T11
/// envelope.DecidedAt = now;                                                            // evidence: value, ±, source, flags
/// now.ToString();   // "2026-10-05T13:30:00.0000000+00:00 ±0.400s CentralAnchor"
/// </code>
/// <para><b>Canonical binary form</b> (<see cref="BinarySize"/> bytes, little-endian, what signed envelopes hash): UTC ticks of
/// <see cref="Value"/>, ticks of <see cref="Uncertainty"/>, ticks of <see cref="SinceLastAnchor"/>, <see cref="Source"/>,
/// <see cref="Flags"/>. The offset of <see cref="Value"/> is not part of the value.</para>
/// </summary>
/// <param name="Value">Best estimate of UTC now.</param>
/// <param name="Uncertainty">± bound: round trip / 2 of the anchoring exchange + drift since then.</param>
/// <param name="Source">What anchored the value.</param>
/// <param name="SinceLastAnchor">Monotonic time since the anchor was taken.</param>
/// <param name="Flags">Quality flags.</param>
public readonly record struct TrustedInstant(DateTimeOffset Value, TimeSpan Uncertainty, TimeSourceKind Source, TimeSpan SinceLastAnchor,
    TimeFlags Flags) : ISpanFormattable, ICanonicalValue<TrustedInstant>, global::YFex.Diagnostics.ILogValue<TrustedInstant>
{
    /// <summary>Size of the canonical binary form.</summary>
    public const int BinarySize = 26;

    /// <summary>The uncertainty reported when nothing bounds the value (no anchor, no authenticated floor).</summary>
    public static readonly TimeSpan UnknownUncertainty = TimeSpan.MaxValue;

    /// <summary>A value straight from a clock that is not a trusted clock (time ⚑T8): flagged unanchored and unauthenticated.</summary>
    public static TrustedInstant FromUntrustedClock(DateTimeOffset value)
        => new(value, UnknownUncertainty, TimeSourceKind.System, TimeSpan.Zero, TimeFlags.Unanchored | TimeFlags.Unauthenticated);

    /// <summary>True when a source answered since the process started.</summary>
    public bool IsAnchored => (Flags & TimeFlags.Unanchored) == 0;

    /// <summary>True when an authenticated source anchors the value (central, the site broker, an authority, later signed sources).</summary>
    public bool IsAuthenticated => (Flags & (TimeFlags.Unanchored | TimeFlags.Unauthenticated)) == 0;

    /// <summary>
    /// Time ⚑T11: may a security decision (snapshot validity, approval as-of, offline budgets) rely on this value? True when an
    /// authenticated source anchors it within <paramref name="maximumUncertainty"/> (a rollback of the device clock does not matter
    /// then: the value does not come from the device clock), or when it is bounded below by authenticated time
    /// (<see cref="TimeFlags.AuthenticatedFloor"/>: an offline start whose stored high-water came from authenticated time) and no
    /// rollback was seen. The floor case accepts the residual of time README §2.5 (powered-off time with the clock set back is not
    /// counted). <see cref="TimeFlags.RollbackDetected"/> stays in the flags as evidence either way.
    /// </summary>
    public bool IsUsableForSecurity(TimeSpan maximumUncertainty)
    {
        if (IsAuthenticated) return Uncertainty <= maximumUncertainty;
        return (Flags & (TimeFlags.AuthenticatedFloor | TimeFlags.RollbackDetected)) == TimeFlags.AuthenticatedFloor;
    }

    /// <summary>Writes the canonical bytes; false when <paramref name="destination"/> is shorter than <see cref="BinarySize"/>.</summary>
    public bool TryWriteBytes(Span<byte> destination)
    {
        if (destination.Length < BinarySize) return false;
        BinaryPrimitives.WriteInt64LittleEndian(destination, Value.UtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], Uncertainty.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], SinceLastAnchor.Ticks);
        destination[24] = (byte)Source;
        destination[25] = (byte)Flags;
        return true;
    }

    /// <summary>Reads the canonical bytes (<see cref="Value"/> comes back in UTC).</summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> is shorter than <see cref="BinarySize"/>.</exception>
    public static TrustedInstant Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < BinarySize)
            throw new ArgumentException($"A trusted instant has {BinarySize} bytes; got {source.Length}.", nameof(source));
        return new TrustedInstant(
            new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(source), TimeSpan.Zero),
            TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(source[8..])),
            (TimeSourceKind)source[24],
            TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(source[16..])),
            (TimeFlags)source[25]);
    }

    static int ICanonicalValue<TrustedInstant>.MaxCanonicalLength => BinarySize;

    static bool ICanonicalValue<TrustedInstant>.TryWriteCanonical(in TrustedInstant value, Span<byte> destination, out int written)
    {
        bool ok = value.TryWriteBytes(destination);
        written = ok ? BinarySize : 0;
        return ok;
    }

    /// <summary><c>"2026-10-05T13:30:00.0000000+00:00 ±0.400s CentralAnchor"</c> + <c>" [flags]"</c> when any.</summary>
    public override string ToString() => ToString(null, CultureInfo.InvariantCulture);

    /// <inheritdoc cref="ToString()"/>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        Span<char> buffer = stackalloc char[160];
        return TryFormat(buffer, out int written, format, formatProvider) ? new string(buffer[..written]) : Fallback();
    }

    /// <inheritdoc cref="ToString()"/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        var utc = Value.ToUniversalTime();
        bool ok = Uncertainty == UnknownUncertainty
            ? destination.TryWrite(CultureInfo.InvariantCulture, $"{utc:O} ±? {Source}", out charsWritten)
            : destination.TryWrite(CultureInfo.InvariantCulture, $"{utc:O} ±{Uncertainty.TotalSeconds:0.000}s {Source}", out charsWritten);
        if (!ok || Flags == TimeFlags.None) return ok;
        int used = charsWritten;
        if (!destination[used..].TryWrite(CultureInfo.InvariantCulture, $" [{Flags}]", out int more)) { charsWritten = 0; return false; }
        charsWritten = used + more;
        return true;
    }

    private string Fallback() => $"{Value.ToUniversalTime():O} {Source} [{Flags}]";

    /// <summary>Logs as its text form (formatted on the stack).</summary>
    static void global::YFex.Diagnostics.ILogValue<TrustedInstant>.Write(in TrustedInstant value, ref global::YFex.Diagnostics.LogWriter writer,
        ushort field)
    {
        Span<char> buffer = stackalloc char[160];
        if (value.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture)) writer.Write(field, buffer[..written]);
        else writer.Write(field, value.Fallback());
    }
}
