using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Transacao;

internal sealed class Compra
{
    [XmlElement("xNEmp")] public string NotaEmpenho { get; set; }
    [XmlElement("xPed")] public string Pedido { get; set; }
    [XmlElement("xCont")] public string Contrato { get; set; }
}