using System.Xml.Serialization;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe;

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