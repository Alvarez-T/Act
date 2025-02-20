using System.Xml.Serialization;

namespace Act.Fiscal.NFe.ProcessamentoXml;

[XmlRoot("TConsReciNFe")]
internal sealed class PedidoConsultaReciboLoteNFe
{
    [XmlElement("tpAmb")] public TipoAmbiente TipoAmbiente { get; set; }

    [XmlElement("nRec")] public string NumeroRecibo { get; set; }

    [XmlAttribute("versao")] public string Versao { get; set; }
}