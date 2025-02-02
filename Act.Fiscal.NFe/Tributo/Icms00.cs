using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

public class Icms00
{
    [XmlElement("orig")] public OrigemMercadoria OrigemMercadoria { get; set; }

    [XmlElement("CST")] public string Cst { get; set; }

    [XmlElement("modBC")] public ModalidadeBaseCalculoIcms ModalidadeBaseCalculo { get; set; }

    [XmlElement("vBC")] public decimal BaseCalculo { get; set; }

    [XmlElement("pICMS")] public decimal Aliquota { get; set; }

    [XmlElement("vICMS")] public decimal ValorIcms { get; set; }

    /// <summary>
    /// Percentual de ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("pFCP")] public decimal? PercentualFcp { get; set; }

    /// <summary>
    /// Valor do ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("vFCP")] public decimal? ValorFcp { get; set; }

}

public class Icms20
{
    [XmlElement("orig")]
    public OrigemMercadoria OrigemMercadoria { get; set; }

    [XmlElement("CST")]
    public string Cst { get; set; }

    [XmlElement("modBC")]
    public ModalidadeBaseCalculoIcms ModalidadeBaseCalculo { get; set; }

    [XmlElement("pRedBC")]
    public decimal PercentualReducaoBaseCalculo { get; set; }

    [XmlElement("vBC")]
    public decimal BaseCalculo { get; set; }

    [XmlElement("pICMS")]
    public decimal Aliquota { get; set; }

    [XmlElement("vICMS")]
    public decimal ValorIcms { get; set; }

    /// <summary>
    /// Valor da Base de cálculo do FCP.
    /// </summary>
    [XmlElement("vBCFCP")]
    public decimal? BaseCalculoFcp { get; set; }

    /// <summary>
    /// Percentual de ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("pFCP")]
    public decimal? PercentualFcp { get; set; }

    /// <summary>
    /// Valor do ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("vFCP")]
    public decimal? ValorFcp { get; set; }

    /// <summary>
    /// Valor do ICMS de desoneração.
    /// </summary>
    [XmlElement("vICMSDeson")]
    public decimal? ValorIcmsDesoneracao { get; set; }

    /// <summary>
    /// Motivo da desoneração do ICMS.
    /// </summary>
    [XmlElement("motDesICMS")]
    public MotivoDesoneracaoIcms? MotivoDesoneracaoIcms { get; set; }

    /// <summary>
    /// Indica se o valor do ICMS desonerado (vICMSDeson) deduz do valor do item (vProd).
    /// </summary>
    [XmlElement("indDeduzDeson")]
    public IndicadorDeducaoDesoneracao? IndicadorDeducaoDesoneracao { get; set; }
}

/// <summary>
/// Enumeração para indicador de dedução da desoneração do ICMS.
/// </summary>
public enum IndicadorDeducaoDesoneracao
{
    [XmlEnum("0")]
    NaoDeduzValor = 0,

    [XmlEnum("1")]
    DeduzValor = 1
}