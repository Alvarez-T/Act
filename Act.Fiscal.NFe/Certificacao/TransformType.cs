using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Certificacao;

[XmlRoot("TransformType")]
internal sealed class TransformType
{
    [XmlElement("XPath")] public List<string> XPaths { get; set; }

    [XmlAttribute("Algorithm")] public string Algorithm { get; set; }
}