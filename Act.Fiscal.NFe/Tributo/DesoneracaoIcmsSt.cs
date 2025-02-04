using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

public class DesoneracaoIcmsSt
{
    [XmlElement("vICMSSTDeson")] public decimal ValorIcmsSTDesonerado { get; set; }

    [XmlElement("motDesICMSST")] public MotivoDesoneracaoIcmsST MotivoDesoneracaoIcmsST { get; set; }
}