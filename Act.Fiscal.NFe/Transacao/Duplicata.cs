using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Transacao;

internal sealed class Duplicata
{
    [XmlElement("nDup")] public string NumeroDuplicata { get; set; }
    [XmlElement("dVenc")] public DateTime DataVencimento { get; set; }
    [XmlElement("vDup")] public decimal ValorDup { get; set; }
}