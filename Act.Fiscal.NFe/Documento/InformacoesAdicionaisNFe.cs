using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Documento;

public class InformacoesAdicionaisNFe
{
    [XmlElement("infAdFisco")] public string InformacoesAdicionaisFisco { get; set; }
    [XmlElement("infCpl")] public string InformacoesComplementaresContribuinte { get; set; }
    [XmlElement("obsCont")] public List<ObservacaoContribuinte> ObservacoesContribuinte { get; set; }
    [XmlElement("obsFisco")] public List<ObservacaoFisco> ObservacoesFisco { get; set; }
    [XmlElement("procRef")] public List<ProcessoReferenciado> ProcessosReferenciados { get; set; }
}