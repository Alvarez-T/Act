using System.Xml.Serialization;
using Act.Xml;

namespace Act.Fiscal.NFe;

[XmlRoot("infNFe")]
public class InformacoesNFe
{
    [XmlAttribute("versao")] internal string Versao => "4.00";
    [XmlAttribute("Id")] public ChaveNFe Chave { get; set; }
}

public class InformacoesSuplementaresNFe
{

}

public readonly struct SerieFiscal : IEquatable<SerieFiscal>
{
    public SerieFiscal(string serie)
    {
        
    }
    public bool Equals(SerieFiscal other)
    {
        throw new NotImplementedException();
    }
}