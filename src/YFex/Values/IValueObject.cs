namespace YFex;

/// <summary>
/// A YFex value object (forms ⚑FM73): says whether it holds a value. <see langword="default"/> of every implementing type is
/// its empty value, and <see cref="object.ToString"/> of an empty value is <c>""</c>, so a screen never shows a placeholder
/// such as <c>00000000000</c> for "no CPF".
/// <para>Presence checks read it: YFex.Validation's <c>[Required]</c> and <c>[Requirable]</c> on a member of an implementing
/// type fail when <see cref="IsEmpty"/> is true, instead of comparing with <see langword="default"/>. Form inputs and
/// displays show an empty value as an empty box.</para>
/// </summary>
/// <example><code>
/// public readonly struct Cpf : IValueObject, …
/// {
///     private readonly string? _digits;
///     public bool IsEmpty =&gt; _digits is null;
///     public override string ToString() =&gt; _digits is null ? "" : Format(_digits);
/// }
///
/// default(Cpf).IsEmpty;          // true
/// default(Cpf).ToString();       // ""
/// </code></example>
public interface IValueObject
{
    /// <summary>True when the value holds nothing (its <see langword="default"/>).</summary>
    bool IsEmpty { get; }
}
