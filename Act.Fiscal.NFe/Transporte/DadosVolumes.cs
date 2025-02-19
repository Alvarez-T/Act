using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Transporte;

internal sealed class DadosVolumes
{
    [XmlElement("qVol")] public int? QuantidadeVolumesTransportados { get; set; }
    [XmlElement("esp")] public string EspecieVolumesTransportados { get; set; }
    [XmlElement("marca")] public string MarcaVolumesTransportados { get; set; }
    [XmlElement("nVol")] public string NumeracaoVolumesTransportados { get; set; }
    [XmlElement("pesoL")] public decimal PesoLiquido { get; set; }
    [XmlElement("pesoB")] public decimal PesoBruto { get; set; }
    [XmlElement("lacres")] public List<string> NumeroLacres { get; set; }
}