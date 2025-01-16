namespace Act.Fiscal.NFe.Documento;

/// <summary>
/// Fonte onde a nota foi emitida
/// </summary>
public enum ProcessoEmissaoNF
{
    /// <summary>
    /// Emissão de NF-e com aplicativo do contribuinte.
    /// </summary>
    AplicativoContribuinte = 0,
    /// <summary>
    ///Emissão de NF-e avulsa pelo Fisco.
    /// </summary>
    AvulsaFisco = 1,
    /// <summary>
    ///Emissão de NF-e avulsa pelo contribuinte com seu certificado digital, através do site do Fisco.
    /// </summary>
    AvulsaSiteFisco = 2,
    /// <summary>
    ///Emissão de NF-e pelo contribuinte com aplicativo fornecido pelo Fisco.
    /// </summary>
    AvulsaAplicativoFisco = 3
}