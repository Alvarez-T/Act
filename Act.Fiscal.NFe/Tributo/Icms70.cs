using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

/// <summary>
/// Tributação pelo ICMS 70 - Com redução de base de cálculo e cobrança do ICMS por substituição tributária
/// </summary>
public class Icms70
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CST")] public Cst Cst => "70";

    [XmlElement("modBC")] public ModalidadeBaseCalculoIcms ModalidadeBaseCalculoIcms { get; set; }

    [XmlElement("pRedBC")] public Percentual PercentualReducaoBaseCalculo { get; set; }

    [XmlElement("vBC")] public decimal ValorBaseCalculoIcms { get; set; }

    [XmlElement("pICMS")] public Percentual AliquotaIcms { get; set; }

    [XmlElement("vICMS")] public decimal ValorIcms { get; set; }

    [XmlElement("vBCFCP")] public decimal? ValorBaseCalculoFcp { get; set; }

    [XmlElement("pFCP")] public Percentual? PercentualRelativoFcp { get; set; }

    [XmlElement("vFCP")] public decimal? ValorIcmsFCP { get; set; }

    [XmlElement("modBCST")] public ModalidadeBaseCalculoIcmsST ModalidadeBaseCalculoIcmsSt { get; set; }

    [XmlElement("pMVAST")] public Percentual? MargemValorAdicionadoIcmsSt { get; set; }

    [XmlElement("pRedBCST")] public Percentual? PercentualReducaoBaseCalculoIcmsSt { get; set; }

    [XmlElement("vBCST")] public decimal ValorBaseCalculoIcmsSt { get; set; }

    [XmlElement("pICMSST")] public Percentual AliquotaIcmsSt { get; set; }

    [XmlElement("vICMSST")] public decimal ValorIcmsSt { get; set; }

    /// <summary>
    /// Valor da Base de cálculo do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vBCFCPST")] public decimal? ValorBaseCalculoFcpPorSt { get; set; }

    [XmlElement("pFCPST")] public Percentual? PercentualFcpPorSt { get; set; }

    [XmlElement("vFCPST")] public decimal? ValorFcpPorSt { get; set; }
    [XmlElement("vICMSDeson")] public decimal? ValorIcmsDesonerado { get; set; }

    [XmlElement("motDesICMS")] public MotivoDesoneracaoIcms? MotivoDesoneracaoIcms { get; set; }

    [XmlElement("indDeduzDeson")] public bool? IndicadorDeducaoDesoneracao { get; set; }

    [XmlElement("vICMSSTDeson")] public decimal? ValorIcmsStDesonerado { get; set; }

    [XmlElement("motDesICMSST")] public MotivoDesoneracaoIcmsST? MotivoDesoneracaoIcmsSt { get; set; }
}