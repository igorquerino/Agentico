namespace BlueOS.Core.Design;

/* ============================================================================
   A DIAGRAMAÇÃO DO TEXTO SOBRE A CENA, sem desenhar: as caixas de headline,
   sub e CTA em frações da peça (como `designer_pecas.texto_layout` do V1,
   lib/designer/diagramacao-centralizada.ts, texto-zonas.ts e auto-layout.ts),
   e a quebra de linhas com a medida que quem desenha informa.
   ----------------------------------------------------------------------------
   O padrão da casa ("centralizado", texto-zonas.ts zonaDoLayout): a faixa da
   logo no topo (150px no feed, 230px no story), e o bloco de texto COMEÇA logo
   abaixo dela (faixa + 56px), centralizado, com 76px de margem: headline em
   Poppins 800, sub em 500 e o CTA em botão (700). É a mesma região que o
   prompt da arte manda deixar calma (a metade de cima); o elemento visual fica
   embaixo. O bloco não passa do fim da área do texto: se passar, os corpos
   encolhem juntos (até 60%) antes de qualquer coisa sair da peça.
   Os tamanhos aqui são frações da ALTURA (headline ≈ 0,047; sub ≈ 0,025;
   CTA ≈ 0,028); o `texto_layout` gravado converte para a fração da LARGURA,
   que é o que o Estúdio do V1 lê (texto-camada.ts).
   ============================================================================ */
public sealed record CaixaDeTexto(string Papel, string Texto, double X, double Y, double W, double Tamanho, int Peso, string Alinhamento, IReadOnlyList<string> Linhas, string? Fundo, string Cor, IReadOnlyList<string> Destaques)
{
    /// <summary>O corpo pedido antes de qualquer encolhimento: quem reempilha (o Compositor, com a medida real) parte dele, e o
    /// encolhimento da estimativa não se soma ao da medida.</summary>
    public double? TamanhoPedido { get; init; }
}

public sealed record Layout(int Largura, int Altura, IReadOnlyList<CaixaDeTexto> Caixas, double FaixaCalma)
{
    public CaixaDeTexto? Caixa(string papel) => Caixas.FirstOrDefault(c => c.Papel == papel);
}

/// <summary>O plano de contraste atrás do texto (camada-degrade.ts): o retângulo e as alças em frações, a cor e as opacidades
/// das duas pontas. Antes da alça de início vale a primeira ponta; depois da de fim, a segunda.</summary>
public sealed record DegradeDoTexto(double X, double Y, double W, double H, string Cor, double OpacidadeDe, double OpacidadePara, double InicioX, double InicioY, double FimX, double FimY);

public static class Diagramacao
{
    public const double Respiro = 40.0 / 1080;
    /// <summary>A margem lateral do centralizado (texto-zonas.ts: MARGEM 76 em 1080).</summary>
    public const double Margem = 76.0 / 1080;
    public const double FaixaCalmaTopo = 150.0 / 1080;
    /// <summary>O respiro entre a faixa da logo e o começo do texto (texto-zonas.ts: RESPIRO_APOS_FAIXA).</summary>
    public const int RespiroAposFaixa = 56;
    /// <summary>Até onde o bloco de texto pode descer, em fração da altura: a área calma que o prompt reserva (a metade de cima).</summary>
    public const double FimDaAreaDoTexto = 0.56;
    /// <summary>O menor corpo, em fração do pedido, quando o bloco precisa encolher para caber na área.</summary>
    public const double EscalaMinima = 0.6;
    public const double TamanhoHeadline = 0.047;
    public const double TamanhoSub = 0.0252;
    public const double TamanhoCta = 0.0284;
    public const double Entrelinha = 1.15;
    public const int MaxLinhas = 3;

    /// <summary>Os dois tamanhos da Meta que o V1 usa (lib/designer/formato.ts): feed 1080x1350 (4:5) e story 1080x1920 (9:16).</summary>
    public static (int Largura, int Altura) Dimensoes(string formato) => formato == "story" ? (1080, 1920) : (1080, 1350);
    public static string Normalizar(string? formato) => formato == "story" ? "story" : "feed";

    /// <summary>A faixa da logo, em px (diagramacao-centralizada.ts FAIXA_LOGO_TOPO): 150 no feed, 230 no story.</summary>
    public static int FaixaDoLogo(string formato) => formato == "story" ? PromptDaArte.FaixaLogoStory : PromptDaArte.FaixaLogoFeed;
    /// <summary>Onde o bloco de texto começa, em px: logo abaixo da faixa da logo.</summary>
    public static int TopoDoTexto(string formato) => FaixaDoLogo(formato) + RespiroAposFaixa;
    static string FormatoDe(int largura, int altura) => altura > largura * 1.5 ? "story" : "feed";

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

