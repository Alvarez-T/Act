namespace YFex.Product.Metadata;

public readonly record struct Sku
{
    private readonly string _id;

    public Sku(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _id = id;
    }
    
    public static implicit operator Sku(string id) => new Sku(id);
    public static implicit operator string(Sku sku) => sku._id;
    public override string ToString() => _id;
}