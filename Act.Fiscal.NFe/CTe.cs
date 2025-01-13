using Act.Common.Types;
using Act.Location.Contracts;
using System.Text.RegularExpressions;

namespace Act.Fiscal.NFe;

public readonly struct CTe : IEquatable<CTe>
{
    private static readonly Regex ChaveRegex = new(@"[0-9]{44}", RegexOptions.Compiled);

    public readonly string Numero;

    public CTe(string numero)
    {
        if (!ChaveRegex.IsMatch(numero))
            throw new ArgumentException("Chave CTe deve conter 44 dígitos");

        Numero = numero;
    }

    public UF ExtrairUF() => (UF)int.Parse(Numero[..1]);
    public DateOnly ExtrairAnoMes() => DateOnly.ParseExact(Numero[2..5], "yyMM");
    public CNPJ ExtrairCNPJ() => new CNPJ(Numero[6..20]);
    public ModeloNotaFiscal ExtrairModelo() => (ModeloNotaFiscal)int.Parse(Numero[21..22]);
    public SerieFiscal ExtrairSerie() => new SerieFiscal(Numero[23..25]);
    public NumeroFiscal ExtrairNumeroNotaFiscal() => new NumeroFiscal(Numero[26..34]);
    public TipoNotaFiscal ExtrairTipoNotaFiscal() => (TipoNotaFiscal)Convert.ToInt32(Numero[35]);
    public string ExtrairCodigoNotaFiscal() => Numero[36..43];
    public int ExtrairDigitoVerificador() => Convert.ToInt32(Numero[44]);

    public bool Equals(CTe otherId) => Numero.Equals(otherId.Numero);
    public override bool Equals(object? obj) => obj is CTe incomeId && Equals(incomeId);
    public override int GetHashCode() => Numero.GetHashCode();
    public override string ToString() => $"NFe{Numero}";

    public static bool operator ==(CTe left, CTe right) => left.Equals(right);
    public static bool operator !=(CTe left, CTe right) => !left.Equals(right);

}
