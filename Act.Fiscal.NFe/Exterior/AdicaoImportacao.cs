using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Exterior;

public class AdicaoImportacao
{
    [XmlElement("nAdicao")] public int NumeroAdicao { get; set; }

    [XmlElement("nSeqAdic")] public int Sequencia { get; set; }

    [XmlElement("cFabricante")] public string CodigoFabricante { get; set; }

    [XmlElement("vDescDI")] public decimal? ValorDescontoItem { get; set; }

    /// <summary>
    /// Número do ato concessório de Drawback
    /// </summary>
    [XmlElement("nDraw")] public string NumeroDrawback { get; set; }
}