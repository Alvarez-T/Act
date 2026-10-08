using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace YFex.Expressions;

/// <summary>A context value an expression reads with <c>{"var": "$name"}</c>; its value is <see cref="ExprEnv"/>[<see cref="Slot"/>] at run time.</summary>
public sealed record ExprSpecial(string Name, ExprType Type, byte Slot, string Description)
{
    /// <summary>Slot value of the built-in "current item index" special (no <see cref="ExprEnv"/> slot).</summary>
    public const byte ItemIndexSlot = byte.MaxValue;

    public bool IsItemIndex => Slot == ItemIndexSlot;
}

/// <summary>
/// The specials a host offers (immutable; build with <c>With…</c>). Names start with <c>$</c>. Up to <see cref="ExprEnv.Length"/>
/// value slots, plus any number of aliases of the built-in item index (Rules: <c>$lineIndex</c>; Composer: <c>$index</c>) and of
/// "unavailable" names that fail with a host-chosen code (Rules: <c>$base</c> in a rule set without amount).
/// </summary>
public sealed class ExprSpecials
{
    private readonly FrozenDictionary<string, ExprSpecial> _specials;
    private readonly FrozenDictionary<string, (string Code, string Detail)> _unavailable;

    private ExprSpecials(FrozenDictionary<string, ExprSpecial> specials, FrozenDictionary<string, (string, string)> unavailable)
    {
        _specials = specials;
        _unavailable = unavailable;
    }

    public static ExprSpecials None { get; } = new(FrozenDictionary<string, ExprSpecial>.Empty, FrozenDictionary<string, (string, string)>.Empty);

    /// <summary>Available specials, in no particular order (authoring catalogs sort them).</summary>
    public IEnumerable<ExprSpecial> All => _specials.Values;

    /// <summary>Adds (or replaces) a value special read from <see cref="ExprEnv"/>[<see cref="ExprSpecial.Slot"/>].</summary>
    public ExprSpecials With(ExprSpecial special)
    {
        if (!special.Name.StartsWith('$')) throw new ArgumentException($"Special names start with '$' ('{special.Name}').", nameof(special));
        if (!special.IsItemIndex && special.Slot >= ExprEnv.Length)
            throw new ArgumentOutOfRangeException(nameof(special), $"Slot {special.Slot} is outside ExprEnv (0..{ExprEnv.Length - 1}).");
        return With(special.Name, special, remove: false);
    }

    /// <summary>Adds an alias of the current item index (an Int64; null outside per-item evaluation).</summary>
    public ExprSpecials WithItemIndex(string name = "$index", string description = "index of the current item (per-item expressions)")
        => With(new ExprSpecial(name, ExprType.Int, ExprSpecial.ItemIndexSlot, description));

    /// <summary>A name that exists in the host's vocabulary but can't be used here: binding fails with <paramref name="code"/>.</summary>
    public ExprSpecials WithUnavailable(string name, string code, string detail)
    {
        var d = new Dictionary<string, (string, string)>(_unavailable, StringComparer.Ordinal) { [name] = (code, detail) };
        var s = new Dictionary<string, ExprSpecial>(_specials, StringComparer.Ordinal);
        s.Remove(name);
        return new(s.ToFrozenDictionary(StringComparer.Ordinal), d.ToFrozenDictionary(StringComparer.Ordinal));
    }

    public bool TryGet(string name, out ExprSpecial special) => _specials.TryGetValue(name, out special!);

    internal bool TryGetUnavailable(string name, out string code, out string detail)
    {
        if (_unavailable.TryGetValue(name, out var u)) { (code, detail) = u; return true; }
        code = detail = "";
        return false;
    }

    private ExprSpecials With(string name, ExprSpecial special, bool remove)
    {
        var s = new Dictionary<string, ExprSpecial>(_specials, StringComparer.Ordinal);
        if (remove) s.Remove(name); else s[name] = special;
        var u = new Dictionary<string, (string, string)>(_unavailable, StringComparer.Ordinal);
        u.Remove(name);
        return new(s.ToFrozenDictionary(StringComparer.Ordinal), u.ToFrozenDictionary(StringComparer.Ordinal));
    }
}

/// <summary>
/// Values of the specials for one run, by slot. An inline array passed by <c>in</c> — never allocated.
/// <code>var env = ExprEnv.Of(runningBase, RawValue.From(date));</code>
/// </summary>
[InlineArray(Length)]
public struct ExprEnv
{
    public const int Length = 4;
    private RawValue _element0;

    public static ExprEnv Of(in RawValue s0 = default, in RawValue s1 = default, in RawValue s2 = default, in RawValue s3 = default)
    {
        var env = new ExprEnv();
        env[0] = s0;
        env[1] = s1;
        env[2] = s2;
        env[3] = s3;
        return env;
    }
}
