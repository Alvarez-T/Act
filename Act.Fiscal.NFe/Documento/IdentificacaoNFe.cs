using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using Act.Fiscal.NFe.Localizacao;
using Act.Location.Contracts;
using Act.Xml;

namespace Act.Fiscal.NFe.Documento;

[XmlRoot("ide")]
public class IdentificacaoNFe
{
    [XmlElement("cUF")] public UF UF { get; set; }

    [XmlElement("cNF")] public string CodigoNotaFiscal { get; }

    [Length(1, 60)]
    [XmlElement("natOp")] public string NaturezaOperacao { get; set; }

    [XmlElement("mod")] public ModeloNotaFiscal Modelo => ModeloNotaFiscal.NFe;

    [XmlElement("serie")] public SerieFiscal Serie { get; set; }

    [XmlElement("nNF")] public NumeroFiscal NumeroFiscal { get; set; }

    [XmlElement("dhEmi")] public DateTime DataEmissao { get; set; }

    [XmlElement("dhSaiEnt")] public DateTime? DataSaidaOuEntrada { get; set; }

    [XmlElement("tpNF")] public TipoNotaFiscal Tipo { get; set; }

    [XmlElement("idDest")] public IdentificadorDestino IdentificadorDestino { get; set; }

    [XmlElement("cMunFG")] public CidadeIBGE Municipio { get; set; }

    [XmlElement("tpImp")] public TipoImpressao TipoImpressao { get; set; }

    [XmlElement("tpEmis")] public TipoEmissao TipoEmissao { get; set; }

    [XmlElement("cDV")] public int DigitoVerificador { get; set; }

    [XmlElement("tpAmb")] public Ambiente Ambiente { get; set; }

    [XmlElement("finNFe")] public FinalidadeNFe Finalidade { get; set; }

    /// <summary>
    /// Indicador de operação com <u>consumidor final</u>
    /// </summary>
    [XmlElement("indFinal")] public bool IndicadorConsumidoFinal { get; set; }

    /// <summary>
    /// Indicador de presença do comprador no estabelecimento comercial no momento da operação
    /// </summary>
    [XmlElement("indPres")] public IndicadorPresenca IndicadorPresenca { get; set; }

    /// <summary>
    /// Indicador de Intermediador/Marketplace
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> = Operação sem intermediador(em site ou plataforma própria)<br/>
    /// <see langword="true"/> = Operação em site ou plataforma de terceiros(intermediadores/marketplace)
    /// </remarks>
    [XmlElement("indIntermed")] public bool? IndicadorIntermediador { get; set; }

    [XmlElement("procEmi")] internal ProcessoEmissaoNF ProcessoDeEmissaoNF { get; set; }

    [XmlElement("verProc")] public string VersaoAplicativo { get; set; }

    [XmlElement("dhCont")] public DateTime? DataContingencia { get; set; }

    [XmlElement("xJust")] public string? JustificativaContingencia { get; set; }

    /// <summary>
    /// Grupo com informações de Documentos Fiscais referenciados. Informação utilizada nas hipóteses previstas na legislação.
    /// (Ex.: Devolução de mercadorias, Substituição de NF cancelada, Complementação de NF, etc.).
    /// </summary>
    [XmlElement("NFref")] public List<DocumentoFiscalReferenciado> DocumentosReferenciados { get; set; }
}