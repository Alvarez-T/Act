using System.Xml.Serialization;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.Especializacao;

internal sealed class GuiaTransitoAgro
{
    [XmlElement("tpGuia")] public TipoGuiaAgro TipoGuiaAgro { get; set; }

    [XmlElement("UFGuia")] public UFSigla UfGuia { get; set; }

    [XmlElement("serieGuia")] public string SerieGuia { get; set; }

    [XmlElement("nGuia")] public string NumeroGuia { get; set; }
}