using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BlueOS.Core.Copy;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

namespace BlueOS.Core.Api;

/* ============================================================================
   O CLIENTE TIPADO DAS ROTAS DO V1 (Next.js, porta 3080 no dev).
   ----------------------------------------------------------------------------
   Plano de migração, "Começar pelo desktop": o desktop lê as rotas que o V1 já
   tem; quando a API .NET existir, só o endereço muda. A sessão é a da pessoa
   (cookie do Supabase gravado pelo /api/os/login); o app NUNCA guarda chave do
   banco nem service role.

   As rotas e os corpos abaixo são os lidos no V1 em 29/10/2026:
     POST /api/os/login                          { login, senha }
     GET  /api/trafego/clientes                  → clientes[] (id, slug, nome, status, temConta, adAccountId, squads)
     GET/POST /api/os/clientes/{id}/trafego      { nicho_slug?, budget_mensal? }
     GET/POST /api/os/clientes/{id}/mql          { regua?, teto_mql?, nicho_slug? }
     GET/POST /api/os/clientes/{id}/assets       multipart file + tipo
     POST /api/trafego/conectar-conta            { cliente_slug, ad_account_id | desconectar }
     GET  /api/trafego/token-meta                → squads[] com valido
     POST /api/os/copy/gerar                     { cliente_id, itens[], briefing? }
     GET  /api/os/copy/lotes?cliente_id=         → lotes[] (lote_id, itens[tipo, funil, pecas[peca_id, ordem, headline, corpo, cta, aprovada]])
     PATCH /api/os/copy/peca/{id}                { aprovada }
     POST /api/designer/gerar                    plano: { cliente, lote_id, formato, nova_geracao } → { lote_id, pecas[funil, numero, head] }
                                                 peça:  { cliente, lote_id, designer_lote_id, formato, peca{funil, numero, formato} } → { pecas[id, formato, url], falhas[] }
     GET  /api/trafego/pecas-designer?cliente=   → pecas[] (id, titulo, lote, url)
     GET  /api/trafego/motor/estrutura?cliente=  → { ok, estrutura } | { ok:false, falta, motivo }
     POST /api/trafego/motor/publicar            { cliente_slug, designer_peca_id, texto?, politica_url?, link?, formulario_id? }
     GET  /api/trafego/motor/ongoing?cliente=    → kpis, leads, acoes, semRegua
     GET/POST /api/trafego/motor/recomendacoes   { cliente_slug }
     POST /api/trafego/motor/decisao             { recomendacao_id, decisao, motivo? }
     GET/PATCH /api/trafego/motor/execucao       { cliente_slug, modo } | { chave_geral: { ligada, motivo } }

   ⚠️ Este cliente NÃO foi exercitado contra um V1 em execução nesta sessão.
   O que não tem rota no V1 (salvar a ficha inteira, gerar arte por aqui) lança
   NotSupportedException com o motivo, em vez de fingir que gravou.
   ============================================================================ */
public sealed class V1Api : IBlueApi, IDisposable
{
    readonly HttpClient _http;
    readonly CookieContainer _cookies = new();
    readonly Dictionary<string, Cliente> _cache = new();
    string? _usuario;

    public string BaseUrl { get; }
    public string Origem => $"V1 · {BaseUrl}{(_usuario is null ? " (sem sessão)" : $" · {_usuario}")}";
    public bool EhDemo => false;
    public ParametrosMotor Parametros { get; } = ParametrosMotor.PadraoDoVault;
    public bool PodeSimularVeiculacao => false;
    public event Action? Mudou;
    void Avisar() => Mudou?.Invoke();

