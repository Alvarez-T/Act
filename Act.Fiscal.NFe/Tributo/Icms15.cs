using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record Icms15
{
    [XmlElement("orig")] public OrigemMercadoria OrigemMercadoria { get; init; }

    [XmlElement("CST")] public Cst Cst { get; } = "15";

    [XmlElement("qBCMono")] public decimal? QuantidadeTributada { get; init; }


    /// <summary>
    /// Alíquota <i>ad rem</i> do ICMS, estabelecida na legislação para o produto
    /// </summary>
    [XmlElement("adRemICMS")] public decimal AliquotaAdRemDoImposto { get; init; }

    [XmlElement("vICMSMono")] public decimal ValorIcmsProprio { get; init; }

    /// <summary>
    /// Quantidade tributada sujeita a retenção.
    /// </summary>
    [XmlElement("qBCMonoReten")] public decimal? QuantidadeTributadaRetencao { get; init; }

    /// <summary>
    /// Alíquota <i>ad rem</i> do imposto com retenção.
    /// </summary>
    [XmlElement("adRemICMSReten")] public decimal AliquotaAdRemRetencao { get; init; }

    [XmlElement("vICMSMonoReten")] public decimal IcmsRetencao { get; init; }

    /// <summary>
    /// Percentual de redução do valor da alíquota <i>ad rem</i> do ICMS
    /// </summary>
    [XmlElement("pRedAdRem")] public decimal? ReducaoAliquotaAdRem { get; init; }

    [XmlElement("motRedAdRem")] public MotivoRetencaoAdRem? MotivoRetencaoAdRem { get; init; }
}