using Act.Product.Metadata;

namespace Act.Fiscal;

public readonly record struct Pis();
public readonly record struct Cofins();
public readonly record struct Icms();

public readonly record struct Cst(string Codigo)
{
    public static implicit operator Cst(string value) => new Cst();
}

public readonly record struct Csosn(string Codigo)
{
    public static implicit operator Csosn(string value) => new Csosn();
}


public class ProdutoFiscal
{
    public required Sku Sku { get; set; }
    public required string Descricao { get; set; }
    public required Ean Ean { get; set; }
    public required UnidadeMedida UnidadeMedida { get; set; }
    public Cfop Cfop { get; set; }
    public Cest Cest { get; set; }
    public Ncm Ncm { get; set; }
    public Cst? Cst { get; set; }
    public Csosn? Csosn { get; set; }
    public required TipoIcms TipoIcms { get; set; }
    public OrigemMercadoria Origem { get; set; }
}