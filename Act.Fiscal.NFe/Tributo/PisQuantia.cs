using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class PisQuantia
{
    [XmlElement("CST")] public Cst Cst { get; set; }

    [XmlElement("qBCProd")] public decimal QuantidadeVendida { get; set; }

    [XmlElement("vAliqProd")] public decimal Aliquota { get; set; }

    [XmlElement("vPIS")] public decimal ValorPis { get; set; }
}