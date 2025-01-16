using System.Xml.Serialization;
using Act.Entidade;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.Entidade;

public class EmitenteAvulso
{
    [XmlAttribute("CNPJ")] public Cnpj CNPJ { get; set; }

    [XmlAttribute("xOrgao")] public string OrgaoEmitente { get; set; }

    [XmlAttribute("matr")] public string MatriculaAgente { get; set; }

    [XmlAttribute("xAgente")] public string Agente { get; set; }

    [XmlAttribute("fone")] public Telefone Telefone { get; set; }

    [XmlAttribute("UF")] public UF UF { get; set; }

    /// <summary>
    /// (DAR) Número do Documento de Arrecadação de Receita
    /// </summary>
    [XmlAttribute("nDAR")] public string? DocumentoArrecadacaoReceita { get; set; }

    /// <summary>
    /// Data de emissão do DAR (Documento de Arrecadação de Receita)
    /// </summary>
    [XmlAttribute("dEmi")] public DateTime? DataEmissaoDAR { get; set; }

    /// <summary>
    /// Valor Total constante no DAR (Documento de Arrecadação de Receita)
    /// </summary>
    [XmlAttribute("vDAR")] public decimal? ValorTotalDAR { get; set; }

    [XmlAttribute("repEmi")] public string ReparticaoFiscal { get; set; }

    /// <summary>
    /// Data de pagamento do DAR (Documento de Arrecadação de Receita)
    /// </summary>
    [XmlAttribute("dPag")] public DateTime? DataPagamentoDAR { get; set; }
}