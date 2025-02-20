using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Documento;

[XmlRoot("infSolicNFF")]
internal sealed class InformacoesSolicitacaoNFF
{
    [XmlElement("xSolic")] public string SolicitacaoPedidoEmissaoNFF { get; set; }
}