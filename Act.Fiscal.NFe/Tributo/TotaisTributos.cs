using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class TotaisTributos
{
    [XmlElement("ICMSTot")] public TotaisIcms TotaisIcms { get; set; }

    [XmlElement("ISSQNtot")] public TotaisISSQN TotaisISSQN { get; set; }

    [XmlElement("retTrib")] public RetencaoTributosFederais RetencaoTributosFederais { get; set; }
}