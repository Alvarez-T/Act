using System.Xml.Serialization;
using Act.Entidade;

namespace Act.Fiscal.NFe.Transacao;

internal sealed class DadosCartao
{
    [XmlElement("tpIntegra")] public TipoIntegracaoPagamento TipoIntegracaoPagamento { get; set; }
    [XmlElement("CNPJ")] public Cnpj CNPJInstituicaoPagamento { get; set; }
    [XmlElement("tBand")] public string BandeiraOperadoraCartao { get; set; }
    [XmlElement("cAut")] public string NumeroAutorizacao { get; set; }
    [XmlElement("CNPJReceb")] public Cnpj CNPJBeneficiarioPagamento { get; set; }
    [XmlElement("idTermPag")] public string IdentificadorTerminalPagamento { get; set; }
}