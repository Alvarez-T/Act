using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record IcmsSt
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CST")] public Cst Cst { get; set; }
    [XmlElement("vBCSTRet")] public decimal? ValorBaseCalcIcmsStRetido { get; set; }

    [XmlElement("pST")] public Percentual? AliquotaConsumidorFinal { get; set; }

    [XmlElement("vICMSSubstituto")] public decimal? ValorIcmsSubstituto { get; set; }

    [XmlElement("vICMSSTRet")] public decimal ValorIcmsStRetido { get; set; }

    [XmlElement("vBCFCPSTRet")] public decimal? ValorBaseCalcFcpRetidoPorST { get; set; }

    [XmlElement("pFCPSTRet")] public Percentual? PercentualFcpRetidoPorST { get; set; }

    [XmlElement("vFCPSTRet")] public decimal? ValorFcpRetidoPorST { get; set; }

    /// <summary>
    /// Informar o valor da BC do ICMS ST da UF destino
    /// </summary>
    [XmlElement("vBCSTDest")] public decimal? ValorBaseCalculoIcmsStDestino { get; set; }

    [XmlElement("vICMSSTDest")] public decimal? ValorIcmsStDestino { get; set; }

    [XmlElement("pRedBCEfet")] public Percentual? PercentualReducaoBaseCalcEfetiva { get; set; }

    [XmlElement("vBCEfet")] public decimal? ValorBaseCalculoEfetiva { get; set; }

    [XmlElement("pICMSEfet")] public Percentual? AliquotaIcmsEfetiva { get; set; }

    [XmlElement("vICMSEfet")] public decimal? ValorIcmsEfetivo { get; set; }
}