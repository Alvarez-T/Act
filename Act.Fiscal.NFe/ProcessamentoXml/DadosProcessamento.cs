using System.Xml.Serialization;
using Act.Fiscal.NFe.Documento;

namespace Act.Fiscal.NFe.ProcessamentoXml;

[XmlRoot("infProt")]
internal sealed class DadosProcessamento
{
    [XmlElement("tpAmb")] public TipoAmbiente TipoAmbiente { get; set; }

    [XmlElement("verAplic")] public string VersaoAplicativo { get; set; }

    [XmlElement("chNFe")] public ChaveNFe ChaveNFe { get; set; }

    [XmlElement("dhRecbto")] public DateTime DataHoraRecebimento { get; set; }

    [XmlElement("nProt")] public string NumeroProtocolo { get; set; }

    [XmlElement("digVal")] public byte[] DigestValue { get; set; }

    [XmlElement("cStat")] public string CodigoStatus { get; set; }

    [XmlElement("xMotivo")] public string MotivoStatus { get; set; }

    [XmlElement("cMsg")] public string CodigoMensagem { get; set; }

    [XmlElement("xMsg")] public string MensagemSefaz { get; set; }

    [XmlAttribute("Id")] public string Id { get; set; }
}