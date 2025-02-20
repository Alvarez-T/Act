using System.Xml.Serialization;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.Exterior;

internal sealed class Exportacao
{
    [XmlElement("UFSaidaPais")] public UFSigla UFEmbarqueOuTransposicaoFronteira { get; set; }
    [XmlElement("xLocExporta")] public string LocalEmbarqueOuTransposicaoFronteira { get; set; }
    [XmlElement("xLocDespacho")] public string? DescricaoLocalDespacho { get; set; }
}