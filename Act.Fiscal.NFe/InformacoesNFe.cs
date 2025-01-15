using System.Xml.Serialization;
using Act.Xml;

namespace Act.Fiscal.NFe;

[XmlRoot("infNFe")]
public class InformacoesNFe
{
    [XmlAttribute("versao")] internal string Versao => "4.00";

    [XmlAttribute("Id")] public ChaveNFe Chave { get; set; }

    [XmlAttribute("ide")] public IdentificacaoNFe IdentificacaoNFe { get; set; }

    [XmlAttribute("emit")] public EmitenteNFe EmitenteNfe { get; set; }

    [XmlAttribute("avulso")] public EmitenteAvulso? EmitenteAvulso { get; set; }



}