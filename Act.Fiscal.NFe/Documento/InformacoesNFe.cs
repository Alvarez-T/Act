using Act.Fiscal.NFe.Entidade;
using Act.Fiscal.NFe.Localizacao;
using System.Xml.Serialization;
using Act.Fiscal.NFe.Especializacao;
using Act.Fiscal.NFe.Exterior;
using Act.Fiscal.NFe.Transacao;
using Act.Fiscal.NFe.Transporte;
using Act.Fiscal.NFe.Tributo;

namespace Act.Fiscal.NFe.Documento;

[XmlRoot("infNFe")]
internal sealed class InformacoesNFe
{
    [XmlAttribute("versao")] internal string Versao => "4.00";

    [XmlAttribute("Id")] public ChaveNFe Chave { get; set; }

    [XmlElement("ide")] public IdentificacaoNFe IdentificacaoNFe { get; set; }

    [XmlElement("emit")] public Emitente EmitenteNfe { get; set; }

    [XmlElement("avulso")] public EmitenteAvulso? EmitenteAvulso { get; set; }

    [XmlElement("dest")] public Destinatario? Destinatario { get; set; }

    /// <summary>
    /// Identificação do Local de Retirada (informado apenas quando for diferente do endereço do remetente)
    /// </summary>
    [XmlElement("retirada")] public Local? LocalRetirada { get; set; }

    /// <summary>
    /// Identificação do Local de Entrega (informar apenas quando for diferente do endereço do destinatário)
    /// </summary>
    [XmlElement("entrega")] public Local? LocalEntrega { get; set; }

    //Todo: [XmlElement("autXML")] public object CNPJ { get; set; }

    [XmlElement("det")] public DetalhesNFe DetalhesNFe { get; set; }

    [XmlElement("total")] public TotaisTributos TotaisTributos { get; set; }

    [XmlElement("transp")] public Transportador Transportador { get; set; }

    [XmlElement("cobr")] public Cobranca? Cobranca { get; set; }

    [XmlElement("pag")] public PagamentoNFCe PagamentoNFCe { get; set; }

    [XmlElement("infIntermed")] public Intermediador? Intermediador { get; set; }

    [XmlElement("infAdic")] public InformacoesAdicionaisNFe? InformacoesAdicionais { get; set; }

    [XmlElement("exporta")] public Exportacao? Exportacao { get; set; }

    [XmlElement("compra")] public Compra? Compra { get; set; }

    [XmlElement("cana")] public Cana? Cana { get; set; }

    [XmlElement("infRespTec")] public ResponsavelTecnico? ResponsavelTecnico { get; set; }

    [XmlElement("infSolicNFF")] public InformacoesSolicitacaoNFF? SolicitacaoNff { get; set; }

    [XmlElement("agropecuario")] public Agropecuario? Agropecuario { get; set; }

}