using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class TotaisISSQN
{
    [XmlElement("vServ")] public decimal? ValorTotalServicos { get; set; }

    [XmlElement("vBC")] public decimal? BaseCalculoISS { get; set; }

    [XmlElement("vISS")] public decimal? ValorTotalISS { get; set; }

    [XmlElement("vPIS")] public decimal? ValorPIS { get; set; }

    [XmlElement("vCOFINS")] public decimal? ValorCOFINS { get; set; }

    [XmlElement("dCompet")] public DateTime DataPrestacaoServico { get; set; }

    [XmlElement("vDeducao")] public decimal? ValorDeducao { get; set; }

    [XmlElement("vOutro")] public decimal? ValorOutrasRetencoes { get; set; }

    [XmlElement("vDescIncond")] public decimal? ValorDescontoIncondicionado { get; set; }

    [XmlElement("vDescCond")] public decimal? ValorDescontoCondicionado { get; set; }

    [XmlElement("vISSRet")] public decimal? ValorTotalRetencaoISS { get; set; }

    [XmlElement("cRegTrib")] public CodigoRegimeEspecialTributacao? CodigoRegimeEspecialTributacao { get; set; }
}