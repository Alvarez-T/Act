using System.Xml.Serialization;
using Act.Entidade;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record Ipi
{
    [XmlElement("CNPJProd")] public Cnpj? CnpjProdutor { get; init; }

    [XmlElement("cSelo")] public string? SeloControle { get; init; }

    [XmlElement("qSelo")] public string? QuantidadeSelo { get; init; }

    [XmlElement("cEnq")] public string CodigoEnquadramentoLegal { get; init; }

    public IpiTributado? IpiTributado { get; set; }

    public IpiInt IpiInt { get; set; }

}