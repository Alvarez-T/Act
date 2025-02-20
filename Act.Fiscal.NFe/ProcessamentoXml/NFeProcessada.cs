using System.Xml.Serialization;

namespace Act.Fiscal.NFe.ProcessamentoXml;

[XmlRoot("TNfeProc")]
internal sealed class NFeProcessada
{
    [XmlElement("NFe")] public NFe NFe { get; set; }

    [XmlElement("protNFe")] public ProtocoloProcessamentoNFe ProtocoloProcessamentoNFe { get; set; }
}