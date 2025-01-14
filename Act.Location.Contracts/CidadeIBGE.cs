namespace Act.Location.Contracts;

public readonly record struct CidadeIBGE
{
    private readonly string _value;

    public CidadeIBGE(string postalCode)
    {
        _value = postalCode;
    }

    public static implicit operator CidadeIBGE(string postalCode) => new CidadeIBGE(postalCode);
    public static implicit operator string(CidadeIBGE postal) => postal._value;
}