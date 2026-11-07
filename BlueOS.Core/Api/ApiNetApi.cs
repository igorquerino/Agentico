using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueOS.Core.Copy;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Publicar;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BlueOS.Tests")]

namespace BlueOS.Core.Api;

/* ============================================================================
   O CLIENTE DA API .NET (BlueOS.Api): um round-trip por tela.
   ----------------------------------------------------------------------------
   Leituras e escritas de dados vão à API; desde 30/10/2026 também a ESCRITA
   NA META (o Publicar do Start e o aceite de PAUSAR/DUPLICAR, pelo executor da
   API) e, desde 31/10/2026, a GERAÇÃO (copy pelo Agente de Copy, artes pelo
   Designer), o setup (assets, conta, token do squad, criar cliente) e rodar o
   motor. Nada mais passa pelo V1: o V1Api recebido no construtor fica só para
   compatibilidade da Fonte de dados.
   ============================================================================ */
public sealed class ApiNetApi : IBlueApi, IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    readonly HttpClient _http;
    /// <summary>O mesmo endereço com 5 minutos de espera: publicar é mais de uma dúzia de idas à Meta.</summary>
    readonly HttpClient _lento;
    readonly V1Api? _v1;
    readonly Dictionary<string, Cliente> _cache = new();
    string? _usuario;

    public string BaseUrl { get; }
    public string Origem => $"API .NET · {BaseUrl}{(_usuario is null ? " (sem sessão)" : $" · {_usuario}")}";
    public bool EhDemo => false;
    public ParametrosMotor Parametros { get; } = ParametrosMotor.PadraoDoVault;
    public bool PodeSimularVeiculacao => false;
    public event Action? Mudou;
    void Avisar() => Mudou?.Invoke();

    public ApiNetApi(string baseUrl, V1Api? v1) : this(baseUrl, v1, null) { }

    /// <summary>Com `handler`, as duas conexões passam por ele (os testes respondem no lugar da API, sem rede).</summary>
    internal ApiNetApi(string baseUrl, V1Api? v1, HttpMessageHandler? handler)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _v1 = v1;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(BaseUrl + "/"); _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _lento = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _lento.BaseAddress = new Uri(BaseUrl + "/"); _lento.Timeout = TimeSpan.FromMinutes(5);
        if (v1 is not null) v1.Mudou += Avisar;
    }

    public void Dispose() { _http.Dispose(); _lento.Dispose(); _v1?.Dispose(); }


    public async Task<string> LoginAsync(string login, string senha)
    {
        var r = await _http.PostAsJsonAsync("api/login", new { login, senha });
        var j = await LerJson(r);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, "login recusado pela API"));
        var token = j.GetProperty("token").GetString()!;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _lento.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
        /* nenhum chamador passa token: o cancelamento aqui é o timeout de 60 s, e a escrita pode ter sido gravada */
        catch (OperationCanceledException) { throw new InvalidOperationException($"a API não respondeu em 60 s ({rota}): a mudança pode ter sido gravada ou não. Releia o cliente antes de repetir."); }
        var j = await LerJson(r);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, $"a API respondeu {(int)r.StatusCode} em {rota}"));
        return j;
    }

    /// <summary>A escrita que mexe na Meta não teve resposta (timeout de 5 min): a API pode já ter escrito.</summary>
    static InvalidOperationException EstadoIncerto(string oQue, string conferir) =>
        new($"a API não respondeu em 5 minutos ao {oQue}: o estado na Meta é INCERTO (pode ter sido feito, inteiro ou pela metade). Confira {conferir} no Gerenciador antes de repetir.");

    /* ---- leituras: um round-trip cada ------------------------------------------ */

    public async Task<IReadOnlyList<Cliente>> ListarClientesAsync()
    {
        var lista = await Get<List<Cliente>>("api/carteira");
        /* Mescla, não limpa: o MainLayout relê a carteira a cada 60 s e a cada escrita, e limpar o cache apagava o
           piloto que o gestor escolheu (o Condutor então escolhia outra arte sozinho). */
        foreach (var c in lista) Guardar(c);
        return lista;
    }

    public async Task<Cliente?> ObterClienteAsync(string slug)
    {
        var c = await Get<Cliente>($"api/clientes/{Uri.EscapeDataString(slug)}");
        /* Os lotes de copy vêm da API (copy_lote), então a releitura é a verdade; o que é local herda da anterior. */
        Guardar(c);
        return c;
    }

    /// <summary>Artes que o gestor reprovou nesta sessão: a carteira da API marca toda arte como aprovada.</summary>
    readonly HashSet<string> _artesReprovadas = new();

    /// <summary>Guarda a instância relida, herdando da anterior o que só existe no desktop (a API ainda não grava):
    /// os pilotos, os conjuntos lidos no ongoing, os campos da conta digitados em Assets (política, link, formulário,
    /// lista, site) e a reprovação local de arte. É paliativo de SESSÃO: não sobrevive a um reinício do app.
    /// O Start em curso (o condutor) também é reaplicado, para o card não oferecer um segundo Start.</summary>
    void Guardar(Cliente c)
    {
        if (_cache.TryGetValue(c.Slug, out var antigo) && !ReferenceEquals(antigo, c))
        {
            c.Pilotos = antigo.Pilotos; c.Conjuntos = antigo.Conjuntos;
            c.Conta.PoliticaUrl ??= antigo.Conta.PoliticaUrl; c.Conta.Link ??= antigo.Conta.Link; c.Conta.FormularioId ??= antigo.Conta.FormularioId;
            c.Conta.TemLista |= antigo.Conta.TemLista; c.Conta.TemSiteOuTag |= antigo.Conta.TemSiteOuTag;
        }
        foreach (var a in c.Artes) if (_artesReprovadas.Contains(a.Id)) a.Aprovada = false;
        Fluxo.Condutor.ReaplicarEstado(c);
        _cache[c.Slug] = c;
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

    public async Task<Cliente> CriarClienteAsync(string nome, string squad)
    {
        var j = await Envie(HttpMethod.Post, "api/clientes", new { nome, squad });
        await ListarClientesAsync();
        var slug = Str(j, "slug") ?? "";
        Avisar();
        return _cache.TryGetValue(slug, out var c) ? c : throw new InvalidOperationException(Bol(j, "sem_squad") == true
            ? "o cliente foi criado sem squad e por isso não aparece na carteira: informe o squad ao criar."
            : "o cliente foi criado, mas não apareceu na carteira: confira o vínculo de squad.");
    }

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

    /// <summary>Negar, ou aceitar: o aceite de PAUSAR/DUPLICAR é APLICADO na Meta pelo executor da API, com as travas
    /// de dinheiro. A resposta traz os avisos da porta (verba que desceu, cópia pausada, ensaio fora da produção).</summary>
    public async Task<Recomendacao> DecidirAsync(Cliente c, string recomendacaoId, bool aceita, string? motivo)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/recomendacoes/{recomendacaoId}/decisao") { Content = JsonContent.Create(new { aceita, motivo }, options: Json) };
        var antes = c.Recomendacoes.FirstOrDefault(x => x.Id == recomendacaoId);
        HttpResponseMessage r;
        try { r = await _lento.SendAsync(req); }
        catch (HttpRequestException e) { throw new InvalidOperationException($"não consegui falar com a API em {BaseUrl}: {e.Message}"); }
        catch (OperationCanceledException) when (aceita) { throw EstadoIncerto("aplicar a decisão", "o conjunto"); }
        catch (OperationCanceledException) { throw new InvalidOperationException("a API não respondeu em 5 minutos ao negar: releia as recomendações antes de repetir."); }
        var j = await LerJson(r);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException(Erro(j, j.ValueKind == JsonValueKind.Object && j.TryGetProperty("erro", out var e1) && e1.ValueKind == JsonValueKind.String ? e1.GetString()! : $"a API respondeu {(int)r.StatusCode}"));
        var lista = await ListarRecomendacoesAsync(c);
        var avisos = j.ValueKind == JsonValueKind.Object && j.TryGetProperty("avisos", out var av) && av.ValueKind == JsonValueKind.Array
            ? av.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList() : new List<string>();
        if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("erro", out var er) && er.ValueKind == JsonValueKind.String) avisos.Insert(0, er.GetString()!);
        /* A lista traz só as mais recentes: a decidida pode não voltar. A escrita já aconteceu, então não lança. */
        var rec = lista.FirstOrDefault(x => x.Id == recomendacaoId);
        if (rec is null)
        {
            rec = RecomendacaoForaDaLista.Montar(antes, recomendacaoId, c.Slug, RecomendacaoForaDaLista.EstadoDe(Str(j, "decisao"), aceita));
            avisos.Add(RecomendacaoForaDaLista.Aviso);
        }
        foreach (var a in avisos) c.Acoes.Insert(0, new AcaoDoAgente(DateTime.Now, "motor", a, rec.Estado.ToString().ToLowerInvariant(), _usuario, null));
        Avisar();
        return rec;
    }

    /* ---- o que ainda passa pelo V1 (Meta e Storage) ------------------------------- */

    /* ---- o setup do cliente, dentro da API (31/10/2026): assets, conta e o token do squad ---- */

    public async Task<Asset> SubirAssetAsync(Cliente c, TipoDeAsset tipo, string nome, string mime, byte[] bytes)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/assets") { Content = JsonContent.Create(new { tipo = Asset.SlugDoTipo(tipo), nome, mime, base64 = Convert.ToBase64String(bytes) }, options: Json) };
        HttpResponseMessage r;
        try { r = await _lento.SendAsync(req); }
        catch (HttpRequestException e) { throw new InvalidOperationException($"não consegui falar com a API em {BaseUrl}: {e.Message}"); }
        var j = ParseOuVazio(await r.Content.ReadAsStringAsync());
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, "o upload falhou"));
        var asset = new Asset
        {
            Id = Str(j, "asset_id") ?? Guid.NewGuid().ToString("n"), Tipo = tipo, Nome = Str(j, "nome") ?? nome, Mime = mime, TamanhoBytes = bytes.Length, Origem = "manual", EmUso = true,
            Url = Str(j, "url") ?? (mime.StartsWith("image/") ? $"data:{mime};base64,{Convert.ToBase64String(bytes)}" : null),
        };
        c.Assets.RemoveAll(a => a.Id == asset.Id || (a.Tipo == tipo && a.Nome == asset.Nome));
        c.Assets.Add(asset); Avisar();
        return asset;
    }

    public async Task RemoverAssetAsync(Cliente c, string assetId)
    {
        var r = await _http.DeleteAsync($"api/clientes/{Uri.EscapeDataString(c.Slug)}/assets/{Uri.EscapeDataString(assetId)}");
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(await LerJson(r), "não consegui remover o asset"));
        c.Assets.RemoveAll(a => a.Id == assetId); Avisar();
    }

    public async Task<ResultadoDaConta> ConectarContaAsync(Cliente c, string? adAccountId, string? pageId, string? pageNome)
    {
        try
        {
            var desconectar = string.IsNullOrWhiteSpace(adAccountId);
            await Envie(HttpMethod.Patch, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/conta", new { adAccountId = desconectar ? null : adAccountId!.Trim(), pageId, pageNome, desconectar });
            c.Conta.AdAccountId = desconectar ? null : adAccountId!.Trim().Replace("act_", "");
            c.Conta.PageId = pageId; c.Conta.PageNome = pageNome;
            Avisar();
            return new(true, null);
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    public Task SalvarContaAsync(Cliente c) { Avisar(); return Task.CompletedTask; }

    public async Task<EstadoDoToken> ConferirTokenAsync(Cliente c)
    {
        var j = await Get<JsonElement>($"api/squads/{Uri.EscapeDataString(c.Squad)}/token");
        c.Conta.TokenValido = Bol(j, "valido"); c.Conta.TokenMotivo = Str(j, "motivo"); c.Conta.TokenConferidoEm = DateTime.Now;
        Avisar();
        return new(c.Conta.TokenValido, c.Conta.TokenMotivo);
    }

    public async Task<EstadoDoToken> GravarTokenDoSquadAsync(Cliente c, string token)
    {
        await Envie(HttpMethod.Post, $"api/squads/{Uri.EscapeDataString(c.Squad)}/token", new { token = token.Trim() });
        return await ConferirTokenAsync(c);
    }
    /* ---- o Agente de Copy, dentro da API (31/10/2026): o mesmo contrato do /api/os/copy/gerar do V1 ---- */

    static string? Str(JsonElement j, string n) => j.ValueKind == JsonValueKind.Object && j.TryGetProperty(n, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : v.GetRawText()) : null;
    static double? Num(JsonElement j, string n) => j.ValueKind == JsonValueKind.Object && j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static bool? Bol(JsonElement j, string n) => j.ValueKind == JsonValueKind.Object && j.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : null : null;
    static IEnumerable<JsonElement> Itens(JsonElement j, string n) => j.ValueKind == JsonValueKind.Object && j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public async Task<LoteDeCopy> GerarCopyAsync(Cliente c, IReadOnlyList<GeradorDemo.Pedido> pedido, string? briefing)
    {
        var itens = pedido.Select(p => new { tipo = p.Tipo == TipoPeca.Video ? "video" : "estatico", funil = p.Etapa.ToString().ToLowerInvariant(), nPecas = p.NPecas });
        var req = new HttpRequestMessage(HttpMethod.Post, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/copy/gerar") { Content = JsonContent.Create(new { itens, briefing }, options: Json) };
        HttpResponseMessage r;
        try { r = await _lento.SendAsync(req); }
        catch (HttpRequestException e) { throw new InvalidOperationException($"não consegui falar com a API em {BaseUrl}: {e.Message}"); }
        catch (TaskCanceledException) { throw new InvalidOperationException("o Agente de Copy não respondeu em 5 minutos."); }
        var texto = await r.Content.ReadAsStringAsync();
        var j = ParseOuVazio(texto);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, $"a API respondeu {(int)r.StatusCode} ao gerar copy"));
        var lote = new LoteDeCopy
        {
            Id = Str(j, "lote_id") ?? Guid.NewGuid().ToString("n"), Numero = (int)(Num(j, "numero") ?? c.Lotes.Count + 1), Modelo = Str(j, "modelo") ?? "", Briefing = briefing,
            VaultSha = Str(j.TryGetProperty("vault", out var v) ? v : default, "sha") ?? Parametros.VaultSha, NichoSlug = c.NichoSlug,
            TokensIn = (int)(Num(j, "tokens_in") ?? 0), TokensOut = (int)(Num(j, "tokens_out") ?? 0),
        };
        foreach (var item in Itens(j, "itens"))
        {
            var funil = Str(item, "funil") ?? "mofu";
            var etapa = funil.ToUpperInvariant() switch { "TOFU" => Etapa.TOFU, "BOFU" => Etapa.BOFU, _ => Etapa.MOFU };
            var tipo = Str(item, "tipo") == "video" ? TipoPeca.Video : TipoPeca.Estatico;
            foreach (var p in Itens(item, "pecas"))
            {
                var formas = Itens(p, "forma_headline").Select(x => x.GetString() ?? "").Where(x => x != "").ToList();
                lote.Pecas.Add(new PecaDeCopy
                {
                    Id = Str(p, "peca_id") ?? Guid.NewGuid().ToString("n"), Etapa = etapa, Tipo = tipo, Numero = (int)(Num(p, "ordem") ?? 0),
                    Headline = Str(p, "headline") ?? "", Sub = Str(p, "corpo") ?? "", Cta = Str(p, "cta") ?? "",
                    AnguloHeadline = Str(p, "angulo_headline") ?? "outro", PapelSub = Str(p, "papel_sub") ?? "fragmento", TipoCta = Str(p, "tipo_cta") ?? "outro", FormasHeadline = formas,
                    CrivoOk = Bol(p, "crivo_ok") ?? true, MotivoDoCrivo = Str(p, "crivo_motivo"), Regeneracoes = Bol(p, "regerada") == true ? [Str(p, "crivo_motivo") ?? "regerada pelo crivo"] : [],
                });
            }
        }
        c.Lotes.Insert(0, lote); Avisar();
        return lote;
    }

    public async Task AjustarPecaAsync(Cliente c, string pecaId, string instrucao)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/pecas/{Uri.EscapeDataString(pecaId)}/ajustar") { Content = JsonContent.Create(new { instrucao }, options: Json) };
        HttpResponseMessage r;
        try { r = await _lento.SendAsync(req); }
        catch (HttpRequestException e) { throw new InvalidOperationException($"não consegui falar com a API em {BaseUrl}: {e.Message}"); }
        var j = ParseOuVazio(await r.Content.ReadAsStringAsync());
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, $"a API respondeu {(int)r.StatusCode} ao ajustar a peça"));
        var p = c.Pecas.First(x => x.Id == pecaId);
        if (j.TryGetProperty("peca", out var np))
        {
            p.Headline = Str(np, "headline") ?? p.Headline; p.Sub = Str(np, "corpo") ?? p.Sub; p.Cta = Str(np, "cta") ?? p.Cta; p.Versao++;
            p.AnguloHeadline = Str(np, "angulo_headline") ?? p.AnguloHeadline; p.PapelSub = Str(np, "papel_sub") ?? p.PapelSub; p.TipoCta = Str(np, "tipo_cta") ?? p.TipoCta;
            p.CrivoOk = Bol(np, "crivo_ok") ?? true; p.MotivoDoCrivo = Str(np, "crivo_motivo");
        }
        Avisar();
    }
    public async Task AprovarPecaAsync(Cliente c, string pecaId, bool aprovada)
    {
        await Envie(HttpMethod.Patch, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/pecas/{Uri.EscapeDataString(pecaId)}", new { aprovada });
        c.Pecas.First(x => x.Id == pecaId).Aprovada = aprovada; Avisar();
    }
    /// <summary>O Designer dentro da API (31/10/2026): uma chamada por peça e formato, como o V1; o lote abre na primeira e
    /// fecha com `encerrar` no fim. Cada arte que sai já entra na tela.</summary>
    public async Task<IReadOnlyList<Arte>> GerarArtesAsync(Cliente c, IReadOnlyList<string> pecaIds, IReadOnlyList<string> formatos, string? template = null)
    {
        var fmts = formatos.Select(f => f == "story" ? "story" : "feed").Distinct().ToList();
        if (fmts.Count == 0) fmts.Add("feed");
        var pecas = c.Pecas.Where(p => pecaIds.Contains(p.Id)).ToList();
        if (pecas.Count == 0) throw new InvalidOperationException("escolha pelo menos uma peça de copy aprovada: o Designer desenha em cima do texto dela.");
        var novas = new List<Arte>(); var falhas = new List<string>();
        string? loteId = null;
        async Task<JsonElement> Chame(object corpo)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/artes/gerar") { Content = JsonContent.Create(corpo, options: Json) };
            HttpResponseMessage r;
            try { r = await _lento.SendAsync(req); }
            catch (HttpRequestException e) { throw new InvalidOperationException($"não consegui falar com a API em {BaseUrl}: {e.Message}"); }
            catch (TaskCanceledException) { throw new InvalidOperationException("o Designer não respondeu em 5 minutos."); }
            var j = ParseOuVazio(await r.Content.ReadAsStringAsync());
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, $"a API respondeu {(int)r.StatusCode} ao gerar a arte"));
            return j;
        }
        foreach (var p in pecas)
            foreach (var f in fmts)
            {
                try
                {
                    var j = await Chame(new { pecaIds = new[] { p.Id }, formatos = new[] { f }, template = string.IsNullOrWhiteSpace(template) ? null : template, designerLoteId = loteId });
                    loteId ??= Str(j, "lote_id");
                    foreach (var s in Itens(j, "pecas"))
                    {
                        var formato = Str(s, "formato") ?? f;
                        var lote = c.Lotes.FirstOrDefault(l => l.Pecas.Any(x => x.Id == p.Id))?.Numero ?? 0;
                        var arte = new Arte
                        {
                            Id = Str(s, "id") ?? Guid.NewGuid().ToString("n"), PecaId = p.Id, Etapa = p.Etapa, Formato = formato,
                            Titulo = $"{p.Etapa} {p.Numero} · {formato} · copy lote {lote}", Url = Str(s, "url") ?? "", Lote = lote, Aprovada = false,
                            Template = string.IsNullOrWhiteSpace(template) ? Design.TemplatesDoDesigner.Auto : template,
                        };
                        c.Artes.Insert(0, arte); novas.Add(arte); Avisar();
                    }
                    foreach (var fl in Itens(j, "falhas")) falhas.Add($"{Str(fl, "funil")} {Num(fl, "numero")} {f}: {Str(fl, "motivo")}");
                }
                catch (InvalidOperationException e) { falhas.Add($"{p.Etapa} {p.Numero} {f}: {e.Message}"); }
            }
        if (loteId is not null) { try { await Chame(new { designerLoteId = loteId, encerrar = true }); } catch { } }
        if (novas.Count == 0) throw new InvalidOperationException("nenhuma arte saiu. " + string.Join(" · ", falhas.Distinct()));
        if (falhas.Count > 0) throw new InvalidOperationException($"saíram {novas.Count} arte(s), mas não saíram: {string.Join(" · ", falhas.Distinct())}");
        return novas;
    }
    public Task AprovarArteAsync(Cliente c, string arteId, bool aprovada)
    {
        c.Artes.First(a => a.Id == arteId).Aprovada = aprovada;
        /* como na demonstração: arte reprovada deixa de ser piloto, e a reprovação sobrevive à releitura da sessão */
        if (aprovada) _artesReprovadas.Remove(arteId);
        else { _artesReprovadas.Add(arteId); foreach (var et in c.Pilotos.Where(kv => kv.Value == arteId).Select(kv => kv.Key).ToList()) c.Pilotos.Remove(et); }
        Avisar(); return Task.CompletedTask;
    }
    public Task EscolherPilotoAsync(Cliente c, Etapa etapa, string? arteId)
    {
        if (arteId is null) c.Pilotos.Remove(etapa); else c.Pilotos[etapa] = arteId;
        Avisar();
        return Task.CompletedTask;
    }
    /// <summary>O Publicar do Start pela API .NET: o piloto de CADA etapa vai no pedido (um anúncio por conjunto, com a arte
    /// da etapa dele), e a API cria campanha, formulário, conjuntos e anúncios, tudo PAUSED, idempotente pelo hash.</summary>
    public async Task<ResultadoDaPublicacao> PublicarAsync(Cliente c, PedidoDePublicacao pedido)
    {
        if (Fluxo.Estagios.ValidarAssentos(c, Parametros).FirstOrDefault(a => !a.Ok && a.Arte is not null) is { } errado)
            return new(false, "assento_errado", $"validador de assento: {errado.Motivo}. Troque o piloto de {errado.Vaga} no Design por uma arte gerada pela copy de {errado.Vaga}.", false, null, null);
        var corpo = new
        {
            pilotos = c.Pilotos.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            texto = pedido.Texto, politicaUrl = pedido.PoliticaUrl, link = pedido.Link, formularioId = pedido.FormularioId,
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/publicar") { Content = JsonContent.Create(corpo, options: Json) };
        HttpResponseMessage r;
        try { r = await _lento.SendAsync(req); }
        catch (HttpRequestException e) { throw new InvalidOperationException($"não consegui falar com a API em {BaseUrl}: {e.Message}"); }
        catch (OperationCanceledException) { throw EstadoIncerto("publicar", "a campanha do cliente"); }
        var texto = await r.Content.ReadAsStringAsync();
        if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("sem sessão na API: faça o login de novo.");
        ResultadoDoStart? res = null;
        try { res = JsonSerializer.Deserialize<ResultadoDoStart>(texto, Json); } catch { }
        if (res is null) throw new InvalidOperationException(Erro(ParseOuVazio(texto), $"a API respondeu {(int)r.StatusCode} ao publicar"));
        /* Um não-2xx com `{error}` (403 "publicar na Meta é de head pra cima", escopo) desserializa sem código nem motivo:
           o texto da API e o status vão no lugar. */
        if (!res.Ok)
            return new(false, res.Codigo ?? $"http_{(int)r.StatusCode}", res.Motivo ?? Erro(ParseOuVazio(texto), $"a API respondeu {(int)r.StatusCode} ao publicar"), res.Simulado, null, null);
        var pub = new Publicacao
        {
            EstruturaHash = res.EstruturaHash ?? "", CampanhaId = res.CampanhaId, FormularioId = res.FormularioId, Link = res.Link,
            /* o que o banco grava: sem_criativo = campanha, formulário e conjuntos criados, e nenhum anúncio */
            Estado = res.SemCriativo ? "sem_criativo" : "ok",
            NomeDaCampanha = res.Conjuntos.Count > 0 ? $"campanha {res.CampanhaId}" : "", VaultSha = Parametros.VaultSha,
        };
        var etapaDoConjunto = new Dictionary<string, Etapa>();
        foreach (var cj in res.Conjuntos)
        {
            var etapa = Enum.TryParse<Etapa>(cj.Etapa, out var et) ? et : Etapa.MOFU;
            pub.Conjuntos.Add(new ConjuntoCriado(cj.Nome, etapa, cj.Publico, cj.AdsetId ?? "", 0));
            if (!string.IsNullOrEmpty(cj.AdsetId)) etapaDoConjunto[cj.AdsetId] = etapa;
        }
        /* cada anúncio leva o piloto da etapa do conjunto dele (não o primeiro piloto para todos) */
        foreach (var an in res.Anuncios)
        {
            var piloto = an.AdsetId is not null && etapaDoConjunto.TryGetValue(an.AdsetId, out var etDoAd) && c.Pilotos.TryGetValue(etDoAd, out var id) ? id : c.Pilotos.Values.FirstOrDefault();
            pub.Anuncios.Add(new AnuncioCriado(an.Nome, an.AdsetId ?? "", an.AdId ?? "", piloto ?? ""));
        }
        pub.Avisos.AddRange(res.Avisos);
        if (res.SemCriativo && res.CriativoNaMao is { } cola)
        {
            pub.Avisos.Add(cola.Motivo);
            foreach (var a in cola.Anuncios)
                pub.Avisos.Add($"na mão: anúncio \"{a.Nome}\" no conjunto \"{a.Conjunto}\" ({a.AdsetId}): headline \"{a.Headline}\", texto \"{a.Corpo}\", botão {a.Cta}, arte {a.ImagemHash ?? "sem hash"}, UTMs {cola.UrlTags}");
        }
        if (res.JaPublicada) pub.Avisos.Insert(0, "esta estrutura já tinha sido publicada: é a mesma campanha.");
        c.Publicacao = pub; Avisar();
        return new(true, null, null, false, pub, res.Recado);
    }
    /// <summary>Rodar o motor pela API (31/10/2026): a passada de decisão sobre a carga do banco; a carga da Meta é o cron.</summary>
    public async Task<RodadaDoMotor> RodarMotorAsync(Cliente c)
    {
        var j = await Envie(HttpMethod.Post, $"api/clientes/{Uri.EscapeDataString(c.Slug)}/motor/rodar", new { });
        var recs = await ListarRecomendacoesAsync(c);
        var naoAgiu = Itens(j, "nao_agiu").Select(x => x.GetString() ?? "").ToList();
        c.UltimaRodada = DateTime.Now; Avisar();
        return new RodadaDoMotor(recs.Where(r => r.Estado == EstadoDaRecomendacao.Pendente).ToList(), (int)(Num(j, "gravadas") ?? 0), (int)(Num(j, "repetidas") ?? 0), [], naoAgiu, [], Str(j, "vault_sha"));
    }
    public Task SimularVeiculacaoAsync(Cliente c, int dias) => throw new NotSupportedException("a veiculação é real: ative a campanha no Gerenciador e a carga diária lê a Meta.");

    /* ---- panorama: montado no app sobre a carteira da API (a rota /api/panorama é o próximo passo) ---- */
    readonly PanoramaLocal _panorama = new("você");
    bool _semRotaDoPanorama;
    /// <summary>GET /api/panorama (KPIs, CS, token, visto e pontos numa leitura). Numa API antiga, sem a rota, monta no app.</summary>
    public async Task<BlueOS.Core.Panorama.PanoramaDoSquad> LerPanoramaAsync(string? squad)
    {
        if (!_semRotaDoPanorama)
        {
            try { return await Get<BlueOS.Core.Panorama.PanoramaDoSquad>("api/panorama" + (string.IsNullOrWhiteSpace(squad) ? "" : "?squad=" + Uri.EscapeDataString(squad))); }
            catch (InvalidOperationException e) when (e.Message.Contains("404")) { _semRotaDoPanorama = true; }
        }
        return await _panorama.MontarAsync(this, squad);
    }
    public async Task MarcarVistoAsync(string clienteSlug, string assinatura, bool visto)
    {
        if (_semRotaDoPanorama) _panorama.MarcarVisto(clienteSlug, assinatura, visto);
        else await Envie(HttpMethod.Post, "api/panorama/visto", new { clienteSlug, assinatura, visto });
        Avisar();
    }
    public async Task RegistrarACaminhoAsync(string clienteSlug, string acao)
    {
        if (_semRotaDoPanorama) _panorama.RegistrarACaminho(await ListarClientesAsync(), Parametros, clienteSlug, acao);
        else await Envie(HttpMethod.Post, "api/panorama/a-caminho", new { clienteSlug, acao });
        Avisar();
    }
}
