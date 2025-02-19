using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Transacao;

internal enum IndicadorFormaPagamento
{
    [XmlEnum("0")] Vista = 0,
    [XmlEnum("1")] Prazo = 1
}