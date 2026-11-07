using BlueOS.Core.Design;
using SkiaSharp;

namespace BlueOS.Api.Infra;

/* ============================================================================
   O COMPOSITOR: a cena (PNG do modelo) vira a peça final nos tamanhos da Meta,
   com o texto da copy carimbado em Poppins, como o texto-camada.ts do V1.
   ----------------------------------------------------------------------------
   · a cena é recortada/ajustada para 1080x1350 (feed) ou 1080x1920 (story);
   · o texto entra logo ABAIXO da faixa da logo, centralizado, na metade de
     cima que o prompt manda deixar calma (Diagramacao.Empilhar, com a medida
     real da fonte), e nunca sai da área do texto: o corpo encolhe antes;
   · o plano de contraste (camada-degrade.ts) fica atrás do texto, denso em
     cima e sumindo abaixo do bloco: a leitura não depende da sorte da foto, e
     o objeto da cena, embaixo, fica limpo;
   · headline (800), sub (500) e CTA em botão (700); as palavras de destaque
     saem na cor da marca;
   · a faixa da logo fica sem texto: a logo real é aplicada depois.
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

    /// <summary>A diagramação conferida com a MEDIDA REAL da fonte: as linhas quebram pelo que cabe na caixa, cada caixa encolhe
    /// até 3 linhas, e o bloco, empilhado logo abaixo da faixa da logo, encolhe inteiro se passar da área do texto
    /// (Diagramacao.Empilhar, a regra pura). O que a Diagramacao estimou por contagem de caracteres vira medida.</summary>
    public static Layout Ajustar(Layout layout) => Diagramacao.Empilhar(layout,
        (texto, peso, tamanho, largura) => { using var f = new SKFont(Fonte(peso), (float)tamanho); return QuebrarMedido(texto, largura, f); },
        (texto, peso, tamanho) => { using var f = new SKFont(Fonte(peso), (float)tamanho); return f.MeasureText(texto); });

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

    /// <summary>A peça final: a cena, o plano de contraste e o texto. Devolve PNG, a diagramação como ficou (medida) e o degradê
    /// que foi desenhado (o mesmo que vai para o `texto_layout`).</summary>
    public static byte[] Compor(byte[] cenaPng, Layout layoutEstimado, out Layout layout, out DegradeDoTexto degrade)
    {
        layout = Ajustar(layoutEstimado);
        degrade = Diagramacao.DegradeDoLayout(layout);
        using var bmp = Enquadrar(cenaPng, layout.Largura, layout.Altura);
        using var canvas = new SKCanvas(bmp);
        var w = layout.Largura; var h = layout.Altura;
        DesenharDegrade(canvas, degrade, w, h);

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

    /// <summary>O plano de contraste (camada-degrade.ts): o retângulo, com a transição entre as duas alças; antes da alça de início
    /// fica a primeira ponta, depois da de fim, a segunda (o Clamp do shader faz isso).</summary>
    static void DesenharDegrade(SKCanvas canvas, DegradeDoTexto d, int w, int h)
    {
        var rx = (float)(d.X * w); var ry = (float)(d.Y * h); var rw = (float)(d.W * w); var rh = (float)(d.H * h);
        if (rw <= 0 || rh <= 0) return;
        var cor = Cor(d.Cor);
        using var tinta = new SKPaint { IsAntialias = true };
        tinta.Shader = SKShader.CreateLinearGradient(
            new SKPoint(rx + (float)d.InicioX * rw, ry + (float)d.InicioY * rh), new SKPoint(rx + (float)d.FimX * rw, ry + (float)d.FimY * rh),
            [cor.WithAlpha((byte)Math.Round(d.OpacidadeDe * 255)), cor.WithAlpha((byte)Math.Round(d.OpacidadePara * 255))], [0f, 1f], SKShaderTileMode.Clamp);
        canvas.DrawRect(rx, ry, rw, rh, tinta);
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
