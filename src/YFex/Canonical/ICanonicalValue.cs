namespace YFex.Canonical;

/// <summary>
/// A value object that encodes itself deterministically for hashing, MACs and signatures: equal values always give equal bytes,
/// on every platform and version (never JSON, never a display mask). Example: <c>Cpf</c> → its 11 digits as 11 bytes.
/// <para>
/// <b>Contract only</b> (cryptography ⚑K2): YFex core declares it so value types anywhere can implement it; the
/// <c>CanonicalWriter</c>/<c>CanonicalReader</c> that use it live in the YFex.Cryptography library (namespace
/// <c>YFex.Canonical</c>). Rules of the encoding: cryptography 05 §10.
/// </para>
/// </summary>
public interface ICanonicalValue<TSelf> where TSelf : ICanonicalValue<TSelf>
{
    /// <summary>Upper bound of <see cref="TryWriteCanonical"/>'s output, so callers can <c>stackalloc</c>.</summary>
    static abstract int MaxCanonicalLength { get; }

    /// <summary>Writes the canonical bytes of <paramref name="value"/>; false when <paramref name="destination"/> is too short.</summary>
    static abstract bool TryWriteCanonical(in TSelf value, Span<byte> destination, out int written);
}
