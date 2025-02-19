using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Especializacao;

internal sealed class Deducao
{
    [XmlElement("xDed")] public string DescricaoDeducao { get; set; }
    [XmlElement("vDed")] public decimal ValorDeducao { get; set; }
}