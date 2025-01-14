namespace Act.Location.Contracts;

public readonly record struct CEP
{
    private readonly string _value;

    public CEP(string postalCode)
    {
        _value = postalCode;
    }

    public static implicit operator CEP(string postalCode) => new CEP(postalCode);
    public static implicit operator string(CEP cep) => cep._value;
}