using Act.Fiscal.NFe.Documento;
using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Exterior;

public class DetalheExportacao
{
    /// <summary>
    /// Número do ato concessório de Drawback
    /// </summary>
    [XmlElement("nDraw")] public string NumeroDrawback { get; set; }

    [XmlElement("exportInd")] public ExportacaoIndireta? ExportacaoIndireta { get; set; }
}

public class ExportacaoIndireta
{
    [XmlElement("nRE")] public int RegistroExportacao { get; set; }

    [XmlElement("chNFe")] public ChaveNFe ChaveExportacao { get; set; }

    /// <summary>
    /// Quantidade do item efetivamente exportado
    /// </summary>
    [XmlElement("qExport")] public decimal QuantidadeExportado { get; set; }
}