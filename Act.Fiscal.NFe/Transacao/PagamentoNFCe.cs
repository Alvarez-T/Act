using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Transacao;

internal sealed class PagamentoNFCe
{
    [XmlElement("detPag")] public List<DetalhamentoPagamento> DetalhamentosPagamento { get; set; }
    [XmlElement("vTroco")] public decimal? ValorTroco { get; set; }
}