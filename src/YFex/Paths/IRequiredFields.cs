namespace YFex;

/// <summary>
/// Which optional fields of a message a configuration made required (forms ⚑FM62-1, ⚑FM72). Asked only for fields the
/// message marks <c>[Requirable]</c> (YFex.Validation); the generated validator checks presence when it answers true, on the
/// client (the form, through <c>[ValidatesAs]</c>) and on the server (Cqrs stage 1) alike.
/// <para>On the server the lookup is the caller's tenant's settings, loaded on the server, so a client cannot turn a
/// requirement off. On the client it is the same settings plus the code layers of the form configuration. Several
/// lookups combine by "any says required": a layer may make a field required, never optional again.</para>
/// </summary>
/// <example><code>
/// public sealed partial record CreateCustomerCommand(
///     PartyName Name,
///     [property: Requirable] Cpf? Document,
///     [property: Requirable] string? BillingCode) : ICommand;
///
/// // generated: if (w.RequiredFields?.IsRequired(typeof(CreateCustomerCommand), Fields.BillingCode) == true
/// //                 &amp;&amp; string.IsNullOrWhiteSpace(v.BillingCode)) → "required"
/// </code></example>
public interface IRequiredFields
{
    /// <summary>
    /// True when <paramref name="path"/> of <paramref name="message"/> must have a value. <paramref name="path"/> is the
    /// member's generated path (its template, relative to <paramref name="message"/>; a nested record is asked with its
    /// own type and path).
    /// </summary>
    bool IsRequired(Type message, in FieldPath path);
}
