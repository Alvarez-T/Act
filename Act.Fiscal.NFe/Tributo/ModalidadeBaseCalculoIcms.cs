namespace Act.Fiscal.NFe.Tributo;

public enum ModalidadeBaseCalculoIcms
{
    MargemValorAgregado = 0,
    Pauta = 1,
    PrecoTabeladoMaximo = 2,
    ValorOperacao = 3
}

public enum ModalidadeBaseCalculoIcmsST
{
    PrecoTabeladoOuMaximoSugerido = 0,
    ListaNegativa = 1,
    ListaPositiva = 2,
    ListaNeutra = 3,
    MargemValorAgregado = 4,
    Pauta = 5,
    ValorOperacao = 6
}