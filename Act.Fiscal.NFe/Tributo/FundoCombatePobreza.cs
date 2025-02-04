using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

/// <summary>
/// Fundo de Combate à Pobreza (FCP).
/// </summary>
public class FundoCombatePobreza
{
    [XmlElement("vBCFCP")] public decimal? BaseCalculoFcp { get; set; }
    /// <summary>
    /// Percentual de ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("pFCP")] public Percentual? PercentualFcp { get; set; }

    /// <summary>
    /// Valor do ICMS relativo ao Fundo de Combate à Pobreza (FCP).
    /// </summary>
    [XmlElement("vFCP")] public decimal? ValorFcp { get; set; }
}

/// <summary>
/// Fundo de Combate à Pobreza (FCP) por ICMS ST.
/// </summary>
public class FundoCombatePobrezaSt
{
    /// <summary>
    /// Valor da Base de cálculo do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vBCFCPST")] public decimal? BaseCalculoFcpPorST { get; set; }

    /// <summary>
    /// Percentual de FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("pFCPST")] public decimal? PercentualFcpPorST { get; set; }

    /// <summary>
    /// Valor do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vFCPST")] public decimal? ValorFcpPorST { get; set; }
}