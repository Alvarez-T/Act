using System.Xml.Serialization;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.ProcessamentoXml;

internal sealed class EnvioAutorizacaoNFe
{
    [XmlElement("idLote")] public string IdLote { get; set; }

    [XmlElement("indSinc")] public bool IndicadorSincrono { get; set; }

    [XmlAttribute("versao")] public string Versao { get; set; }

    [XmlElement("NFe")] public NFe NFe { get; set; }

}

[XmlRoot("TConsReciNFe")]
internal sealed class PedidoConsultaReciboLoteNFe
{
    [XmlElement("tpAmb")] public TipoAmbiente TipoAmbiente { get; set; }

    [XmlElement("nRec")] public string NumeroRecibo { get; set; }

    [XmlAttribute("versao")] public string Versao { get; set; }
}

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

[XmlRoot("TNfeProc")]
internal sealed class NFeProcessada
{
    [XmlElement("NFe")] public NFe NFe { get; set; }

    [XmlElement("protNFe")] public ProtocoloProcessamentoNFe ProtocoloProcessamentoNFe { get; set; }
}