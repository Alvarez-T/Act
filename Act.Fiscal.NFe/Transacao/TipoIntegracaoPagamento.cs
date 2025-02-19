using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Transacao;

internal enum TipoIntegracaoPagamento
{
    [XmlEnum("1")] IntegradoComAutomacao = 1,
    [XmlEnum("2")] NaoIntegradoComAutomacao = 2
}