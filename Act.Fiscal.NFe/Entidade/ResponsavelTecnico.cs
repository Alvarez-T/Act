using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Entidade;

internal sealed class ResponsavelTecnico
{
    [XmlElement("CNPJ")] public string Cnpj { get; set; }

    [XmlElement("xContato")] public string NomeContato { get; set; }

    [XmlElement("email")] public string EmailContato { get; set; }

    [XmlElement("fone")] public string TelefoneContato { get; set; }

    [XmlElement("idCSRT")] public string IdentificadorCsrt { get; set; }

    [XmlElement("hashCSRT")] public byte[] HashCsrt { get; set; }
}