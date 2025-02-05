using Act.Fiscal.NFe.Entidade;
using Act.Fiscal.NFe.Localizacao;
using System.Xml.Serialization;

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
}