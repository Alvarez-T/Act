using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Especializacao;

[XmlRoot("agropecuario")]
internal sealed class Agropecuario
{
    [XmlElement("defensivo")] public DefensivoAgricola Defensivo { get; set; }

    [XmlElement("guiaTransito")] public GuiaTransitoAgro GuiaTransitoAgro { get; set; }
}