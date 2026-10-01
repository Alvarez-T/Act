namespace YFex.Validation;

/// <summary>
/// Rewrites violation paths: command paths → VM paths, or prefixing <c>"Address."</c>. Generated per
/// VM/command pair (<c>[ValidatesAs]</c>, <c>ApplyServer</c>). Implemented by structs / ref structs so
/// <see cref="ValidationResult.MapPaths{TMap}"/> is devirtualized.
/// </summary>
public interface IPathMap
{
    /// <summary>Maps <paramref name="from"/>; false when the map does not know the path.</summary>
    bool TryMap(in FieldPath from, out FieldPath to);
}

/// <summary>
/// THE validation result for the whole ecosystem (D4) — replaces
/// <c>YFex.State.Validation.ValidationResult</c> and <c>YFex.Cqrs.ValidationResult</c>.
/// Valid ⇔ no <see cref="Severity.Error"/> violation (warnings/infos may be present and are still
/// shown). <c>default</c> = valid, no allocation.
/// </summary>
public readonly struct ValidationResult
{
    private readonly bool _hasErrors;
    private readonly bool _hasWarnings;

    public ValidationResult(ViolationList violations)
    {
        Violations = violations;
        _hasErrors = violations.Any(Severity.Error);
        _hasWarnings = false;
        foreach (ref readonly Violation v in violations)
            if (v.Severity == Severity.Warning) { _hasWarnings = true; break; }
    }

    public ViolationList Violations { get; }

    public bool IsValid => !_hasErrors;
    public bool HasErrors => _hasErrors;
    public bool HasWarnings => _hasWarnings;

    public static ValidationResult Valid => default;

    /// <summary>Single violation (allocates a 1-element array).</summary>
    public static ValidationResult Of(in Violation violation) => new(new ViolationList([violation]));

    public static ValidationResult Of(ViolationList violations) => new(violations);

    /// <summary>Concatenation (allocates only when both have violations).</summary>
    public ValidationResult Merge(in ValidationResult other)
    {
        if (other.Violations.IsEmpty) return this;
        if (Violations.IsEmpty) return other;
        return new ValidationResult(Violations.Concat(other.Violations));
    }

    /// <summary>
    /// Rewrites every path through <paramref name="map"/>. Paths the map does not recognize become
    /// object-level (shown in the form summary) — a map that wants to keep a path returns it unchanged.
    /// Allocates one array when there are violations.
    /// </summary>
    public ValidationResult MapPaths<TMap>(ref TMap map) where TMap : struct, IPathMap, allows ref struct
    {
        ReadOnlySpan<Violation> items = Violations.AsSpan();
        if (items.IsEmpty) return this;

        var mapped = new Violation[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly Violation v = ref items[i];
            mapped[i] = map.TryMap(v.Path, out FieldPath to) ? v.WithPath(in to) : v.WithPath(FieldPath.Empty);
        }
        return new ValidationResult(new ViolationList(mapped));
    }

    public override string ToString() => IsValid
        ? Violations.IsEmpty ? "Valid" : $"Valid ({Violations.Count} warning/info)"
        : $"Invalid ({Violations.ErrorCount} error(s))";
}
