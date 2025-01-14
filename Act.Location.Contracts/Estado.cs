using Act.Utilities;

namespace Act.Location.Contracts;

public static class Estado
{
    private static readonly Dictionary<UF, string> SiglasPorEstado = new()
    {
        { UF.Rondonia, "RO" },
        { UF.Acre, "AC" },
        { UF.Amazonas, "AM" },
        { UF.Roraima, "RR" },
        { UF.Para, "PA" },
        { UF.Amapa, "AP" },
        { UF.Tocantins, "TO" },
        { UF.Maranhao, "MA" },
        { UF.Piaui, "PI" },
        { UF.Ceara, "CE" },
        { UF.RioGrandeDoNorte, "RN" },
        { UF.Paraiba, "PB" },
        { UF.Pernambuco, "PE" },
        { UF.Alagoas, "AL" },
        { UF.Sergipe, "SE" },
        { UF.Bahia, "BA" },
        { UF.MinasGerais, "MG" },
        { UF.EspiritoSanto, "ES" },
        { UF.RioDeJaneiro, "RJ" },
        { UF.SaoPaulo, "SP" },
        { UF.Parana, "PR" },
        { UF.SantaCatarina, "SC" },
        { UF.RioGrandeDoSul, "RS" },
        { UF.Goias, "GO" },
        { UF.MatoGrosso, "MT" },
        { UF.MatoGrossoDoSul, "MS" },
        { UF.DistritoFederal, "DF" }
    };

    public static string ObterSiglaTexto(this UF uf)
    {
        if (SiglasPorEstado.TryGetValue(uf, out string sigla))
        {
            return sigla;
        }
        throw new ArgumentException("Estado não encontrado.", nameof(uf));
    }

    public static string ObterSiglaTexto(this UFSigla uf) => uf.ToString();

    public static UFSigla ObterSigla(this UF uf) => (UFSigla)uf.ToInt();
}