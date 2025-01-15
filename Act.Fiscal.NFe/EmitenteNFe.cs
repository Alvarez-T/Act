using System.Xml.Serialization;
using Act.Common.Types;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe;

public class EmitenteNFe
{
    [XmlAttribute("CNPJ")] public CNPJ CNPJ { get; set; }

    [XmlAttribute("CPF")] public CPF CPF { get; set; }

    /// <summary>
    /// Razão social ou Nome do emitente.
    /// </summary>
    [XmlAttribute("xNome")] public string Nome { get; set; }

    [XmlAttribute("xFant")] public string? NomeFantasia { get; set; }

    [XmlAttribute("enderEmit")] public Endereco Endereco { get; set; }

    [XmlAttribute("IE")] public InscricaoEstadual InscricaoEstadual { get; set; }

    /// <summary>
    /// Inscricao Estadual do Substituto Tributário
    /// </summary>
    [XmlAttribute("IEST")] public InscricaoEstadual? IESubstitutoTributario { get; set; }

    [XmlAttribute("IM")] public InscricaoMunicipal? InscricaoMunicipal { get; set; }

    /// <summary>
    /// CNAE Fiscal
    /// </summary>
    [XmlAttribute("CNAE")] public string CNAE { get; set; }

    [XmlAttribute("CRT")] public CRT CRT { get; set; }

}