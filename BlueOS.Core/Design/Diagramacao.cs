namespace BlueOS.Core.Design;

/* ============================================================================
   A DIAGRAMAÇÃO DO TEXTO SOBRE A CENA, sem desenhar: as caixas de headline,
   sub e CTA em frações da peça (como `designer_pecas.texto_layout` do V1,
   lib/designer/diagramacao-centralizada.ts e auto-layout.ts), e a quebra de
   linhas com a medida que quem desenha informa.
   ----------------------------------------------------------------------------
   O padrão da casa ("centralizado"): faixa calma no topo (150px em 1080, onde
   a logo real entra depois), a cena no meio, e o bloco de texto na base, com
   headline em Poppins 800, sub em 500 e o CTA em botão (700), tudo no mesmo
   eixo (esquerda ou centro). Os tamanhos são frações da ALTURA, como no V1
   (headline ≈ 0,047; sub ≈ 0,025; CTA ≈ 0,028), e o texto nunca encosta na
   borda (respiro de 40px em 1080).
   ============================================================================ */
public sealed record CaixaDeTexto(string Papel, string Texto, double X, double Y, double W, double Tamanho, int Peso, string Alinhamento, IReadOnlyList<string> Linhas, string? Fundo, string Cor, IReadOnlyList<string> Destaques);

public sealed record Layout(int Largura, int Altura, IReadOnlyList<CaixaDeTexto> Caixas, double FaixaCalma)
{
    public CaixaDeTexto? Caixa(string papel) => Caixas.FirstOrDefault(c => c.Papel == papel);
}

public static class Diagramacao
{
    public const double Respiro = 40.0 / 1080;
    public const double FaixaCalmaTopo = 150.0 / 1080;
    public const double TamanhoHeadline = 0.047;
    public const double TamanhoSub = 0.0252;
    public const double TamanhoCta = 0.0284;
    public const double Entrelinha = 1.15;

    /// <summary>Os dois tamanhos da Meta que o V1 usa (lib/designer/formato.ts): feed 1080x1350 (4:5) e story 1080x1920 (9:16).</summary>
    public static (int Largura, int Altura) Dimensoes(string formato) => formato == "story" ? (1080, 1920) : (1080, 1350);
    public static string Normalizar(string? formato) => formato == "story" ? "story" : "feed";

    /// <summary>Quebra em linhas pela largura em caracteres (medida aproximada: Poppins ≈ 0,56 × tamanho por caractere), até `maxLinhas`.
    /// A última linha recebe o que sobrar: o texto da copy nunca é cortado.</summary>
    public static List<string> Quebrar(string texto, int colunas, int maxLinhas)
    {
        var linhas = new List<string>();
        var atual = new System.Text.StringBuilder();
        foreach (var palavra in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (atual.Length > 0 && atual.Length + 1 + palavra.Length > colunas && linhas.Count < maxLinhas - 1)
            {
                linhas.Add(atual.ToString()); atual.Clear();
            }
            if (atual.Length > 0) atual.Append(' ');
            atual.Append(palavra);
        }
        if (atual.Length > 0) linhas.Add(atual.ToString());
        return linhas.Count == 0 ? [""] : linhas;
    }

    public static int Colunas(double larguraPx, double tamanhoPx, double fatorLargura = 0.56) => Math.Max(8, (int)Math.Floor(larguraPx / (tamanhoPx * fatorLargura)));

    /// <summary>O layout centralizado da casa. `alinhamento`: "esquerda" (padrão) ou "centro". As palavras em `destaques` saem na cor da marca.</summary>
    public static Layout Centralizado(string formato, string headline, string sub, string cta, string corTexto, string corDestaque, string corBotao, string corTextoBotao, string alinhamento = "esquerda", IReadOnlyList<string>? destaques = null)
    {
        var (w, h) = Dimensoes(formato);
        var respiro = Respiro * 1080;
        var larguraUtil = w - 2 * respiro;

        var tamH = TamanhoHeadline * h; var tamS = TamanhoSub * h; var tamC = TamanhoCta * h;
        /* no story a headline cresce um pouco, porque a peça é mais alta e vista no celular inteiro */
        if (formato == "story") { tamH *= 0.78; tamS *= 0.78; tamC *= 0.78; }
        var linhasH = Quebrar(headline, Colunas(larguraUtil, tamH), 3);
        var linhasS = Quebrar(sub, Colunas(larguraUtil * 0.72, tamS), 3);

        var altH = linhasH.Count * tamH * Entrelinha;
        var altS = linhasS.Count * tamS * Entrelinha;
        var altC = tamC * 1.9;
        var vao = 0.022 * h;
        var total = altH + vao + altS + vao * 1.3 + altC;
        var yFim = h - respiro * 1.2;
        var yIni = yFim - total;
        var x = respiro;
        var larguraCta = Math.Max(0.3 * w, cta.Length * tamC * 0.62 + tamC * 2.2);
        var xCta = alinhamento == "centro" ? (w - larguraCta) / 2 : x;

        var caixas = new List<CaixaDeTexto>
        {
            new("headline", headline, x / w, yIni / h, larguraUtil / w, tamH / h, 800, alinhamento, linhasH, null, corTexto, destaques ?? []),
            new("sub", sub, x / w, (yIni + altH + vao) / h, larguraUtil * 0.72 / w, tamS / h, 500, alinhamento, linhasS, null, corTexto, []),
            new("cta", cta, xCta / w, (yIni + altH + vao + altS + vao * 1.3) / h, larguraCta / w, tamC / h, 700, "centro", [cta], corBotao, corTextoBotao, []),
        };
        return new Layout(w, h, caixas, FaixaCalmaTopo * h / h);
    }

    /// <summary>As palavras da headline que ganham a cor da marca: as 1-2 primeiras palavras "fortes" (com 4+ letras), como o V1 destaca.</summary>
    public static List<string> DestaquesDaHeadline(string headline)
    {
        var fortes = headline.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim(',', '.', ':', '?', '!', '"')).Where(p => p.Length >= 4 && !char.IsDigit(p[0])).Take(2).ToList();
        return fortes;
    }
}
