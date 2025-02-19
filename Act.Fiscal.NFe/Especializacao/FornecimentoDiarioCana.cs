using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Especializacao;

internal sealed class FornecimentoDiarioCana
{
    [XmlAttribute("dia")] public string Dia { get; set; }
    [XmlElement("qtde")] public decimal QuantidadeQuilogramas { get; set; }
}