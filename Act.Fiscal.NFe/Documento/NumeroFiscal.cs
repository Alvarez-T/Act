using System.Text.RegularExpressions;
using Act.Utilities;

namespace Act.Fiscal.NFe.Documento;

public readonly struct NumeroFiscal : IEquatable<NumeroFiscal>, IComparable<NumeroFiscal>
{
    private static readonly Regex NumeroRegex = new(@"^\d+$", RegexOptions.Compiled);

    private readonly int _numero;

    public NumeroFiscal(string numero)
    {
        if (!NumeroRegex.IsMatch(numero))
            throw new ArgumentException("Número da Nota Fiscal não pode conter letras");

        if (numero.Length > 8)
            throw new ArgumentException("Número da Nota Fiscal não pode conter mais de 8 dígitos");

        _numero = int.Parse(numero);
    }

    public NumeroFiscal(int numero)
    {
        if (!numero.IsBetween(0, 99999999))
            throw new ArgumentException("Número da Nota Fiscal não pode conter mais de 8 dígitos");

        _numero = numero;
    }

    public bool Equals(NumeroFiscal otherId) => _numero.Equals(otherId._numero);
    public int CompareTo(NumeroFiscal other) => _numero.CompareTo(other._numero);

    public override bool Equals(object? obj) => obj is NumeroFiscal incomeId && Equals(incomeId);
    public override int GetHashCode() => _numero.GetHashCode();
    public override string ToString() => _numero.ToString().PadLeft(8, '0');

    public static bool operator ==(NumeroFiscal left, NumeroFiscal right) => left.Equals(right);
    public static bool operator !=(NumeroFiscal left, NumeroFiscal right) => !left.Equals(right);

}