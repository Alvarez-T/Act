using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Certificacao;

internal sealed class DigestMethod
{
    [XmlAttribute("Algorithm")] public string Algorithm { get; set; }
}