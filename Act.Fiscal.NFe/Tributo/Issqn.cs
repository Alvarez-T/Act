using System.Xml.Serialization;
using Act.Location.Contracts;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class Issqn
{
    [XmlElement("vBC")] public decimal BaseCalculo { get; set; }

    [XmlElement("vAliq")] public Percentual Aliquota { get; set; }

    [XmlElement("vISSQN")] public decimal ValorIssqn { get; set; }

    [XmlElement("cMunFG")] public MunicipioIBGE CodigoMunicipio { get; set; }

    [XmlElement("cListServ")] public string ItemServico { get; set; }

    /// <summary>
    /// Valor de dedução para redução da base de cálculo
    /// </summary>
    [XmlElement("vDeducao")] public decimal? ValorDeducao { get; set; }

    [XmlElement("vOutro")] public decimal? ValorOutrasRetencoes { get; set; }

    [XmlElement("vDescIncond")] public decimal? ValorDescontoIncondicionado { get; set; }

    [XmlElement("vDescCond")] public decimal? ValorDescontoCondicionado { get; set; }

    /// <summary>
    /// Valor Retenção ISS
    /// </summary>
    [XmlElement("vISSRet")] public decimal? IssRetido { get; set; }

    [XmlElement("indISS")] public ExibilidadeIss ExibilidadeIss { get; set; }

    [XmlElement("cServico")] public string? CodigoServico { get; set; }

    [XmlElement("cMun")] public MunicipioIBGE? CodigoMunicipioIncidencia { get; set; }

    [XmlElement("cPais")] public string? CodigoPais { get; set; }

    [XmlElement("nProcesso")] public string? NumeroProcesso { get; set; }

    [XmlElement("indIncentivo")] public bool IndicadorIncentivoFiscal { get; set; }
}