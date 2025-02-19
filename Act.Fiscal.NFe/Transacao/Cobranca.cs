using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Transacao;

internal sealed class Cobranca
{
    [XmlElement("fat")] public Fatura Fat { get; set; }
    [XmlElement("dup")] public List<Duplicata> Dup { get; set; }
}