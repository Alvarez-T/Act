using System.Xml.Serialization;
using Act.Entidade;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.Transacao;

internal sealed class DetalhamentoPagamento
{
    [XmlElement("indPag")] public IndicadorFormaPagamento IndicadorFormaPagamento { get; set; }
    [XmlElement("tPag")] public FormaPagamentoFiscal FormaPagamento { get; set; }
    [XmlElement("xPag")] public string DescricaoFormaPagamento { get; set; }
    [XmlElement("vPag")] public decimal? ValorPagamento { get; set; }
    [XmlElement("dPag")] public DateTime? DataPagamento { get; set; }
    [XmlElement("CNPJPag")] public Cnpj CNPJTransacional { get; set; }
    [XmlElement("UFPag")] public UFSigla UFTransacional { get; set; }
    [XmlElement("grpDocPag")] public DadosCartao? DadosCartao { get; set; }
}