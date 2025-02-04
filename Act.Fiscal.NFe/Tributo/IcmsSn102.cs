using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record IcmsSn102
{
    [XmlElement("orig")] public OrigemMercadoria? Origem { get; set; }

    [XmlElement("CSOSN")] public Csosn Csosn { get; set; }
}