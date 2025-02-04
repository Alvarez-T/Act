using System.Xml.Serialization;
using Act.Xml;

namespace Act.Fiscal.NFe.Item;

public class CreditoPresumido
{
    /// <summary>
    /// Código de Benefício Fiscal de Crédito Presumido na UF aplicado ao item
    /// </summary>
    [XmlElement("cCredPresumido")] public string Codigo { get; set; }

    [XmlElement("pCredPresumido")] public decimal Percentual { get; set; }

    [XmlElement("vCredPresumido")] public decimal Valor { get; set; }
}