using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record IcmsSn101
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CSOSN")] public Csosn Csosn => "101";

    [XmlElement("pCredSN")] public Percentual AliquotaCreditoIcms { get; set; }

    [XmlElement("vCredICMSSN")] public decimal ValorCreditoIcms { get; set; }
}