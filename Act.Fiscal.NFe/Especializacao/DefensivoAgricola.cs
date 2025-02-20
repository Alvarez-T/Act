using System.Xml.Serialization;
using Act.Entidade;

namespace Act.Fiscal.NFe.Especializacao;

internal sealed class DefensivoAgricola
{
    [XmlElement("nReceituario")] public string NumeroReceituario { get; set; }

    [XmlElement("CPFRespTec")] public Cpf CpfResponsavelTecnico { get; set; }
}