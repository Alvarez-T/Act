using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class Cofins
{
    [XmlElement("COFINSAliq")] public CofinsAliquota CofinsAliquota { get; set; }

    [XmlElement("COFINSQtde")] public CofinsQuantia CofinsQuantia { get; set; }

    [XmlElement("COFINSNT")] public Cst Cst { get; set; }

    [XmlElement("COFINSOutr")] public CofinsOutros CofinsOutros { get; set; }
}