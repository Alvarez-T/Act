using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class CofinsQuantia
{
    [XmlElement("CST")] public Cst Cst { get; set; }

    [XmlElement("qBCProd")] public decimal QuantidadeVendida { get; set; }

    [XmlElement("vAliqProd")] public decimal Aliquota { get; set; }

    [XmlElement("vCOFINS")] public decimal ValorCofins { get; set; }
}