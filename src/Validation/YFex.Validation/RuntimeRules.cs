namespace YFex.Validation;

/// <summary>
/// A rule that runs at runtime: fluent <c>Invariant(...)</c> lambdas and fluent rules the generator could not
/// compile (non-constant arguments, message not <c>partial</c> / in another compilation).
/// </summary>
public delegate void InvariantFn<T>(in T value, ref ViolationWriter w);

/// <summary>
/// Static generic holder for the rules of <typeparamref name="T"/> that cannot be emitted at compile
/// time: fluent <c>Invariant(...)</c> lambdas and rules with non-constant arguments. Filled by the fluent
/// <see cref="ValidationBuilder{T}"/> (Cqrs <c>.Validation(...)</c>) at registry build; generated <c>Validate</c>
/// calls <see cref="Run"/> (a null check when empty), <see cref="ValidationSlot{T}"/> runs it when no generated
/// validator exists, and generated <c>TryGetConstraints</c> falls back to <see cref="TryGetConstraints"/>.
/// Registration is copy-on-write (thread-safe); reads are lock-free.
/// </summary>
public static class RuntimeRules<T>
{
    private static readonly Lock s_gate = new();
    private static InvariantFn<T>[]? s_rules;
    private static string?[]? s_keys;   // parallel to s_rules; null = unkeyed
    private static (string Path, FieldConstraints Constraints)[]? s_constraints;

    public static bool HasRules => s_rules is not null;

    public static void Register(InvariantFn<T> rule) => Register(null, rule);

    /// <summary>
    /// Registers <paramref name="rule"/> under <paramref name="key"/>, replacing a rule already registered with the
    /// same key — so rebuilding a registry (tests, hot reload) never runs the same fluent rule twice.
    /// </summary>
    public static void Register(string? key, InvariantFn<T> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (s_gate)
        {
            InvariantFn<T>[] current = s_rules ?? [];
            string?[] keys = s_keys ?? [];
            if (key is not null)
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!string.Equals(keys[i], key, StringComparison.Ordinal)) continue;
                    var copy = (InvariantFn<T>[])current.Clone();
                    copy[i] = rule;
                    s_rules = copy;
                    return;
                }
            }
            var next = new InvariantFn<T>[current.Length + 1];
            var nextKeys = new string?[current.Length + 1];
            current.CopyTo(next, 0);
            keys.CopyTo(nextKeys, 0);
            next[^1] = rule;
            nextKeys[^1] = key;
            s_keys = nextKeys;
            s_rules = next;
        }
    }

    /// <summary>Registers constraints for <paramref name="path"/>, merged (most restrictive) with any already registered.</summary>
    public static void RegisterConstraints(string path, in FieldConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (s_gate)
        {
            var current = s_constraints ?? [];
            for (int i = 0; i < current.Length; i++)
            {
                if (current[i].Path != path) continue;
                var copy = ((string, FieldConstraints)[])current.Clone();
                copy[i] = (path, current[i].Constraints.Merge(constraints));
                s_constraints = copy;
                return;
            }
            var next = new (string, FieldConstraints)[current.Length + 1];
            current.CopyTo(next, 0);
            next[^1] = (path, constraints);
            s_constraints = next;
        }
    }

    /// <summary>Runs every registered rule.</summary>
    public static void Run(in T value, ref ViolationWriter w)
    {
        InvariantFn<T>[]? rules = s_rules;
        if (rules is null) return;
        foreach (InvariantFn<T> rule in rules) rule(in value, ref w);
    }

    public static bool TryGetConstraints(ReadOnlySpan<char> path, out FieldConstraints constraints)
    {
        var all = s_constraints;
        if (all is not null)
        {
            foreach (var (p, c) in all)
            {
                if (!path.SequenceEqual(p)) continue;
                constraints = c;
                return true;
            }
        }
        constraints = default;
        return false;
    }

    /// <summary>Removes everything (registry rebuild, tests).</summary>
    public static void Clear()
    {
        lock (s_gate)
        {
            s_rules = null;
            s_keys = null;
            s_constraints = null;
        }
    }
}

/// <summary>
/// The fluent rules of <typeparamref name="T"/> that YFex.Validation.Generator compiled into <c>T.Validate</c>
/// (D27). Filled by the generated module initializer of the assembly that holds the fluent configuration; the
/// runtime <see cref="ValidationBuilder{T}"/> skips every rule whose key is listed and registers only the leftovers
/// (non-constant arguments, lambdas) into <see cref="RuntimeRules{T}"/> — so no rule runs twice.
/// </summary>
/// <remarks>
/// Keys are per member + rule + arguments (<see cref="FluentRuleKey"/>): at runtime <c>Length(2, 80)</c> and
/// <c>Length(1, opts.Max)</c> look the same, only the generator knows which one had constants.
/// </remarks>
public static class CompiledFluentRules<T>
{
    private static HashSet<string>? s_keys;

    public static bool IsSet => s_keys is not null;

    public static void Set(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var set = new HashSet<string>(keys, StringComparer.Ordinal);
        if (s_keys is { } existing) set.UnionWith(existing);
        s_keys = set;
    }

    public static bool Contains(string key) => s_keys is { } keys && keys.Contains(key);

    /// <summary>Tests only.</summary>
    public static void Clear() => s_keys = null;
}
