using System.Xml.Serialization;
using Act.Fiscal.NFe.Certificacao;
using Act.Fiscal.NFe.Documento;

namespace Act.Fiscal.NFe;

internal sealed class NFe
{
    [XmlElement("infNFe")] public InformacoesNFe InformacoesNFe { get; set; }

    [XmlElement("infNFeSupl")] public InformacoesSuplementaresNFe InformacoesSuplementaresNFe { get; set; }

    [XmlElement("Signature", Namespace = "http://www.w3.org/2000/09/xmldsig#")]
    public SignatureType Signature { get; set; }

}