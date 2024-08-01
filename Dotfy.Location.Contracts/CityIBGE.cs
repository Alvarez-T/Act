namespace Dotfy.Location.Contracts;

public readonly record struct CityIBGE
{
    private readonly string _value;

    public CityIBGE(string postalCode)
    {
        _value = postalCode;
    }

    public static implicit operator CityIBGE(string postalCode) => new CityIBGE(postalCode);
    public static implicit operator string(CityIBGE postal) => postal._value;
}