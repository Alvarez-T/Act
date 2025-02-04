
using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Item;

public class Rastreabilidade
{
    [XmlElement("nLote")] public string Lote { get; set; }

    [XmlElement("qLote")] public decimal QuantidadeLote { get; set; }

    /// <summary>
    /// Data de Fabricação / Produção.
    /// </summary>
    [XmlElement("dFab")] public DateOnly DataFabricacao { get; set; }

    [XmlElement("dVal")] public DateOnly DataValidade { get; set; }

    [XmlElement("cAgreg")] public string CodigoAgregacao { get; set; }
}