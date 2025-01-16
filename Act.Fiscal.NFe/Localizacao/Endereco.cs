using Act.Entidade;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.Localizacao;

public class Endereco
{
    [XmlElement("xLgr")] public string Logradouro { get; set; }

    [XmlElement("nro")] public string Numero { get; set; }

    [XmlElement("xCpl")] public string? Complemento { get; set; }

    [XmlElement("xBairro")] public string Bairro { get; set; }

    /// <summary>
    /// Código município IBGE
    /// </summary>
    [XmlElement("cMun")] public CidadeIBGE CodigoMunicipio { get; set; }

    [XmlElement("xMun")] public string Municipio { get; set; }

    [XmlElement("UF")] public UFSigla UF { get; set; }

    [XmlElement("CEP")] public CEP CEP { get; set; }

    [XmlElement("cPais")] public string? CodigoPais { get; set; }

    [XmlElement("xPais")] public string? Pais { get; set; }

    [XmlElement("fone")] public Telefone? Telefone { get; set; }

}