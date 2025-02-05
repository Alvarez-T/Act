using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class CofinsOutros
{
    [XmlElement("CST")] public Cst Cst { get; set; }

    [XmlElement("vBC")] public decimal? BaseCalculo { get; set; }

    [XmlElement("pCOFINS")] public Percentual? PercentualAliquota { get; set; }

    [XmlElement("qBCProd")] public decimal? QuantidadeVendida { get; set; }

    [XmlElement("vAliqProd")] public decimal? Aliquota { get; set; }

    [XmlElement("vCOFINS")] public decimal ValorCofins { get; set; }
}