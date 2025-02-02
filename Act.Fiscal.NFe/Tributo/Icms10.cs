namespace Act.Fiscal.NFe.Tributo;

/// <summary>
/// Tributação pelo ICMS 10 - Tributada e com cobrança do ICMS por substituição tributária
/// </summary>
public class Icms10
{
    [XmlElement("orig")] public OrigemMercadoria OrigemMercadoria { get; set; }

    [XmlElement("CST")] public string Cst { get; set; }

    [XmlElement("modBC")] public ModalidadeBaseCalculoIcms ModalidadeBaseCalculo { get; set; }

    [XmlElement("vBC")] public decimal BaseCalculo { get; set; }

    [XmlElement("pICMS")] public decimal Aliquota { get; set; }

    [XmlElement("vICMS")] public decimal ValorIcms { get; set; }

    [XmlElement("vBC")] public decimal? BaseCalculoFcp { get; set; }

    /// <summary>
    /// Percentual de ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("pFCP")] public decimal? PercentualFcp { get; set; }

    /// <summary>
    /// Valor do ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("vFCP")] public decimal? ValorFcp { get; set; }

    [XmlElement("modBCST")] public ModalidadeBaseCalculoIcmsST ModalidadeBaseCalculoST { get; set; }

    /// <summary>
    /// Percentual da margem de valor Adicionado do ICMS ST
    /// </summary>
    [XmlElement("pMVAST")] public decimal? MargemValorAdicionadoST { get; set; }

    [XmlElement("pRedBCST")] public decimal? PercentualReducaoBaseCalculoST { get; set; }

    /// <summary>
    /// Base de cálculo do ICMS ST
    /// </summary>
    [XmlElement("vBCST")] public decimal? BaseCalculoST { get; set; }

    /// <summary>
    /// Alíquota do ICMS ST
    /// </summary>
    [XmlElement("pICMSST")] public decimal? AliquotaST { get; set; }

    [XmlElement("vICMSST")] public decimal? ValorIcmsST { get; set; }

    /// <summary>
    /// Valor da Base de cálculo do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vBCFCPST")] public decimal? BaseCalculoFcpPorST { get; set; }

    /// <summary>
    /// Percentual de FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("pFCPST")] public decimal? PercentualFcpPorST { get; set; }

    /// <summary>
    /// Valor do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vFCPST")] public decimal? ValorFcpPorST { get; set; }

    [XmlElement("vICMSSTDeson")] public decimal? ValorIcmsSTDesonerado { get; set; }

    [XmlElement("motDesICMSST")] public MotivoDesoneracaoIcmsST? MotivoDesoneracaoIcmsST { get; set; }

}