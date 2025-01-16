using Act.Entidade;
using Act.Fiscal.NFe.Exterior;
using Act.Product.Metadata;

namespace Act.Fiscal.NFe.Item;

public class Produto
{
    /// <summary>
    /// Código do produto ou serviço. Preencher com CFOP caso se trate de itens não relacionados com mercadorias/produto e que o contribuinte não possua codificação própria<br/>
    /// Ex: ”CFOP9999”.
    /// </summary>
    [XmlElement("cProd")] public Sku CodigoProduto { get; set; }

    /// <summary>
    /// GTIN (Global Trade Item Number) do produto, antigo código EAN ou código de barras
    /// </summary>
    [XmlElement("cEAN")] public Ean CodigoEan { get; set; }

    /// <summary>
    /// Codigo de barras diferente do padrão GTIN
    /// </summary>
    [XmlElement("cBarras")] public string CodigoBarras { get; set; }

    [XmlElement("xProd")] public string Descricao { get; set; }

    [XmlElement("NCM")] public Ncm Ncm { get; set; }

    [XmlElement("NVE")] public string Nve { get; set; }

    /// <summary>
    /// Codigo especificador da Substuicao Tributaria - CEST,
    /// que identifica a mercadoria sujeita aos regimes de  substituicao tributária e de antecipação do recolhimento  do imposto
    /// </summary>
    [XmlElement("CEST")] public Cest Cest { get; set; }

    /// <summary>
    /// Indicador de Produção em escala relevante, conforme Cláusula 23 do Convenio ICMS 52/2017:<br/>
    /// S - Produzido em Escala Relevante <br/>
    /// N – Produzido em Escala NÃO Relevante.<br/>
    /// <para>Nota: preenchimento Obrigatório para produtos com NCM 
    /// relacionado no Anexo XXVII do Convenio 52/2017 </para> 
    /// </summary>
    [XmlElement("indEscala")] public char? IndicadorEscala { get; set; }

    /// <summary>
    /// CNPJ do Fabricante da Mercadoria, obrigatório para produto em escala NÃO relevante.
    /// </summary>
    [XmlElement("CNPJFab")] public Cnpj CnpjFabricante { get; set; }

    /// <summary>
    /// Código de Benefício Fiscal na UF aplicado ao item 
    /// </summary>
    [XmlElement("cBenef")] public string CodigoBeneficio { get; set; }

    [XmlElement("gCred")] public CreditoPresumido? CreditoPresumido { get; set; }

    /// <summary>
    /// Código EX TIPI
    /// </summary>
    /// <returns></returns>
    [XmlElement("EXTIPI")] public string? CodigoExTipi { get; set; }

    [XmlElement("CFOP")] public Cfop Cfop { get; set; }

    [XmlElement("uCom")] public UnidadeMedida UnidadeComercial { get; set; }

    [XmlElement("qCom")] public decimal QuantidadeComercial { get; set; }

    [XmlElement("vUnCom")] public decimal ValorUnitarioComercial { get; set; }

    /// <summary>
    /// Valor bruto do produto ou serviço
    /// </summary>
    [XmlElement("vProd")] public decimal ValorBruto { get; set; }

    /// <summary>
    /// Preencher com o código GTIN-8, GTIN-12, GTIN-13 ou GTIN-14 (antigos códigos EAN, UPC e DUN-14) da unidade tributável do produto. <br/>
    /// O GTIN da unidade tributável deve corresponder àquele da menor unidade comercializável identificada por código GTIN. <br/>
    /// Para produtos que não possuem código de barras com GTIN, deve ser informado o literal "SEM GTIN".
    /// </summary>
    [XmlElement("cEANTrib")] public string EanTributavel { get; set; }

    [XmlElement("uTrib")] public UnidadeMedida UnidadeTributavel { get; set; }

    [XmlElement("qTrib")] public decimal QuantidadeTributavel { get; set; }

    [XmlElement("vUnTrib")] public decimal ValorUnitarioDeTributacao { get; set; }

    [XmlElement("vFrete")] public decimal ValorFrete { get; set; }

    [XmlElement("vSeg")] public decimal ValorSeguro { get; set; }

    [XmlElement("vDesc")] public decimal ValorDesconto { get; set; }

    [XmlElement("vOutro")] public decimal OutrasDespesasAcessorias { get; set; }

    /// <summary>
    /// 0 = Valor do item (vProd) não compõe o valor total da NF-e <br/>
    /// 1 = Valor do item(vProd) compõe o valor total da NFe(vProd)
    /// </summary>
    [XmlElement("indTot")] public int IndicaTotal { get; set; }

    [XmlElement("DI")] public List<DeclaracaoImportacao>? DeclaracoesImportacao { get; set; }

    [XmlElement("detExport")] public List<ExportacaoIndireta>? ExportacoesIndiretas { get; set; }

    [XmlElement("xPed")] public string? PedidoCompra { get; set; }

    [XmlElement("nItemPed")] public int? ItemPedidoCompra { get; set; }

    /// <summary>
    /// Número de controle da FCI - Ficha de Conteúdo de Importação
    /// </summary>
    [XmlElement("nFCI")] public Guid? Fci { get; set; }

    [XmlElement("rastro")] public List<Rastreabilidade>? Lotes { get; set; }

    /// <summary>
    /// Informações mais detalhadas do produto (usada na NFF)
    /// </summary>
    [XmlElement("infProdNFF")] public InformacaoProdutoNff? InformacaoProdutoNff { get; set; }

    [XmlElement("infProdEmb")] public InformacaoEmbalagem? InformacaoEmbalagem { get; set; }

    //[XmlElement("veicProd")] public object? Veiculo { get; set; }

    [XmlElement("med")] public Medicamento? Medicamento { get; set; }

    //[XmlElement("arma")] public List<object>? Armas { get; set; }

    //[XmlElement("comb")] public object Combustivel { get; set; }

    [XmlElement("nRECOPI")] public string? NumeroRecopi { get; set; }

}