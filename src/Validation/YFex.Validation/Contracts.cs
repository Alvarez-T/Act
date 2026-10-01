namespace YFex.Validation;

/// <summary>
/// Implemented by the GENERATOR on every validatable type (never by hand). All members are static
/// abstract → callers dispatch at compile time (no instance, no DI, inlinable).
/// </summary>
public interface IValidatable<TSelf> where TSelf : IValidatable<TSelf>
{
    /// <summary>Runs all member rules, then invariants; true when no Error was added.</summary>
    static abstract bool Validate(in TSelf value, ref ViolationWriter w);

    /// <summary>
    /// Clamps every <c>Range(coerce: true)</c> member into its bounds (query paging, 09 §7). Generated for records and
    /// structs that have such rules; the default returns the value unchanged.
    /// </summary>
    static virtual TSelf Coerce(in TSelf value) => value;

    /// <summary>Runs the rules of ONE member (and nothing else). Used by UI per-field validation.</summary>
    static abstract bool ValidateMember(FieldId member, in TSelf value, ref ViolationWriter w);

    /// <summary>Static metadata for a member path (<c>"Name"</c>, <c>"Address.City"</c>). Switch-based.</summary>
    static abstract bool TryGetConstraints(ReadOnlySpan<char> path, out FieldConstraints constraints);

    /// <summary>Id of the member at <paramref name="path"/>.</summary>
    static abstract bool TryGetFieldId(ReadOnlySpan<char> path, out FieldId id);

    /// <summary>Member shapes for JSON Schema export (<see cref="JsonSchemaWriter"/>; generated). Null = not described.</summary>
    static virtual TypeShape? Shape => null;
}

// ── Value rules: used by [ValidateWith<T>] (D21/P1) ──────────────────────────────────────
// All static abstract → generated code calls them directly (inlinable, no instance, no DI).

/// <summary>
/// Rule over a typed value.
/// <code>
/// public readonly struct EvenNumber : IValueRule&lt;int&gt;
/// { public static RuleCode Code => "even"; public static bool IsValid(in int v) => (v &amp; 1) == 0; }
/// </code>
/// </summary>
public interface IValueRule<T>
{
    static abstract RuleCode Code { get; }
    static abstract bool IsValid(in T value);
}

/// <summary>
/// Rule with ONE compile-time argument taken from the attribute: <c>[ValidateWith&lt;MultipleOf&gt;(5)]</c>.
/// The generator emits <c>MultipleOf.IsValid(value, 5)</c> and passes <c>RawValue.From(5)</c> as message arg 0.
/// </summary>
public interface IValueRule<T, TArg>
{
    static abstract RuleCode Code { get; }
    static abstract bool IsValid(in T value, TArg arg);
}

/// <summary>
/// Text rule over a span → works for string properties AND raw UI text before parsing.
/// <code>
/// public readonly struct CpfOrCnpj : ITextRule
/// {
///     public static RuleCode Code => "br.document";
///     public static bool IsValid(ReadOnlySpan&lt;char&gt; v) => Cpf.TryParse(v, out _) || Cnpj.TryParse(v, out _);
/// }
/// </code>
/// </summary>
public interface ITextRule
{
    static abstract RuleCode Code { get; }
    static abstract bool IsValid(ReadOnlySpan<char> value);
}

/// <summary>Text rule with one compile-time argument.</summary>
public interface ITextRule<TArg>
{
    static abstract RuleCode Code { get; }
    static abstract bool IsValid(ReadOnlySpan<char> value, TArg arg);
}

/// <summary>
/// Async value rule (UI: debounced by YFex.State; server: runs in the context stage). Must be pure
/// with respect to the value; I/O allowed. Honour <paramref name="ct"/>.
/// </summary>
public interface IAsyncValueRule<T>
{
    static abstract RuleCode Code { get; }
    static abstract ValueTask<bool> IsValidAsync(T value, IServiceProvider services, CancellationToken ct);
}

/// <summary>
/// Whole-object async server rule (layer 1 "context"): uniqueness, FK exists, stock…
/// Instance interface, DI-constructed by Cqrs through generated factories (no Activator).
/// <code>
/// public sealed class StockAvailable(IStockQueries stock) : IContextRule&lt;CloseSale&gt;
/// {
///     public async ValueTask CheckAsync(CloseSale c, ViolationCollector v, CancellationToken ct)
///     { foreach (var m in await stock.MissingFor(c.SaleId, ct))
///           v.Add(FieldPath.Of("Lines[].Quantity", m.Index), "stock.out", RawValue.From(m.Available)); }
/// }
/// </code>
/// </summary>
public interface IContextRule<in T>
{
    ValueTask CheckAsync(T value, ViolationCollector violations, CancellationToken ct);
}
