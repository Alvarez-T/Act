using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Certificacao;

internal sealed class SignatureMethod
{
    [XmlAttribute("Algorithm")] public string Algorithm { get; set; } = "http://www.w3.org/2000/09/xmldsig#rsa-sha1";
}