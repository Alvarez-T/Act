using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using YFex.Validation.BuiltIn;

namespace YFex.Validation;

/// <summary>
/// Builder used inside <c>.Validation(v => …)</c> (Cqrs command/query configuration, D14/D23). Member lambdas are
/// plain <see cref="Func{T,TResult}"/> (AOT-safe) and the path comes from the lambda text via
/// <see cref="CallerArgumentExpressionAttribute"/> — no expression trees.
/// </summary>
/// <remarks>
/// <para>The same lambda is read twice (D27): YFex.Validation.Generator compiles every rule with constant arguments
/// into <c>T.Validate</c> and lists it in <see cref="CompiledFluentRules{T}"/>; at registry build this builder runs
/// for real and registers into <see cref="RuntimeRules{T}"/> only what is NOT listed — non-constant arguments,
/// non-trivial <c>When</c> lambdas, <c>Invariant</c> lambdas, or everything when the message is not <c>partial</c>.</para>
/// <para>Modifiers: <see cref="FieldRuleBuilder{T,TMember}.Severity"/> and <see cref="FieldRuleBuilder{T,TMember}.Code"/>
/// change the rule declared just before them; <see cref="FieldRuleBuilder{T,TMember}.When"/> and
/// <see cref="FieldRuleBuilder{T,TMember}.Profile"/> apply to every rule declared before them in the chain.</para>
/// </remarks>
public sealed class ValidationBuilder<T>
{
    private readonly List<FluentChain<T>> _chains = [];
    private readonly List<(string Expr, InvariantFn<T> Fn)> _invariants = [];

    /// <summary>Runs <paramref name="rules"/> on a new builder and registers the runtime leftovers.</summary>
    public static void Apply(Action<ValidationBuilder<T>> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var builder = new ValidationBuilder<T>();
        rules(builder);
        builder.Register();
    }

    /// <summary>Rules for one member. <paramref name="member"/> must be a member access (<c>c =&gt; c.Name</c>).</summary>
    public FieldRuleBuilder<T, TMember> Field<TMember>(Func<T, TMember> member,
        [CallerArgumentExpression(nameof(member))] string expr = "")
    {
        ArgumentNullException.ThrowIfNull(member);
        var chain = new FieldRuleBuilder<T, TMember>(FluentRuleKey.PathOf(expr), member);
        _chains.Add(chain);
        return chain;
    }

    /// <summary>Validates every element of a collection member with the element type's own rules.</summary>
    public ValidationBuilder<T> Each<TItem>(Func<T, IReadOnlyList<TItem>?> member,
        [CallerArgumentExpression(nameof(member))] string expr = "")
        where TItem : IValidatable<TItem>
    {
        ArgumentNullException.ThrowIfNull(member);
        _chains.Add(new EachChain<T, TItem>(FluentRuleKey.PathOf(expr), member));
        return this;
    }

    /// <summary>Cross-field rule. Always runs at runtime (the generator cannot inline a lambda from another method).</summary>
    public ValidationBuilder<T> Invariant(InvariantFn<T> invariant,
        [CallerArgumentExpression(nameof(invariant))] string expr = "")
    {
        ArgumentNullException.ThrowIfNull(invariant);
        _invariants.Add((expr, invariant));
        return this;
    }

    /// <summary>Registers every rule the generator did not compile into <see cref="RuntimeRules{T}"/> (idempotent).</summary>
    public void Register()
    {
        ObjectValidation.Register<T>();   // type-erased boundaries (server invoker) find runtime-only messages too
        foreach (var chain in _chains) chain.Register();
        foreach (var (expr, fn) in _invariants) RuntimeRules<T>.Register("invariant|" + expr, fn);
    }
}

/// <summary>A per-member rule chain; registers its runtime leftovers.</summary>
public abstract class FluentChain<T>
{
    private protected FluentChain() { }

    internal abstract void Register();
}

/// <summary>Rules of one member inside <c>.Validation(v =&gt; v.Field(c =&gt; c.X)…)</c>.</summary>
public sealed class FieldRuleBuilder<T, TMember> : FluentChain<T>
{
    private readonly string _path;
    private readonly Func<T, TMember> _get;
    private readonly List<Spec> _specs = [];

    internal FieldRuleBuilder(string path, Func<T, TMember> get)
    {
        _path = path;
        _get = get;
    }

    public FieldRuleBuilder<T, TMember> Required()
        => Add(new Spec("required", "required", static v => IsMissing(v, required: true)) { Required = true });

