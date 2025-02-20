using System.Xml.Serialization;
using Act.Fiscal.NFe.Certificacao;

namespace Act.Fiscal.NFe.ProcessamentoXml;

internal sealed class ProtocoloProcessamentoNFe
{
    [XmlElement("infProt")] public DadosProcessamento DadosProcessamento { get; set; }

    [XmlElement("Signature", Namespace = "http://www.w3.org/2000/09/xmldsig#")]
    public SignatureType Signature { get; set; }

    [XmlAttribute("versao")] public string Versao { get; set; }
}