using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class DesoneracaoIcmsSt
{
    [XmlElement("vICMSSTDeson")] public decimal ValorIcmsSTDesonerado { get; set; }

    [XmlElement("motDesICMSST")] public MotivoDesoneracaoIcmsST MotivoDesoneracaoIcmsST { get; set; }
}