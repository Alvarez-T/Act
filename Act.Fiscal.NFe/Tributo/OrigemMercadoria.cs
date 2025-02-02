namespace Act.Fiscal.NFe.Tributo;

public readonly record struct OrigemMercadoria
{
    private static readonly IReadOnlyDictionary<int, string> OrigemDescriptions = new Dictionary<int, string>
    {
        { 0, "Nacional, exceto as indicadas nos códigos 3, 4, 5 e 8" },
        { 1, "Estrangeira - Importação direta, exceto a indicada no código 6" },
        { 2, "Estrangeira - Adquirida no mercado interno, exceto a indicada no código 7" },
        { 3, "Nacional, mercadoria ou bem com Conteúdo de Importação superior a 40% e inferior ou igual a 70%" },
        { 4, "Nacional, cuja produção tenha sido feita em conformidade com os processos produtivos básicos de que tratam as legislações citadas nos Ajustes" },
        { 5, "Nacional, mercadoria ou bem com Conteúdo de Importação inferior ou igual a 40%" },
        { 6, "Estrangeira - Importação direta, sem similar nacional, constante em lista da CAMEX e gás natural" },
        { 7, "Estrangeira - Adquirida no mercado interno, sem similar nacional, constante lista CAMEX e gás natural" },
        { 8, "Nacional, mercadoria ou bem com Conteúdo de Importação superior a 70%" }
    };

    public int Codigo { get; init; }

    public OrigemMercadoria(int codigo)
    {
        if (!OrigemDescriptions.ContainsKey(codigo))
        {
            throw new ArgumentOutOfRangeException(nameof(codigo), "Código inválido para origem da mercadoria.");
        }

        Codigo = codigo;
    }

    public string GetDescricao()
    {
        return OrigemDescriptions.TryGetValue(Codigo, out var descricao)
            ? descricao
            : throw new InvalidOperationException("Código de origem inválido.");
    }

    public static implicit operator int(OrigemMercadoria origem) => origem.Codigo;
}