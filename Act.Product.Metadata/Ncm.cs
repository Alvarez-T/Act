namespace YFex.Product.Metadata;

/// <summary>
/// Nomenclatura Comum do Mercosul
/// </summary>
public readonly struct Ncm
{
    private readonly string _id;

    public Ncm(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _id = id;
    }
    
    public override string ToString() => _id;
}