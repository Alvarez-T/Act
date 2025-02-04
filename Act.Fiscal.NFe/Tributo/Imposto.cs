using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

public class Imposto
{
    [XmlElement("vTotTrib")] public decimal ValorTotalTributos { get; set; }

}