﻿using System.Xml.Serialization;

namespace Act.Fiscal.NFe.Especializacao;

internal sealed class InformacoesAquisicoesCana
{
    [XmlElement("safra")] public string Safra { get; set; }
    [XmlElement("ref")] public string Referencia { get; set; }
    [XmlElement("forDia")] public List<FornecimentoDiarioCana> FornecimentosDiarios { get; set; }
    [XmlElement("qTotMes")] public decimal TotalMes { get; set; }
    [XmlElement("qTotAnt")] public decimal TotalAnterior { get; set; }
    [XmlElement("qTotGer")] public decimal TotalGeral { get; set; }
    [XmlElement("deduc")] public List<Deducao> Deducoes { get; set; }
    [XmlElement("vFor")] public decimal ValorFornecimentos { get; set; }
    [XmlElement("vTotDed")] public decimal ValorTotalDeducoes { get; set; }
    [XmlElement("vLiqFor")] public decimal ValorLiquidoFornecimentos { get; set; }
}