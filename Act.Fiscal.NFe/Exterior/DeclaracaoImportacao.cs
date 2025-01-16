using System.Xml.Serialization;
using Act.Entidade;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.Exterior;

public class DeclaracaoImportacao
{
    /// <summary>
    /// Número do Documento de Importação (DI, DSI, DIRE, DUImp)
    /// </summary>
    [XmlElement("nDI")] public string NumeroDocumento { get; set; }

    /// <summary>
    /// Data de registro da DI/DSI/DA
    /// </summary>
    [XmlElement("dDI")] public DateOnly DataRegistro { get; set; }

    /// <summary>
    /// Local do desembaraço aduaneiro
    /// </summary>
    [XmlElement("xLocDesemb")] public string LocalDesembaraco { get; set; }

    [XmlElement("UFDesemb")] public UFSigla UFDesembaraco { get; set; }

    [XmlElement("dDesemb")] public DateOnly DataDesembaraco { get; set; }

    /// <summary>
    /// Via de transporte internacional informada na Declaração de Importação(DI)
    /// </summary>
    [XmlElement("tpViaTransp")] public TipoTransporte TipoTransporte { get; set; }

    /// <summary>
    /// Valor Adicional ao frete para renovação de marinha mercante (Utilizado apenas quando o tipo de transporte for marítimo
    /// </summary>
    [XmlElement("vAFRMM")] public decimal? ValorAfrmm { get; set; }

    [XmlElement("tpIntermedio")] public FormaImportacao FormaImportacao { get; set; }

    /// <summary>
    /// CNPJ do adquirente ou do encomendante
    /// <para>Obrigatória a informação no caso de importação por conta e ordem ou por encomenda.
    /// Informar os zeros não significativos</para>
    /// </summary>
    [XmlElement("CNPJ")] public Cnpj? Cnpj { get; set; }

    /// <summary>
    /// Sigla da UF do adquirente ou do encomendante
    /// </summary>
    [XmlElement("UFTerceiro")] public UFSigla? UFTerceiro { get; set; }

    [XmlElement("cExportador")] public string CodigoExportador { get; set; }

    [XmlElement("adi")] public List<AdicaoImportacao> Adicoes { get; set; }
}