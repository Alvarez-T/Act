using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class Pis
{
    [XmlElement("PISAliq")] public PisAliquota PisAliquota { get; set; }

    [XmlElement("PISQtde")] public PisQuantia PisQuantia { get; set; }

    [XmlElement("PISNT")] public Cst Cst { get; set; }

    [XmlElement("PISOutr")] public PisOutros PisOutros { get; set; }
}