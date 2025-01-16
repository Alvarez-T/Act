using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Item;

public class Medicamento
{
    /// <summary>
    /// Utilizar o número do registro ANVISA  ou preencher com o literal “ISENTO”, no caso de medicamento isento de registro na ANVISA.
    /// </summary>
    [XmlElement("cProdANVISA")] public string RegistroAnvisa { get; set; }

    /// <summary>
    /// Obs.: Para medicamento isento de registro na ANVISA, informar o número da decisão que o isenta,
    /// como por exemplo o número da Resolução da Diretoria Colegiada da ANVISA (RDC).
    /// </summary>
    [XmlElement("xMotivoIsencao")] public string? MotivoIsencao { get; set; }

    [XmlElement("vPMC")] public decimal PrecoMaximoConsumidor { get; set; }
}