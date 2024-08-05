namespace Act.Location.Contracts;

public readonly record struct PostalCode
{
    private readonly string _value;

    public PostalCode(string postalCode)
    {
        _value = postalCode;
    }

    public static implicit operator PostalCode(string postalCode) => new PostalCode(postalCode);
    public static implicit operator string(PostalCode postalCode) => postalCode._value;
}