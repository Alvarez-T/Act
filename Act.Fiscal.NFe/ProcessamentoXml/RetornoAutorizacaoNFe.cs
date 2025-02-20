using System.Xml.Serialization;

namespace Act.Fiscal.NFe.ProcessamentoXml;

internal sealed class RetornoAutorizacaoNFe
{
    [XmlElement("tpAmb")] public TipoAmbiente TipoAmbiente { get; set; }

    [XmlElement("verAplic")] public string VersaoAplicativo { get; set; }

    [XmlElement("cStat")] public string CodigoStatus { get; set; }

    [XmlElement("xMotivo")] public string MotivoStatus { get; set; }

    [XmlElement("cUF")] public string CodigoUfIBGE { get; set; }

    [XmlElement("dhRecbto")] public string DataHoraRecebimento { get; set; }

    [XmlElement("infRec")] public InformacoesReciboLote InformacoesRecibo { get; set; }

    [XmlElement("protNFe")] public ProtocoloProcessamentoNFe ProtocoloProcessamentoNFe { get; set; }

    [XmlAttribute("versao")] public string Versao { get; set; }
}