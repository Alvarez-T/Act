using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

public class ValoresIcms
{
    [XmlElement("modBC")] public ModalidadeBaseCalculoIcms ModalidadeBaseCalculo { get; set; }

    [XmlElement("vBC")] public decimal BaseCalculo { get; set; }

    [XmlElement("pICMS")] public decimal Aliquota { get; set; }

    [XmlElement("vICMS")] public decimal ValorIcms { get; set; }
    public FundoCombatePobreza? FundoCombatePobreza { get; set; }

    public ValoresIcms()
    {
        var icms = new Icms10
        {
            Cst = "0"
        };
    }
}