using System.Xml.Serialization;
using Act.Xml;

namespace Act.Fiscal.NFe.Tributo;

public class Imposto
{
    [XmlElement("vTotTrib")] public decimal ValorTotalTributos { get; set; }

}

public class Icms15
{
    [XmlElement("orig")] public OrigemMercadoria OrigemMercadoria { get; set; }

    [XmlElement("CST")] public string Cst { get; } = "15";

    [XmlElement("qBCMono")] public decimal? QuantidadeTributada { get; set; }


    /// <summary>
    /// Alíquota <i>ad rem</i> do ICMS, estabelecida na legislação para o produto
    /// </summary>
    [XmlElement("adRemICMS")] public decimal AliquotaAdRemDoImposto { get; set; }

    [XmlElement("vICMSMono")] public decimal ValorIcmsProprio { get; set; }

    /// <summary>
    /// Quantidade tributada sujeita a retenção.
    /// </summary>
    [XmlElement("qBCMonoReten")] public decimal? QuantidadeTributadaRetencao { get; set; }

    /// <summary>
    /// Alíquota <i>ad rem</i> do imposto com retenção.
    /// </summary>
    [XmlElement("adRemICMSReten")] public decimal AliquotaAdRemRetencao { get; set; }

    [XmlElement("vICMSMonoReten")] public decimal IcmsRetencao { get; set; }

    /// <summary>
    /// Percentual de redução do valor da alíquota <i>ad rem</i> do ICMS
    /// </summary>
    [XmlElement("pRedAdRem")] public decimal? ReducaoAliquotaAdRem { get; set; }

    [XmlElement("motRedAdRem")] public MotivoRetencaoAdRem MotivoRetencaoAdRem { get; set; }
}

public enum MotivoRetencaoAdRem
{
    TransporteColetivo = 1,
    Outros = 9
}