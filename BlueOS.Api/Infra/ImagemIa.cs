using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BlueOS.Api.Infra;

/* ============================================================================
   O MODELO DE IMAGEM, por HTTP: a cena da arte sai de um modelo generativo e o
   texto é carimbado DEPOIS, aqui na API (como o V1: lib/designer/openai.ts
   gera, texto-camada.ts carimba).
   ----------------------------------------------------------------------------
   Dois provedores, escolhidos por `Imagem:Provedor` ("openai" | "gemini") ou,
   sem configuração, pela chave que existir (OPENAI_API_KEY primeiro, porque é
   o provedor do V1; depois GOOGLE_AI_API_KEY). Modelos: `Imagem:OpenAiModelo`
   (OPENAI_IMAGE_MODEL do .env do V1, ou gpt-image-2.5-sunburst, o padrão do V1) e `Imagem:GeminiModelo`
   (gemini-3.1-flash-image-preview). O resultado é sempre PNG em bytes.
   ============================================================================ */
public sealed record ImagemGerada(byte[] Png, string Provedor, string Modelo, int Largura, int Altura);

public sealed class ImagemIa(IHttpClientFactory fabrica, IConfiguration cfg, Segredos segredos, ILogger<ImagemIa> log)
{
    public const string ModeloOpenAiPadrao = "gpt-image-2.5-sunburst";
    public const string ModeloGeminiPadrao = "gemini-3.1-flash-image-preview";

