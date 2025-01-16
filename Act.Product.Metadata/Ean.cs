namespace Act.Product.Metadata;

public readonly struct Ean
{
    private readonly string _id;

    public Ean(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _id = id;
    }
    
    public static implicit operator Ean(string id) => new Ean(id);
    public static implicit operator string(Ean ean) => ean._id;
    public override string ToString() => _id;
}