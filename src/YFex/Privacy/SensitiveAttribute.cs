namespace YFex.Privacy;

/// <summary>
/// Declares that a type holds personal data (privacy design 02 §4, 02 §5.5). Put it ONCE on the type, never on every member that
/// uses it:
/// <list type="bullet">
/// <item><b>Value objects</b> (<c>Cpf</c>, <c>Email</c>): every member of that type, anywhere, inherits the classification.</item>
/// <item><b>Record/entity types</b> whose content is sensitive as a whole (<c>DispensacaoControlado</c>: a person + a medication is
/// health data): every member is treated at least at this level, except the record's own id.</item>
/// </list>
/// The YFex.Privacy generator implements <see cref="IClassifiedData{TSelf}"/> on the (partial) type. On a raw member
/// (property, field, parameter) it is the rare exception for data that has no value object yet.
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field
              | AttributeTargets.Parameter, Inherited = false)]
public sealed class SensitiveAttribute(DataSensitivity sensitivity) : Attribute
{
    /// <summary>Protection level. Write only the personal levels (<see cref="DataSensitivity.Personal"/> and up).</summary>
    public DataSensitivity Sensitivity { get; } = sensitivity;

    /// <summary>
    /// <see cref="DataCategory"/> key. Default by sensitivity: Personal/PersonalIdentifier → <c>identification</c>,
    /// Financial → <c>financial</c>, Secret → <c>credentials</c>. Required for <see cref="DataSensitivity.SensitivePersonal"/>.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Display mask (02 §7). Positional pattern (<c>#</c> keep, <c>*</c> hide, other characters literal; alternatives separated by
    /// <c>|</c>, chosen by length) or a preset: <c>{hidden}</c>, <c>{email}</c>, <c>{initials}</c>, <c>{ip}</c>, <c>{last:N}</c>,
    /// <c>{first:N}</c>. Default by sensitivity (see <see cref="Masks.DefaultPreset"/>).
    /// </summary>
    public string? Mask { get; init; }

    /// <summary>
    /// Format passed to <see cref="ISpanFormattable.TryFormat"/> to obtain the text the mask applies to (e.g. <c>"F"</c> for the
    /// punctuated CPF <c>123.456.789-09</c>). Default: the type's default format.
    /// </summary>
    public string? MaskFormat { get; init; }

    /// <summary>Natural identifier of a person (CPF, e-mail, phone): can be used to find the person's records.</summary>
    public bool Identifier { get; init; }
}