    /// <summary>Um valor de configuração ou segredo: vazio é AUSENTE (o exemplo de config traz `"OpenAiModelo": ""`, e `model: ""`
    /// faria toda peça falhar depois do tema, que é pago).</summary>
    public static string? Valor(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    bool TemChave(string provedor) => provedor == "gemini"
        ? segredos.Tem("GOOGLE_AI_API_KEY") || segredos.Tem("GEMINI_API_KEY")
        : segredos.Tem("OPENAI_API_KEY");

    /// <summary>O provedor em uso, ou null quando não há como gerar: o provedor pedido em `Imagem:Provedor` SEM a chave dele
    /// também é null, para o Designer responder 503 antes de abrir lote e pagar o tema.</summary>
    public string? Provedor => Escolher(cfg["Imagem:Provedor"], TemChave("openai"), TemChave("gemini"));

    /// <summary>A regra do provedor, pura: o pedido explícito vale só com a chave dele; sem pedido, a chave que existir (OpenAI
    /// primeiro, o provedor do V1).</summary>
    public static string? Escolher(string? pedido, bool temOpenAi, bool temGemini)
    {
        var p = (Valor(pedido) ?? "").ToLowerInvariant();
        if (p == "openai") return temOpenAi ? p : null;
        if (p == "gemini") return temGemini ? p : null;
        return temOpenAi ? "openai" : temGemini ? "gemini" : null;
    }

    public string Modelo => Provedor == "gemini"
        ? (Valor(cfg["Imagem:GeminiModelo"]) ?? Valor(segredos.Ler("NANOBANANA_MODEL")) ?? ModeloGeminiPadrao)
        : (Valor(cfg["Imagem:OpenAiModelo"]) ?? Valor(segredos.Ler("OPENAI_IMAGE_MODEL")) ?? ModeloOpenAiPadrao);

    public object Estado() => new
    {
        provedor = Provedor ?? "nenhum", modelo = Provedor is null ? null : Modelo,
        openai = segredos.Origem("OPENAI_API_KEY"), gemini = segredos.Tem("GOOGLE_AI_API_KEY") ? segredos.Origem("GOOGLE_AI_API_KEY") : segredos.Origem("GEMINI_API_KEY"),
    };

    HttpClient Http(int segundos)
    {
        var h = fabrica.CreateClient("imagem-ia");
        h.Timeout = TimeSpan.FromSeconds(segundos);
        return h;
    }

    /// <summary>Gera a cena. `formato` é "feed" (1080x1350) ou "story" (1080x1920): o provedor dá o mais perto e o compositor ajusta.</summary>
    public async Task<ImagemGerada> GerarAsync(string prompt, string formato, CancellationToken ct = default)
    {
        var provedor = Provedor ?? throw new InvalidOperationException("sem modelo de imagem: informe OPENAI_API_KEY ou GOOGLE_AI_API_KEY (em Segredos, no ambiente ou num arquivo .env listado em Segredos:Arquivos).");
        return provedor == "gemini" ? await GeminiAsync(prompt, formato, ct) : await OpenAiAsync(prompt, formato, ct);
    }

    async Task<ImagemGerada> OpenAiAsync(string prompt, string formato, CancellationToken ct)
    {
        var chave = segredos.Ler("OPENAI_API_KEY") ?? throw new InvalidOperationException("sem OPENAI_API_KEY.");
        var modelo = Modelo;
        /* os tamanhos da Meta (1080x1350 / 1080x1920) a OpenAI não aceita: saem os do V1 (formato.ts) e o compositor ajusta */
        var size = Valor(cfg["Imagem:OpenAiTamanho"]) ?? (formato == "story" ? "1024x1792" : "1024x1280");
        var corpo = new Dictionary<string, object?> { ["model"] = modelo, ["prompt"] = prompt, ["size"] = size, ["n"] = 1 };
        if (!modelo.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase)) corpo["quality"] = Valor(cfg["Imagem:OpenAiQualidade"]) ?? "high";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations")
        { Content = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chave);
        using var r = await Http(300).SendAsync(req, ct);
        var texto = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"o modelo de imagem (OpenAI {modelo}) recusou ({(int)r.StatusCode}): {Erro(texto)}");
        var j = JsonDocument.Parse(texto).RootElement;
        var item = j.GetProperty("data")[0];
        byte[] png;
        if (item.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String) png = Convert.FromBase64String(b64.GetString()!);
        else if (item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) png = await Http(120).GetByteArrayAsync(u.GetString()!, ct);
        else throw new InvalidOperationException("o modelo de imagem não devolveu a imagem.");
        var partes = size.Split('x');
        return new ImagemGerada(png, "openai", modelo, int.TryParse(partes[0], out var w) ? w : 0, partes.Length > 1 && int.TryParse(partes[1], out var h) ? h : 0);
    }

    async Task<ImagemGerada> GeminiAsync(string prompt, string formato, CancellationToken ct)
    {
        var chave = Valor(segredos.Ler("GOOGLE_AI_API_KEY")) ?? Valor(segredos.Ler("GEMINI_API_KEY")) ?? throw new InvalidOperationException("sem GOOGLE_AI_API_KEY.");
        var modelo = Modelo;
        var corpo = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { responseModalities = new[] { "IMAGE" }, imageConfig = new { aspectRatio = formato == "story" ? "9:16" : "4:5" } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{modelo}:generateContent")
        { Content = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-goog-api-key", chave);
        using var r = await Http(300).SendAsync(req, ct);
        var texto = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"o modelo de imagem (Gemini {modelo}) recusou ({(int)r.StatusCode}): {Erro(texto)}");
        var j = JsonDocument.Parse(texto).RootElement;
        if (j.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
            foreach (var c in cands.EnumerateArray())
                if (c.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                    foreach (var p in parts.EnumerateArray())
                        if (p.TryGetProperty("inlineData", out var inl) && inl.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
                        {
                            var bytes = Convert.FromBase64String(d.GetString()!);
                            return new ImagemGerada(bytes, "gemini", modelo, 0, 0);
                        }
        var motivo = j.TryGetProperty("candidates", out var c2) && c2.ValueKind == JsonValueKind.Array && c2.GetArrayLength() > 0 && c2[0].TryGetProperty("finishReason", out var fr) ? fr.GetString() : null;
        throw new InvalidOperationException($"o modelo de imagem (Gemini) não devolveu imagem{(motivo is not null ? $" ({motivo})" : "")}.");
    }

    static string Erro(string texto)
    {
        try
        {
            var j = JsonDocument.Parse(texto).RootElement;
            if (j.TryGetProperty("error", out var e)) return (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : e.GetRawText()) ?? texto;
        }
        catch { }
        return texto.Length > 300 ? texto[..300] : texto;
    }
}
