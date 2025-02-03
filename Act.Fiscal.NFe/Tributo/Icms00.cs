
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