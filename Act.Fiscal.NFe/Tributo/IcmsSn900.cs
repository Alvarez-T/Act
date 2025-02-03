using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

public class IcmsSn900
{
    [XmlElement("orig")] public OrigemMercadoria? Origem { get; set; }

    [XmlElement("CSOSN")] public Csosn Csosn { get; set; }

    [XmlElement("modBC")] public ModalidadeBaseCalculoIcms ModalidadeBaseCalculoIcms { get; set; }

    [XmlElement("vBC")] public decimal ValorBaseCalculoIcms { get; set; }

    [XmlElement("pRedBC")] public Percentual? PercentualReducaoBaseCalculo { get; set; }

    [XmlElement("pICMS")] public Percentual AliquotaIcms { get; set; }

    [XmlElement("vICMS")] public decimal ValorIcms { get; set; }

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

    [XmlElement("pCredSN")] public Percentual AliquotaCreditoIcms { get; set; }

    [XmlElement("vCredICMSSN")] public decimal ValorCreditoIcms { get; set; }

}