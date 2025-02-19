using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Documento;

public enum OrigemProcesso
{
    [XmlEnum("0")] SEFAZ = 0,
    [XmlEnum("1")] JusticaFederal = 1,
    [XmlEnum("2")] JusticaEstadual = 2,
    [XmlEnum("3")] SecexRFB = 3,
    [XmlEnum("4")] CONFAZ = 4,
    [XmlEnum("9")] Outros = 9
}