using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class PisAliquota
{
    [XmlElement("CST")] public Cst Cst { get; set; }

    [XmlElement("vBC")] public decimal BaseCalculo { get; set; }

    [XmlElement("pPIS")] public Percentual Aliquota { get; set; }

    [XmlElement("vPIS")] public decimal ValorPis { get; set; }
}