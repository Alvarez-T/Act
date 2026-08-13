using Sample.Generated;

namespace YFex.Xml.Sample;

/// <summary>
/// Compile-time + runtime proof that the generator emits correct shapes:
/// faceted struct rejects bad input, plain string stays string, occurrence
/// rules produce nullable / List / non-null, and choice yields a discriminator.
/// </summary>
internal static class Verify
{
    public static IReadOnlyList<string> Run()
    {
        var results = new List<string>();

        // Faceted enum struct: valid accepted, invalid rejected.
        results.Add($"TUf SP valid = {TUf.TryParse("SP", out _)}");      // true
        results.Add($"TUf XX valid = {TUf.TryParse("XX", out _)}");      // false

        // Faceted pattern struct: 44 digits ok, short rejected.
        results.Add($"TChNFe 44d valid = {TChNFe.TryParse(new string('1', 44), out _)}"); // true
        results.Add($"TChNFe short valid = {TChNFe.TryParse("123", out _)}");             // false

        // POCO shape checks (compile-time): build a det with the expected member types.
        var p = new prod
        {
            cProd = "ABC",          // required string -> non-nullable string
            cEAN = null,            // optional -> nullable string
            vUnCom = default,       // faceted struct, required
            NVE = { "a", "b" },     // unbounded -> List<string> (non-null, init)
            orig = "0",             // choice member -> nullable
        };
        results.Add($"prod choice discriminator = {p.prodChoice}"); // orig

        var d = new det
        {
            prod = p,               // complex type ref
            uf = default,           // TUf faceted struct, required
            chave = null,           // optional faceted struct -> nullable
            nItem = 1,              // required attribute -> int
        };
        results.Add($"det nItem = {d.nItem}, list count = {d.prod!.NVE.Count}");

        return results;
    }
}
