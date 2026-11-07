using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BlueOS.Core.Meta;

namespace BlueOS.Api.Infra;

/* ============================================================================
   O MCP OFICIAL DA META, por HTTP: JSON-RPC em `tools/call` contra
   https://mcp.facebook.com/ads, com `Authorization: Bearer <token do squad>`.
   O servidor é sem sessão (não precisa de `initialize`) e responde em JSON ou
   em SSE (`data:` por linha); os dois chegam aqui como um JsonElement e as
   regras de leitura moram em BlueOS.Core.Meta.McpRegras.
   ----------------------------------------------------------------------------
   Mcp:Squads (appsettings): os squads cujos clientes escrevem por aqui. O
   token continua sendo o da linha do squad em `trafego_integracoes`; nada de
   token na configuração.
   ============================================================================ */
public sealed class MetaMcp(IHttpClientFactory fabrica, IConfiguration cfg, ILogger<MetaMcp> log)
{
    static readonly ConcurrentDictionary<string, (DateTime Em, List<ContaNoMcp> Contas)> ContasCache = new();
    static readonly ConcurrentDictionary<string, (DateTime Em, List<PaginaNoMcp> Paginas)> PaginasCache = new();
    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Url => string.IsNullOrWhiteSpace(cfg["Meta:Mcp:Url"]) ? McpRegras.Url : cfg["Meta:Mcp:Url"]!.Trim();
    public string? Squads => cfg["Meta:Mcp:Squads"];
    public bool SquadPeloMcp(string? squad) => McpRegras.SquadPeloMcp(Squads, squad);

    public object Estado() => new
    {
        url = Url,
        squads = (Squads ?? "").Split(',').Select(s => s.Trim().ToUpperInvariant()).Where(s => s != "").ToList(),
        regra = "os clientes dos squads em Meta:Mcp:Squads escrevem pelo MCP oficial da Meta (campanha, conjunto, anúncio, pausa, verba, ativação), com o token do squad; formulário e cópia de conjunto seguem pela Graph com o mesmo token.",
    };

    HttpClient Http(int segundos)
    {
        var h = fabrica.CreateClient("meta-mcp");
        h.Timeout = TimeSpan.FromSeconds(segundos);
        return h;
    }

    static string Chave(string token) => $"{token.Length}:{token[^Math.Min(10, token.Length)..]}";

    /// <summary>`tools/call`. Nunca lança por recusa do MCP: a recusa volta em `RespostaDoMcp.Erro`. Lança só quando a rede
    /// ou o envelope falham de vez.</summary>
    public async Task<RespostaDoMcp> ChamarAsync(string ferramenta, IReadOnlyDictionary<string, object?> argumentos, string token, int segundos = 90)
    {
        var corpo = new { jsonrpc = "2.0", id = Random.Shared.Next(1, 1_000_000), method = "tools/call", @params = new { name = ferramenta, arguments = argumentos } };
        using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(JsonSerializer.Serialize(corpo, Json), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var r = await Http(segundos).SendAsync(req);
        var texto = await r.Content.ReadAsStringAsync();
        if ((int)r.StatusCode == 401) throw new ErroDaMeta("o MCP da Meta não aceitou o token do squad (401): reconecte o MCP e grave o token novo no squad.", 190);
        if (!r.IsSuccessStatusCode && !texto.TrimStart().StartsWith('{') && !texto.Contains("data:"))
            throw new ErroDaMeta($"o MCP da Meta respondeu {(int)r.StatusCode} sem JSON.", (int)r.StatusCode >= 500 ? 2 : null);
        var resposta = McpRegras.Ler(Envelope(texto));
        if (!resposta.Ok) log.LogWarning("MCP {Ferramenta} recusou: {Erro} ({Categoria})", ferramenta, resposta.Erro, resposta.Categoria);
        return resposta;
    }

    /// <summary>JSON puro ou SSE (McpRegras.Envelope, com o `data:` de várias linhas juntado): vale o último evento JSON-RPC.</summary>
    static JsonElement Envelope(string texto) => McpRegras.Envelope(texto) ?? throw new ErroDaMeta("o MCP da Meta respondeu sem JSON-RPC.");

    /* ---- as leituras que decidem a rota ------------------------------------------- */

    /// <summary>Todas as contas do token (paginado), por 10 minutos. A lista que não chega ao fim (passou do teto de
    /// páginas, ou o cursor se repete) LANÇA: guardá-la cortada faria "a conta não está no token" ser dito em falso.</summary>
    public async Task<List<ContaNoMcp>> ContasAsync(string token, string conversa)
    {
        var k = Chave(token);
        if (ContasCache.TryGetValue(k, out var g) && DateTime.UtcNow - g.Em < TimeSpan.FromMinutes(10)) return g.Contas;
        var todas = new List<ContaNoMcp>(); string? cursor = null; var vistos = new HashSet<string>();
        for (var pagina = 0; ; pagina++)
        {
            if (pagina >= McpRegras.MaxPaginasDeContas)
                throw new ErroDaMeta($"o token do MCP alcança mais de {McpRegras.MaxPaginasDeContas * 100} contas de anúncio e a lista não terminou: não dá para confirmar a conta deste cliente. Nada foi feito.");
            var r = await ChamarAsync("ads_get_ad_accounts", McpRegras.ArgsDasContas(cursor, McpRegras.Comuns(conversa, "report_measure_or_look_up", "listar as contas de anúncio que este token alcança")), token, 60);
            if (!r.Ok) throw new ErroDaMeta(McpRegras.FraseDoMcp(r.Erro));
            todas.AddRange(McpRegras.Contas(r.Dados));
            cursor = McpRegras.ProximoCursor(r.Dados);
            if (cursor is null) break;
            if (!vistos.Add(cursor)) throw new ErroDaMeta("a lista de contas do MCP da Meta repetiu a página: não dá para confirmar a conta deste cliente. Nada foi feito; tente de novo em instantes.");
        }
        ContasCache[k] = (DateTime.UtcNow, todas);
        return todas;
    }

    public async Task<ContaNoMcp?> ContaAsync(string token, string act, string conversa)
    {
        var id = GraphRegras.SemAct(act);
        return (await ContasAsync(token, conversa)).FirstOrDefault(c => c.Id == id);
    }

    /// <summary>As páginas anunciadas pela conta, com o aceite dos termos de lead, por 10 minutos.</summary>
    public async Task<List<PaginaNoMcp>> PaginasAsync(string token, string act, string conversa)
    {
        var k = $"{Chave(token)}:{GraphRegras.SemAct(act)}";
        if (PaginasCache.TryGetValue(k, out var g) && DateTime.UtcNow - g.Em < TimeSpan.FromMinutes(10)) return g.Paginas;
        var r = await ChamarAsync("ads_get_ad_account_pages", McpRegras.ArgsDasPaginas(act, McpRegras.Comuns(conversa, "report_measure_or_look_up", "listar as páginas anunciadas por esta conta")), token, 60);
        if (!r.Ok) throw new ErroDaMeta(McpRegras.FraseDoMcp(r.Erro));
        var paginas = McpRegras.Paginas(r.Dados);
        PaginasCache[k] = (DateTime.UtcNow, paginas);
        return paginas;
    }

    public static void EsquecerCaches() { ContasCache.Clear(); PaginasCache.Clear(); }
}
