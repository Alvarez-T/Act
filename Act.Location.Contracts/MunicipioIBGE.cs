namespace Act.Location.Contracts;

public readonly record struct MunicipioIBGE
{
    private readonly string _value;

    public MunicipioIBGE(string postalCode)
    {
        _value = postalCode;
    }

    public static implicit operator MunicipioIBGE(string postalCode) => new MunicipioIBGE(postalCode);
    public static implicit operator string(MunicipioIBGE postal) => postal._value;
}