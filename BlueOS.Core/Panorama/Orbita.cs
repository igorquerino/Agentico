namespace BlueOS.Core.Panorama;

/// <summary>Onde uma estrela fica no mapa: o anel (1 centro … 4 borda) e o ângulo em graus (0 = direita, sentido horário).</summary>
public sealed record Posicao(int Anel, double Angulo, int Volta = 0);

/* ============================================================================
   A ÓRBITA: a gravidade puxa para o centro.
   ----------------------------------------------------------------------------
   Anel 1 (1-2), anel 2 (3-4), anel 3 (5-6), anel 4 os saudáveis. Dentro de
   cada anel as estrelas se espalham em ângulos iguais, as mais graves
   primeiro a partir do topo (-90°), e os saudáveis em ordem alfabética para
   o olho decorar onde cada um fica. O raio de cada anel é da tela (CSS);
   aqui só sai o ângulo. Os raios do mock: 135, 195, 255 e 335 px num mapa
   de 700. Capacidade: um anel com mais estrelas que cabem (14 px + 10 de
   folga) ganha uma segunda volta deslocada; a tela desenha os dois raios.
   ============================================================================ */
public static class Orbita
{
    public const int AnelDosSaudaveis = 4;
    /// <summary>Raios do mock, em px, para um mapa de 700. A tela escala pelo tamanho real.</summary>
    public static readonly IReadOnlyList<int> RaiosDoMock = [0, 135, 195, 255, 335];
    /// <summary>O lado do mapa do mock, em px: é por ele que os raios viram fração do mapa real.</summary>
    public const double LadoDoMock = 700;
    const double TamanhoDaEstrela = 14, Folga = 10;

    /// <summary>O raio do anel como fração do lado do mapa (a tela multiplica pela variável --mapa). Um lugar só para a geometria.</summary>
    public static double Fator(int anel) => RaiosDoMock[anel] / LadoDoMock;

    /// <summary>Quantas estrelas cabem numa volta do anel sem encostar, para o raio dado.</summary>
    public static int Capacidade(int anel, double raioPx) => Math.Max(1, (int)Math.Floor(2 * Math.PI * raioPx / (TamanhoDaEstrela + Folga)));

    /// <summary>Distribui as estrelas de um anel em ângulos iguais a partir do topo (-90°). Volta = 0 ou 1 (segunda volta, raio um pouco menor).</summary>
    public static List<(int Indice, double Angulo, int Volta)> Distribuir(int quantidade, int capacidadePorVolta)
    {
        var saida = new List<(int, double, int)>();
        if (quantidade <= 0) return saida;
        var voltas = Math.Max(1, (int)Math.Ceiling(quantidade / (double)capacidadePorVolta));
        var porVolta = (int)Math.Ceiling(quantidade / (double)voltas);
        var i = 0;
        for (var v = 0; v < voltas && i < quantidade; v++)
        {
            var nesta = Math.Min(porVolta, quantidade - i);
            var passo = 360.0 / nesta;
            var deslocamento = v * passo / 2; /* a segunda volta entra entre as estrelas da primeira */
            for (var j = 0; j < nesta; j++, i++)
                saida.Add((i, -90 + deslocamento + j * passo, v));
        }
        return saida;
    }

    /// <summary>Os saudáveis vão para a borda em ordem alfabética, com a primeira letra no topo: o gestor decora o lugar.</summary>
    public static double AnguloDaBorda(int indice, int total) => total <= 0 ? -90 : -90 + indice * 360.0 / total;
}
