using System.Xml.Serialization;

namespace Act.Fiscal.NFe.ProcessamentoXml;

internal sealed class InformacoesReciboLote
{
    [XmlElement("nRec")] public string NumeroRecibo { get; set; }

    [XmlElement("tMed")] public int TempoMedioResposta { get; set; }
}