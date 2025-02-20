using System.Xml.Serialization;

namespace Act.Fiscal.NFe.ProcessamentoXml;

internal sealed class EnvioAutorizacaoNFe
{
    [XmlElement("idLote")] public string IdLote { get; set; }

    [XmlElement("indSinc")] public bool IndicadorSincrono { get; set; }

    [XmlAttribute("versao")] public string Versao { get; set; }

    [XmlElement("NFe")] public NFe NFe { get; set; }

}