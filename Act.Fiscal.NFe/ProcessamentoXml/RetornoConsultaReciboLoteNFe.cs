using System.Xml.Serialization;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.ProcessamentoXml;

[XmlRoot("TRetConsReciNFe")]
internal sealed class RetornoConsultaReciboLoteNFe
{
    [XmlElement("tpAmb")] public TipoAmbiente TipoAmbiente { get; set; }

    [XmlElement("verAplic")] public string VersaoAplicativo { get; set; }

    [XmlElement("nRec")] public string NumeroReciboConsultado { get; set; }

    [XmlElement("cStat")] public string CodigoStatus { get; set; }

    [XmlElement("xMotivo")] public string MotivoStatus { get; set; }

    [XmlElement("cUF")] public UF CodigoUfIBGE { get; set; }

    [XmlElement("dhRecbto")] public string DataHoraRecebimento { get; set; }

    [XmlElement("cMsg")] public string CodigoMensagem { get; set; }

    [XmlElement("xMsg")] public string MensagemSefaz { get; set; }

    [XmlElement("protNFe")] public List<ProtocoloProcessamentoNFe> ProtocolosNFe { get; set; }

    [XmlAttribute("versao")] public string Versao { get; set; }
}