using BlueOS.Core.Design;
using SkiaSharp;

namespace BlueOS.Api.Infra;

/* ============================================================================
   O COMPOSITOR: a cena (PNG do modelo) vira a peça final nos tamanhos da Meta,
   com o texto da copy carimbado em Poppins, como o texto-camada.ts do V1.
   ----------------------------------------------------------------------------
   · a cena é recortada/ajustada para 1080x1350 (feed) ou 1080x1920 (story);
   · um gradiente escuro sobe pela base, onde o texto entra, para a leitura
     não depender da sorte da foto (o V1 chama de "plano de contraste");
   · headline (800), sub (500) e CTA em botão (700), nas caixas que a
     Diagramacao calculou; as palavras de destaque saem na cor da marca;
   · a faixa calma do topo fica como veio: a logo real é aplicada depois.
   As fontes vêm de BlueOS.Api/Fontes (Poppins, as do V1).
   ============================================================================ */
public sealed class Compositor
{
    static readonly Dictionary<int, SKTypeface> Fontes = new();
    static readonly object Trava = new();

    static SKTypeface Fonte(int peso)
    {
        lock (Trava)
        {
            if (Fontes.TryGetValue(peso, out var f)) return f;
            var pasta = Path.Combine(AppContext.BaseDirectory, "Fontes");
            var candidatos = new[] { peso, 700, 600, 500, 400, 800 };
            foreach (var p in candidatos)
            {
                var caminho = Path.Combine(pasta, $"poppins-{p}.ttf");
                if (File.Exists(caminho)) { var tf = SKTypeface.FromFile(caminho); if (tf is not null) { Fontes[peso] = tf; return tf; } }
            }
            var padrao = SKTypeface.FromFamilyName("Segoe UI", peso >= 700 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;
            Fontes[peso] = padrao;
            return padrao;
        }
    }

    public static bool TemPoppins => File.Exists(Path.Combine(AppContext.BaseDirectory, "Fontes", "poppins-800.ttf"));

    /// <summary>A cena no tamanho da peça (cover: preenche e recorta pelo centro, mantendo o topo).</summary>
    public static SKBitmap Enquadrar(byte[] cenaPng, int largura, int altura)
    {
        using var origem = SKBitmap.Decode(cenaPng) ?? throw new InvalidOperationException("a cena não é uma imagem legível.");
        var escala = Math.Max((double)largura / origem.Width, (double)altura / origem.Height);
        var w = (int)Math.Round(origem.Width * escala); var h = (int)Math.Round(origem.Height * escala);
        var saida = new SKBitmap(largura, altura, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(saida);
        using var redim = origem.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        /* recorta pelo centro na horizontal e pelo topo na vertical: a faixa calma do topo tem de sobreviver */
        var x = (largura - w) / 2; var y = 0;
        canvas.DrawBitmap(redim, x, y);
        return saida;
    }

    /// <summary>A diagramação conferida com a MEDIDA REAL da fonte: as linhas quebram pelo que cabe na caixa e, se passarem do
    /// teto de linhas (3), o corpo desce até caber. O que a Diagramacao estimou por contagem de caracteres vira medida.</summary>
    public static Layout Ajustar(Layout layout)
    {
        var w = layout.Largura; var h = layout.Altura;
        var respiro = Diagramacao.Respiro * 1080;
        var caixas = new List<CaixaDeTexto>();
        foreach (var c in layout.Caixas)
        {
            if (c.Papel == "cta")
            {
                var tam = (float)(c.Tamanho * h);
                using var f = new SKFont(Fonte(c.Peso), tam);
                var largura = Math.Max(0.3 * w, f.MeasureText(c.Texto) + tam * 2.2);
                /* o botão segue o EIXO do texto (a headline): à esquerda com ela, ao centro com ela */
                var eixo = layout.Caixa("headline")?.Alinhamento ?? "esquerda";
                var x = eixo == "centro" ? (w - largura) / 2 : respiro;
                caixas.Add(c with { W = largura / w, X = x / w });
                continue;
            }
            var maxLinhas = 3; var tamanho = c.Tamanho * h; var larguraPx = c.W * w;
            List<string> linhas;
            while (true)
            {
                using var f = new SKFont(Fonte(c.Peso), (float)tamanho);
                linhas = QuebrarMedido(c.Texto, larguraPx, f);
                if (linhas.Count <= maxLinhas || tamanho <= c.Tamanho * h * 0.6) break;
                tamanho *= 0.92;
            }
            caixas.Add(c with { Tamanho = tamanho / h, Linhas = linhas });
        }
        /* as alturas de novo, de baixo para cima, como a Diagramacao fez por estimativa */
        var head = caixas.First(x => x.Papel == "headline"); var sub = caixas.First(x => x.Papel == "sub"); var cta = caixas.First(x => x.Papel == "cta");
        var altH = head.Linhas.Count * head.Tamanho * h * Diagramacao.Entrelinha;
        var altS = sub.Linhas.Count * sub.Tamanho * h * Diagramacao.Entrelinha;
        var altC = cta.Tamanho * h * 1.9; var vao = 0.022 * h;
        var yIni = h - respiro * 1.2 - (altH + vao + altS + vao * 1.3 + altC);
        yIni = Math.Max(yIni, Diagramacao.FaixaCalmaTopo * 1080 + respiro);
        var saida = new List<CaixaDeTexto>
        {
            head with { Y = yIni / h }, sub with { Y = (yIni + altH + vao) / h }, cta with { Y = (yIni + altH + vao + altS + vao * 1.3) / h },
        };
        return layout with { Caixas = saida };
    }

    static List<string> QuebrarMedido(string texto, double larguraPx, SKFont fonte)
    {
        var linhas = new List<string>(); var atual = "";
        foreach (var palavra in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var tentativa = atual == "" ? palavra : atual + " " + palavra;
            if (atual != "" && fonte.MeasureText(tentativa) > larguraPx) { linhas.Add(atual); atual = palavra; }
            else atual = tentativa;
        }
        if (atual != "") linhas.Add(atual);
        return linhas.Count == 0 ? [""] : linhas;
    }

    /// <summary>A peça final: a cena, o plano de contraste e o texto. Devolve PNG e a diagramação como ficou (medida).</summary>
    public static byte[] Compor(byte[] cenaPng, Layout layoutEstimado, out Layout layout)
    {
        layout = Ajustar(layoutEstimado);
        using var bmp = Enquadrar(cenaPng, layout.Largura, layout.Altura);
        using var canvas = new SKCanvas(bmp);
        var w = layout.Largura; var h = layout.Altura;

        /* o plano de contraste: gradiente da base até um pouco acima da headline */
        var topoDoTexto = layout.Caixas.Min(c => c.Y) * h;
        var inicio = Math.Max(0, topoDoTexto - 0.14 * h);
        using (var tinta = new SKPaint())
        {
            tinta.Shader = SKShader.CreateLinearGradient(new SKPoint(0, (float)inicio), new SKPoint(0, h),
                [SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(150), SKColors.Black.WithAlpha(200)], [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawRect(0, (float)inicio, w, (float)(h - inicio), tinta);
        }

        foreach (var caixa in layout.Caixas)
        {
            var tamanho = (float)(caixa.Tamanho * h);
            var tf = Fonte(caixa.Peso);
            using var fonte = new SKFont(tf, tamanho) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
            var x0 = (float)(caixa.X * w); var y0 = (float)(caixa.Y * h); var largura = (float)(caixa.W * w);
            var entrelinha = tamanho * (float)Diagramacao.Entrelinha;

            if (caixa.Papel == "cta")
            {
                var altura = tamanho * 1.9f;
                using var fundo = new SKPaint { Color = Cor(caixa.Fundo ?? "#ffffff"), IsAntialias = true };
                canvas.DrawRoundRect(new SKRect(x0, y0, x0 + largura, y0 + altura), altura / 2, altura / 2, fundo);
                using var texto = new SKPaint { Color = Cor(caixa.Cor), IsAntialias = true };
                var medida = fonte.MeasureText(caixa.Texto);
                var metricas = fonte.Metrics;
                var yTexto = y0 + altura / 2 - (metricas.Ascent + metricas.Descent) / 2;
                canvas.DrawText(caixa.Texto, x0 + (largura - medida) / 2, yTexto, SKTextAlign.Left, fonte, texto);
                continue;
            }

            var y = y0 - fonte.Metrics.Ascent;
            foreach (var linha in caixa.Linhas)
            {
                var medidaLinha = fonte.MeasureText(linha);
                var x = caixa.Alinhamento == "centro" ? x0 + (largura - medidaLinha) / 2 : x0;
                DesenharComDestaques(canvas, fonte, linha, x, y, Cor(caixa.Cor), caixa.Destaques, caixa.Papel == "headline" ? layout.Caixa("cta")?.Fundo : null);
                y += entrelinha;
            }
        }

        using var imagem = SKImage.FromBitmap(bmp);
        using var dados = imagem.Encode(SKEncodedImageFormat.Png, 100);
        return dados.ToArray();
    }

    /// <summary>Desenha a linha palavra a palavra: as palavras em `destaques` saem na cor da marca (a do botão), com uma sombra leve atrás de tudo.</summary>
    static void DesenharComDestaques(SKCanvas canvas, SKFont fonte, string linha, float x, float y, SKColor cor, IReadOnlyList<string> destaques, string? corDestaque)
    {
        using var sombra = new SKPaint { Color = SKColors.Black.WithAlpha(110), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) };
        using var tinta = new SKPaint { Color = cor, IsAntialias = true };
        using var tintaDestaque = new SKPaint { Color = corDestaque is null ? cor : Cor(corDestaque), IsAntialias = true };
        var espaco = fonte.MeasureText(" ");
        var cursor = x;
        foreach (var palavra in linha.Split(' '))
        {
            var limpa = palavra.Trim(',', '.', ':', '?', '!', '"');
            var destacada = corDestaque is not null && destaques.Any(d => string.Equals(d, limpa, StringComparison.OrdinalIgnoreCase));
            canvas.DrawText(palavra, cursor + 2, y + 3, SKTextAlign.Left, fonte, sombra);
            canvas.DrawText(palavra, cursor, y, SKTextAlign.Left, fonte, destacada ? tintaDestaque : tinta);
            cursor += fonte.MeasureText(palavra) + espaco;
        }
    }

    public static SKColor Cor(string hex) => SKColor.TryParse(hex, out var c) ? c : SKColors.White;

    /// <summary>A paleta de uma logo: as cores mais frequentes (fora de branco/preto/cinza), em hex, para a marca travar a arte.</summary>
    public static List<string> PaletaDaImagem(byte[] png, int maximo = 5)
    {
        using var bmp = SKBitmap.Decode(png);
        if (bmp is null) return [];
        var contagem = new Dictionary<(int, int, int), int>();
        var passo = Math.Max(1, Math.Min(bmp.Width, bmp.Height) / 96);
        for (var y = 0; y < bmp.Height; y += passo)
            for (var x = 0; x < bmp.Width; x += passo)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Alpha < 128) continue;
                var max = Math.Max(p.Red, Math.Max(p.Green, p.Blue)); var min = Math.Min(p.Red, Math.Min(p.Green, p.Blue));
                if (max - min < 24) continue; /* cinza, branco, preto: não são cor de marca */
                var chave = (p.Red / 24, p.Green / 24, p.Blue / 24);
                contagem[chave] = contagem.GetValueOrDefault(chave) + 1;
            }
        return contagem.OrderByDescending(kv => kv.Value).Take(maximo)
            .Select(kv => $"#{Math.Min(255, kv.Key.Item1 * 24 + 12):x2}{Math.Min(255, kv.Key.Item2 * 24 + 12):x2}{Math.Min(255, kv.Key.Item3 * 24 + 12):x2}").ToList();
    }
}
