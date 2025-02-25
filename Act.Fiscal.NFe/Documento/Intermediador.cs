﻿using System.Xml.Serialization;
using Act.Entidade;

namespace Act.Fiscal.NFe.Documento;

public class Intermediador
{
    [XmlElement("CNPJ")] public Cnpj CNPJ { get; set; }
    [XmlElement("idCadIntTran")] public string CodigoIntermediador { get; set; }
}