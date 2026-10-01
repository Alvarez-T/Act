using System.Collections.Concurrent;

namespace YFex.Validation;

/// <summary>
/// Heap collector for async code (a <see cref="ViolationWriter"/> cannot cross <c>await</c>). Used by
/// <see cref="IAsyncValueRule{T}"/> and <see cref="IContextRule{T}"/>. Pooled: <see cref="Rent"/>,
/// then <see cref="Dispose"/> to return it. Not thread-safe: context rules run sequentially.
/// </summary>
public sealed class ViolationCollector : IDisposable
{
    private const int MaxPooled = 32;
    private const int MaxRetainedCapacity = 64;
    private static readonly ConcurrentBag<ViolationCollector> s_pool = new();

    private Violation[] _items = new Violation[4];
    private int _count;
    private int _errorCount;

    private ViolationCollector() { }

    /// <summary>Gets an empty collector from the pool.</summary>
    public static ViolationCollector Rent() => s_pool.TryTake(out var c) ? c : new ViolationCollector();

    public int Count => _count;
    public bool HasErrors => _errorCount > 0;
    public int ErrorCount => _errorCount;

    /// <summary>Stamped on violations added without their own origin.</summary>
    public ViolationOrigin CurrentOrigin { get; set; }

    public ReadOnlySpan<Violation> AsSpan() => _items.AsSpan(0, _count);

    public void Add(in Violation violation)
    {
        if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        _items[_count++] = violation.Origin == default && CurrentOrigin != default
            ? violation.WithOrigin(CurrentOrigin)
            : violation;
        if (violation.Severity == Severity.Error) _errorCount++;
    }

    public void Add(in FieldPath path, RuleCode code, RawValue a0 = default, Severity severity = Severity.Error)
        => Add(new Violation(in path, code, severity, a0.IsNone ? default : new RuleArgs(a0), CurrentOrigin));

    public void AddRange(in ValidationResult other)
    {
        foreach (ref readonly Violation v in other.Violations.AsSpan()) Add(in v);
    }

    /// <summary>Copies into an exact-size array. <see cref="ValidationResult.Valid"/> when empty.</summary>
    public ValidationResult ToResult()
        => _count == 0 ? default : new ValidationResult(new ViolationList(AsSpan().ToArray()));

    /// <summary>Clears and returns this collector to the pool. Do not use it afterwards.</summary>
    public void Dispose()
    {
        Array.Clear(_items, 0, _count);
        _count = 0;
        _errorCount = 0;
        CurrentOrigin = default;
        if (_items.Length > MaxRetainedCapacity) _items = new Violation[4];
        if (s_pool.Count < MaxPooled) s_pool.Add(this);
    }
}
