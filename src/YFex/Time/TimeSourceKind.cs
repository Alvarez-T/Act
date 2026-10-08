namespace YFex.Time;

/// <summary>
/// Where a trusted time value came from (time README §3). Stored in envelopes and audit records, so values are fixed forever.
/// <para><b>Type only</b> (time ⚑T2): the sources live in the YFex.Time library and in the libraries that own the transport.</para>
/// </summary>
public enum TimeSourceKind : byte
{
    /// <summary>The device's own clock: start-up value and fallback, never a voting source.</summary>
    System = 0,

    /// <summary>Our central server over the RPC connection (authenticated by TLS/mTLS).</summary>
    CentralAnchor = 1,

    /// <summary>The shop's site broker over mutual TLS on the LAN (time README §8).</summary>
    SiteAnchor = 2,

    /// <summary>A public NTP server (SNTP v4, unauthenticated).</summary>
    NetworkTime = 3,

    /// <summary>The <c>Date</c> header of an HTTPS response (1 s resolution, unauthenticated for voting).</summary>
    HttpsDate = 4,

    /// <summary>A public authority, e.g. SEFAZ <c>dhRecbto</c> (authenticated by mTLS with the company certificate).</summary>
    Authority = 5,

    /// <summary>A GNSS receiver (GPS NMEA, unauthenticated).</summary>
    Satellite = 6,

    /// <summary>Another installation of the same site (evidence only: never an anchor, time README §8.3).</summary>
    Peer = 7,
}
