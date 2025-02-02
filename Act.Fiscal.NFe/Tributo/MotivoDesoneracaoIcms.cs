using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

/// <summary>
/// Enumeração para motivo da desoneração do ICMS.
/// </summary>
public enum MotivoDesoneracaoIcms
{
    [XmlEnum("3")]
    UsoAgropecuaria = 3,

    [XmlEnum("9")]
    Outros = 9,

    [XmlEnum("12")]
    FomentoAgropecuario = 12
}