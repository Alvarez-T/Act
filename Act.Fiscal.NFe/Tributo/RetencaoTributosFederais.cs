using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class RetencaoTributosFederais
{
    [XmlElement("vRetPIS")] public decimal? ValorRetidoPIS { get; set; }

    [XmlElement("vRetCOFINS")] public decimal? ValorRetidoCOFINS { get; set; }

    [XmlElement("vRetCSLL")] public decimal? ValorRetidoCSLL { get; set; }

    [XmlElement("vBCIRRF")] public decimal? BaseCalculoIRRF { get; set; }

    [XmlElement("vIRRF")] public decimal? ValorRetidoIRRF { get; set; }

    [XmlElement("vBCRetPrev")] public decimal? BaseCalculoRetencaoPrevidenciaSocial { get; set; }

    [XmlElement("vRetPrev")] public decimal? ValorRetencaoPrevidenciaSocial { get; set; }
}