using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record IpiInt
{
    [XmlElement("CST")] public Cst Cst { get; init; }
}