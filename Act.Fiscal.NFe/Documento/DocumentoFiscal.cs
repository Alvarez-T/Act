using System.Xml.Serialization;
using Act.Entidade;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.Documento;

public class DocumentoFiscal
{
    [XmlElement("cUF")] public UF UF { get; set; }

    [XmlElement("AAMM")] public DateOnly AnoMesEmissao { get; set; }

    [XmlElement("CNPJ")] public Cnpj CNPJEmitente { get; set; }

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