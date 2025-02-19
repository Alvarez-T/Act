using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Transacao;

internal sealed class Fatura
{
    [XmlElement("nFat")] public string NumeroFatura { get; set; }
    [XmlElement("vOrig")] public decimal ValorOriginal { get; set; }
    [XmlElement("vDesc")] public decimal ValorDesconto { get; set; }
    [XmlElement("vLiq")] public decimal ValorLiquido { get; set; }
}