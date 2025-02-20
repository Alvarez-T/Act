using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Certificacao;

[XmlRoot("SignatureValueType")]
internal sealed class SignatureValueType
{
    [XmlText] public byte[] Value { get; set; }

    [XmlAttribute("Id")] public string Id { get; set; }
}