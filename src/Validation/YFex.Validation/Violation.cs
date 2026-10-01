using System.Runtime.CompilerServices;

namespace YFex.Validation;

/// <summary>Inline storage for the two <see cref="RuleArgs"/> slots.</summary>
[InlineArray(2)]
public struct RuleArgsBuffer
{
    private RawValue _e0;
}

/// <summary>
/// Up to two typed arguments for message formatting (<c>"{field} must be between {0} and {1}"</c>).
/// Example: a <c>Length(2, 80)</c> failure → <c>new RuleArgs(RawValue.From(2), RawValue.From(80))</c>.
/// </summary>
public readonly struct RuleArgs : IEquatable<RuleArgs>
{
    /// <summary>Maximum number of arguments.</summary>
    public const int MaxCount = 2;

    private readonly RuleArgsBuffer _items;
    private readonly byte _count;

    public RuleArgs(RawValue a0)
    {
        _items[0] = a0;
        _count = 1;
    }

    public RuleArgs(RawValue a0, RawValue a1)
    {
        _items[0] = a0;
        _items[1] = a1;
        _count = 2;
    }

    public static RuleArgs None => default;

    public int Count => _count;

    public RawValue this[int index]
        => (uint)index < _count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>
    /// Copy of the inline storage. Take a span over the local copy to pass the arguments on:
    /// <c>var b = args.Buffer; ReadOnlySpan&lt;RawValue&gt; s = ((ReadOnlySpan&lt;RawValue&gt;)b)[..args.Count];</c>
    /// </summary>
    public RuleArgsBuffer Buffer => _items;

    public bool Equals(RuleArgs other)
    {
        if (_count != other._count) return false;
        for (int i = 0; i < _count; i++)
            if (!_items[i].Equals(other._items[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is RuleArgs other && Equals(other);
    public override int GetHashCode() => _count switch
    {
        0 => 0,
        1 => _items[0].GetHashCode(),
        _ => HashCode.Combine(_items[0], _items[1]),
    };
}

/// <summary>Who produced a violation.</summary>
public enum ViolationSource : byte
{
    /// <summary>Attribute/fluent rule on a single member.</summary>
    Field = 0,
    /// <summary><c>[Invariant]</c> method (cross-field).</summary>
    Invariant = 1,
    /// <summary>YFex.Rules decision (limits / deny / warn).</summary>
    Rule = 2,
    /// <summary>Async server-side context rule (stock, uniqueness).</summary>
    Context = 3,
    /// <summary>Came back from the server in a response (<c>ApplyServer</c>).</summary>
    Server = 4,
    /// <summary>App code added it manually.</summary>
    Manual = 5,
}

/// <summary>
/// Producer of a violation. The owner lets a producer REPLACE only its own previous violations
/// (re-running an invariant clears the invariant's old errors, not the field's own).
/// <see cref="OwnerId"/> is the generated id of the invariant / rule; 0 for Field/Server/Manual.
/// </summary>
public readonly record struct ViolationOrigin(ViolationSource Source, ushort OwnerId);

/// <summary>
/// One failed check: where (<see cref="Path"/>), what (<see cref="Code"/> + <see cref="Args"/>),
/// how serious, and who produced it. Never a pre-built string (D6): text comes from the message
/// catalog at the edge. Large (~130 bytes): pass by <c>in</c>, store only in arrays, never box.
/// </summary>
public readonly struct Violation
{
    public Violation(in FieldPath path, RuleCode code, Severity severity = Severity.Error,
        in RuleArgs args = default, ViolationOrigin origin = default, string? detail = null)
    {
        Path = path;
        Code = code;
        Severity = severity;
        Args = args;
        Origin = origin;
        Detail = detail;
    }

    public FieldPath Path { get; }
    public RuleCode Code { get; }
    public Severity Severity { get; }
    public RuleArgs Args { get; }
    public ViolationOrigin Origin { get; }

    /// <summary>
    /// Optional literal text that bypasses the catalog. Only for runtime-authored rules whose message
    /// is stored with the rule version, or for adapters (FluentValidation).
    /// </summary>
    public string? Detail { get; }

    public bool IsError => Severity == Severity.Error;

    /// <summary>Same violation at another path (used by path mapping).</summary>
    public Violation WithPath(in FieldPath path) => new(in path, Code, Severity, Args, Origin, Detail);

    /// <summary>Same violation with another origin.</summary>
    public Violation WithOrigin(ViolationOrigin origin) => new(Path, Code, Severity, Args, origin, Detail);

    public override string ToString()
        => Path.IsEmpty ? $"{Severity}: {Code}" : $"{Severity}: {Code} @ {Path}";
}
