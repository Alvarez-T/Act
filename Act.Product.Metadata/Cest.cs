namespace Act.Product.Metadata;

public readonly struct Cest
{
    private readonly string _id;

    public Cest(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _id = id;
    }
    
    public override string ToString() => _id;
}