using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

public class IcmsSn500
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CSOSN")] public Csosn Csosn { get; set; }
    [XmlElement("vBCSTRet")] public decimal? ValorBaseCalcIcmsStRetido { get; set; }

    [XmlElement("pST")] public Percentual? AliquotaConsumidorFinal { get; set; }

    [XmlElement("vICMSSubstituto")] public decimal? ValorIcmsSubstituto { get; set; }

    [XmlElement("vICMSSTRet")] public decimal? ValorIcmsStRetido { get; set; }

    [XmlElement("vBCFCPSTRet")] public decimal? ValorBaseCalcFCPRetidoPorST { get; set; }

    [XmlElement("pFCPSTRet")] public Percentual? PercentualFcpRetidoPorST { get; set; }

    [XmlElement("vFCPSTRet")] public decimal? ValorFCPRetidoPorST { get; set; }

    [XmlElement("pRedBCEfet")] public Percentual? PercentualReducaoBaseCalcEfetiva { get; set; }

    [XmlElement("vBCEfet")] public decimal? ValorBaseCalculoEfetiva { get; set; }

    [XmlElement("pICMSEfet")] public Percentual? AliquotaIcmsEfetiva { get; set; }

    [XmlElement("vICMSEfet")] public decimal? ValorIcmsEfetivo { get; set; }
}