    public FieldRuleBuilder<T, TMember> NotEmpty()
        => Add(new Spec("notempty", "notempty", static v => IsMissing(v, required: false)) { MinLength = 1 });

    public FieldRuleBuilder<T, TMember> Length(int max) => Length(0, max);

    public FieldRuleBuilder<T, TMember> Length(int min, int max)
    {
        if (min < 0 || max < min) throw new ArgumentOutOfRangeException(nameof(max), $"Invalid length bounds {min}..{max}.");
        string code = max == int.MaxValue ? "len.min" : min == 0 ? "len.max" : "len";
        RuleArgs args = max == int.MaxValue ? new RuleArgs(RawValue.From(min))
            : min == 0 ? new RuleArgs(RawValue.From(max))
            : new RuleArgs(RawValue.From(min), RawValue.From(max));
        return Add(new Spec(FluentRuleKey.Length(min, max), code, v => LengthOf(v) is int n and >= 0 && !Checks.LengthWithin(n, min, max))
        {
            Args = args,
            MinLength = min > 0 ? min : null,
            MaxLength = max != int.MaxValue ? max : null,
        });
    }

    /// <summary>Inclusive range. <paramref name="coerce"/> = clamp instead of failing (query paging); applied by the pipeline.</summary>
    public FieldRuleBuilder<T, TMember> Range(TMember min, TMember max, bool coerce = false)
    {
        var cmp = Comparer<TMember>.Default;
        if (cmp.Compare(min, max) > 0) throw new ArgumentOutOfRangeException(nameof(max), $"Range min {min} > max {max}.");
        RawValue rawMin = FluentRuleKey.Raw(min), rawMax = FluentRuleKey.Raw(max);
        return Add(new Spec(FluentRuleKey.Range(min, max, coerce), "range",
            coerce ? null : v => v is not null && (cmp.Compare(v, min) < 0 || cmp.Compare(v, max) > 0))
        {
            // Clamping rebuilds T (`with`), which only generated code can do — no reflection at runtime.
            CompiledOnly = coerce ? "Range(coerce: true)" : null,
            Args = rawMin.IsNone || rawMax.IsNone ? RuleArgs.None : new RuleArgs(rawMin, rawMax),
            Min = rawMin,
            Max = rawMax,
        });
    }

    public FieldRuleBuilder<T, TMember> Pattern([StringSyntax(StringSyntaxAttribute.Regex)] string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        // Runtime fallback only (the generator uses [GeneratedRegex] for constant patterns).
        var regex = new Regex(pattern, RegexOptions.CultureInvariant);
        return Add(new Spec(FluentRuleKey.Pattern(pattern), "pattern",
            v => Text(v) is { Length: > 0 } s && !regex.IsMatch(s)));
    }

    public FieldRuleBuilder<T, TMember> Email()
        => Add(new Spec("email", RuleCodes.Email, static v => Text(v) is { Length: > 0 } s && !BuiltIn.Email.IsValid(s))
        {
            Format = RuleCodes.Email,
        });

    /// <summary>
    /// Custom rule (<see cref="IValueRule{T}"/>, <see cref="ITextRule"/>, <see cref="IAsyncValueRule{T}"/>). Static
    /// abstract rules can only be called from generated code, so the message must be <c>partial</c> and in the same
    /// compilation as the configuration; otherwise registry build throws.
    /// </summary>
    public FieldRuleBuilder<T, TMember> With<TRule>()
        => Add(new Spec(FluentRuleKey.With(typeof(TRule).FullName, null), "with", null) { CompiledOnly = typeof(TRule).FullName });

    /// <inheritdoc cref="With{TRule}()"/>
    public FieldRuleBuilder<T, TMember> With<TRule, TArg>(TArg arg)
        => Add(new Spec(FluentRuleKey.With(typeof(TRule).FullName, FluentRuleKey.Format(arg)), "with", null)
        {
            CompiledOnly = typeof(TRule).FullName,
        });

    /// <summary>Makes every rule declared so far in this chain conditional.</summary>
    public FieldRuleBuilder<T, TMember> When(Func<T, bool> condition,
        [CallerArgumentExpression(nameof(condition))] string expr = "")
    {
        ArgumentNullException.ThrowIfNull(condition);
        foreach (var s in _specs)
        {
            if (s.When is not null) continue;
            s.When = condition;
            s.WhenExpr = expr;
        }
        return this;
    }

    /// <summary>Severity of the rule declared just before.</summary>
    public FieldRuleBuilder<T, TMember> Severity(Severity severity)
    {
        Last(nameof(Severity)).Severity = severity;
        return this;
    }

