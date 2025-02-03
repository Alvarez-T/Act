using System.Xml.Serialization;
using Act.Xml;

namespace Act.Fiscal.NFe.Tributo;

public class Imposto
{
    [XmlElement("vTotTrib")] public decimal ValorTotalTributos { get; set; }

}