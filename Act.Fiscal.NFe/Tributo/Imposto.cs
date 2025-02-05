using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class Imposto
{
    [XmlElement("vTotTrib")] public decimal? ValorTotalTributos { get; set; }

    [XmlElement("ICMS")] public Icms? Icms { get; set; }

    [XmlElement("IPI")] public Ipi? Ipi { get; set; }

    [XmlElement("II")] public ImpostoImportacao? ImpostoImportacao { get; set; }

    [XmlElement("ISSQN")] public Issqn? Issqn { get; set; }

    [XmlElement("PIS")] public Pis? Pis { get; set; }

    [XmlElement("PISST")] public PisSt? PisSt { get; set; }

    [XmlElement("COFINS")] public Cofins? Cofins { get; set; }

    [XmlElement("COFINSST")] public CofinsSt? CofinsSt { get; set; }

    [XmlElement("ICMSUFDest")] public IcmsUfDestinatario? IcmsUfDestinatario { get; set; }

}