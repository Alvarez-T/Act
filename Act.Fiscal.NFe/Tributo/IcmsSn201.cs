using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

public class IcmsSn201
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CSOSN")] public Csosn Csosn => "201";

    [XmlElement("modBCST")] public ModalidadeBaseCalculoIcmsST ModalidadeBaseCalculoST { get; set; }

    /// <summary>
    /// Percentual da margem de valor Adicionado do ICMS ST
    /// </summary>
    [XmlElement("pMVAST")] public Percentual? MargemValorAdicionadoST { get; set; }

    [XmlElement("pRedBCST")] public Percentual? PercentualReducaoBaseCalculoST { get; set; }

    /// <summary>
    /// Base de cálculo do ICMS ST
    /// </summary>
    [XmlElement("vBCST")] public decimal? BaseCalculoIcmsST { get; set; }

    /// <summary>
    /// Alíquota do ICMS ST
    /// </summary>
    [XmlElement("pICMSST")] public decimal? AliquotaIcmsST { get; set; }

    [XmlElement("vICMSST")] public decimal? ValorIcmsST { get; set; }

    /// <summary>
    /// Valor da Base de cálculo do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vBCFCPST")] public decimal? BaseCalculoFcpPorST { get; set; }

    /// <summary>
    /// Percentual de FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("pFCPST")] public decimal? PercentualFcpPorST { get; set; }

    [XmlElement("vFCPST")] public decimal? ValorFcpPorST { get; set; }

    [XmlElement("pCredSN")] public Percentual AliquotaCreditoIcms { get; set; }

    [XmlElement("vCredICMSSN")] public decimal ValorCreditoIcms { get; set; }
}