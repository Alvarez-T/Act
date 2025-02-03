namespace Act.Fiscal;

public readonly record struct Pis();
public readonly record struct Cofins();
public readonly record struct Icms();

public readonly record struct Cst(string Codigo)
{
    public static implicit operator Cst(string value) => new Cst();
}

public readonly record struct Csosn(string Codigo)
{
    public static implicit operator Csosn(string value) => new Csosn();
}

