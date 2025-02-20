using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Certificacao;

internal sealed class X509DataType
{
    [XmlElement("X509Certificate")] public byte[] X509Certificate { get; set; }
}