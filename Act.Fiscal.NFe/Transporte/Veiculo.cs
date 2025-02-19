using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Act.Location.Contracts;

namespace Act.Fiscal.NFe.Transporte
{
    internal sealed class Veiculo
    {
        [XmlElement("placa")] public string Placa { get; set; }
        [XmlElement("UF")] public UFSigla UF { get; set; }
        /// <summary>
        /// Registro Nacional Transportador Carga
        /// </summary>
        [XmlElement("RNTC")] public string Rntc { get; set; }
    }
}
