namespace Act.Fiscal.NFe.Entidade;

/// <summary>
/// Código de Regime Tributário
/// </summary>
public enum CRT
{
    /// <summary>
    /// Simples Nacional
    /// </summary>
    SimplesNacional = 1,

    /// <summary>
    /// Simples Nacional – excesso de sublimite de receita bruta
    /// </summary>
    SimplesNacionalExcedido = 2,

    /// <summary>
    /// Regime Normal (Lucro Presumido e Lucro Real)
    /// </summary>
    RegimeNormal = 3,
    /// <summary>
    /// Simples Nacional - Microempreendedor individual - MEI
    /// </summary>
    SimplesNacionalMEI = 4
}