    /// <summary>Overrides the code of the rule declared just before.</summary>
    public FieldRuleBuilder<T, TMember> Code(RuleCode code)
    {
        Last(nameof(Code)).Code = code;
        return this;
    }

    /// <summary>Restricts every rule declared so far in this chain to a profile (<c>[ValidatesAs(Profile = …)]</c>).</summary>
    public FieldRuleBuilder<T, TMember> Profile(string profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var s in _specs) s.Profile ??= profile;
        return this;
    }

    internal override void Register()
    {
        List<Spec>? leftovers = null;
        foreach (var s in _specs)
        {
            string key = FluentRuleKey.Full(_path, s.KeyCore, s.WhenExpr, s.Profile);
            if (CompiledFluentRules<T>.Contains(key)) continue;
            if (s.CompiledOnly is { } rule)
                throw new NotSupportedException(
                    $"{typeof(T).Name}.{_path}: {(rule.StartsWith("Range", StringComparison.Ordinal) ? rule : "With<" + rule + ">")} was not compiled by YFex.Validation.Generator " +
                    "(non-constant arguments, or the message is not partial). " +
                    "Declare the message 'partial' in the same project as its configuration, or use [ValidateWith<T>] on the message.");
            (leftovers ??= []).Add(s);
        }
        if (leftovers is null) return;

        // Profile-scoped rules run under ProfileId.All (the whole-object Validate), like the generated code.
        Spec[] specs = leftovers.ToArray();
        FieldPath path = FieldPath.Of(_path);
        Func<T, TMember> get = _get;
        var chainKey = new System.Text.StringBuilder(_path).Append("|chain");
        foreach (var s in specs) chainKey.Append('|').Append(FluentRuleKey.Full("", s.KeyCore, s.WhenExpr, s.Profile));

        RuntimeRules<T>.Register(chainKey.ToString(), (in T value, ref ViolationWriter w) =>
        {
            TMember member = get(value);
            foreach (Spec s in specs)
            {
                if (s.Fails is null || (s.When is not null && !s.When(value)) || !s.Fails(member)) continue;
                w.Add(new Violation(in path, s.Code, s.Severity, s.Args));
                if (s.Severity == Validation.Severity.Error) return;   // an error stops the member's chain (as generated)
            }
        });

        foreach (var s in specs)
        {
            if (s.When is not null || s.Profile is not null || s.Severity != Validation.Severity.Error) continue;
            if (!s.Required && s.MinLength is null && s.MaxLength is null && s.Min.IsNone && s.Format.IsEmpty) continue;
            RuntimeRules<T>.RegisterConstraints(_path, FieldConstraints.Create(
                required: s.Required, minLength: s.MinLength, maxLength: s.MaxLength, min: s.Min, max: s.Max, format: s.Format));
        }
    }

    private FieldRuleBuilder<T, TMember> Add(Spec spec)
    {
        _specs.Add(spec);
        return this;
    }

    private Spec Last(string modifier)
        => _specs.Count > 0 ? _specs[^1]
            : throw new InvalidOperationException($"{modifier}(...) must follow a rule (e.g. .Required().{modifier}(...)).");

    private static bool IsMissing(TMember value, bool required)
    {
        if (value is null) return true;
        if (typeof(TMember) == typeof(string))
        {
            string s = Unsafe.As<TMember, string>(ref value);
            return required ? string.IsNullOrWhiteSpace(s) : s.Length == 0;
        }
        if (value is ICollection c) return c.Count == 0;
        return default(TMember) is not null && EqualityComparer<TMember>.Default.Equals(value, default!);
    }

    private static int? LengthOf(TMember value) => value switch
    {
        null => null,
        string s => s.Length,
        ICollection c => c.Count,
        _ => throw new NotSupportedException($"Length(...) is not applicable to {typeof(TMember).Name}."),
    };

    private static string? Text(TMember value) => value as string;

    private sealed class Spec(string keyCore, RuleCode code, Func<TMember, bool>? fails)
    {
        public string KeyCore { get; } = keyCore;
        public RuleCode Code { get; set; } = code;
        public Func<TMember, bool>? Fails { get; } = fails;   // null = nothing to check at runtime (coerce / compiled-only)
        public Severity Severity { get; set; } = Validation.Severity.Error;
        public RuleArgs Args { get; init; }
        public Func<T, bool>? When { get; set; }
        public string? WhenExpr { get; set; }
        public string? Profile { get; set; }
        public string? CompiledOnly { get; init; }
        public bool Required { get; init; }
        public int? MinLength { get; init; }
        public int? MaxLength { get; init; }
        public RawValue Min { get; init; }
        public RawValue Max { get; init; }
        public RuleCode Format { get; init; }
    }
}