    /// <summary>A quebra estimada (por contagem de caracteres) para quem não tem a fonte à mão.</summary>
    public static List<string> QuebraEstimada(string texto, int peso, double tamanhoPx, double larguraPx) => Quebrar(texto, Colunas(larguraPx, tamanhoPx), 8);
    /// <summary>A largura estimada de um texto em px (Poppins negrito ≈ 0,62 × corpo por caractere).</summary>
    public static double MedidaEstimada(string texto, int peso, double tamanhoPx) => texto.Length * tamanhoPx * 0.62;

    /// <summary>O layout centralizado da casa. `alinhamento`: "centro" (o padrão da casa) ou "esquerda". As palavras em `destaques`
    /// saem na cor da marca. As linhas e as alturas saem da medida estimada; quem tem a fonte refaz com `Empilhar`.</summary>
    public static Layout Centralizado(string formato, string headline, string sub, string cta, string corTexto, string corDestaque, string corBotao, string corTextoBotao, string alinhamento = "centro", IReadOnlyList<string>? destaques = null)
    {
        var (w, h) = Dimensoes(formato);
        var margem = Margem * w;
        var larguraUtil = w - 2 * margem;
        var larguraSub = larguraUtil * 0.72;
        var centro = alinhamento == "centro";

        var tamH = TamanhoHeadline * h; var tamS = TamanhoSub * h; var tamC = TamanhoCta * h;
        /* no story o corpo é proporcionalmente menor: a peça é mais alta, mas a largura é a mesma */
        if (formato == "story") { tamH *= 0.78; tamS *= 0.78; tamC *= 0.78; }

        var caixas = new List<CaixaDeTexto>
        {
            new("headline", headline, (centro ? (w - larguraUtil) / 2 : margem) / w, 0, larguraUtil / w, tamH / h, 800, alinhamento, [], null, corTexto, destaques ?? []) { TamanhoPedido = tamH / h },
            new("sub", sub, (centro ? (w - larguraSub) / 2 : margem) / w, 0, larguraSub / w, tamS / h, 500, alinhamento, [], null, corTexto, []) { TamanhoPedido = tamS / h },
            new("cta", cta, margem / w, 0, 0.3, tamC / h, 700, "centro", [cta], corBotao, corTextoBotao, []) { TamanhoPedido = tamC / h },
        };
        return Empilhar(new Layout(w, h, caixas, (double)FaixaDoLogo(formato) / h), QuebraEstimada, MedidaEstimada);
    }

    /// <summary>O bloco empilhado de cima para baixo a partir do topo da área do texto (faixa da logo + respiro), com as linhas
    /// que `quebrar` mede (texto, peso, corpo px, largura px) e o botão do CTA na largura que `medir` dá. Cada caixa de texto
    /// encolhe até caber em 3 linhas; se o bloco inteiro passar do fim da área do texto, todos os corpos encolhem juntos até
    /// `EscalaMinima`. O topo nunca entra na faixa da logo, e o botão nunca é mais largo que a coluna.</summary>
    public static Layout Empilhar(Layout layout, Func<string, int, double, double, List<string>> quebrar, Func<string, int, double, double> medir)
    {
        var w = layout.Largura; var h = layout.Altura;
        var topo = (double)TopoDoTexto(FormatoDe(w, h));
        var fim = FimDaAreaDoTexto * h;
        var margem = Margem * w;
        var eixo = layout.Caixa("headline")?.Alinhamento ?? "centro";

        var escala = 1.0;
        List<CaixaDeTexto> caixas;
        double altura;
        while (true)
        {
            caixas = new List<CaixaDeTexto>();
            foreach (var c in layout.Caixas)
            {
                var pedido = (c.TamanhoPedido ?? c.Tamanho) * h * escala;
                if (c.Papel == "cta")
                {
                    var largura = Math.Min(w - 2 * margem, Math.Max(0.3 * w, medir(c.Texto, c.Peso, pedido) + pedido * 2.2));
                    var x = eixo == "centro" ? (w - largura) / 2 : margem;
                    caixas.Add(c with { Tamanho = pedido / h, W = largura / w, X = x / w, Linhas = [c.Texto], TamanhoPedido = c.TamanhoPedido ?? c.Tamanho });
                    continue;
                }
                var tamanho = pedido; var larguraPx = c.W * w;
                List<string> linhas;
                while (true)
                {
                    linhas = quebrar(c.Texto, c.Peso, tamanho, larguraPx);
                    if (linhas.Count <= MaxLinhas || tamanho <= pedido * 0.6) break;
                    tamanho *= 0.92;
                }
                caixas.Add(c with { Tamanho = tamanho / h, Linhas = linhas, TamanhoPedido = c.TamanhoPedido ?? c.Tamanho });
            }
            altura = AlturaDoBloco(caixas, h, escala);
            if (topo + altura <= fim || escala <= EscalaMinima) break;
            escala = Math.Max(EscalaMinima, escala * 0.92);
        }

        var vao = 0.022 * h * escala;
        var y = topo;
        var saida = new List<CaixaDeTexto>();
        foreach (var c in caixas)
        {
            saida.Add(c with { Y = y / h });
            y += AlturaDaCaixa(c, h) + (c.Papel == "headline" ? vao : vao * 1.3);
        }
        return layout with { Caixas = saida, FaixaCalma = (double)FaixaDoLogo(FormatoDe(w, h)) / h };
    }

