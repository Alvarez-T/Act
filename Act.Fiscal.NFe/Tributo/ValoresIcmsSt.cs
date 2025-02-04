using System.Xml.Serialization;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

public class ValoresIcmsSt
{
    [XmlElement("modBCST")] public ModalidadeBaseCalculoIcmsST ModalidadeBaseCalculoST { get; set; }

    /// <summary>
    /// Percentual da margem de valor Adicionado do ICMS ST
    /// </summary>
    [XmlElement("pMVAST")] public Percentual? MargemValorAdicionadoST { get; set; }

    [XmlElement("pRedBCST")] public Percentual? PercentualReducaoBaseCalculoST { get; set; }

    /// <summary>
    /// Base de cálculo do ICMS ST
    /// </summary>
    [XmlElement("vBCST")] public decimal? BaseCalculoIcmsST { get; set; }

    /// <summary>
    /// Alíquota do ICMS ST
    /// </summary>
    [XmlElement("pICMSST")] public decimal? AliquotaIcmsST { get; set; }

    [XmlElement("vICMSST")] public decimal? ValorIcmsST { get; set; }

    public FundoCombatePobrezaSt? FundoCombatePobrezaSt { get; set; }
}