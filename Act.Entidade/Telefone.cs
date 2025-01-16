using System.Text.RegularExpressions;

namespace Act.Entidade;

public readonly struct Telefone : IEquatable<Telefone>
{
    private static readonly Regex TelefoneRegex = new(@"^(?:(?:\+|00)?(55)\s?)?(?:\(?([1-9][0-9])\)?\s?)?(?:((?:9\d|[2-9])\d{3})\-?(\d{4}))$", RegexOptions.Compiled);
    private readonly string _numero { get; }
    public string Numero => Regex.Replace(_numero, @"[^\d]", "");
    public bool IndicaCelular{ get; }

    private Telefone(string numero, bool celular)
    {
        _numero = numero;
        IndicaCelular = celular;
    }

    public static Telefone Criar(string numero)
    {
        var (valido, ehCelular) = ValidarNumeroTelefone(numero);

        if (valido)
            return new Telefone(numero, ehCelular);

        throw new ArgumentException("Número de telefone inválido.");
    }

    private static (bool valido, bool indicaCelular) ValidarNumeroTelefone(string numero)
    {
        var match = TelefoneRegex.Match(numero);

        if (match.Success)
        {
            // Verifica se é celular (números que começam com 9 após o DDD)
            bool indicaCelular = match.Groups[3].Value.StartsWith("9");
            return (true, indicaCelular);
        }

        return (false, false);
    }

    public string ObterNumeroComMascara(bool incluirCodigoPais = false)
    {
        string numeroLimpo = Numero;

        // Verifica se o número contém o código do país (55) e remove se necessário
        if (numeroLimpo.StartsWith("55") && numeroLimpo.Length > 10)
        {
            numeroLimpo = numeroLimpo.Substring(2); // Remove o "55"
        }

        if (IndicaCelular)
        {
            string formato = incluirCodigoPais ? "+55 (##) #####-####" : "(##) #####-####";
            return long.Parse(numeroLimpo).ToString(formato);
        }
        else
        {
            string formato = incluirCodigoPais ? "+55 (##) ####-####" : "(##) ####-####";
            return long.Parse(numeroLimpo).ToString(formato);
        }
    }

    public bool Equals(Telefone other) => Numero == other.Numero;

    public override string ToString() => ObterNumeroComMascara();

    public override bool Equals(object obj) => obj is Telefone other && Equals(other);

    public override int GetHashCode() => Numero.GetHashCode();
}