using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record IpiTributado
{
    [XmlElement("CST")] public Cst Cst { get; init; }

    [XmlElement("vBC")] public decimal? BaseCalculo { get; init; }

    [XmlElement("pIPI")] public Percentual? Aliquota { get; set; }

    [XmlElement("qUnid")] public decimal? Quantidade { get; set; }

    [XmlElement("vUnid")] public decimal? ValorPorUnidadeTributavel { get; set; }

    [XmlElement("vIPI")] public decimal ValorIpi { get; set; }
}