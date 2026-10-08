namespace YFex.Time;

/// <summary>
/// One exchange with a time source, measured by the client (time README §2.4): device monotonic timestamps around the request plus
/// the server's <see cref="TimeReply"/>. The YFex.Time library turns it into "server time at <see cref="LocalReceived"/>" ± round trip / 2.
/// </summary>
/// <param name="LocalSent">Device <see cref="TimeProvider.GetTimestamp"/> just before the request left (NTP t1).</param>
/// <param name="LocalReceived">Device <see cref="TimeProvider.GetTimestamp"/> just after the reply arrived (NTP t4).</param>
/// <param name="Reply">What the server answered (t2, t3, its own uncertainty, flags, stratum).</param>
/// <param name="Resolution">Resolution of the server's time (1 s for an HTTP <c>Date</c> header, ~0 for NTP).</param>
public readonly record struct TimeSample(long LocalSent, long LocalReceived, TimeReply Reply, TimeSpan Resolution);
