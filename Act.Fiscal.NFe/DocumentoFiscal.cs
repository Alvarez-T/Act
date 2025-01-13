using Act.Common.Types;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe;

public class DocumentoFiscal
{
    [XmlElement("cUF")] public UF UF { get; set; }

    [XmlElement("AAMM")] public DateOnly AnoMesEmissao { get; set; }

    [XmlElement("CNPJ")] public CNPJ CNPJEmitente { get; set; }

    [XmlElement("mod")] public ModeloDocumentoFiscal Modelo { get; set; }

    /// <summary>
    /// Série do Documento Fiscal.
    /// </summary>
    [XmlElement("serie")] public SerieFiscal Serie { get; set; }

    /// <summary>
    /// Número do Documento Fiscal.
    /// </summary>
    [XmlElement("nNF")] public NumeroFiscal Numero { get; set; }
}