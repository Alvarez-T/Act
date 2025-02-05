using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Item;

internal sealed class ObservacaoItem
{
    [XmlElement("obsCont")] public string? Observacoes { get; set; }

    [XmlElement("obsFisco")] public string? ObservacoesFisco { get; set; }
}