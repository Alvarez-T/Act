using System.Xml.Serialization;
using Act.Location.Contracts;
using Act.Utils;
using Act.Xml;

namespace Act.Fiscal.NFe.Tributo;

/// <summary>
/// Partilha do ICMS entre a UF de origem e UF de destino ou a UF definida na legislação
/// Operação interestadual para consumidor final com partilha do ICMS devido na operação entre a UF de origem e a UF do destinatário ou ou a UF definida na legislação. (Ex.UF da concessionária de entrega de veículos)
/// </summary>
[XmlRoot("ICMSPart")]
public class IcmsPartilhado
{
    [XmlElement("orig")] public OrigemMercadoria Origem { get; set; }

    [XmlElement("CST")] public Cst Cst { get; set; }

    [XmlElement("modBC")] public ModalidadeBaseCalculoIcms ModalidadeBaseCalculoIcms { get; set; }

    [XmlElement("vBC")] public decimal ValorBaseCalculoIcms { get; set; }

    [XmlElement("pRedBC")] public Percentual? PercentualReducaoBaseCalculo { get; set; }

    [XmlElement("pICMS")] public Percentual AliquotaIcms { get; set; }

    [XmlElement("vICMS")] public decimal ValorIcms { get; set; }

    [XmlElement("modBCST")] public ModalidadeBaseCalculoIcmsST ModalidadeBaseCalculoIcmsSt { get; set; }

    [XmlElement("pMVAST")] public Percentual? MargemValorAdicionadoIcmsSt { get; set; }

    [XmlElement("pRedBCST")] public Percentual? PercentualReducaoBaseCalculoIcmsSt { get; set; }

    [XmlElement("vBCST")] public decimal ValorBaseCalculoIcmsSt { get; set; }

    [XmlElement("pICMSST")] public Percentual AliquotaIcmsSt { get; set; }

    [XmlElement("vICMSST")] public decimal ValorIcmsSt { get; set; }

    /// <summary>
    /// Valor da Base de cálculo do FCP retido por substituição tributária.
    /// </summary>
    [XmlElement("vBCFCPST")] public decimal? ValorBaseCalculoFcpPorSt { get; set; }

    [XmlElement("pFCPST")] public Percentual? PercentualFcpPorSt { get; set; }

    [XmlElement("vFCPST")] public decimal? ValorFcpPorSt { get; set; }

    /// <summary>
    /// Percentual para determinação do valor  da Base de Cálculo da operação própria
    /// </summary>
    [XmlElement("pBCOp")] public Percentual PercentualBaseCalculoPropria { get; set; }

    [XmlElement("UFST")] public UFSigla UFDevidoIcmsSt { get; set; }
}