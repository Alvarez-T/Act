namespace Dotfy.Location.Contracts;

public readonly record struct AddressId
{
    private readonly int _id;

    public AddressId(int id)
    {
        if (id <= 0)
            throw new ArgumentException("Id cannot be less or equal zero");
        
        _id = id;
    }
    
    public static implicit operator AddressId(int id) => new AddressId(id);
    public static implicit operator int(AddressId addressId) => addressId._id;
    public override string ToString() => _id.ToString();
}