using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueOS.Core.Copy;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

namespace BlueOS.Core.Api;

/* ============================================================================
   O CLIENTE DA API .NET (BlueOS.Api): um round-trip por tela.
   ----------------------------------------------------------------------------
   Leituras e escritas de dados vão à API. O que ainda escreve na Meta ou no
   Storage (publicar, rodar o motor, aceite com escrita, token, upload, copy
   pelo modelo) continua indo ao V1 pela mesma sessão, até o executor e a carga
   existirem na API. Sem V1 ligado, essas ações explicam o que falta.
   ============================================================================ */
public sealed class ApiNetApi : IBlueApi, IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    readonly HttpClient _http;
    readonly V1Api? _v1;
    readonly Dictionary<string, Cliente> _cache = new();
    string? _usuario;

    public string BaseUrl { get; }
    public string Origem => $"API .NET · {BaseUrl}{(_usuario is null ? " (sem sessão)" : $" · {_usuario}")}{(_v1 is null ? " · sem V1 para a Meta" : "")}";
    public bool EhDemo => false;
    public ParametrosMotor Parametros { get; } = ParametrosMotor.PadraoDoVault;
    public bool PodeSimularVeiculacao => false;
    public event Action? Mudou;
    void Avisar() => Mudou?.Invoke();

    public ApiNetApi(string baseUrl, V1Api? v1)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _v1 = v1;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (v1 is not null) v1.Mudou += Avisar;
    }

    public void Dispose() { _http.Dispose(); _v1?.Dispose(); }

    V1Api V1 => _v1 ?? throw new InvalidOperationException("esta ação ainda passa pelo V1 (escrita na Meta ou no Storage) e o V1 não está ligado: informe o endereço dele na Fonte de dados.");

    public async Task<string> LoginAsync(string login, string senha)
    {
        var r = await _http.PostAsJsonAsync("api/login", new { login, senha });
        var j = await LerJson(r);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, "login recusado pela API"));
        var token = j.GetProperty("token").GetString()!;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _usuario = j.TryGetProperty("usuario", out var u) && u.TryGetProperty("nome", out var n) ? n.GetString() : login;
        Avisar();
        return _usuario ?? login;
    }

    static async Task<JsonElement> LerJson(HttpResponseMessage r)
    {
        var texto = await r.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(texto)) return default;
        try { return JsonDocument.Parse(texto).RootElement.Clone(); } catch { return default; }
    }

    static string Erro(JsonElement j, string padrao) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : padrao;

    async Task<T> Get<T>(string rota)
    {
        HttpResponseMessage r;
        try { r = await _http.GetAsync(rota); }
        catch (TaskCanceledException) { throw new InvalidOperationException($"a API não respondeu em 60 s ({rota})."); }
        catch (HttpRequestException e) { throw new InvalidOperationException($"não consegui falar com a API em {BaseUrl}: {e.Message}"); }
        var texto = await r.Content.ReadAsStringAsync();
        if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("sem sessão na API: faça o login de novo.");
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(ParseOuVazio(texto), $"a API respondeu {(int)r.StatusCode} em {rota}"));
        return JsonSerializer.Deserialize<T>(texto, Json) ?? throw new InvalidOperationException($"resposta vazia em {rota}");
    }

    static JsonElement ParseOuVazio(string texto) { try { return JsonDocument.Parse(texto).RootElement.Clone(); } catch { return default; } }

    async Task<JsonElement> Envie(HttpMethod metodo, string rota, object corpo)
    {
        var req = new HttpRequestMessage(metodo, rota) { Content = JsonContent.Create(corpo, options: Json) };
        HttpResponseMessage r;
        try { r = await _http.SendAsync(req); }
        catch (HttpRequestException e) { throw new InvalidOperationException($"não consegui falar com a API em {BaseUrl}: {e.Message}"); }
        var j = await LerJson(r);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, $"a API respondeu {(int)r.StatusCode} em {rota}"));
        return j;
    }

    /* ---- leituras: um round-trip cada ------------------------------------------ */

    public async Task<IReadOnlyList<Cliente>> ListarClientesAsync()
    {
        var lista = await Get<List<Cliente>>("api/carteira");
        _cache.Clear();
        foreach (var c in lista) _cache[c.Slug] = c;
        return lista;
    }

    public async Task<Cliente?> ObterClienteAsync(string slug)
    {
        var c = await Get<Cliente>($"api/clientes/{Uri.EscapeDataString(slug)}");
        /* O que é local ao desktop (piloto, lotes de copy gerados aqui) sobrevive à releitura. */
        if (_cache.TryGetValue(slug, out var antigo)) { c.ArtePilotoId = antigo.ArtePilotoId; c.Lotes = antigo.Lotes; c.Conjuntos = antigo.Conjuntos; }
        _cache[slug] = c;
        return c;
    }

    public async Task<Ongoing> LerOngoingAsync(Cliente c)
    {
        var r = await Get<OngoingResposta>($"api/clientes/{Uri.EscapeDataString(c.Slug)}/ongoing");
        c.Conjuntos = r.Conjuntos;
        return r.Ongoing;
    }
    sealed record OngoingResposta(Ongoing Ongoing, List<ConjuntoEmVeiculacao> Conjuntos);

    public async Task<ResultadoDaEstrutura> MontarEstruturaAsync(Cliente c)
    {
        var j = await Get<JsonElement>($"api/clientes/{Uri.EscapeDataString(c.Slug)}/estrutura");
        if (j.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && j.TryGetProperty("estrutura", out var e))
            return new(true, JsonSerializer.Deserialize<EstruturaProposta>(e.GetRawText(), Json), null, null);
        return new(false, null, j.TryGetProperty("falta", out var f) ? f.GetString() : null, j.TryGetProperty("motivo", out var m) ? m.GetString() : "a API não montou a estrutura");
    }

    public async Task<IReadOnlyList<Recomendacao>> ListarRecomendacoesAsync(Cliente c)
    {
        var j = await Get<JsonElement>($"api/clientes/{Uri.EscapeDataString(c.Slug)}/recomendacoes");
        var lista = j.TryGetProperty("recomendacoes", out var r) ? JsonSerializer.Deserialize<List<Recomendacao>>(r.GetRawText(), Json) ?? new() : new();
        c.Recomendacoes = lista;
        return lista;
    }

    public async Task<IReadOnlyList<FormularioDaMeta>> ListarFormulariosAsync(Cliente c) =>
        await Get<List<FormularioDaMeta>>($"api/clientes/{Uri.EscapeDataString(c.Slug)}/formularios");

    public async Task<(bool Ligada, string? Motivo)> ChaveGeralAsync()
    {
        var j = await Get<JsonElement>("api/motor/chave-geral");
        return (j.TryGetProperty("ligada", out var l) && l.ValueKind == JsonValueKind.True, j.TryGetProperty("motivo", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null);
    }

    /* ---- escritas de dados: a API ------------------------------------------------- */

    public Task<Cliente> CriarClienteAsync(string nome, string squad) => V1.CriarClienteAsync(nome, squad);

    public async Task SalvarFichaAsync(Cliente c)
    {
        await Envie(HttpMethod.Patch, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/ficha", new { ficha = c.Ficha, icp = c.Icp, produto = c.Produto, estrategia = c.Estrategia });
        Avisar();
    }

    public async Task SalvarCardTrafegoAsync(Cliente c, string? nichoSlug, decimal? budgetMensal)
    {
        await Envie(HttpMethod.Post, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/trafego", new { nichoSlug, budgetMensal });
        c.NichoSlug = nichoSlug; c.BudgetMensal = budgetMensal; Avisar();
    }

    public async Task SalvarCriterioMqlAsync(Cliente c, ReguaV2? regua, decimal? tetoMql)
    {
        await Envie(HttpMethod.Post, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/mql", new { regua, tetoMql });
        c.Regua = regua; c.TetoMql = tetoMql; Avisar();
    }

    public async Task MudarModoAsync(Cliente c, ModoDeExecucao modo)
    {
        await Envie(HttpMethod.Patch, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/modo", new { modo });
        c.Modo = modo; Avisar();
    }

    public async Task MudarChaveGeralAsync(bool ligada, string? motivo)
    {
        await Envie(HttpMethod.Patch, "api/motor/chave-geral", new { ligada, motivo });
        Avisar();
    }

    public async Task<Recomendacao> DecidirAsync(Cliente c, string recomendacaoId, bool aceita, string? motivo)
    {
        var rec = c.Recomendacoes.FirstOrDefault(r => r.Id == recomendacaoId) ?? throw new KeyNotFoundException("recomendação não encontrada");
        if (aceita && Arvore.Escreve(rec.Acao)) return await V1.DecidirAsync(c, recomendacaoId, true, motivo);
        await Envie(HttpMethod.Post, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/recomendacoes/{recomendacaoId}/decisao", new { aceita, motivo });
        var lista = await ListarRecomendacoesAsync(c);
        Avisar();
        return lista.First(r => r.Id == recomendacaoId);
    }

    /* ---- o que ainda passa pelo V1 (Meta e Storage) ------------------------------- */

    public Task<Asset> SubirAssetAsync(Cliente c, TipoDeAsset tipo, string nome, string mime, byte[] bytes) => V1.SubirAssetAsync(c, tipo, nome, mime, bytes);
    public Task RemoverAssetAsync(Cliente c, string assetId) => V1.RemoverAssetAsync(c, assetId);
    public Task<ResultadoDaConta> ConectarContaAsync(Cliente c, string? adAccountId, string? pageId, string? pageNome) => V1.ConectarContaAsync(c, adAccountId, pageId, pageNome);
    public Task SalvarContaAsync(Cliente c) { Avisar(); return Task.CompletedTask; }
    public Task<EstadoDoToken> ConferirTokenAsync(Cliente c) => V1.ConferirTokenAsync(c);
    public Task<EstadoDoToken> GravarTokenDoSquadAsync(Cliente c, string token) => V1.GravarTokenDoSquadAsync(c, token);
    public Task<LoteDeCopy> GerarCopyAsync(Cliente c, IReadOnlyList<GeradorDemo.Pedido> pedido, string? briefing) => V1.GerarCopyAsync(c, pedido, briefing);
    public Task AjustarPecaAsync(Cliente c, string pecaId, string instrucao) => V1.AjustarPecaAsync(c, pecaId, instrucao);
    public Task AprovarPecaAsync(Cliente c, string pecaId, bool aprovada) { c.Pecas.First(x => x.Id == pecaId).Aprovada = aprovada; Avisar(); return Task.CompletedTask; }
    public Task<IReadOnlyList<Arte>> GerarArtesAsync(Cliente c, IReadOnlyList<string> pecaIds, IReadOnlyList<string> formatos) => V1.GerarArtesAsync(c, pecaIds, formatos);
    public Task AprovarArteAsync(Cliente c, string arteId, bool aprovada) { c.Artes.First(a => a.Id == arteId).Aprovada = aprovada; Avisar(); return Task.CompletedTask; }
    public Task EscolherPilotoAsync(Cliente c, string? arteId) { c.ArtePilotoId = arteId; Avisar(); return Task.CompletedTask; }
    public Task<ResultadoDaPublicacao> PublicarAsync(Cliente c, PedidoDePublicacao pedido) => V1.PublicarAsync(c, pedido);
    public Task<RodadaDoMotor> RodarMotorAsync(Cliente c) => V1.RodarMotorAsync(c);
    public Task SimularVeiculacaoAsync(Cliente c, int dias) => throw new NotSupportedException("a veiculação é real: ative a campanha no Gerenciador e a carga diária lê a Meta.");
}
