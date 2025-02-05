using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class PisOutros
{
    [XmlElement("vBC")] public decimal? BaseCalculo { get; set; }

    [XmlElement("pPIS")] public Percentual? PercentualAliquota { get; set; }

    [XmlElement("qBCProd")] public decimal? QuantidadeVendida { get; set; }

    [XmlElement("vAliqProd")] public decimal? Aliquota { get; set; }

    [XmlElement("vPIS")] public decimal ValorPis { get; set; }
}