internal sealed class EachChain<T, TItem>(string path, Func<T, IReadOnlyList<TItem>?> get) : FluentChain<T>
    where TItem : IValidatable<TItem>
{
    internal override void Register()
    {
        string key = FluentRuleKey.Full(path, "each", null, null);
        if (CompiledFluentRules<T>.Contains(key)) return;
        FieldPath itemPath = FieldPath.Of(path + "[]");
        RuntimeRules<T>.Register(key, (in T value, ref ViolationWriter w) =>
        {
            if (get(value) is not { } items) return;
            for (int i = 0; i < items.Count; i++)
            {
                TItem item = items[i];
                if (item is null) continue;
                Validator.ValidateAt(in item, ref w, itemPath.WithIndex(i));
            }
        });
    }
}

/// <summary>
/// Canonical keys shared by YFex.Validation.Generator (which lists compiled fluent rules in
/// <see cref="CompiledFluentRules{T}"/>) and <see cref="ValidationBuilder{T}"/> (which skips them). Format:
/// <c>{path}|{rule}[|when:{lambda text}][|profile:{name}]</c> where rule is <c>required</c>, <c>notempty</c>,
/// <c>len:{min}:{max}</c>, <c>range:{min}:{max}:{0|1}</c>, <c>pattern:{regex}</c>, <c>email</c>,
/// <c>with:{rule type FullName}[:{arg}]</c> or <c>each</c>; numbers use the invariant culture.
/// </summary>
public static class FluentRuleKey
{
    public static string Full(string path, string rule, string? whenExpr, string? profile)
    {
        string key = path.Length == 0 ? rule : path + "|" + rule;
        if (whenExpr is not null) key += "|when:" + whenExpr;
        if (profile is not null) key += "|profile:" + profile;
        return key;
    }

    public static string Length(int min, int max)
        => string.Create(CultureInfo.InvariantCulture, $"len:{min}:{max}");

    public static string Range<TValue>(TValue min, TValue max, bool coerce)
        => "range:" + Format(min) + ":" + Format(max) + (coerce ? ":1" : ":0");

    public static string Pattern(string pattern) => "pattern:" + pattern;

    public static string With(string? ruleFullName, string? arg)
        => arg is null ? "with:" + ruleFullName : "with:" + ruleFullName + ":" + arg;

    /// <summary>Invariant text of a rule argument (matches the generator's rendering of constants).</summary>
    public static string Format<TValue>(TValue value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// Path template of a member-access lambda text: <c>c =&gt; c.Name</c> → <c>Name</c>,
    /// <c>static (x) =&gt; x.Address.City</c> → <c>Address.City</c>.
    /// </summary>
    public static string PathOf(string lambdaText)
    {
        ArgumentNullException.ThrowIfNull(lambdaText);
        int arrow = lambdaText.IndexOf("=>", StringComparison.Ordinal);
        if (arrow > 0)
        {
            ReadOnlySpan<char> param = lambdaText.AsSpan(0, arrow).Trim();
            if (param.StartsWith("static ", StringComparison.Ordinal)) param = param[7..].Trim();
            param = param.Trim("()").Trim();
            int space = param.LastIndexOf(' ');
            if (space >= 0) param = param[(space + 1)..];

            ReadOnlySpan<char> body = lambdaText.AsSpan(arrow + 2).Trim();
            if (param.Length > 0 && body.Length > param.Length + 1 && body.StartsWith(param, StringComparison.Ordinal)
                && body[param.Length] == '.' && IsMemberChain(body[(param.Length + 1)..]))
                return body[(param.Length + 1)..].ToString();
        }
        throw new ArgumentException(
            $"Expected a member access lambda such as 'c => c.Name', got '{lambdaText}'.", nameof(lambdaText));
    }

    private static bool IsMemberChain(ReadOnlySpan<char> s)
    {
        bool start = true;
        foreach (char c in s)
        {
            if (c == '.') { if (start) return false; start = true; continue; }
            if (!(char.IsLetter(c) || c == '_' || (!start && char.IsDigit(c)))) return false;
            start = false;
        }
        return !start;
    }

    /// <summary>Non-boxing <see cref="RawValue"/> of a bound; <see cref="RawValue.None"/> when the type has no raw form.</summary>
    public static RawValue Raw<TValue>(TValue value) => RawValueConvert.From(value);
}
