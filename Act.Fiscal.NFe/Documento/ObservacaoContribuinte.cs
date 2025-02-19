using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Documento;

public class ObservacaoContribuinte
{
    [XmlAttribute("xCampo")] public string NomeCampo { get; set; }
    [XmlElement("xTexto")] public string ConteudoCampo { get; set; }
}