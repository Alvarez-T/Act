
using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record Icms00
{
    [XmlElement("orig")] public OrigemMercadoria OrigemMercadoria { get; init; }

    [XmlElement("CST")] public string Cst { get; init; }

    [XmlElement("modBC")] public ModalidadeBaseCalculoIcms ModalidadeBaseCalculo { get; init; }

    [XmlElement("vBC")] public decimal BaseCalculo { get; init; }

    [XmlElement("pICMS")] public decimal Aliquota { get; init; }

    [XmlElement("vICMS")] public decimal ValorIcms { get; init; }

    /// <summary>
    /// Percentual de ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("pFCP")] public decimal? PercentualFcp { get; init; }

    /// <summary>
    /// Valor do ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("vFCP")] public decimal? ValorFcp { get; init; }

}