using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Tributo;

/// <summary>
/// Enumeração para motivo da desoneração do ICMS.
/// </summary>
public enum MotivoDesoneracaoIcms
{
    Taxi = 1,
    UsoAgropecuaria = 3,
    FrotistaOuLocadora = 4,
    DiplomaticoOuConsular = 5,
    Utilitarios = 6,
    SUFRAMA = 7,
    VendaOrgaoPublico = 8,
    Outros = 9,
    DeficienteCondutor = 10,
    DeficienteNaoCondutor = 11,
    FomentoAgropecuario = 12,
    Olimpiadas = 16,
    SolicitadoFisco = 90
}