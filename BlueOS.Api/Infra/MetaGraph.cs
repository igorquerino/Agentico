using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BlueOS.Core.Meta;
using BlueOS.Core.Publicar;

namespace BlueOS.Api.Infra;

/* ============================================================================
   A GRAPH API DA META, por HTTP: a única porta por onde a API lê e escreve na
   Meta. As regras (classe do erro, BUC, corpo das ações, a trava de escrita)
   moram em BlueOS.Core.Meta.GraphRegras; aqui é só rede.
   ----------------------------------------------------------------------------
   A TRAVA DE ESCRITA (AD-MT-20): esta API só fala com a homologação, e a conta
   de anúncio é a MESMA em produção e na homologação. Então toda escrita exige
   `Meta:EscritaForaDaProd = true` E a conta em `Meta:ContasTeste`; sem isso o
   caminho é só ensaio (`validate_only`), com `simulado: true` na resposta.
   O token nunca aparece em log nem em erro: `Sanear` tira o access_token de
   qualquer texto que suba.
   ============================================================================ */
public sealed record RespostaGraph(JsonElement Json, int AguardarMs);

public sealed class MetaGraph(IHttpClientFactory fabrica, IConfiguration cfg)
{
    public const string Graph = GraphRegras.Graph;

    HttpClient Http(int segundos)
    {
        var h = fabrica.CreateClient("meta-graph");
        h.Timeout = TimeSpan.FromSeconds(segundos);
        return h;
    }

    /* ---- a trava de escrita ----------------------------------------------------- */

    public bool VariavelDeEscritaLigada => (cfg["Meta:EscritaForaDaProd"] ?? "").Trim().ToLowerInvariant() is "1" or "true";
    public string? ContasTeste => cfg["Meta:ContasTeste"];
    public bool EscritaLiberada(string act) => GraphRegras.EscritaLiberada(VariavelDeEscritaLigada, ContasTeste, act);
    public (string AppId, string AppSecret)? App =>
        string.IsNullOrWhiteSpace(cfg["Meta:AppId"]) || string.IsNullOrWhiteSpace(cfg["Meta:AppSecret"]) ? null : (cfg["Meta:AppId"]!.Trim(), cfg["Meta:AppSecret"]!.Trim());

    public object EstadoDaTrava() => new
    {
        escrita_fora_da_prod = VariavelDeEscritaLigada,
        contas_teste = (ContasTeste ?? "").Split(',').Select(s => s.Trim()).Where(s => s != "").Select(GraphRegras.SemAct).ToList(),
        app_configurado = App is not null,
        regra = "a escrita na Meta só acontece com Meta:EscritaForaDaProd = true E a conta em Meta:ContasTeste; fora disso é só ensaio (validate_only).",
    };

    /* ---- as chamadas ------------------------------------------------------------- */

    public async Task<JsonElement> GetAsync(string caminho, string token, int segundos = 45)
    {
        var sep = caminho.Contains('?') ? "&" : "?";
        using var r = await Http(segundos).GetAsync($"{Graph}/{caminho}{sep}access_token={Uri.EscapeDataString(token)}");
        return Ler(await r.Content.ReadAsStringAsync(), (int)r.StatusCode);
    }

    /// <summary>POST na Graph (form-urlencoded, como o V1). Com `validarSo`, a Meta processa e devolve o erro SEM alterar
    /// nada. Lança ErroDaMeta quando ela recusa.</summary>
    public async Task<RespostaGraph> PostAsync(string caminho, IReadOnlyDictionary<string, string> corpo, string token, bool validarSo = false, int segundos = 45)
    {
        var campos = new List<KeyValuePair<string, string>>(corpo);
        if (validarSo) campos.Add(new("execution_options", "[\"validate_only\"]"));
        campos.Add(new("access_token", token));
        /* FormUrlEncodedContent passa por Uri.EscapeDataString; a imagem em base64 é grande demais para ele. */
        var body = string.Join("&", campos.Select(kv => $"{WebUtility.UrlEncode(kv.Key)}={WebUtility.UrlEncode(kv.Value)}"));
        using var conteudo = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var r = await Http(segundos).PostAsync($"{Graph}/{caminho}", conteudo);
        var buc = r.Headers.TryGetValues("x-business-use-case-usage", out var v) ? v.FirstOrDefault() : null;
        var j = Ler(await r.Content.ReadAsStringAsync(), (int)r.StatusCode);
        return new RespostaGraph(j, GraphRegras.EsperaPeloBUCMs(buc));
    }

    /// <summary>O post NÃO PUBLICADO na página, com a arte e a legenda (multipart, `/{page}/photos`), com o token DA
    /// PÁGINA. Devolve o id do POST, que é o `object_story_id` do criativo.</summary>
    public async Task<string> PostarFotoNaPaginaAsync(string pageId, string tokenDaPagina, string legenda, byte[] bytes)
    {
        using var fd = new MultipartFormDataContent();
        fd.Add(new StringContent("false"), "published");
        fd.Add(new StringContent(legenda), "caption");
        var foto = new ByteArrayContent(bytes);
        foto.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        fd.Add(foto, "source", "arte.png");
        fd.Add(new StringContent(tokenDaPagina), "access_token");
        using var r = await Http(90).PostAsync($"{Graph}/{pageId}/photos", fd);
        var j = Ler(await r.Content.ReadAsStringAsync(), (int)r.StatusCode);
        var id = StartRegras.IdDoPostDaFoto(pageId, S(j, "id"), S(j, "post_id"));
        if (id == "") throw new ErroDaMeta("a Meta aceitou a foto mas não devolveu o id do post.");
        return id;
    }

    /// <summary>Os bytes da arte pela URL (https) do Storage. Sem URL não há como ler daqui: a API não tem a chave do bucket.</summary>
    public async Task<byte[]> LerImagemAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("a arte não tem URL pública no Storage: gere a arte de novo pelo Designer.");
        using var r = await Http(45).GetAsync(url);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"não consegui ler a arte ({(int)r.StatusCode})");
        return await r.Content.ReadAsByteArrayAsync();
    }

    /* ---- a resposta ---------------------------------------------------------------- */

    /// <summary>A regra mora em GraphRegras.LerResposta: o `error` vira ErroDaMeta, e status fora de 2xx sem ele também.</summary>
    static JsonElement Ler(string texto, int status) => GraphRegras.LerResposta(texto, status);

    public static string? S(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.GetRawText() : null;

    /// <summary>Nenhum token sobe em mensagem: o que parecer `access_token=...` vira ***.</summary>
    public static string Sanear(string texto) => System.Text.RegularExpressions.Regex.Replace(texto, @"access_token=[^&\s""]+", "access_token=***");
}
