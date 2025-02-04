using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed record ImpostoImportacao
{
    [XmlElement("vBC")] public decimal BaseCalculo { get; set; }

    [XmlElement("vDespAdu")] public decimal DespesasAduaneiras { get; set; }

    [XmlElement("vII")] public decimal ValorImpostoImportacao { get; set; }

    [XmlElement("vIOF")] public decimal ImpostoOperacoesFinanceiras { get; set; }
}