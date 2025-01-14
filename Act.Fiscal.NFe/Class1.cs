using System.Xml.Serialization;
using Act.Common.Types;
using Act.Location.Contracts;
using Act.Xml;

namespace Act.Fiscal.NFe;

[XmlRoot("infNFe")]
public class InformacoesNFe
{
    [XmlAttribute("versao")] internal string Versao => "4.00";

    [XmlAttribute("Id")] public ChaveNFe Chave { get; set; }

    [XmlAttribute("ide")] public IdentificacaoNFe IdentificacaoNFe { get; set; }

    [XmlAttribute("emit")] public EmitenteNFe EmitenteNfe { get; set; }

}

public class EmitenteNFe
{
    [XmlAttribute("CNPJ")] public CNPJ CNPJ { get; set; }

    [XmlAttribute("CPF")] public CPF CPF { get; set; }

    /// <summary>
    /// Razão social ou Nome do emitente.
    /// </summary>
    [XmlAttribute("xNome")] public string Nome { get; set; }

    [XmlAttribute("xFant")] public string? NomeFantasia { get; set; }

}

public class Endereco
{
    [XmlAttribute("xLgr")] public string Logradouro { get; set; }

    [XmlAttribute("nro")] public string Numero { get; set; }

    [XmlAttribute("xCpl")] public string? Complemento { get; set; }

    [XmlAttribute("xBairro")] public string Bairro { get; set; }

    /// <summary>
    /// Código município IBGE
    /// </summary>
    [XmlAttribute("cMun")] public CidadeIBGE CodigoMunicipio { get; set; }

    [XmlAttribute("xMun")] public string Municipio { get; set; }

    [XmlAttribute("UF")] public UFSigla UF { get; set; }

    [XmlAttribute("CEP")] public CEP CEP { get; set; }

    [XmlAttribute("cPais")] public string? CodigoPais { get; set; }

    [XmlAttribute("xPais")] public string? Pais { get; set; }

    [XmlAttribute("fone")] public Telefone? Telefone { get; set;  }





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