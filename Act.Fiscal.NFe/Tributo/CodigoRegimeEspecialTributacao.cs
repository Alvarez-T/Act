using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal enum CodigoRegimeEspecialTributacao
{
    [XmlEnum("1")] Microempresa = 1,
    [XmlEnum("2")] Estimativa = 2,
    [XmlEnum("3")] Sociedade = 3,
    [XmlEnum("4")] Cooperativa = 4,
    [XmlEnum("5")] MicroempresarioIndividual = 5,
    [XmlEnum("6")] microempresarioPequenoPorte = 6
}