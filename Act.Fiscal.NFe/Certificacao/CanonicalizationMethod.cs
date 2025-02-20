using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Certificacao;

internal sealed class CanonicalizationMethod
{
    [XmlAttribute("Algorithm")] public string Algorithm { get; set; } = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";
}