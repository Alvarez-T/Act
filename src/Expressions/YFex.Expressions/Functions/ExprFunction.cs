using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using YFex.Helpers;

namespace YFex.Expressions;

/// <summary>Implementation of an expression function: pure, synchronous, no I/O (data comes from the schema the host exposes).</summary>
public delegate RawValue ExprFunctionFn(ReadOnlySpan<RawValue> args);

/// <summary>What a generic parameter (<c>T</c>) of a function accepts.</summary>
public enum TypeConstraint : byte
{
    Any = 0,
    /// <summary>Supports &lt; &gt; (numbers, amounts, dates, text).</summary>
    Comparable,
    /// <summary>Plain numbers or amounts (Money, Mass, Percentual…).</summary>
    Numeric,
    /// <summary>Amounts only (app value types).</summary>
    Amount,
}

/// <summary>A parameter/return type of a function: a fixed <see cref="ExprType"/> or generic parameter <see cref="Generic"/>.</summary>
public readonly record struct TypeRef(ExprType? Fixed, int Generic = -1)
{
    public static TypeRef Of(ExprType type) => new(type);
    public static TypeRef T => new(null, 0);
    public static TypeRef U => new(null, 1);
    public static implicit operator TypeRef(ExprType type) => new(type);

    public override string ToString() => Fixed is { } f ? f.ToString() : Generic == 0 ? "T" : "U";
}

public sealed record ExprFunctionParameter(string Name, TypeRef Type, string? Description = null);

/// <summary>
/// A function expressions can call (<c>{"round": [x, 2]}</c>). Three origins, one shape:
/// <list type="bullet">
/// <item>built-ins (<see cref="BuiltInFunctions"/>);</item>
/// <item>app/domain functions written in C# (<c>[ExprFunction]</c> methods — the generator emits descriptors), or
///   <see cref="Create"/> by hand;</item>
/// <item><b>contracts</b> — an abstract signature (<see cref="Contract"/>, or an <c>[ExprFunctions]</c> interface with static
///   abstract methods) that a library declares and the domain app implements later (<see cref="FunctionCatalog.Implement"/>).
///   Expressions may be authored against a contract; a host without the implementation rejects them when binding.</item>
/// </list>
/// Generic parameters (<see cref="Generics"/>) keep functions domain-agnostic: <c>max&lt;T: Comparable&gt;(T, T) → T</c> works for
/// Money, Mass, dates…. <see cref="IsDeterministic"/> = false marks functions that read context such as the clock or the user
/// (screen formulas may use them; hosts that need replayable results — YFex.Rules — reject them at bind time).
/// </summary>
public sealed class ExprFunction
{
    private ExprFunction(string name, ImmutableArray<ExprFunctionParameter> parameters, TypeRef returns, ImmutableArray<TypeConstraint> generics,
        string description, ExprFunctionFn? implementation, string? category, bool deterministic)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A function needs a name.", nameof(name));
        if (ExprJson.IsReserved(name)) throw new ArgumentException($"'{name}' is a reserved operator name.", nameof(name));
        Name = name;
        Parameters = parameters;
        Returns = returns;
        Generics = generics;
        Description = description;
        Implementation = implementation;
        Category = category;
        IsDeterministic = deterministic;
    }

    public string Name { get; }
    public ImmutableArray<ExprFunctionParameter> Parameters { get; }
    public TypeRef Returns { get; }
    /// <summary>Constraints of generic parameters T (0), U (1).</summary>
    public ImmutableArray<TypeConstraint> Generics { get; }
    /// <summary>For editors and AI (required in practice: good descriptions are a functional requirement, composer-design §12).</summary>
    public string Description { get; }
    public string? Category { get; }
    public ExprFunctionFn? Implementation { get; }
    public bool IsContract => Implementation is null;
    /// <summary>Same arguments → same result, always (no clock, user, random…). Default true.</summary>
    public bool IsDeterministic { get; }

    public static ExprFunction Create(string name, TypeRef returns, ImmutableArray<ExprFunctionParameter> parameters, ExprFunctionFn implementation,
        string description, ImmutableArray<TypeConstraint> generics = default, string? category = null, bool deterministic = true)
        => new(name, parameters, returns, generics.IsDefault ? [] : generics, description,
            implementation ?? throw new ArgumentNullException(nameof(implementation)), category, deterministic);

    /// <summary>An abstract function: signature + description, implemented by the domain app (<see cref="FunctionCatalog.Implement"/>).</summary>
    public static ExprFunction Contract(string name, TypeRef returns, ImmutableArray<ExprFunctionParameter> parameters, string description,
        ImmutableArray<TypeConstraint> generics = default, string? category = null, bool deterministic = true)
        => new(name, parameters, returns, generics.IsDefault ? [] : generics, description, null, category, deterministic);

    /// <summary>The same signature with <paramref name="implementation"/>.</summary>
    public ExprFunction WithImplementation(ExprFunctionFn implementation)
        => new(Name, Parameters, Returns, Generics, Description, implementation ?? throw new ArgumentNullException(nameof(implementation)),
            Category, IsDeterministic);

    /// <summary><c>round(value: T, decimals: Int64) → T</c> (catalog text for editors, AI, version hashes).</summary>
    public string Signature
    {
        get
        {
            var sb = new StringBuilder(Name);
            if (!Generics.IsEmpty)
            {
                sb.Append('<');
                for (int i = 0; i < Generics.Length; i++) sb.Append(i > 0 ? ", " : "").Append(i == 0 ? "T" : "U").Append(": ").Append(Generics[i]);
                sb.Append('>');
            }
            sb.Append('(');
            for (int i = 0; i < Parameters.Length; i++) sb.Append(i > 0 ? ", " : "").Append(Parameters[i].Name).Append(": ").Append(Parameters[i].Type);
            return sb.Append(") → ").Append(Returns).ToString();
        }
    }

    public override string ToString() => Signature;
}

