namespace YFex.Product.Metadata;

public readonly struct Cfop
{
    private readonly string _id;

    public Cfop(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _id = id;
    }
    
    public override string ToString() => _id;
}