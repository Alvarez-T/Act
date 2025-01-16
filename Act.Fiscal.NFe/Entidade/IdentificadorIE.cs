namespace Act.Fiscal.NFe.Entidade;

/// <summary>
/// Identificador da Inscrição Estadual (IE) do destinatário.
/// </summary>
public enum IdentificadorIE
{
    /// <summary>
    /// Contribuinte ICMS pagamento à vista.
    /// </summary>
    Contribuinte = 1,

    /// <summary>
    /// Contribuinte isento de inscrição.
    /// </summary>
    Isento = 2,

    /// <summary>
    /// Não Contribuinte.
    /// </summary>
    NaoContribuinte = 3,
}