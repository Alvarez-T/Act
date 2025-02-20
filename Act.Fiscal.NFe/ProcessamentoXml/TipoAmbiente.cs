using System.Xml.Serialization;

namespace Act.Fiscal.NFe.ProcessamentoXml;

internal enum TipoAmbiente
{
    [XmlEnum("1")] Producao = 1,
    [XmlEnum("2")] Homologacao = 2
}