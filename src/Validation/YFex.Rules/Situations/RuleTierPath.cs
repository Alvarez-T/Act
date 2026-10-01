using System.Runtime.CompilerServices;
using YFex.Helpers;

namespace YFex.Rules;

/// <summary>
/// Position in the app-declared rule-tier hierarchy (04 §9): the values of a situation's <see cref="RuleTierAttribute"/>
/// fields, outermost first — e.g. Company <c>"acme"</c> / Branch <c>"recife"</c>. Rule data (table versions, parameter
/// values, overrides) is published AT a path and an evaluation sees the data of every prefix of its own path:
/// code (depth 0) → tier 1 → tier 2 … (the most specific wins). The library assigns no meaning to the tiers — an app
/// may call them Tenant/Store, Carrier/Hub, Hospital/Ward, or declare none.
/// <code>
/// var path = RuleTierPath.Of(RawValue.From(companyId), RawValue.From(branchId));   // depth 2
/// path.Parent;                        // company only (depth 1)
/// RuleTierPath.Code.Covers(path);     // true — code data applies everywhere
/// </code>
/// </summary>
public readonly struct RuleTierPath : IEquatable<RuleTierPath>
{
    /// <summary>Deepest supported hierarchy.</summary>
    public const int MaxDepth = 4;

    private readonly TierValues _values;
    private readonly byte _depth;

    private RuleTierPath(scoped ReadOnlySpan<RawValue> values)
    {
        for (int i = 0; i < values.Length; i++) _values[i] = values[i];
        _depth = (byte)values.Length;
    }

    private RuleTierPath(in TierValues values, int depth)
    {
        _values = values;
        for (int i = depth; i < MaxDepth; i++) _values[i] = default;
        _depth = (byte)depth;
    }

    /// <summary>The code tier (data shipped with the build) — depth 0.</summary>
    public static RuleTierPath Code => default;

    /// <summary>
    /// Path from tier values, outermost first. Trailing <see cref="RawValue.None"/> values are dropped (a situation
    /// without a branch is a company-level evaluation); a None before a set value is invalid.
    /// </summary>
    public static RuleTierPath Of(params ReadOnlySpan<RawValue> tiers)
    {
        int depth = tiers.Length;
        while (depth > 0 && tiers[depth - 1].IsNone) depth--;
        if (depth > MaxDepth) throw new ArgumentOutOfRangeException(nameof(tiers), $"At most {MaxDepth} rule tiers are supported.");
        for (int i = 0; i < depth; i++)
            if (tiers[i].IsNone) throw new ArgumentException($"Rule tier {i + 1} is empty but tier {depth} is set.", nameof(tiers));
        return new RuleTierPath(tiers[..depth]);
    }

    /// <summary>0 = code; n = data of the n-th tier.</summary>
    public int Depth => _depth;

    public bool IsCode => _depth == 0;

    /// <summary>Value of tier <paramref name="index"/> (0-based); None beyond <see cref="Depth"/>.</summary>
    public RawValue this[int index] => (uint)index < _depth ? _values[index] : RawValue.None;

    /// <summary>Next less specific path (tier n → n−1; code stays code).</summary>
    public RuleTierPath Parent => _depth == 0 ? this : Prefix(_depth - 1);

    /// <summary>The first <paramref name="depth"/> tiers.</summary>
    public RuleTierPath Prefix(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        return depth >= _depth ? this : new RuleTierPath(in _values, depth);
    }

    /// <summary>True when data published at this path applies to an evaluation at <paramref name="evaluation"/> (prefix).</summary>
    public bool Covers(in RuleTierPath evaluation)
    {
        if (_depth > evaluation._depth) return false;
        for (int i = 0; i < _depth; i++)
            if (!_values[i].Equals(evaluation._values[i])) return false;
        return true;
    }

    public bool Equals(RuleTierPath other)
    {
        if (_depth != other._depth) return false;
        for (int i = 0; i < _depth; i++)
            if (!_values[i].Equals(other._values[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is RuleTierPath p && Equals(p);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(_depth);
        for (int i = 0; i < _depth; i++) h.Add(_values[i]);
        return h.ToHashCode();
    }

    public static bool operator ==(RuleTierPath left, RuleTierPath right) => left.Equals(right);
    public static bool operator !=(RuleTierPath left, RuleTierPath right) => !left.Equals(right);

    /// <summary>Feeds the path into a fingerprint/cache hash.</summary>
    public void AppendTo(ref StableHash64 hash)
    {
        hash.Append(_depth);
        for (int i = 0; i < _depth; i++) _values[i].AppendTo(ref hash);
    }

    /// <summary><c>"code"</c> or <c>"tier:acme/recife"</c> (display/logs; storage uses <see cref="RuleTierPaths.Format"/>).</summary>
    public override string ToString()
    {
        if (_depth == 0) return "code";
        var parts = new string[_depth];
        for (int i = 0; i < _depth; i++) parts[i] = _values[i].ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        return "tier:" + string.Join('/', parts);
    }

    [InlineArray(MaxDepth)]
    private struct TierValues
    {
        private RawValue _e0;
    }
}
