using System.Xml.Serialization;
using Act.Product.Metadata;

namespace Act.Fiscal.NFe.Item;

/// <summary>
/// /// Informações de embalagem do produto (usada no Regime Especial Nota Fiscal Facil)
/// <para>Ex: Caixa com 3 KG <br/>
/// - xEmb: caixa <br/>
/// - qVolEmb: 3 <br/>
/// - uEmb: kg</para>
/// </summary>
public class InformacaoEmbalagem
{
    [XmlElement("xEmb")] public string Embalagem { get; set; }

    [XmlElement("qVolEmb")] public decimal VolumeProduto { get; set; }

    [XmlElement("uEmb")] public UnidadeMedida UnidadeMedida { get; set; }
}

/// <summary>
/// Informações mais detalhadas do produto (usada no Regime Especial Nota Fiscal Facil)
/// </summary>
public class InformacaoProdutoNff
{
    [XmlElement("cProdFisco")] public string CodigoFiscalProduto { get; set; }

    /// <summary>
    /// Código da operação selecionada na NFF e relacionada ao item
    /// </summary>
    [XmlElement("cOperNFF")] public string CodigoOperacaoNff { get; set; }
}