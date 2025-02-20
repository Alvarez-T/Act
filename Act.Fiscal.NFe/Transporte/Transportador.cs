using System.Xml.Serialization;
using Act.Entidade;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.Transporte;

internal sealed class Transportador
{
    [XmlElement("CNPJ")] public Cnpj CNPJ { get; set; }
    [XmlElement("Cpf")] public Cpf CPF { get; set; }
    [XmlElement("xNome")] public string RazaoSocialNome { get; set; }
    [XmlElement("IE")] public InscricaoEstadual InscricaoEstadual { get; set; }
    [XmlElement("xEnder")] public string EnderecoCompleto { get; set; }
    [XmlElement("xMun")] public MunicipioIBGE Municipio { get; set; }
    [XmlElement("UF")] public UFSigla SiglaUF { get; set; }
}