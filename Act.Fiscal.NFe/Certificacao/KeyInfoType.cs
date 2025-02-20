using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Certificacao;

[XmlRoot("KeyInfoType")]
internal sealed class KeyInfoType
{
    [XmlElement("X509Data")] public X509DataType X509Data { get; set; }

    [XmlAttribute("Id")] public string Id { get; set; }
}