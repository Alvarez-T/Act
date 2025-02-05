using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

/// <summary>
/// Grupo a ser informado nas vendas interestarduais para consumidor final, não contribuinte de ICMS
/// </summary>
internal sealed class IcmsUfDestinatario
{
    [XmlElement("vBCUFDest")] public decimal BaseCalculo { get; set; }

    [XmlElement("vBCFCPUFDest")] public decimal? BaseCalculoFcp { get; set; }

    [XmlElement("pFCPUFDest")] public Percentual PercentualAdicionalFcp { get; set; }

    /// <summary>
    /// Alíquota adotada nas operações internas na UF do destinatário para o produto / mercadoria.
    /// </summary>
    [XmlElement("pICMSUFDest")] public Percentual Aliquota { get; set; }

    /// <summary>
    /// Alíquota interestadual das UF envolvidas: - 4% alíquota interestadual para produtos importados; - 7%
    /// para os Estados de origem do Sul e Sudeste (exceto ES), destinado para os Estados do Norte e Nordeste  ou ES; - 12% para os demais casos.
    /// </summary>
    [XmlElement("pICMSInter")] public Percentual AliquotaInterestadual { get; set; }

    /// <summary>
    /// Percentual de partilha para a UF do destinatário: - 40% em 2016; - 60% em 2017; - 80% em 2018; - 100% a partir de 2019.
    /// </summary>
    [XmlElement("pICMSInterPart")] public Percentual PercentualPartilha { get; set; }

    /// <summary>
    /// Valor do ICMS relativo ao Fundo de Combate à Pobreza (FCP) da UF de destino
    /// </summary>
    [XmlElement("vFCPUFDest")] public decimal? ValorFcp { get; set; }

    [XmlElement("vICMSUFDest")] public decimal ValorIcmsDestinatario { get; set; }

    [XmlElement("vICMSUFRemet")] public decimal ValorIcmsRemetente { get; set; }
}