    static double AlturaDaCaixa(CaixaDeTexto c, int h) => c.Papel == "cta" ? c.Tamanho * h * 1.9 : c.Linhas.Count * c.Tamanho * h * Entrelinha;

    static double AlturaDoBloco(IReadOnlyList<CaixaDeTexto> caixas, int h, double escala)
    {
        var vao = 0.022 * h * escala;
        var total = 0.0;
        for (var i = 0; i < caixas.Count; i++)
        {
            total += AlturaDaCaixa(caixas[i], h);
            if (i < caixas.Count - 1) total += caixas[i].Papel == "headline" ? vao : vao * 1.3;
        }
        return total;
    }

    /// <summary>A faixa vertical que o bloco de texto ocupa, em frações da altura: do topo da primeira caixa ao fim da última.</summary>
    public static (double Y, double H) Bloco(Layout l)
    {
        if (l.Caixas.Count == 0) return (0, 0);
        var topo = l.Caixas.Min(c => c.Y);
        var fundo = l.Caixas.Max(c => c.Y + AlturaDaCaixa(c, l.Altura) / l.Altura);
        return (topo, fundo - topo);
    }

    /// <summary>Luminância relativa (WCAG 2.1) de um hex #rrggbb; -1 quando não é hex.</summary>
    public static double Luminancia(string? hex)
    {
        var m = System.Text.RegularExpressions.Regex.Match(hex ?? "", "^#([0-9a-fA-F]{2})([0-9a-fA-F]{2})([0-9a-fA-F]{2})$");
        if (!m.Success) return -1;
        static double Lin(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        double C(int i) => Lin(Convert.ToInt32(m.Groups[i].Value, 16) / 255.0);
        return 0.2126 * C(1) + 0.7152 * C(2) + 0.0722 * C(3);
    }

    /// <summary>camada-degrade.ts degradeAtrasDoTexto: o degradê de leitura que nasce atrás do texto. Texto claro pede preto,
    /// texto escuro pede branco (o corte 0,179 é onde os dois contrastam igual). Texto na metade de cima: o retângulo vai do topo
    /// até 20% abaixo do texto, denso em cima e sumindo embaixo; na metade de baixo, o contrário.</summary>
    public static DegradeDoTexto DegradeAtrasDoTexto(double blocoY, double blocoH, string? tinta)
    {
        const double transicao = 0.2;
        var lum = Luminancia(tinta);
        var cor = lum >= 0 && lum < 0.179 ? "#ffffff" : "#000000";
        var topoTexto = Math.Clamp(blocoY, 0, 1);
        var baseTexto = Math.Clamp(blocoY + blocoH, topoTexto, 1);
        if (topoTexto + (baseTexto - topoTexto) / 2 >= 0.5)
        {
            var y = Math.Max(0, topoTexto - transicao);
            var hh = 1 - y;
            return new DegradeDoTexto(0, y, 1, hh, cor, 0, 0.8, 0.5, 0, 0.5, hh > 0 ? (baseTexto - y) / hh : 1);
        }
        var h = Math.Min(1, baseTexto + transicao);
        return new DegradeDoTexto(0, 0, 1, h, cor, 0, 0.8, 0.5, 1, 0.5, h > 0 ? topoTexto / h : 0);
    }

    /// <summary>O degradê do layout: atrás do bloco, pela cor da headline.</summary>
    public static DegradeDoTexto DegradeDoLayout(Layout l)
    {
        var (y, hh) = Bloco(l);
        return DegradeAtrasDoTexto(y, hh, l.Caixa("headline")?.Cor);
    }

    /// <summary>As palavras da headline que ganham a cor da marca: as 1-2 primeiras palavras "fortes" (com 4+ letras), como o V1 destaca.</summary>
    public static List<string> DestaquesDaHeadline(string headline)
    {
        var fortes = headline.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim(',', '.', ':', '?', '!', '"')).Where(p => p.Length >= 4 && !char.IsDigit(p[0])).Take(2).ToList();
        return fortes;
    }
}
