using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class ImpostoDevolucao
{
    [XmlElement("pDevol")] public Percentual PercentualMercadoriaDevolvida { get; set; }

    [XmlElement("IPI|vIPIDevol")] public decimal ValorIpiDevolvido { get; set; }
}