/// <summary>
/// The functions available to expressions — immutable; layer it: <c>FunctionCatalog.BuiltIn.With(CalendarFunctions.All)
/// .Implement("isHoliday", …)</c>. <see cref="Version"/> hashes every signature + whether it is implemented + whether it is
/// deterministic, so programs compiled under one catalog are never reused with another.
/// </summary>
public sealed class FunctionCatalog
{
    private readonly FrozenDictionary<string, ExprFunction> _functions;

    private FunctionCatalog(FrozenDictionary<string, ExprFunction> functions)
    {
        _functions = functions;
        var names = new List<string>(functions.Keys);
        names.Sort(StringComparer.Ordinal);
        var h = StableHash64.Create(0xF0C7);
        foreach (var n in names)
        {
            var f = functions[n];
            h.Append(f.Signature);
            h.Append(f.IsContract);
            if (!f.IsDeterministic) h.Append("nondeterministic");     // deterministic catalogs keep their pre-Phase-X version
        }
        Version = h.Finish();
    }

    public static FunctionCatalog Empty { get; } = new(FrozenDictionary<string, ExprFunction>.Empty);

    /// <summary>The built-in functions (math, percent, text, dates) — all deterministic.</summary>
    public static FunctionCatalog BuiltIn { get; } = Empty.With(BuiltInFunctions.All.AsSpan());

    public ulong Version { get; }
    public IEnumerable<ExprFunction> All => _functions.Values;
    public int Count => _functions.Count;

    public bool TryGet(string name, out ExprFunction function) => _functions.TryGetValue(name, out function!);

    /// <summary>Adds (or replaces, by name) functions — contracts included.</summary>
    public FunctionCatalog With(params ReadOnlySpan<ExprFunction> functions)
    {
        var d = new Dictionary<string, ExprFunction>(_functions, StringComparer.Ordinal);
        foreach (var f in functions) d[f.Name] = f;
        return new(d.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Implements the contract <paramref name="name"/> (the declared signature is kept).</summary>
    public FunctionCatalog Implement(string name, ExprFunctionFn implementation)
    {
        if (!_functions.TryGetValue(name, out var f)) throw new ArgumentException($"No function '{name}' is declared.", nameof(name));
        return With(f.WithImplementation(implementation));
    }

    /// <summary>Contracts nobody implemented yet (startup check).</summary>
    public IEnumerable<ExprFunction> Unimplemented
    {
        get { foreach (var f in _functions.Values) if (f.IsContract) yield return f; }
    }
}
