namespace Dotfy.Location.Contracts;

public readonly record struct StateIBGE
{
    private readonly string _value;

    public StateIBGE(string postalCode)
    {
        _value = postalCode;
    }

    public static implicit operator StateIBGE(string postalCode) => new StateIBGE(postalCode);
    public static implicit operator string(StateIBGE postal) => postal._value;
}