using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

public class Icms20
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CST")] public Cst Cst => "20";

    [XmlElement("modBC")] public ModalidadeBaseCalculoIcms ModalidadeBaseCalculoIcms { get; set; }

    [XmlElement("pRedBC")] public Percentual ReducaoBaseCalculoIcms { get; set; }

    [XmlElement("vBC")] public decimal ValorBaseCalculoIcms { get; set; }

    [XmlElement("pICMS")] public Percentual AliquotaIcms { get; set; }

    [XmlElement("vICMS")] public decimal ValorIcms { get; set; }

    [XmlElement("vBCFCP")] public decimal? ValorBaseCalculoFcp { get; set; }

    /// <summary>
    /// Percentual de ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("pFCP")] public Percentual? PercentualFcp { get; set; }

    /// <summary>
    /// Valor do ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("vFCP")] public decimal? ValorFcp { get; set; }

    [XmlElement("vICMSDeson")] public decimal? ValorIcmsDesonerado { get; set; }

    [XmlElement("motDesICMS")] public MotivoDesoneracaoIcms MotivoDesoneracaoIcms { get; set; }

    /// <summary>
    /// Indica se o valor do ICMS desonerado (vICMSDeson) deduz do valor do item (vProd).
    /// </summary>
    [XmlElement("indDeduzDeson")] public bool DeduzDesoneracao { get; set; }

}