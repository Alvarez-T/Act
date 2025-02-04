using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record Icms02
{
    [XmlElement("orig")] public OrigemMercadoria OrigemMercadoria { get; init; }

    [XmlElement("CST")] public Cst Cst { get; } = "02";

    [XmlElement("qBCMono")] public decimal? QuantidadeTributada { get; init; }


    /// <summary>
    /// Alíquota <i>ad rem</i> do ICMS, estabelecida na legislação para o produto
    /// </summary>
    [XmlElement("adRemICMS")] public decimal AliquotaAdRemDoImposto { get; init; }

    [XmlElement("vICMSMono")] public decimal ValorIcmsMonofasico { get; init; }
}