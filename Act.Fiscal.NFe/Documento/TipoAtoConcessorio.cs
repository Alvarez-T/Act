using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Documento;

public enum TipoAtoConcessorio
{
    [XmlEnum("08")] TermoAcordo = 8,
    [XmlEnum("10")] RegimeEspecial = 10,
    [XmlEnum("12")] AutorizacaoEspecifica = 12,
    [XmlEnum("14")] AjusteSINIEF = 14,
    [XmlEnum("15")] ConvencioICMS = 15
}