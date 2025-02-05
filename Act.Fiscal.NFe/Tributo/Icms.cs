using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class Icms
{
    [XmlElement("ICMS00")] public Icms00? Icms00 { get; set; }

    [XmlElement("ICMS02")] public Icms02? Icms02 { get; set; }

    [XmlElement("ICMS10")] public Icms10? Icms10 { get; set; }

    [XmlElement("ICMS15")] public Icms15? Icms15 { get; set; }

    [XmlElement("ICMS20")] public Icms20? Icms20 { get; set; }

    [XmlElement("ICMS30")] public Icms30? Icms30 { get; set; }

    [XmlElement("ICMS40")] public Icms40? Icms40 { get; set; }

    [XmlElement("ICMS51")] public Icms51? Icms51 { get; set; }

    [XmlElement("ICMS53")] public Icms53? Icms53 { get; set; }

    [XmlElement("ICMS60")] public Icms60? Icms60 { get; set; }

    [XmlElement("ICMS61")] public Icms61? Icms61 { get; set; }

    [XmlElement("ICMS70")] public Icms70? Icms70 { get; set; }

    [XmlElement("ICMS90")] public Icms90? Icms90 { get; set; }

    [XmlElement("ICMSPart")] public IcmsPartilhado? IcmsPartilhado { get; set; }

    [XmlElement("ICMSST")] public IcmsSt? IcmsSt { get; set; }

    [XmlElement("ICMSSN101")] public IcmsSn101? IcmsSn101 { get; set; }

    [XmlElement("ICMSSN102")] public IcmsSn102? IcmsSn102 { get; set; }

    [XmlElement("ICMSSN201")] public IcmsSn201? IcmsSn201 { get; set; }

    [XmlElement("ICMSSN202")] public IcmsSn202? IcmsSn202 { get; set; }

    [XmlElement("ICMSSN500")] public IcmsSn500? IcmsSn500 { get; set; }

    [XmlElement("ICMSSN900")] public IcmsSn900? IcmsSn900 { get; set; }
}