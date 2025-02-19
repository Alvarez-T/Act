using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Transporte;

internal enum ModalidadeFrete
{
    [XmlEnum("0")] ContratacaoFretePorContaRemetenteCIF = 0,
    [XmlEnum("1")] ContratacaoFretePorContaDestinatarioRemetenteFOB = 1,
    [XmlEnum("2")] ContratacaoFretePorContaTerceiros = 2,
    [XmlEnum("3")] TransporteProprioPorContaRemetente = 3,
    [XmlEnum("4")] TransporteProprioPorContaDestinatario = 4,
    [XmlEnum("9")] SemOcorrenciaTransporte = 9
}