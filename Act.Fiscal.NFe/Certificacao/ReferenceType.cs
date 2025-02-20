using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Certificacao;

internal sealed class ReferenceType
{
    [XmlElement("Transforms")] public TransformType Transforms { get; set; }

    [XmlElement("DigestMethod")] public DigestMethod DigestMethod { get; set; }

    [XmlElement("DigestValue")] public byte[] DigestValue { get; set; }

    [XmlAttribute("Id")] public string Id { get; set; }

    [XmlAttribute("URI")] public string URI { get; set; }

    [XmlAttribute("Type")] public string Type { get; set; }
}