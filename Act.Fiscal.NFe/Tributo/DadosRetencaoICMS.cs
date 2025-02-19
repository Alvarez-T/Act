using System.Xml.Serialization;
using Act.Location.Contracts;
using Act.Product.Metadata;
using Act.Utils;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class DadosRetencaoICMS
{
    [XmlElement("vServ")] public decimal ValorServico { get; set; }
    [XmlElement("vBCRet")] public decimal BaseCalculo{ get; set; }
    [XmlElement("pICMSRet")] public Percentual Aliquota { get; set; }
    [XmlElement("vICMSRet")] public decimal ValorICMSRetido { get; set; }
    [XmlElement("CFOP")] public Cfop CodigoFiscalOperacoesPrestacoes { get; set; }
    [XmlElement("cMunFG")] public MunicipioIBGE CodigoMunicipioOcorrenciaFatoGerador { get; set; }
}