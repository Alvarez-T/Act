using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace YFex.Validation;

/// <summary>Inline storage for the first four violations of a <see cref="ViolationWriter"/>.</summary>
[InlineArray(4)]
internal struct InlineViolations4
{
    private Violation _e0;
}

/// <summary>
/// The writer's state, kept in a plain (non-ref) struct embedded in <see cref="ViolationWriter"/>, so a
/// <see cref="ViolationScope"/> can hold a <c>ref</c> to it (C# forbids ref fields of ref struct type,
/// CS9050). Lives wherever the writer lives — on the stack; no indirection.
/// </summary>
internal struct ViolationStorage
{
    private const int InlineCapacity = 4;
    private const int FirstSpillSize = 16;

    private InlineViolations4 _inline;
    private Violation[]? _spill;   // rented from ArrayPool once the 5th violation arrives
    public int Count;
    public int ErrorCount;

    [UnscopedRef]
    public readonly ReadOnlySpan<Violation> AsSpan()
        => _spill is not null ? _spill.AsSpan(0, Count) : ((ReadOnlySpan<Violation>)_inline)[..Count];

    public void Store(in Violation v)
    {
        if (_spill is null)
        {
            if (Count < InlineCapacity) _inline[Count] = v;
            else SpillAndStore(in v);
        }
        else
        {
            if (Count == _spill.Length) Grow();
            _spill[Count] = v;
        }

        Count++;
        if (v.Severity == Severity.Error) ErrorCount++;
    }

    public void Dispose()
    {
        Violation[]? spill = _spill;
        if (spill is not null)
        {
            _spill = null;
            Array.Clear(spill, 0, Count);          // drop references before returning to the pool
            ArrayPool<Violation>.Shared.Return(spill);
        }
        else if (Count > 0)
        {
            _inline = default;
        }
        Count = 0;
        ErrorCount = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SpillAndStore(in Violation v)
    {
        Violation[] spill = ArrayPool<Violation>.Shared.Rent(FirstSpillSize);
        ((ReadOnlySpan<Violation>)_inline).CopyTo(spill);
        _inline = default;
        spill[Count] = v;
        _spill = spill;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        Violation[] old = _spill!;
        Violation[] bigger = ArrayPool<Violation>.Shared.Rent(old.Length * 2);
        Array.Copy(old, bigger, Count);
        Array.Clear(old, 0, Count);
        ArrayPool<Violation>.Shared.Return(old);
        _spill = bigger;
    }
}

/// <summary>
/// Stack-only collector used by ALL generated <c>Validate</c> methods and by rules.
/// Holds 4 violations inline (no allocation); the 5th moves everything to an <see cref="ArrayPool{T}"/>
/// array, returned on dispose.
/// <para>
/// C# forbids passing a <c>using</c> variable by <c>ref</c> (CS1657), so open a writer with
/// <see cref="Begin"/>: the writer is an ordinary local and the returned <see cref="ViolationScope"/>
/// disposes it through a <c>ref</c> field — same cost as a hand-written try/finally.
/// </para>
/// <code>
/// using var scope = ViolationWriter.Begin(out var w);
/// CreateCustomer.Validate(in cmd, ref w);
/// if (w.HasErrors) return w.ToResult();   // ToResult allocates exactly once (array sized to Count)
/// </code>
/// </summary>
public ref struct ViolationWriter
{
    private ViolationStorage _storage;

    public ViolationWriter()
    {
    }

    /// <summary>
    /// Opens a writer in <paramref name="writer"/> (a normal local of the caller, so it can be passed by
    /// <c>ref</c>) and returns the scope that disposes it: <c>using var s = ViolationWriter.Begin(out var w);</c>.
    /// </summary>
    public static ViolationScope Begin([UnscopedRef] out ViolationWriter writer)
    {
        writer = new ViolationWriter();
        return new ViolationScope(ref writer._storage);
    }

    public readonly int Count => _storage.Count;

    /// <summary><see cref="Severity.Error"/> violations only — generated Validate compares before/after.</summary>
    public readonly int ErrorCount => _storage.ErrorCount;

    public readonly bool HasErrors => _storage.ErrorCount > 0;

    /// <summary>Stamped on each violation added without its own origin; generated code sets it per
    /// invariant / rule and restores it afterwards.</summary>
    public ViolationOrigin CurrentOrigin { readonly get; set; }

    /// <summary>Adds <paramref name="violation"/>; a default origin is replaced by <see cref="CurrentOrigin"/>.</summary>
    public void Add(in Violation violation)
    {
        if (violation.Origin == default && CurrentOrigin != default)
            _storage.Store(violation.WithOrigin(CurrentOrigin));
        else
            _storage.Store(in violation);
    }

    public void Add(in FieldPath path, RuleCode code, Severity severity = Severity.Error)
        => _storage.Store(new Violation(in path, code, severity, default, CurrentOrigin));

    public void Add(in FieldPath path, RuleCode code, RawValue a0, Severity severity = Severity.Error)
        => _storage.Store(new Violation(in path, code, severity, new RuleArgs(a0), CurrentOrigin));

    public void Add(in FieldPath path, RuleCode code, RawValue a0, RawValue a1, Severity severity = Severity.Error)
        => _storage.Store(new Violation(in path, code, severity, new RuleArgs(a0, a1), CurrentOrigin));

    /// <summary>Adds every violation of <paramref name="result"/> (origins kept).</summary>
    public void AddRange(in ValidationResult result)
    {
        foreach (ref readonly Violation v in result.Violations.AsSpan()) _storage.Store(in v);
    }

    /// <summary>
    /// True if an Error already exists for exactly <paramref name="path"/>. Generated code uses it to
    /// skip invariants whose inputs already failed ("avoid noise").
    /// </summary>
    public readonly bool HasErrorFor(in FieldPath path)
    {
        if (_storage.ErrorCount == 0) return false;
        foreach (ref readonly Violation v in _storage.AsSpan())
            if (v.Severity == Severity.Error && v.Path.Equals(path)) return true;
        return false;
    }

    /// <summary>Current violations. Valid only until the next Add / Dispose.</summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<Violation> AsSpan() => _storage.AsSpan();

    /// <summary>Copies into an exact-size array. <see cref="ValidationResult.Valid"/> when empty.</summary>
    public readonly ValidationResult ToResult()
        => _storage.Count == 0 ? default : new ValidationResult(new ViolationList(_storage.AsSpan().ToArray()));

    /// <summary>Returns the pooled spill array (if any) and clears the writer. Prefer <see cref="Begin"/>.</summary>
    public void Dispose() => _storage.Dispose();
}

/// <summary>
/// Disposes a <see cref="ViolationWriter"/> opened with <see cref="ViolationWriter.Begin"/>. Holds only a
/// <c>ref</c> to the writer's storage on the caller's stack (pointer-sized, no copy, no allocation).
/// </summary>
public readonly ref struct ViolationScope
{
    private readonly ref ViolationStorage _storage;

    internal ViolationScope(ref ViolationStorage storage) => _storage = ref storage;

    /// <summary>Returns the writer's pooled spill array (if any).</summary>
    public void Dispose() => _storage.Dispose();
}
