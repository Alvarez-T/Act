namespace YFex.Time;

/// <summary>
/// A provider of real time (time README §3). The contract is in core so Messaging.Rpc (central anchor), YFex.Offline (site broker,
/// peers), YFex.Fiscal (SEFAZ) and YFex.Devices (GNSS) implement it without referencing the YFex.Time library.
/// <code>
/// public async ValueTask&lt;TimeSample?&gt; SampleAsync(TimeProvider device, CancellationToken cancellationToken)
/// {
///     long sent = device.GetTimestamp();                         // t1: right before the request
///     TimeReply reply = await server.GetTimeAsync(cancellationToken);
///     return new TimeSample(sent, device.GetTimestamp(), reply, TimeSpan.Zero);   // t4: right after the reply
/// }
/// </code>
/// </summary>
public interface ITimeSource
{
    /// <summary>Display name for logs and events (e.g. <c>"ntp:a.ntp.br"</c>).</summary>
    string Name { get; }

    /// <summary>What kind of source this is.</summary>
    TimeSourceKind Kind { get; }

    /// <summary>
    /// True when the answer cannot be forged by the network (TLS/mTLS to our own server, a signed reply). Only authenticated sources
    /// may anchor security decisions or pull time back (time ⚑T4).
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// One exchange, or null when the source has no answer (unreachable, refused, invalid reply). Take t1/t4 from
    /// <paramref name="device"/> as close to the wire as possible. Must honour <paramref name="cancellationToken"/> (the library
    /// gives each source a short budget) and should not throw for ordinary network failures.
    /// </summary>
    ValueTask<TimeSample?> SampleAsync(TimeProvider device, CancellationToken cancellationToken);
}
