using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Especializacao;

internal enum TipoGuiaAgro
{
    [XmlEnum("1")] GTA = 1,
    [XmlEnum("2")] TTA = 2,
    [XmlEnum("3")] DTA = 3,
    [XmlEnum("4")] ATV = 4,
    [XmlEnum("5")] PTV = 5,
    [XmlEnum("6")] GTV = 6,
    [XmlEnum("7")] GuiaFlorestal = 7
}