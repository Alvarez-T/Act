using System.Xml.Serialization;
using Act.Fiscal.NFe.Item;
using Act.Fiscal.NFe.Tributo;

namespace Act.Fiscal.NFe.Documento;

internal sealed class DetalhesNFe
{
    [XmlAttribute("nItem")] public string NumeroItem { get; set; }

    [XmlElement("prod")] public Produto Produto { get; set; }

    [XmlElement("imposto")] public Imposto Imposto { get; set; }

    [XmlElement("impostoDevol")] public ImpostoDevolucao? ImpostoDevolucao { get; set; }

    [XmlElement("infAdProd")] public string? InformacoesAdicionaisProduto { get; set; }

    [XmlElement("obsItem")] public ObservacaoItem? ObservacaoItem { get; set; }
}