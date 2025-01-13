namespace Act.Fiscal.NFe;

public class DocumentoFiscalReferenciado
{
    /// <summary>
    /// Chave de acesso da NF-e referenciada
    /// </summary>
    [XmlElement("refNFe")] public ChaveNFe ChaveReferenciada { get; set; }

    /// <summary>
    /// Referencia uma NF-e (modelo 55) emitida anteriormente pela sua Chave de Acesso com código numérico zerado, permitindo manter o sigilo da NF-e referenciada.
    /// </summary>
    [XmlElement("refNFeSig")] public ChaveNFe ChaveReferenciadaSigilosa { get; set; }

    /// <summary>
    /// Dados da NF modelo 1/1A referenciada ou NF modelo 2 referenciada
    /// </summary>
    [XmlElement("refNF")] public DocumentoFiscal DocumentoFiscal { get; set; }

    [XmlElement("refNFP")] internal object? DocumentoFiscalProdutor { get => null; set => throw new NotSupportedException("A Nota Fiscal contém referência a um Documento fiscal de Produtor não suportado."); }

    [XmlElement("refCTe")] public CTe CTeReferenciado { get; set; }

    [XmlElement("refECF")] internal object? DocumentoECF { get => null; set => throw new NotSupportedException("A Nota Fiscal contém referência a um Documento ECF não suportado."); }
}