using System.Xml;
using Act.Entidade;
using Act.Fiscal.NFe.Localizacao;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.Entidade;

public class Destinatario
{
    [XmlElement("CNPJ")] public Cnpj? Cnpj { get; set; }

    [XmlElement("CPF")] public CPF? Cpf { get; set; }

    [XmlElement("idEstrangeiro")] public string? Estrangeiro { get; set; }

    /// <summary>
    /// Razão Social ou nome do Destinatário.
    /// </summary>
    [XmlElement("xNome")] public string? Nome { get; set; }

    [XmlElement("enderDest")] public Endereco? Endereco { get; set; }

    [XmlElement("indIEDest")] public IdentificadorIE IdentificadorIE { get; set; }

    [XmlElement("IE")] public InscricaoEstadual? InscricaoEstadual { get; set; }

    /// <summary>
    /// Inscrição na SUFRAMA (Obrigatório nas operações com as áreas com benefícios de incentivos fiscais sob controle da SUFRAMA) PL_005d - 11/08/09 
    /// </summary>
    [XmlElement("ISUF")] public string? InscricaoSuframa { get; set; }

    [XmlElement("IM")] public InscricaoMunicipal? InscricaoMunicipal { get; set; }

    /// <summary>
    /// Email do Destinatário ou Recepção do Destinatário
    /// </summary>
    [XmlElement("email")] public Email Email { get; set; }
}