    public V1Api(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        var handler = new HttpClientHandler { CookieContainer = _cookies, UseCookies = true, AllowAutoRedirect = false };
        _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(320) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose() => _http.Dispose();

    /* ---- sessão -------------------------------------------------------------- */

    public async Task<string> LoginAsync(string login, string senha)
    {
        var r = await _http.PostAsJsonAsync("api/os/login", new { login, senha });
        var j = await LerJson(r);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, "login recusado"));
        _usuario = j.TryGetProperty("usuario", out var u) && u.TryGetProperty("nome", out var n) ? n.GetString() : login;
        Avisar();
        return _usuario ?? login;
    }

    static async Task<JsonElement> LerJson(HttpResponseMessage r)
    {
        var texto = await r.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(texto)) return default;
        try { return JsonDocument.Parse(texto).RootElement.Clone(); }
        catch { return default; }
    }

    static string Erro(JsonElement j, string padrao) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : padrao;

    /// <summary>Teto de uma LEITURA. O dev server do Next já travou mais de uma vez; sem este teto a tela
    /// esperava os 320 s do HttpClient (que são para a geração de copy e o Publicar, não para um GET).</summary>
    const int SegundosDeLeitura = 60;

    async Task<JsonElement> Get(string rota, int segundos = SegundosDeLeitura)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(segundos));
        HttpResponseMessage r;
        try { r = await _http.GetAsync(rota, cts.Token); }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"o V1 não respondeu em {segundos} s ({rota}). O dev server do Next pode ter travado: reinicie o pnpm dev e tente de novo.");
        }
        if (r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("sem sessão no V1: faça o login de novo.");
        var j = await LerJson(r);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, $"o V1 respondeu {(int)r.StatusCode} em {rota}"));
        return j;
    }

    async Task<JsonElement> Envie(HttpMethod metodo, string rota, object corpo)
    {
        var req = new HttpRequestMessage(metodo, rota) { Content = JsonContent.Create(corpo) };
        var r = await _http.SendAsync(req);
        var j = await LerJson(r);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, $"o V1 respondeu {(int)r.StatusCode} em {rota}"));
        return j;
    }

    static string? S(JsonElement j, string nome) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static decimal? D(JsonElement j, string nome) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
    static bool? B(JsonElement j, string nome) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(nome, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
    static IEnumerable<JsonElement> Lista(JsonElement j, string nome) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /* ---- clientes ------------------------------------------------------------ */

    public async Task<IReadOnlyList<Cliente>> ListarClientesAsync()
    {
        var j = await Get("api/trafego/clientes");
        var saida = new List<Cliente>();
        foreach (var x in Lista(j, "clientes"))
        {
            var slug = S(x, "slug") ?? "";
            if (!_cache.TryGetValue(slug, out var c))
            {
                c = new Cliente { Id = S(x, "id") ?? slug, Slug = slug, Nome = S(x, "nome") ?? slug };
                _cache[slug] = c;
            }
            c.Status = S(x, "status") ?? c.Status;
            c.Conta.AdAccountId = S(x, "adAccountId") ?? c.Conta.AdAccountId;
            /* `squads: []` é comum (cliente sem vínculo): o FirstOrDefault devolve um JsonElement
               vazio, e `.GetString()` nele lança "Operation is not valid". Só lê se for string. */
            c.Squads = Lista(x, "squads").Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!.Trim().ToUpperInvariant()).ToList();
            c.Squad = c.Squads.FirstOrDefault() ?? "sem squad";
            saida.Add(c);
        }
        return saida;
    }

    public async Task<Cliente?> ObterClienteAsync(string slug)
    {
        if (!_cache.ContainsKey(slug)) await ListarClientesAsync();
        if (!_cache.TryGetValue(slug, out var c)) return null;
        /* As sete leituras são independentes e vão EM PARALELO: em série, abrir um cliente
           levava a soma delas (10 s ou mais no dev server). Cada uma falha sozinha, sem
           derrubar as outras: o que não veio fica como estava e a tela diz o que falta. */
        var primeiroErro = (string?)null;
        async Task Seguro(Func<Task> f)
        {
            try { await f(); }
            catch (Exception e) { primeiroErro ??= e.Message; }
        }
        await Task.WhenAll(
            Seguro(() => LerDetalheAsync(c)),
            Seguro(async () =>
            {
                var t = await Get($"api/os/clientes/{c.Id}/trafego");
                c.NichoSlug = S(t, "nicho_slug");
                c.BudgetMensal = D(t, "budget_mensal");
            }),
            Seguro(async () =>
            {
                var m = await Get($"api/os/clientes/{c.Id}/mql");
                c.TetoMql = D(m, "teto_mql");
                c.Regua = m.TryGetProperty("regua", out var rg) && rg.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<ReguaV2>(rg.GetRawText(), Json)
                    : null;
            }),
            Seguro(async () =>
            {
                var a = await Get($"api/os/clientes/{c.Id}/assets");
                var lidos = Lista(a, "assets").Select(x =>
                {
                    var tipo = S(x, "tipo") switch { "logo_principal" => TipoDeAsset.LogoPrincipal, "logo_variacao" => TipoDeAsset.LogoVariacao, _ => TipoDeAsset.Tela };
                    return new Asset { Id = S(x, "asset_id") ?? "", Tipo = tipo, Nome = S(x, "nome") ?? "", Mime = S(x, "mime") ?? "", TamanhoBytes = (long)(D(x, "tamanho_bytes") ?? 0), Origem = S(x, "origem") ?? "manual", EmUso = B(x, "em_uso") ?? true, Url = S(x, "url") };
                }).ToList();
                c.Assets.Clear();
                c.Assets.AddRange(lidos);
            }),
            Seguro(async () =>
            {
                var e = await Get($"api/trafego/motor/execucao?cliente={Uri.EscapeDataString(slug)}");
                c.Modo = S(e, "modo") switch { "sombra" => ModoDeExecucao.Sombra, "executa" => ModoDeExecucao.Executa, _ => ModoDeExecucao.Recomenda };
            }),
            Seguro(async () =>
            {
                var p = await Get($"api/trafego/pecas-designer?cliente={Uri.EscapeDataString(slug)}");
                var lidas = Lista(p, "pecas").Select(x => new Arte { Id = S(x, "id") ?? "", PecaId = "", Etapa = EtapaDoTitulo(S(x, "titulo")), Formato = S(x, "titulo") is { } tf && tf.EndsWith("story", StringComparison.OrdinalIgnoreCase) ? "story" : "feed", Titulo = S(x, "titulo") ?? "", Url = S(x, "url") ?? "", Lote = (int)(D(x, "lote") ?? 0), Aprovada = true }).ToList();
                c.Artes.Clear();
                c.Artes.AddRange(lidas);
            }),
            Seguro(() => LerCopyAsync(c)),
            Seguro(() => ListarRecomendacoesAsync(c)));
        /* Se NADA veio (dev server travado, sessão caída), a tela precisa saber: o erro sobe. */
        if (primeiroErro is not null && c.Ficha.Responsavel is null && c.NichoSlug is null && c.Assets.Count == 0)
            throw new InvalidOperationException(primeiroErro);
        return c;
    }

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Cliente> CriarClienteAsync(string nome, string squad)
    {
        var j = await Envie(HttpMethod.Post, "api/os/clientes/criar", new { nome });
        await ListarClientesAsync();
        var slug = S(j, "slug") ?? S(j.TryGetProperty("cliente", out var cl) ? cl : default, "cliente_slug") ?? "";
        Avisar();
        return _cache.TryGetValue(slug, out var c) ? c : throw new InvalidOperationException("o cliente foi criado, mas não apareceu na carteira de tráfego: confira o vínculo de squad no V1.");
    }

    /* ---- ficha --------------------------------------------------------------- */

    /// <summary>
    /// Contato: PATCH /informacoes (os campos CONTATO da rota; dinheiro é de coordenador pra cima e fica de fora).
    /// ICP: PATCH /contexto com o perfil (cliente_icp) e o recorte do ICP principal (cliente_icp_segmento).
    /// Produto e estratégia não têm rota de escrita no V1 por aqui: ficam só de leitura.
    /// </summary>
    public async Task SalvarFichaAsync(Cliente c)
    {
        var contato = new Dictionary<string, object?>();
        void Campo(string nome, string? v) { if (!string.IsNullOrWhiteSpace(v)) contato[nome] = v.Trim(); }
        Campo("responsavel", c.Ficha.Responsavel);
        Campo("contato_principal", c.Ficha.ContatoPrincipal);
        Campo("cnpj", c.Ficha.Cnpj);
        Campo("razao_social", c.Ficha.RazaoSocial);
        Campo("whatsapp", c.Ficha.Whatsapp);
        if (contato.Count > 0) await Envie(HttpMethod.Patch, $"api/os/clientes/{c.Id}/informacoes", contato);

        await Envie(HttpMethod.Patch, $"api/os/clientes/{c.Id}/contexto", new
        {
            icp = new
            {
                tipo = c.Icp.Tipo, nivel_consciencia = c.Icp.NivelConsciencia, temperatura = c.Icp.Temperatura,
                tamanho_mercado = c.Icp.TamanhoMercado, persona = c.Icp.Persona, publico_alvo = c.Icp.PublicoAlvo, obs = c.Icp.Obs,
                dores_principais = c.Icp.DoresPrincipais, desejos_principais = c.Icp.DesejosPrincipais, canais_onde_estao = c.Icp.CanaisOndeEstao,
            },
            segmentos = new[]
            {
                new
                {
                    segmento_id = c.Icp.SegmentoId, setor = c.Icp.Setor, descricao = c.Icp.Segmento, cargo_decisor = c.Icp.CargoDecisor,
                    faturamento_minimo = c.Icp.FaturamentoMinimo, localizacao = c.Icp.Localizacao,
                },
            },
        });
        Avisar();
    }

    /// <summary>Os lotes de copy do cliente (GET /api/os/copy/lotes): é deles que o Designer desenha. O número do lote
    /// é a posição cronológica (o mais antigo é o 1); o número da peça é a `ordem` dentro do item, que é o que
    /// a rota do Designer chama de `numero`.</summary>
    async Task LerCopyAsync(Cliente c)
    {
        var j = await Get($"api/os/copy/lotes?cliente_id={Uri.EscapeDataString(c.Id)}");
        var lidos = Lista(j, "lotes").ToList();   /* o V1 devolve do mais novo para o mais antigo */
        var lotes = new List<LoteDeCopy>();
        for (var i = 0; i < lidos.Count; i++)
        {
            var l = lidos[i];
            var lote = new LoteDeCopy
            {
                Id = S(l, "lote_id") ?? Guid.NewGuid().ToString("n"), Numero = lidos.Count - i, Modelo = S(l, "modelo") ?? "", Briefing = S(l, "briefing"),
                CriadoEm = DateTime.TryParse(S(l, "criado_em"), out var dt) ? dt.ToLocalTime() : DateTime.MinValue,
                TokensIn = (int)(D(l, "tokens_in") ?? 0), TokensOut = (int)(D(l, "tokens_out") ?? 0), VaultSha = Parametros.VaultSha, NichoSlug = c.NichoSlug,
            };
            foreach (var item in Lista(l, "itens"))
            {
                var etapa = (S(item, "funil") ?? "mofu").ToUpperInvariant() switch { "TOFU" => Etapa.TOFU, "BOFU" => Etapa.BOFU, _ => Etapa.MOFU };
                var tipo = S(item, "tipo") == "video" ? TipoPeca.Video : TipoPeca.Estatico;
                foreach (var p in Lista(item, "pecas"))
                {
                    var id = S(p, "peca_id") ?? Guid.NewGuid().ToString("n");
                    var local = c.Pecas.FirstOrDefault(x => x.Id == id);
                    lote.Pecas.Add(new PecaDeCopy
                    {
                        Id = id, Etapa = etapa, Tipo = tipo, Numero = (int)(D(p, "ordem") ?? lote.Pecas.Count + 1),
                        Headline = S(p, "headline") ?? "", Sub = S(p, "corpo") ?? "", Cta = S(p, "cta") ?? "",
                        /* o histórico não traz as etiquetas nem o crivo: o que já estava em memória (lote gerado aqui) fica */
                        AnguloHeadline = local?.AnguloHeadline ?? "outro", PapelSub = local?.PapelSub ?? "fragmento", TipoCta = local?.TipoCta ?? "outro",
                        CrivoOk = local?.CrivoOk ?? true, MotivoDoCrivo = local?.MotivoDoCrivo, Hipotese = local?.Hipotese ?? "",
                        Aprovada = B(p, "aprovada") ?? local?.Aprovada ?? false,
                    });
                }
            }
            lotes.Add(lote);
        }
        c.Lotes = lotes;
    }

    /// <summary>A ficha inteira vem de GET /detalhe: cadastro, contrato, ICP, recorte, produto e estratégia.</summary>
    async Task LerDetalheAsync(Cliente c)
    {
        var d = await Get($"api/os/clientes/{c.Id}/detalhe");
        var cli = d.TryGetProperty("cliente", out var cl) ? cl : default;
        c.Ficha.Cnpj = S(cli, "cnpj"); c.Ficha.RazaoSocial = S(cli, "razao_social"); c.Ficha.ContatoPrincipal = S(cli, "contato");
        c.Ficha.Responsavel = S(cli, "responsavel"); c.Ficha.Whatsapp = S(cli, "whatsapp");
        if (DateOnly.TryParse(S(cli, "desde") ?? S(cli, "criado_em")?[..10], out var desde)) c.Desde = desde;
        var ct = d.TryGetProperty("contrato", out var cc) ? cc : default;
        if (ct.ValueKind == JsonValueKind.Object)
        {
            c.Ficha.Modalidade = S(ct, "modalidade"); c.Ficha.Mrr = D(ct, "mrr"); c.Ficha.ValorTotal = D(ct, "valor_total");
            c.Ficha.DataVencimento = DateOnly.TryParse(S(ct, "data_vencimento"), out var dv) ? dv : null;
        }
        var ctx = d.TryGetProperty("contexto", out var cx) ? cx : default;
        var icp = ctx.TryGetProperty("icp", out var ic) ? ic : default;
        if (icp.ValueKind == JsonValueKind.Object)
        {
            c.Icp.Tipo = SN(icp, "tipo") ?? c.Icp.Tipo; c.Icp.NivelConsciencia = SN(icp, "nivel_consciencia") ?? c.Icp.NivelConsciencia;
            c.Icp.Temperatura = SN(icp, "temperatura") ?? c.Icp.Temperatura; c.Icp.TamanhoMercado = S(icp, "tamanho_mercado");
            c.Icp.Persona = S(icp, "persona"); c.Icp.PublicoAlvo = S(icp, "publico_alvo"); c.Icp.Obs = S(icp, "obs");
            c.Icp.DoresPrincipais = Textos(icp, "dores_principais"); c.Icp.DesejosPrincipais = Textos(icp, "desejos_principais"); c.Icp.CanaisOndeEstao = Textos(icp, "canais_onde_estao");
        }
        var seg = Lista(ctx, "segmentos").FirstOrDefault();
        if (seg.ValueKind == JsonValueKind.Object)
        {
            c.Icp.SegmentoId = SN(seg, "segmento_id"); c.Icp.Setor = S(seg, "setor"); c.Icp.Segmento = S(seg, "descricao");
            c.Icp.CargoDecisor = S(seg, "cargo_decisor"); c.Icp.FaturamentoMinimo = S(seg, "faturamento_minimo"); c.Icp.Localizacao = S(seg, "localizacao");
        }
        var prod = ctx.TryGetProperty("produto", out var pr) ? pr : default;
        if (prod.ValueKind == JsonValueKind.Object)
        {
            c.Produto.NomeCorreto = S(prod, "nome_correto"); c.Produto.Descricao = S(prod, "descricao"); c.Produto.ModeloNegocio = S(prod, "modelo_negocio");
            c.Produto.Diferenciais = S(prod, "diferenciais"); c.Produto.FuncionalidadesChave = S(prod, "funcionalidades_chave"); c.Produto.ObjecoesComuns = S(prod, "objecoes_comuns");
        }
        var est = ctx.TryGetProperty("estrategia", out var es) ? es : default;
        if (est.ValueKind == JsonValueKind.Object)
        {
            c.Estrategia.PromessaPrincipal = S(est, "promessa_principal"); c.Estrategia.Posicionamento = S(est, "posicionamento");
            c.Estrategia.Diferencial = S(est, "diferencial"); c.Estrategia.FunilTipo = S(est, "funil_tipo"); c.Estrategia.Gatilhos = S(est, "gatilhos");
        }
        var squadAtual = S(d, "squad_atual_id");
        var nomeSquad = Lista(d, "squads").FirstOrDefault(s => S(s, "squad_id") == squadAtual);
        if (nomeSquad.ValueKind == JsonValueKind.Object) c.Squad = S(nomeSquad, "squad_nome") ?? c.Squad;
    }

    /* O titulo da biblioteca do Designer (tituloDaPeca) comeca pelo funil: "MOFU 1 - feed". */
    static Etapa EtapaDoTitulo(string? t) =>
        t is null ? Etapa.MOFU
        : t.StartsWith("TOFU", StringComparison.OrdinalIgnoreCase) || t.StartsWith("topo", StringComparison.OrdinalIgnoreCase) ? Etapa.TOFU
        : t.StartsWith("BOFU", StringComparison.OrdinalIgnoreCase) || t.StartsWith("fundo", StringComparison.OrdinalIgnoreCase) ? Etapa.BOFU
        : Etapa.MOFU;

    static string? SN(JsonElement j, string nome)
    {
        if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty(nome, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
    }

    static List<string> Textos(JsonElement j, string nome) =>
        Lista(j, nome).Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText()).Where(x => x != "").ToList();

    public async Task SalvarCardTrafegoAsync(Cliente c, string? nichoSlug, decimal? budgetMensal)
    {
        await Envie(HttpMethod.Post, $"api/os/clientes/{c.Id}/trafego", new { nicho_slug = nichoSlug, budget_mensal = budgetMensal });
        c.NichoSlug = nichoSlug; c.BudgetMensal = budgetMensal; Avisar();
    }

    public async Task SalvarCriterioMqlAsync(Cliente c, ReguaV2? regua, decimal? tetoMql)
    {
        object corpo = regua is null
            ? new { regua = (object?)null, teto_mql = tetoMql }
            : new { regua = new { versao = 2, perguntas = regua.Perguntas.Select(p => new { nome = p.Nome, chaves = p.Chaves, rotulo = p.Rotulo, forte = p.Forte, media = p.Media, elim = p.Elim }) }, teto_mql = tetoMql };
        await Envie(HttpMethod.Post, $"api/os/clientes/{c.Id}/mql", corpo);
        c.Regua = regua; c.TetoMql = tetoMql; Avisar();
    }

    public Task<IReadOnlyList<FormularioDaMeta>> ListarFormulariosAsync(Cliente c) =>
        throw new NotSupportedException("o V1 não tem rota que liste os formulários da Meta: use a fonte API .NET, que lê a página do cliente com o token do squad.");

    /* ---- assets -------------------------------------------------------------- */

    public async Task<Asset> SubirAssetAsync(Cliente c, TipoDeAsset tipo, string nome, string mime, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var arquivo = new ByteArrayContent(bytes);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(arquivo, "file", nome);
        form.Add(new StringContent(Asset.SlugDoTipo(tipo)), "tipo");
        var r = await _http.PostAsync($"api/os/clientes/{c.Id}/assets", form);
        var j = await LerJson(r);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(j, "o upload falhou"));
        await ObterClienteAsync(c.Slug);
        Avisar();
        return c.Assets.FirstOrDefault(a => a.Id == S(j, "asset_id")) ?? c.Assets.Last();
    }

    public async Task RemoverAssetAsync(Cliente c, string assetId)
    {
        var r = await _http.DeleteAsync($"api/os/clientes/{c.Id}/assets/{assetId}");
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Erro(await LerJson(r), "não consegui remover o asset"));
        c.Assets.RemoveAll(a => a.Id == assetId); Avisar();
    }

    public async Task<ResultadoDaConta> ConectarContaAsync(Cliente c, string? adAccountId, string? pageId, string? pageNome)
    {
        try
        {
            object corpo = string.IsNullOrWhiteSpace(adAccountId)
                ? new { cliente_slug = c.Slug, desconectar = true }
                : new { cliente_slug = c.Slug, ad_account_id = adAccountId.Trim() };
            await Envie(HttpMethod.Post, "api/trafego/conectar-conta", corpo);
            c.Conta.AdAccountId = string.IsNullOrWhiteSpace(adAccountId) ? null : adAccountId.Trim().Replace("act_", "");
            c.Conta.PageId = pageId; c.Conta.PageNome = pageNome;
            Avisar();
            return new(true, null);
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    public Task SalvarContaAsync(Cliente c) { Avisar(); return Task.CompletedTask; }

    public async Task<EstadoDoToken> ConferirTokenAsync(Cliente c)
    {
        var j = await Get("api/trafego/token-meta");
        foreach (var s in Lista(j, "squads"))
        {
            if (!string.Equals(S(s, "nome"), c.Squad, StringComparison.OrdinalIgnoreCase)) continue;
            c.Conta.TokenValido = B(s, "valido"); c.Conta.TokenMotivo = S(s, "motivo"); c.Conta.TokenConferidoEm = DateTime.Now;
            Avisar();
            return new(c.Conta.TokenValido, c.Conta.TokenMotivo);
        }
        return new(null, $"o squad {c.Squad} não está no seu escopo do token Meta.");
    }

    public async Task<EstadoDoToken> GravarTokenDoSquadAsync(Cliente c, string token)
    {
        var j = await Get("api/trafego/token-meta");
        var squad = Lista(j, "squads").FirstOrDefault(s => string.Equals(S(s, "nome")?.Trim(), c.Squad, StringComparison.OrdinalIgnoreCase));
        if (squad.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"o squad {c.Squad} não está no seu escopo do token Meta.");
        /* A rota confere com a Meta (/me) antes de gravar e registra a troca mascarada na trilha. */
        await Envie(HttpMethod.Post, "api/trafego/token-meta", new { squad_v0_id = S(squad, "v0_id"), access_token = token.Trim() });
        return await ConferirTokenAsync(c);
    }

    /* ---- copy ---------------------------------------------------------------- */

    public async Task<LoteDeCopy> GerarCopyAsync(Cliente c, IReadOnlyList<GeradorDemo.Pedido> pedido, string? briefing)
    {
        var itens = pedido.Select(p => new { tipo = p.Tipo == TipoPeca.Video ? "video" : "estatico", funil = p.Etapa.ToString().ToLowerInvariant(), n_pecas = p.NPecas });
        var j = await Envie(HttpMethod.Post, "api/os/copy/gerar", new { cliente_id = c.Id, itens, briefing });
        var lote = new LoteDeCopy { Id = S(j, "lote_id") ?? Guid.NewGuid().ToString("n"), Numero = c.Lotes.Count + 1, Modelo = S(j, "modelo") ?? "", Briefing = briefing, VaultSha = Parametros.VaultSha, NichoSlug = c.NichoSlug };
        var n = 0;
        foreach (var item in Lista(j, "itens"))
        {
            var funil = S(item, "funil") ?? "mofu";
            var etapa = funil.ToUpperInvariant() switch { "TOFU" => Etapa.TOFU, "BOFU" => Etapa.BOFU, _ => Etapa.MOFU };
            var tipo = S(item, "tipo") == "video" ? TipoPeca.Video : TipoPeca.Estatico;
            foreach (var p in Lista(item, "pecas"))
            {
                lote.Pecas.Add(new PecaDeCopy
                {
                    /* o número da peça é a `ordem` do V1 (é por ela que o Designer acha a copy); o contador só cobre resposta antiga */
                    Id = S(p, "peca_id") ?? S(p, "id") ?? Guid.NewGuid().ToString("n"), Etapa = etapa, Tipo = tipo, Numero = (int)(D(p, "ordem") ?? ++n),
                    Headline = S(p, "headline") ?? S(p, "hook") ?? "", Sub = S(p, "sub") ?? S(p, "body") ?? S(p, "corpo") ?? "", Cta = S(p, "cta") ?? "",
                    AnguloHeadline = S(p, "angulo_headline") ?? "outro", PapelSub = S(p, "papel_sub") ?? "fragmento", TipoCta = S(p, "tipo_cta") ?? "outro",
                    CrivoOk = B(p, "crivo_ok") ?? true, MotivoDoCrivo = S(p, "crivo_motivo"), Hipotese = S(p, "hipotese") ?? "",
                });
            }
        }
        c.Lotes.Insert(0, lote); Avisar();
        return lote;
    }

    public async Task AjustarPecaAsync(Cliente c, string pecaId, string instrucao)
    {
        var j = await Envie(HttpMethod.Post, "api/os/copy/ajustar", new { peca_id = pecaId, instrucao });
        var p = c.Pecas.First(x => x.Id == pecaId);
        if (j.TryGetProperty("peca", out var np))
        {
            p.Headline = S(np, "headline") ?? p.Headline; p.Sub = S(np, "sub") ?? p.Sub; p.Cta = S(np, "cta") ?? p.Cta; p.Versao++;
        }
        Avisar();
    }

    /// <summary>Aprovar é decisão de gente (AD-5) e fica gravada: PATCH /api/os/copy/peca/{id} { aprovada }.</summary>
    public async Task AprovarPecaAsync(Cliente c, string pecaId, bool aprovada)
    {
        await Envie(HttpMethod.Patch, $"api/os/copy/peca/{Uri.EscapeDataString(pecaId)}", new { aprovada });
        c.Pecas.First(x => x.Id == pecaId).Aprovada = aprovada; Avisar();
    }

    /* ---- design -------------------------------------------------------------- */

    /* ---- o Agente Designer do V1 ---------------------------------------------
       POST /api/designer/gerar tem dois modos (a rota explica o porquê: cada imagem
       leva de 100 a 150 s, então o lote inteiro numa chamada só não cabe):
         sem `peca`  + nova_geracao  -> abre um lote NOVO do Designer e devolve o plano
                                        (funil + número de cada copy do lote)
         com `peca`  + designer_lote_id -> gera SÓ aquela peça, naquele formato
       A arte entra na galeria assim que a chamada volta (Avisar), uma por uma.
       A copy identifica-se por (funil, número=ordem); o `head` do plano cobre o
       caso de número divergente. */
    public async Task<IReadOnlyList<Arte>> GerarArtesAsync(Cliente c, IReadOnlyList<string> pecaIds, IReadOnlyList<string> formatos, string? template = null)
    {
        /* "auto" é o padrão do V1 (rodízio de cena): não viaja; os outros ids vão em peca.template */
        var templateId = string.IsNullOrWhiteSpace(template) || template == Design.TemplatesDoDesigner.Auto ? null : template.Trim();
        var fmts = formatos.Select(f => f == "story" ? "story" : "feed").Distinct().ToList();
        if (fmts.Count == 0) fmts.Add("feed");
        var lotes = c.Lotes.Where(l => l.Pecas.Any(p => pecaIds.Contains(p.Id))).ToList();
        if (lotes.Count == 0) throw new InvalidOperationException("escolha pelo menos uma peça de copy aprovada: o Designer desenha em cima do texto dela.");
        var novas = new List<Arte>(); var falhas = new List<string>();
        foreach (var lote in lotes)
        {
            var plano = await Envie(HttpMethod.Post, "api/designer/gerar", new { cliente = c.Slug, lote_id = lote.Id, formato = fmts.Count == 2 ? "ambos" : fmts[0], nova_geracao = true });
            var designerLoteId = S(plano, "lote_id") ?? throw new InvalidOperationException("o Designer não abriu o lote da geração.");
            var noPlano = Lista(plano, "pecas").Select(x => (Funil: S(x, "funil") ?? "", Numero: (int)(D(x, "numero") ?? 0), Head: S(x, "head") ?? "")).ToList();
            foreach (var p in lote.Pecas.Where(p => pecaIds.Contains(p.Id)))
            {
                var alvo = noPlano.FirstOrDefault(x => x.Numero == p.Numero && string.Equals(x.Funil, p.Etapa.ToString(), StringComparison.OrdinalIgnoreCase));
                if (alvo.Funil == "") alvo = noPlano.FirstOrDefault(x => x.Head.Length > 0 && p.Headline.StartsWith(x.Head, StringComparison.Ordinal));
                if (alvo.Funil == "") { falhas.Add($"{p.Etapa} {p.Numero}: essa copy não está no lote que o Designer leu"); continue; }
                foreach (var f in fmts)
                {
                    try
                    {
                        var r = await Envie(HttpMethod.Post, "api/designer/gerar",
                            new { cliente = c.Slug, lote_id = lote.Id, designer_lote_id = designerLoteId, formato = f, peca = new { funil = alvo.Funil, numero = alvo.Numero, formato = f, template = templateId } });
                        foreach (var s in Lista(r, "pecas"))
                        {
                            var formato = S(s, "formato") ?? f;
                            var arte = new Arte
                            {
                                Id = S(s, "id") ?? Guid.NewGuid().ToString("n"), PecaId = p.Id, Etapa = p.Etapa, Formato = formato,
                                Titulo = $"{p.Etapa} {p.Numero} · {formato} · copy lote {lote.Numero}", Url = S(s, "url") ?? "", Lote = lote.Numero, Aprovada = false,
                                Template = templateId ?? Design.TemplatesDoDesigner.Auto,
                            };
                            c.Artes.Insert(0, arte); novas.Add(arte); Avisar();
                        }
                        foreach (var fl in Lista(r, "falhas")) falhas.Add($"{S(fl, "funil")} {D(fl, "numero")} {f}: {S(fl, "motivo")}");
                    }
                    catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException)
                    {
                        falhas.Add($"{p.Etapa} {p.Numero} {f}: {e.Message}");
                    }
                }
            }
        }
        if (novas.Count == 0) throw new InvalidOperationException("nenhuma arte saiu. " + string.Join(" · ", falhas.Distinct()));
        if (falhas.Count > 0) throw new InvalidOperationException($"saíram {novas.Count} arte(s), mas não saíram: {string.Join(" · ", falhas.Distinct())}");
        return novas;
    }

    public Task AprovarArteAsync(Cliente c, string arteId, bool aprovada) { c.Artes.First(a => a.Id == arteId).Aprovada = aprovada; Avisar(); return Task.CompletedTask; }
    public Task EscolherPilotoAsync(Cliente c, Etapa etapa, string? arteId)
    {
        if (arteId is null) c.Pilotos.Remove(etapa); else c.Pilotos[etapa] = arteId;
        Avisar();
        return Task.CompletedTask;
    }

    /* ---- campanha ------------------------------------------------------------ */

    public async Task<ResultadoDaEstrutura> MontarEstruturaAsync(Cliente c)
    {
        var j = await Get($"api/trafego/motor/estrutura?cliente={Uri.EscapeDataString(c.Slug)}");
        if (B(j, "ok") != true) return new(false, null, S(j, "falta"), S(j, "motivo"));
        /* O V1 já devolve a estrutura pronta; para a tela ser uma só, remontamos com o mesmo planejador. */
        if (c.BudgetMensal is > 0)
            return new(true, Estrutura.Montar(c.BudgetMensal.Value, c.Conta.TemLista, c.Conta.TemSiteOuTag, c.TetoMql, c.NichoSlug, Parametros, DateOnly.FromDateTime(DateTime.Now)), null, null);
        return new(false, null, "budget", "sem budget no card Tráfego");
    }

    public async Task<ResultadoDaPublicacao> PublicarAsync(Cliente c, PedidoDePublicacao pedido)
    {
        /* O Publicar do V1 (publicar/start.ts) sobe UM designer_peca_id para todos os conjuntos. Vai o piloto
           de MOFU (toda faixa tem MOFU); os outros pilotos ficam registrados e entram pela troca de criativo. */
        if (Fluxo.Estagios.ValidarAssentos(c, Parametros).FirstOrDefault(a => !a.Ok && a.Arte is not null) is { } errado)
            return new(false, "assento_errado", $"validador de assento: {errado.Motivo}. Troque o piloto de {errado.Vaga} no Design por uma arte gerada pela copy de {errado.Vaga}.", false, null, null);
        var piloto = c.ArtePiloto;
        if (piloto is null) return new(false, "sem_estrutura", "escolha o criativo piloto de MOFU (uma peça do Designer) antes de publicar.", false, null, null);
        var corpo = new
        {
            cliente_slug = c.Slug, designer_peca_id = piloto.Id,
            texto = pedido.Texto is null ? null : new { headline = pedido.Texto.Headline, corpo = pedido.Texto.Corpo, cta = pedido.Texto.Cta },
            politica_url = pedido.PoliticaUrl, link = pedido.Link, formulario_id = pedido.FormularioId,
        };
        var r = await _http.PostAsJsonAsync("api/trafego/motor/publicar", corpo);
        var j = await LerJson(r);
        if (B(j, "ok") == true)
        {
            var pub = new Publicacao { EstruturaHash = S(j, "estrutura_hash") ?? "", CampanhaId = S(j, "campanha_id"), FormularioId = S(j, "formulario_id"), Link = S(j, "link"), Estado = "ok" };
            foreach (var cj in Lista(j, "conjuntos")) pub.Conjuntos.Add(new ConjuntoCriado(S(cj, "nome") ?? "", Enum.TryParse<Etapa>(S(cj, "etapa"), out var et) ? et : Etapa.MOFU, S(cj, "publico") ?? "", S(cj, "adset_id") ?? "", 0));
            foreach (var an in Lista(j, "anuncios")) pub.Anuncios.Add(new AnuncioCriado(S(an, "nome") ?? "", S(an, "adset_id") ?? "", S(an, "ad_id") ?? "", piloto.Id));
            foreach (var av in Lista(j, "avisos")) pub.Avisos.Add(av.GetString() ?? "");
            var outras = c.Pilotos.Where(kv => kv.Value != piloto.Id).Select(kv => kv.Key.ToString()).ToList();
            if (outras.Count > 0)
                pub.Avisos.Add($"o Publicar do V1 sobe um criativo só: os conjuntos de {string.Join(" e ", outras)} nasceram com o piloto de MOFU. Troque o anúncio deles no Gerenciador ou deixe a árvore trocar (TROCAR_CRIATIVO).");
            c.Publicacao = pub; Avisar();
            return new(true, null, null, false, pub, S(j, "recado"));
        }
        return new(false, S(j, "codigo"), S(j, "error") ?? S(j, "motivo") ?? $"o V1 respondeu {(int)r.StatusCode}", B(j, "simulado") == true, null, null);
    }

    /* ---- automação ------------------------------------------------------------ */

    public async Task<Ongoing> LerOngoingAsync(Cliente c)
    {
        var j = await Get($"api/trafego/motor/ongoing?cliente={Uri.EscapeDataString(c.Slug)}");
        var k = j.TryGetProperty("kpis", out var kk) ? kk : default;
        long L(string n) => (long)(D(k, n) ?? 0);
        int I(string n) => (int)(D(k, n) ?? 0);
        var kpis = new KpisDoMes(S(k, "mes") ?? "", L("gastoCentavos"), D(k, "budgetCentavos") is { } bc ? (long)bc : null, I("diasDoMes"), I("diasLidos"), I("leads"), I("avaliados"), I("pendentes"), I("naoMql"), I("mql"), I("mqlA"), I("mqlB"), I("mqlC"),
            D(k, "metaMql") is { } mm ? (int)mm : null, D(k, "custoPorMqlCentavos") is { } cp ? (long)cp : null, D(k, "tetoCentavos") is { } tc ? (long)tc : null, 0);
        var leads = Lista(j, "leads").Select(l => new LeadDaCentral
        {
            Id = S(l, "lead_id") ?? "", CriadoEm = DateTime.TryParse(S(l, "criado_em"), out var dt) ? dt : DateTime.MinValue, AdsetId = S(l, "adset_id"), Conjunto = S(l, "conjunto"),
            Situacao = S(l, "situacao") switch { "mql" => Situacao.Mql, "nao_mql" => Situacao.NaoMql, "pendente" => Situacao.Pendente, _ => null },
            Tier = Enum.TryParse<Tier>(S(l, "tier"), out var t) ? t : null, Motivo = S(l, "motivo"),
            Respostas = Lista(l, "respostas").Select(r => new Resposta(S(r, "pergunta") ?? "", S(r, "resposta") ?? "")).ToList(),
        }).ToList();
        var acoes = Lista(j, "acoes").Select(a => new AcaoDoAgente(DateTime.TryParse(S(a, "quando"), out var q) ? q : DateTime.MinValue, S(a, "tipo") ?? "log", S(a, "texto") ?? "", S(a, "estado"), S(a, "por"), S(a, "motivo"))).ToList();
        return new Ongoing(kpis, leads, acoes, B(j, "semRegua") ?? false, null);
    }

    public async Task<RodadaDoMotor> RodarMotorAsync(Cliente c)
    {
        var j = await Envie(HttpMethod.Post, "api/trafego/motor/recomendacoes", new { cliente_slug = c.Slug });
        var recs = await ListarRecomendacoesAsync(c);
        var naoAgiu = Lista(j, "nao_agiu").Select(x => x.GetString() ?? "").ToList();
        Avisar();
        return new RodadaDoMotor(recs.Where(r => r.Estado == EstadoDaRecomendacao.Pendente).ToList(), (int)(D(j, "gravadas") ?? 0), (int)(D(j, "repetidas") ?? 0), [], naoAgiu, [], S(j, "vault_sha"));
    }

    public async Task<IReadOnlyList<Recomendacao>> ListarRecomendacoesAsync(Cliente c)
    {
        var j = await Get($"api/trafego/motor/recomendacoes?cliente={Uri.EscapeDataString(c.Slug)}");
        c.Recomendacoes.Clear();
        foreach (var r in Lista(j, "recomendacoes"))
        {
            var acao = Enum.TryParse<Acao>(S(r, "acao"), out var a) ? a : Acao.ALERTAR;
            var numeros = r.TryGetProperty("numeros", out var n) ? n : default;
            var decisao = new Decisao(acao, Lista(r, "chaves").Select(x => x.GetString() ?? "").ToList(), [], [],
                new NumerosDaDecisao((double)(D(numeros, "ponderado") ?? 0), (double)(D(numeros, "taxa") ?? 0), D(numeros, "custoCentavos") is { } cc ? (long)cc : null, D(numeros, "multiplo") is { } mu ? (double)mu : null), false, null, false);
            c.Recomendacoes.Add(new Recomendacao
            {
                Id = S(r, "id") ?? "", ClienteSlug = c.Slug, MetaId = S(r, "meta_id") ?? "", Nome = S(numeros, "nome") ?? S(r, "meta_id") ?? "", Acao = acao, Decisao = decisao,
                Texto = S(r, "texto") ?? "", TextoFonte = S(r, "texto_fonte") ?? "trilha", VaultSha = S(r, "vault_sha") ?? "", ContagemMql = S(r, "contagem_mql") ?? "regua",
                Estado = S(r, "estado") switch { "aceita" => EstadoDaRecomendacao.Aceita, "negada" => EstadoDaRecomendacao.Negada, "aplicada" => EstadoDaRecomendacao.Aplicada, "falhou" => EstadoDaRecomendacao.Falhou, "incerto" => EstadoDaRecomendacao.Incerto, _ => EstadoDaRecomendacao.Pendente },
                DecididaPor = S(r, "decidida_por"), Motivo = S(r, "motivo"), DecididaEm = DateTime.TryParse(S(r, "decidida_em"), out var de) ? de : null,
            });
        }
        return c.Recomendacoes.ToList();
    }

    public async Task<Recomendacao> DecidirAsync(Cliente c, string recomendacaoId, bool aceita, string? motivo)
    {
        await Envie(HttpMethod.Post, "api/trafego/motor/decisao", new { recomendacao_id = recomendacaoId, decisao = aceita ? "aceita" : "negada", motivo });
        var recs = await ListarRecomendacoesAsync(c);
        Avisar();
        return recs.First(r => r.Id == recomendacaoId);
    }

    public async Task MudarModoAsync(Cliente c, ModoDeExecucao modo)
    {
        await Envie(HttpMethod.Patch, "api/trafego/motor/execucao", new { cliente_slug = c.Slug, modo = modo.ToString().ToLowerInvariant() });
        c.Modo = modo; Avisar();
    }

    public async Task<(bool Ligada, string? Motivo)> ChaveGeralAsync()
    {
        var j = await Get("api/trafego/motor/execucao");
        var cg = j.TryGetProperty("chave_geral", out var x) ? x : default;
        return (B(cg, "ligada") ?? true, S(cg, "motivo"));
    }

    public async Task MudarChaveGeralAsync(bool ligada, string? motivo)
    {
        await Envie(HttpMethod.Patch, "api/trafego/motor/execucao", new { chave_geral = new { ligada, motivo } });
        Avisar();
    }

    public Task SimularVeiculacaoAsync(Cliente c, int dias) =>
        throw new NotSupportedException("a veiculação é real no V1: ative a campanha no Gerenciador e a carga diária lê a Meta às 07:30 UTC.");

    /* ---- panorama: montado no app sobre a carteira do V1 ------------------------- */
    readonly PanoramaLocal _panorama = new("você");
    public Task<BlueOS.Core.Panorama.PanoramaDoSquad> LerPanoramaAsync(string? squad) => _panorama.MontarAsync(this, squad);
    public Task MarcarVistoAsync(string clienteSlug, string assinatura, bool visto) { _panorama.MarcarVisto(clienteSlug, assinatura, visto); Avisar(); return Task.CompletedTask; }
    public async Task RegistrarACaminhoAsync(string clienteSlug, string acao) { _panorama.RegistrarACaminho(await ListarClientesAsync(), Parametros, clienteSlug, acao); Avisar(); }
}
