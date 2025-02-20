using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

internal sealed class TotaisIcms
{
    [XmlElement("vBC")] public decimal BaseCalculo { get; set; }

    [XmlElement("vICMS")] public decimal ValorIcms { get; set; }

    [XmlElement("vICMSDeson")] public decimal ValorIcmsDesonerado { get; set; }

    [XmlElement("vFCPUFDest")] public decimal? ValorFcpUfDestino { get; set; }

    [XmlElement("vICMSUFDest")] public decimal? ValorIcmsPartilhaDestino { get; set; }

    [XmlElement("vICMSUFRemet")] public decimal? ValorIcmsPartilhaRemetente { get; set; }

    [XmlElement("vFCP")] public decimal ValorFcp { get; set; }

    [XmlElement("vBCST")] public decimal BaseCalculoSt { get; set; }

    [XmlElement("vST")] public decimal ValorIcmsSt { get; set; }

    [XmlElement("vFCPST")] public decimal ValorFcpSt { get; set; }

    [XmlElement("vFCPSTRet")] public decimal ValorFcpStRetido { get; set; }

    [XmlElement("qBCMono")] public decimal? QuantidadeMonofasico { get; set; }

    [XmlElement("vICMSMono")] public decimal? IcmsMonofasico { get; set; }

    [XmlElement("vICMSMonoReten")] public decimal? IcmsMonofasicoRetencao { get; set; }

    [XmlElement("qBCMonoRet")] public decimal? QuantidadeMonofasicoRetido { get; set; }

    [XmlElement("vICMSMonoRet")] public decimal? IcmsMonofasicoRetido { get; set; }

    [XmlElement("vProd")] public decimal ValorProdutoServicos { get; set; }

    [XmlElement("vFrete")] public decimal Frete { get; set; }

    [XmlElement("vSeg")] public decimal Seguro { get; set; }

    [XmlElement("vDesc")] public decimal Desconto { get; set; }

    [XmlElement("vII")] public decimal ImpostoImportacao { get; set; }

    [XmlElement("vIPI")] public decimal ValorIpi { get; set; }

    [XmlElement("vIPIDevol")] public decimal ValorIpiDevolvido { get; set; }

    [XmlElement("vPIS")] public decimal ValorPis { get; set; }

    [XmlElement("vCOFINS")] public decimal ValorCofins { get; set; }

    [XmlElement("vOutro")] public decimal OutrasDespesas { get; set; }

    [XmlElement("vNF")] public decimal ValorNFe { get; set; }

    [XmlElement("vTotTrib")] public decimal? ValorEstimadoImposto { get; set; }

}