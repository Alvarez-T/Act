using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record Icms30
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CST")] public Cst Cst => "30";

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
    [XmlElement("pICMSST")] public Percentual? AliquotaIcmsST { get; set; }

    [XmlElement("vICMSST")] public decimal? ValorIcmsST { get; set; }

    /// <summary>
    /// Valor da Base de cálculo do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vBCFCPST")] public decimal? BaseCalculoFcpPorST { get; set; }

    /// <summary>
    /// Percentual de FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("pFCPST")] public decimal? PercentualFcpPorST { get; set; }

    /// <summary>
    /// Valor do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vFCPST")] public decimal? ValorFcpPorST { get; set; }

    [XmlElement("vICMSDeson")] public decimal? ValorIcmsDesonerado { get; set; }

    [XmlElement("motDesICMS")] public MotivoDesoneracaoIcms MotivoDesoneracaoIcms { get; set; }

    /// <summary>
    /// Indica se o valor do ICMS desonerado (vICMSDeson) deduz do valor do item (vProd).
    /// </summary>
    [XmlElement("indDeduzDeson")] public bool DeduzDesoneracao { get; set; }

}