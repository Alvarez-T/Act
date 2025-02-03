using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

/// <summary>
/// Tributação monofásica sobre combustíveis cobrada anteriormente
/// </summary>
public class Icms61
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CST")] public Cst Cst => "61";

    [XmlElement("qBCMonoRet")] public decimal? QuantidadeRetida { get; set; }

    [XmlElement("adRemICMSRet")] public Percentual AliquotaAdRemIcmsRetida { get; set; }

    [XmlElement("vICMSMonoRet")] public decimal IcmsRetido { get; set; }
}