namespace Act.Fiscal.NFe.Tributo;

public class Icms40
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CST")] public Cst Cst { get; set; }

    [XmlElement("vICMSDeson")] public decimal? ValorIcmsDesonerado { get; set; }

    [XmlElement("motDesICMS")] public MotivoDesoneracaoIcms MotivoDesoneracaoIcms { get; set; }

    /// <summary>
    /// Indica se o valor do ICMS desonerado (vICMSDeson) deduz do valor do item (vProd).
    /// </summary>
    [XmlElement("indDeduzDeson")] public bool DeduzDesoneracao { get; set; }
}