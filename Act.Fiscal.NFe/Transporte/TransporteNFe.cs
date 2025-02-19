using Act.Fiscal.NFe.Tributo;
using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Transporte;

internal sealed class TransporteNFe
{
    [XmlElement("modFrete")] public ModalidadeFrete ModalidadeFrete { get; set; }
    [XmlElement("transporta")] public DadosTransportador? DadosTransportador { get; set; }
    [XmlElement("retTransp")] public DadosRetencaoICMS? DadosRetencaoICMS { get; set; }
    [XmlElement("veicTransp")] public Veiculo? Veiculo { get; set; }
    [XmlElement("reboque")] public List<Veiculo>? DadosReboqueDolly { get; set; }
    [XmlElement("vagao")] public string IdentificacaoVagao { get; set; }
    [XmlElement("balsa")] public string IdentificacaoBalsa { get; set; }
    [XmlElement("vol")] public DadosVolumes DadosVolumes { get; set; }
}