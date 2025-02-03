namespace Act.Fiscal.NFe.Tributo;

public class IcmsSn102
{
    [XmlElement("orig")] public OrigemMercadoria? Origem { get; set; }

    [XmlElement("CSOSN")] public Csosn Csosn { get; set; }
}