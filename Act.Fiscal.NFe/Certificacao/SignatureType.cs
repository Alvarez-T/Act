using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Certificacao;

[XmlRoot("SignatureType")]
internal sealed class SignatureType
{
    [XmlElement("SignedInfo")] public SignedInfoType SignedInfo { get; set; }

    [XmlElement("SignatureValue")] public string SignatureValue { get; set; }

    [XmlElement("KeyInfo")] public KeyInfoType KeyInfo { get; set; }

    [XmlAttribute("Id")] public string Id { get; set; }
}