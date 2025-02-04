using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

/// <summary>
/// Tributação pelo ICMS 10 - Tributada e com cobrança do ICMS por substituição tributária
/// </summary>
internal sealed record Icms10
{
    [XmlElement("orig")] public OrigemMercadoria OrigemMercadoria { get; init; }

    [XmlElement("CST")] public Cst Cst { get; init; }

    [XmlElement("modBC")] public ModalidadeBaseCalculoIcms ModalidadeBaseCalculo { get; init; }

    [XmlElement("vBC")] public decimal BaseCalculo { get; init; }

    [XmlElement("pICMS")] public decimal Aliquota { get; init; }

    [XmlElement("vICMS")] public decimal ValorIcms { get; init; }

    [XmlElement("vBC")] public decimal? BaseCalculoFcp { get; init; }

    /// <summary>
    /// Percentual de ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("pFCP")] public decimal? PercentualFcp { get; init; }

    /// <summary>
    /// Valor do ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("vFCP")] public decimal? ValorFcp { get; init; }

    [XmlElement("modBCST")] public ModalidadeBaseCalculoIcmsST ModalidadeBaseCalculoST { get; init; }

    /// <summary>
    /// Percentual da margem de valor Adicionado do ICMS ST
    /// </summary>
    [XmlElement("pMVAST")] public Percentual? MargemValorAdicionadoST { get; init; }

    [XmlElement("pRedBCST")] public Percentual? PercentualReducaoBaseCalculoST { get; init; }

    /// <summary>
    /// Base de cálculo do ICMS ST
    /// </summary>
    [XmlElement("vBCST")] public decimal? BaseCalculoIcmsST { get; init; }

    /// <summary>
    /// Alíquota do ICMS ST
    /// </summary>
    [XmlElement("pICMSST")] public decimal? AliquotaIcmsST { get; init; }

    [XmlElement("vICMSST")] public decimal? ValorIcmsST { get; set; }

    /// <summary>
    /// Valor da Base de cálculo do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vBCFCPST")] public decimal? BaseCalculoFcpPorST { get; init; }

    /// <summary>
    /// Percentual de FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("pFCPST")] public decimal? PercentualFcpPorST { get; init; }

    /// <summary>
    /// Valor do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vFCPST")] public decimal? ValorFcpPorST { get; init; }

    [XmlElement("vICMSSTDeson")] public decimal? ValorIcmsSTDesonerado { get; init; }

    [XmlElement("motDesICMSST")] public MotivoDesoneracaoIcmsST? MotivoDesoneracaoIcmsST { get; init; }

}