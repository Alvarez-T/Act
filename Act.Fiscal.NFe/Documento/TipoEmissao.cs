namespace Act.Fiscal.NFe.Documento;

public enum TipoEmissao
{
    Normal = 1,
    ContingenciaFS = 2,

    /// <summary>
    /// Regime Especial Nota Fiscal Fácil
    /// </summary>
    RegimeEspecialNFF = 3,
    ContingenciaDPEC = 4,
    ContingenciaFSDA = 5,
    ContingenciaSVC_AN = 6,
    ContingenciaSVC_RS = 7,
    ContingenciaNFCe = 9
}