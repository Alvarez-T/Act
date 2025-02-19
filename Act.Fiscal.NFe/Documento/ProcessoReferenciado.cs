using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Documento;

internal sealed class ProcessoReferenciado
{
    [XmlElement("nProc")] public string IdentificadorProcesso { get; set; }
    [XmlElement("indProc")] public OrigemProcesso OrigemProcesso { get; set; }
    [XmlElement("tpAto")] public TipoAtoConcessorio? TipoAtoConcessorio { get; set; }
}