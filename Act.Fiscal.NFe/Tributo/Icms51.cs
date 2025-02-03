using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

public class Icms51
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CST")] public Cst Cst => "51";

    [XmlElement("modBC")] public ModalidadeBaseCalculoIcms? ModalidadeBaseCalculoIcms { get; set; }

    [XmlElement("pRedBC")] public Percentual? ReducaoBaseCalculoIcms { get; set; }

    /// <summary>
    /// Código de Benefício Fiscal na UF aplicado ao item quando houver RBC.
    /// </summary>
    [XmlElement("cBenefRBC")] public string? CodigoBeneficioFiscalRBC { get; set; }

    [XmlElement("vBC")] public decimal ValorBaseCalculoIcms { get; set; }

    [XmlElement("pICMS")] public Percentual? AliquotaIcms { get; set; }

    [XmlElement("vICMSOp")] public decimal? ValorIcmsOperacao { get; set; }

    [XmlElement("pDif")] public Percentual? PercentualDiferimento { get; set; }

    [XmlElement("vICMSDif")] public decimal? ValorIcmsDiferido { get; set; }

    [XmlElement("vICMS")] public decimal? ValorIcms { get; set; }

    [XmlElement("vBCFCP")] public decimal? ValorBaseCalculoFCP { get; set; }

    [XmlElement("pFCP")] public Percentual? PercentualFCP { get; set; }

    [XmlElement("vFCP")] public decimal? ValorFCP { get; set; }

    [XmlElement("pFCPDif")] public Percentual? PercentualDiferimentoFCP { get; set; }

    [XmlElement("vFCPDif")] public decimal? ValorDiferimentoFCP { get; set; }

    [XmlElement("vFCPEfet")] public decimal? ValorEfetivoFCP { get; set; }

}

/// <summary>
/// Tributação monofásica sobre combustíveis com recolhimento diferido
/// </summary>
public class Icms53
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CST")] public Cst Cst => "53";

    [XmlElement("qBCMono")] public decimal? QuantidadeTributada { get; set; }

    [XmlElement("adRemiICMS")] public Percentual? AliquotaAdRemIcms { get; set; }

    [XmlElement("vICMSMonoOp")] public decimal? ValorIcms { get; set; }

    [XmlElement("pDif")] public Percentual? PercentualDiferimento { get; set; }

    [XmlElement("vICMSMonoDif")] public decimal? ValorIcmsDiferido { get; set; }

    /// <summary>
    /// Valor do ICMS próprio devido
    /// </summary>
    [XmlElement("vICMSMono")] public decimal? ValorIcmsDevido { get; set; }

    /// <summary>
    /// Quantidade tributada diferida
    /// </summary>
    [XmlElement("qBCMonoDif")] public decimal? QuantidadeDiferida { get; set; }

    /// <summary>
    /// Alíquota <i>ad rem</i> do imposto diferido
    /// </summary>
    [XmlElement("adRemICMSDif")] public Percentual? AliquotaAdRemDiferido { get; set; }
}