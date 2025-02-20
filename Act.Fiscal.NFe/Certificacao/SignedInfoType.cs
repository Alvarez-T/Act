using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Certificacao;

[XmlRoot("SignedInfoType")]
internal sealed class SignedInfoType
{
    [XmlElement("CanonicalizationMethod")] public CanonicalizationMethod CanonicalizationMethod { get; set; }

    [XmlElement("SignatureMethod")] public SignatureMethod SignatureMethod { get; set; }

    [XmlElement("Reference")] public ReferenceType Reference { get; set; }

    [XmlAttribute("Id")] public string Id { get; set; }
}