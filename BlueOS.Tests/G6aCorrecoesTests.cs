using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueOS.Core.Api;
using BlueOS.Core.Copy;
using BlueOS.Core.Fluxo;
using BlueOS.Core.Modelos;
using BlueOS.Core.Publicar;

namespace BlueOS.Tests;

/* Correções da revisão G6a: os clientes de dados (ApiNetApi, V1Api, FonteDeDados) e o fluxo (Estagios, Condutor).
   O Condutor roda contra uma fonte que RELÊ uma instância nova a cada ObterClienteAsync, como a API .NET faz;
   o ApiNetApi roda contra uma API falsa (um HttpMessageHandler), sem rede. */

/// <summary>A fonte como a API se comporta: cada leitura devolve uma instância NOVA (ida e volta em JSON); as escritas
/// mexem na instância que a tela tem e gravam no "banco". Por baixo, a demonstração faz o trabalho.</summary>
sealed class ApiQueRele : IBlueApi
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    readonly DemoApi _demo = new();
    readonly Dictionary<string, string> _banco = new();

    public int Leituras, CopiasGeradas, ArtesGeradas, Publicacoes, Modos;
    public List<string> ArtesAprovadas { get; } = new();
    /// <summary>O Publicar volta como a API fora do MCP: campanha, formulário e conjuntos, sem anúncio.</summary>
    public bool SemCriativo { get; set; }
    public Action<LoteDeCopy>? DepoisDaCopy { get; set; }
    public Action? DepoisDePublicar { get; set; }
    public Exception? FalhaNaCopy { get; set; }
    public TaskCompletionSource? SegurarLeitura { get; set; }

    static Cliente Copia(Cliente c) => JsonSerializer.Deserialize<Cliente>(JsonSerializer.Serialize(c, Json), Json)!;

    /// <summary>Põe no banco um cliente da demonstração com um slug ÚNICO (o condutor guarda os Starts em curso por slug).</summary>
    public async Task<string> SemearAsync(string slugDaDemo, Action<Cliente>? mexer = null)
    {
        var c = Copia((await _demo.ObterClienteAsync(slugDaDemo))!);
        c.Slug = $"{slugDaDemo}-{Guid.NewGuid():n}";
        mexer?.Invoke(c);
        Gravar(c);
        return c.Slug;
    }

    void Gravar(Cliente c) => _banco[c.Slug] = JsonSerializer.Serialize(c, Json);
    public Cliente DoBanco(string slug) => JsonSerializer.Deserialize<Cliente>(_banco[slug], Json)!;

    public string Origem => "teste: relê como a API";
    public bool EhDemo => false;
    public ParametrosMotor Parametros => _demo.Parametros;
    public event Action? Mudou { add { } remove { } }
    public bool PodeSimularVeiculacao => false;

    public Task<IReadOnlyList<Cliente>> ListarClientesAsync() => Task.FromResult<IReadOnlyList<Cliente>>(_banco.Keys.Select(DoBanco).ToList());
    public async Task<Cliente?> ObterClienteAsync(string slug)
    {
        Leituras++;
        if (SegurarLeitura is { } s) await s.Task;
        return _banco.ContainsKey(slug) ? DoBanco(slug) : null;
    }
    public Task<Cliente> CriarClienteAsync(string nome, string squad) => throw new NotSupportedException();

    public async Task SalvarFichaAsync(Cliente c) { await _demo.SalvarFichaAsync(c); Gravar(c); }
    public async Task SalvarCardTrafegoAsync(Cliente c, string? nichoSlug, decimal? budgetMensal) { await _demo.SalvarCardTrafegoAsync(c, nichoSlug, budgetMensal); Gravar(c); }
    public async Task SalvarCriterioMqlAsync(Cliente c, ReguaV2? regua, decimal? tetoMql) { await _demo.SalvarCriterioMqlAsync(c, regua, tetoMql); Gravar(c); }
    public Task<IReadOnlyList<FormularioDaMeta>> ListarFormulariosAsync(Cliente c) => _demo.ListarFormulariosAsync(c);

    public Task<Asset> SubirAssetAsync(Cliente c, TipoDeAsset tipo, string nome, string mime, byte[] bytes) => throw new NotSupportedException();
    public Task RemoverAssetAsync(Cliente c, string assetId) => throw new NotSupportedException();
    public Task<ResultadoDaConta> ConectarContaAsync(Cliente c, string? adAccountId, string? pageId, string? pageNome) => throw new NotSupportedException();
    public Task SalvarContaAsync(Cliente c) { Gravar(c); return Task.CompletedTask; }
    public Task<EstadoDoToken> ConferirTokenAsync(Cliente c) => _demo.ConferirTokenAsync(c);
    public Task<EstadoDoToken> GravarTokenDoSquadAsync(Cliente c, string token) => throw new NotSupportedException();

    public async Task<LoteDeCopy> GerarCopyAsync(Cliente c, IReadOnlyList<GeradorDemo.Pedido> pedido, string? briefing)
    {
        CopiasGeradas++;
        if (FalhaNaCopy is { } f) throw f;
        var lote = await _demo.GerarCopyAsync(c, pedido, briefing);
        DepoisDaCopy?.Invoke(lote);
        Gravar(c);
        return lote;
    }
    public async Task AjustarPecaAsync(Cliente c, string pecaId, string instrucao) { await _demo.AjustarPecaAsync(c, pecaId, instrucao); Gravar(c); }
    public async Task AprovarPecaAsync(Cliente c, string pecaId, bool aprovada) { await _demo.AprovarPecaAsync(c, pecaId, aprovada); Gravar(c); }

    public async Task<IReadOnlyList<Arte>> GerarArtesAsync(Cliente c, IReadOnlyList<string> pecaIds, IReadOnlyList<string> formatos, string? template = null)
    {
        ArtesGeradas++;
        var novas = await _demo.GerarArtesAsync(c, pecaIds, formatos, template);
        Gravar(c);
        return novas;
    }
    public async Task AprovarArteAsync(Cliente c, string arteId, bool aprovada)
    {
        if (aprovada) ArtesAprovadas.Add(arteId);
        await _demo.AprovarArteAsync(c, arteId, aprovada); Gravar(c);
    }
    public async Task EscolherPilotoAsync(Cliente c, Etapa etapa, string? arteId) { await _demo.EscolherPilotoAsync(c, etapa, arteId); Gravar(c); }

    public Task<ResultadoDaEstrutura> MontarEstruturaAsync(Cliente c) => _demo.MontarEstruturaAsync(c);
    public async Task<ResultadoDaPublicacao> PublicarAsync(Cliente c, PedidoDePublicacao pedido)
    {
        Publicacoes++;
        var r = await _demo.PublicarAsync(c, pedido);
        if (SemCriativo && r.Publicacao is { } pub) { pub.Estado = "sem_criativo"; pub.Anuncios.Clear(); }
        Gravar(c);
        DepoisDePublicar?.Invoke();
        return r;
    }

    public Task<Ongoing> LerOngoingAsync(Cliente c) => _demo.LerOngoingAsync(c);
    public Task<RodadaDoMotor> RodarMotorAsync(Cliente c) => _demo.RodarMotorAsync(c);
    public Task<IReadOnlyList<Recomendacao>> ListarRecomendacoesAsync(Cliente c) => _demo.ListarRecomendacoesAsync(c);
    public Task<Recomendacao> DecidirAsync(Cliente c, string recomendacaoId, bool aceita, string? motivo) => _demo.DecidirAsync(c, recomendacaoId, aceita, motivo);
    public async Task MudarModoAsync(Cliente c, ModoDeExecucao modo) { Modos++; await _demo.MudarModoAsync(c, modo); Gravar(c); }
    public Task<(bool Ligada, string? Motivo)> ChaveGeralAsync() => _demo.ChaveGeralAsync();
    public Task MudarChaveGeralAsync(bool ligada, string? motivo) => _demo.MudarChaveGeralAsync(ligada, motivo);

    public Task<BlueOS.Core.Panorama.PanoramaDoSquad> LerPanoramaAsync(string? squad) => throw new NotSupportedException();
    public Task MarcarVistoAsync(string clienteSlug, string assinatura, bool visto) => Task.CompletedTask;
    public Task RegistrarACaminhoAsync(string clienteSlug, string acao) => Task.CompletedTask;
    public Task SimularVeiculacaoAsync(Cliente c, int dias) => throw new NotSupportedException();
}

/// <summary>A API .NET falsa: responde no lugar do servidor, sem rede.</summary>
sealed class ApiFalsa(Func<HttpRequestMessage, HttpResponseMessage> responda) : HttpMessageHandler
{
    public List<string> Rotas { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Rotas.Add($"{req.Method} {req.RequestUri!.AbsolutePath}");
        return Task.FromResult(responda(req));
    }
    public static HttpResponseMessage Json(HttpStatusCode status, object corpo) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(corpo, ApiQueRele.Json), Encoding.UTF8, "application/json") };
    /// <summary>O que o HttpClient lança quando o Timeout estoura.</summary>
    public static HttpResponseMessage Timeout() => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout elapsing.");
}

public class G6aCondutorTests
{
    static PlanoDoStart Plano(Cliente c, ModoDeExecucao modo = ModoDeExecucao.Sombra)
    {
        var s = Condutor.Sugerir(c, null);
        return new PlanoDoStart(s.NichoSlug, s.BudgetMensal, s.Regua, s.TetoMql, modo);
    }

    static PecaDeCopy Peca(string id, Etapa et, bool aprovada, TipoPeca tipo = TipoPeca.Estatico) =>
        new() { Id = id, Etapa = et, Tipo = tipo, Numero = 1, Headline = $"headline {id}", Sub = $"sub {id}", Cta = "Saiba mais", Aprovada = aprovada };

    static Arte ArteDe(string id, string pecaId, Etapa et, bool aprovada) =>
        new() { Id = id, PecaId = pecaId, Etapa = et, Formato = "feed", Titulo = id, Url = "data:image/svg+xml;base64,", Aprovada = aprovada };

    [Fact]
    public async Task Com_a_releitura_trocando_a_instancia_o_Start_chega_ao_modo_e_grava_o_plano_editado()
    {
        var api = new ApiQueRele();
        var slug = await api.SemearAsync("horizonte-imoveis");
        var c = api.DoBanco(slug);
        var s = Condutor.Sugerir(c, null);
        /* o gestor editou o plano no card: budget de f1 (só MOFU) e outro teto */
        var plano = new PlanoDoStart(s.NichoSlug, 3000m, s.Regua, 55m, ModoDeExecucao.Sombra);
        var vistos = new List<Cliente>();
        await new Condutor(api).ConduzirAsync(c, plano, x => vistos.Add(x));

        Assert.Equal(PassoDoStart.Pronto, c.Automacao.Passo);
        Assert.Null(c.Automacao.PrecisaDeVoce);
        Assert.False(c.Automacao.EmCurso);
        /* a troca de instância: o condutor seguiu na relida, com o MESMO estado do Start */
        Assert.Contains(vistos, x => !ReferenceEquals(x, c));
        Assert.All(vistos, x => Assert.Same(c.Automacao, x.Automacao));
        var banco = api.DoBanco(slug);
        Assert.Equal(3000m, banco.BudgetMensal);
        Assert.Equal(55m, banco.TetoMql);
        Assert.Equal(ModoDeExecucao.Sombra, banco.Modo);
        Assert.Equal(Estagio.Automacao, Estagios.DoCliente(banco, api.Parametros));
        Assert.Null(Condutor.EstadoEmCurso(slug));
    }

    [Fact]
    public async Task Sem_a_politica_o_Start_para_antes_de_publicar_e_nao_liga_o_agente()
    {
        var api = new ApiQueRele();
        var slug = await api.SemearAsync("horizonte-imoveis", c => { c.Conta.PoliticaUrl = null; c.Conta.FormularioId = null; });
        var c = api.DoBanco(slug);
        await new Condutor(api).ConduzirAsync(c, Plano(c));

        Assert.True(c.Automacao.Parou);
        Assert.Contains("política de privacidade", c.Automacao.PrecisaDeVoce);
        Assert.Equal(0, api.Publicacoes);
        Assert.Equal(0, api.Modos);
    }

    [Fact]
    public async Task Sem_assets_o_Start_para_antes_de_qualquer_geracao_paga()
    {
        var api = new ApiQueRele();
        var slug = await api.SemearAsync("horizonte-imoveis", c => c.Conta.PageId = null);
        var c = api.DoBanco(slug);
        await new Condutor(api).ConduzirAsync(c, Plano(c));

        Assert.True(c.Automacao.Parou);
        Assert.StartsWith("assets do cliente", c.Automacao.PrecisaDeVoce);
        Assert.Contains("página do Facebook", c.Automacao.PrecisaDeVoce);
        Assert.Equal(0, api.CopiasGeradas);
        Assert.Equal(0, api.ArtesGeradas);
    }

    [Fact]
    public async Task Copy_reprovada_no_crivo_nao_e_aprovada_e_o_Start_para_na_copy()
    {
        var api = new ApiQueRele
        {
            DepoisDaCopy = lote => { foreach (var p in lote.Pecas.Where(x => x.Etapa == Etapa.BOFU)) { p.CrivoOk = false; p.MotivoDoCrivo = "promessa sem prova"; } },
        };
        var slug = await api.SemearAsync("horizonte-imoveis");   /* R$ 4.500: f2, MOFU e BOFU */
        var c = api.DoBanco(slug);
        await new Condutor(api).ConduzirAsync(c, Plano(c));

        Assert.True(c.Automacao.Parou);
        Assert.Contains("a copy de BOFU não passou no crivo", c.Automacao.PrecisaDeVoce);
        var banco = api.DoBanco(slug);
        Assert.DoesNotContain(banco.Pecas, p => p.Etapa == Etapa.BOFU && p.Aprovada);
        Assert.Contains(banco.Pecas, p => p.Etapa == Etapa.MOFU && p.Aprovada && p.CrivoOk);
        Assert.Equal(0, api.ArtesGeradas);
    }

    [Fact]
    public async Task O_piloto_so_sai_de_arte_aprovada_com_copy_aprovada_e_estatica_e_arte_reprovada_nunca_e_aprovada_sozinha()
    {
        var api = new ApiQueRele();
        var slug = await api.SemearAsync("horizonte-imoveis", c =>
        {
            c.Lotes.Add(new LoteDeCopy
            {
                Id = "lote-t", Numero = 1,
                Pecas = [Peca("m1", Etapa.MOFU, true), Peca("m2", Etapa.MOFU, false), Peca("m3", Etapa.MOFU, true, TipoPeca.Video), Peca("b1", Etapa.BOFU, true)],
            });
            c.Artes.AddRange(
            [
                ArteDe("arte-copy-desaprovada", "m2", Etapa.MOFU, true),
                ArteDe("arte-biblioteca", "biblioteca-1", Etapa.MOFU, true),
                ArteDe("arte-de-video", "m3", Etapa.MOFU, true),
                ArteDe("arte-reprovada", "m1", Etapa.MOFU, false),
                ArteDe("arte-bofu-ok", "b1", Etapa.BOFU, true),
            ]);
        });
        var c = api.DoBanco(slug);
        await new Condutor(api).ConduzirAsync(c, Plano(c));

        Assert.Equal(PassoDoStart.Pronto, c.Automacao.Passo);
        Assert.Equal(0, api.CopiasGeradas);
        Assert.Equal(1, api.ArtesGeradas);   /* só a de MOFU: nenhuma arte existente servia */
        var banco = api.DoBanco(slug);
        var mofu = banco.PilotoDa(Etapa.MOFU)!;
        Assert.DoesNotContain(mofu.Id, new[] { "arte-copy-desaprovada", "arte-biblioteca", "arte-de-video", "arte-reprovada" });
        Assert.Equal("m1", mofu.PecaId);
        Assert.Equal("arte-bofu-ok", banco.Pilotos[Etapa.BOFU]);
        Assert.DoesNotContain("arte-reprovada", api.ArtesAprovadas);
        Assert.False(banco.Artes.Single(a => a.Id == "arte-reprovada").Aprovada);
    }

    [Fact]
    public async Task Peca_de_video_aprovada_nao_fecha_a_copy_e_o_Designer_desenha_sobre_copy_estatica()
    {
        var api = new ApiQueRele();
        var slug = await api.SemearAsync("horizonte-imoveis", c =>
            c.Lotes.Add(new LoteDeCopy { Id = "lote-v", Numero = 1, Pecas = [Peca("v1", Etapa.MOFU, true, TipoPeca.Video), Peca("v2", Etapa.BOFU, true, TipoPeca.Video)] }));
        var c = api.DoBanco(slug);
        Assert.Equal(Estagio.Copy, Estagios.DoCliente(c, api.Parametros));
        Assert.Contains("peça aprovada de MOFU", Estagios.CopyPronta(c, api.Parametros).Pendencias);

        await new Condutor(api).ConduzirAsync(c, Plano(c));

        Assert.Equal(PassoDoStart.Pronto, c.Automacao.Passo);
        Assert.Equal(1, api.CopiasGeradas);
        var banco = api.DoBanco(slug);
        foreach (var et in new[] { Etapa.MOFU, Etapa.BOFU })
            Assert.Equal(TipoPeca.Estatico, banco.Pecas.Single(p => p.Id == banco.PilotoDa(et)!.PecaId).Tipo);
    }

    [Fact]
    public async Task Sem_criativo_o_Start_para_pedindo_os_anuncios_e_a_releitura_nao_publica_de_novo()
    {
        var api = new ApiQueRele { SemCriativo = true };
        var slug = await api.SemearAsync("horizonte-imoveis");
        var c = api.DoBanco(slug);
        var plano = Plano(c);
        await new Condutor(api).ConduzirAsync(c, plano);

        Assert.True(c.Automacao.Parou);
        Assert.StartsWith("subir os anúncios no Gerenciador", c.Automacao.PrecisaDeVoce);
        Assert.DoesNotContain(c.Automacao.Trilha, t => t.Contains("campanha publicada"));
        Assert.Equal(1, api.Publicacoes);
        Assert.Equal(0, api.Modos);

        /* relido do banco: "sem_criativo" com a campanha conta como publicada */
        var relido = api.DoBanco(slug);
        Assert.Equal("sem_criativo", relido.Publicacao!.Estado);
        Assert.True(Estagios.CampanhaPublicada(relido));
        Assert.Equal(Estagio.Automacao, Estagios.DoCliente(relido, api.Parametros));

        /* o Start seguinte vai direto ao modo, sem uma 2ª campanha */
        await new Condutor(api).ConduzirAsync(relido, plano);
        Assert.Equal(PassoDoStart.Pronto, relido.Automacao.Passo);
        Assert.Equal(1, api.Publicacoes);
        Assert.Equal(1, api.Modos);
    }

    [Fact]
    public async Task Um_segundo_Start_do_mesmo_cliente_e_recusado_e_a_carteira_relida_mostra_o_Start_em_curso()
    {
        var api = new ApiQueRele();
        var slug = await api.SemearAsync("horizonte-imoveis");
        var c1 = api.DoBanco(slug);
        var c2 = api.DoBanco(slug);   /* a carteira relida ao voltar à página: outra instância, Automacao vazia */
        var plano = Plano(c1);
        var portao = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.SegurarLeitura = portao;

        var primeiro = new Condutor(api).ConduzirAsync(c1, plano);
        Assert.True(c1.Automacao.EmCurso);
        Assert.Same(c1.Automacao, Condutor.EstadoEmCurso(slug));

        var segundo = new Condutor(api).ConduzirAsync(c2, plano);
        Assert.True(segundo.IsCompleted);
        Assert.Same(c1.Automacao, c2.Automacao);
        Assert.Equal(1, api.Leituras);

        /* a API .NET, ao reler a carteira, devolve o cliente com o Start vivo */
        var handler = new ApiFalsa(_ => ApiFalsa.Json(HttpStatusCode.OK, new[] { api.DoBanco(slug) }));
        using (var net = new ApiNetApi("http://api.teste", null, handler))
        {
            var relida = (await net.ListarClientesAsync()).Single();
            Assert.True(relida.Automacao.EmCurso);
            Assert.Same(c1.Automacao, relida.Automacao);
        }

        portao.SetResult();
        await primeiro;
        Assert.Equal(PassoDoStart.Pronto, c1.Automacao.Passo);
        Assert.Equal(1, api.CopiasGeradas);
        Assert.Null(Condutor.EstadoEmCurso(slug));
    }

    [Fact]
    public async Task Cancelar_depois_de_publicar_nao_muda_o_modo()
    {
        using var cts = new CancellationTokenSource();
        var api = new ApiQueRele { DepoisDePublicar = () => cts.Cancel() };
        var slug = await api.SemearAsync("horizonte-imoveis");
        var c = api.DoBanco(slug);
        await new Condutor(api).ConduzirAsync(c, Plano(c), ct: cts.Token);

        Assert.Equal("Start cancelado", c.Automacao.PrecisaDeVoce);
        Assert.Equal(1, api.Publicacoes);
        Assert.Equal(0, api.Modos);
    }

    [Fact]
    public async Task Timeout_da_fonte_nao_vira_Start_cancelado()
    {
        var api = new ApiQueRele { FalhaNaCopy = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.") };
        var slug = await api.SemearAsync("horizonte-imoveis");
        var c = api.DoBanco(slug);
        await new Condutor(api).ConduzirAsync(c, Plano(c));

        Assert.True(c.Automacao.Parou);
        Assert.NotEqual("Start cancelado", c.Automacao.PrecisaDeVoce);
        Assert.Contains("HttpClient.Timeout", c.Automacao.PrecisaDeVoce);
    }
}

public class G6aEstagiosTests
{
    [Fact]
    public void Sem_criativo_com_campanha_conta_como_publicada_e_sem_campanha_nao()
    {
        var c = new Cliente { Id = "1", Slug = "x", Nome = "X" };
        c.Publicacao = new Publicacao { EstruturaHash = "h", Estado = "sem_criativo", CampanhaId = "120" };
        Assert.True(Estagios.CampanhaPublicada(c));
        c.Publicacao = new Publicacao { EstruturaHash = "h", Estado = "sem_criativo", CampanhaId = null };
        Assert.False(Estagios.CampanhaPublicada(c));
        c.Publicacao = new Publicacao { EstruturaHash = "h", Estado = "parcial", CampanhaId = "120" };
        Assert.False(Estagios.CampanhaPublicada(c));
        c.Publicacao = new Publicacao { EstruturaHash = "h", Estado = "ok", CampanhaId = "120" };
        Assert.True(Estagios.CampanhaPublicada(c));
    }

    [Fact]
    public void Copy_pronta_pede_peca_estatica()
    {
        var p = ParametrosMotor.PadraoDoVault;
        var c = new Cliente { Id = "1", Slug = "x", Nome = "X" };   /* sem budget: a etapa exigida é MOFU */
        var lote = new LoteDeCopy { Id = "l", Numero = 1 };
        lote.Pecas.Add(new PecaDeCopy { Id = "v", Etapa = Etapa.MOFU, Tipo = TipoPeca.Video, Aprovada = true });
        c.Lotes.Add(lote);
        Assert.False(Estagios.CopyPronta(c, p).Ok);
        lote.Pecas.Add(new PecaDeCopy { Id = "e", Etapa = Etapa.MOFU, Tipo = TipoPeca.Estatico, Aprovada = true });
        Assert.True(Estagios.CopyPronta(c, p).Ok);
    }
}

public class G6aApiNetApiTests
{
    const string Base = "http://api.teste";

    static Cliente Cli(string slug) => new()
    {
        Id = "id-" + slug, Slug = slug, Nome = "Cliente " + slug,
        Conta = new ContaDeAnuncios { AdAccountId = "123456789", PageId = "998877" },
        Artes = [new Arte { Id = "arte1", PecaId = "p1", Etapa = Etapa.MOFU, Formato = "feed", Titulo = "a1", Url = "u", Aprovada = true }],
    };

    static HttpResponseMessage Carteira(HttpRequestMessage req, string slug) =>
        req.RequestUri!.AbsolutePath == "/api/carteira" ? ApiFalsa.Json(HttpStatusCode.OK, new[] { Cli(slug) }) : ApiFalsa.Json(HttpStatusCode.OK, Cli(slug));

    [Fact]
    public async Task Reler_a_carteira_nao_apaga_o_piloto_nem_a_politica_digitados_na_sessao()
    {
        using var api = new ApiNetApi(Base, null, new ApiFalsa(req => Carteira(req, "cli-a")));
        var c1 = (await api.ListarClientesAsync()).Single();
        await api.EscolherPilotoAsync(c1, Etapa.MOFU, "arte1");
        c1.Conta.PoliticaUrl = "https://cliente.com.br/privacidade";
        await api.SalvarContaAsync(c1);

        var c2 = (await api.ListarClientesAsync()).Single();
        Assert.NotSame(c1, c2);
        Assert.Equal("arte1", c2.Pilotos[Etapa.MOFU]);
        Assert.Equal("https://cliente.com.br/privacidade", c2.Conta.PoliticaUrl);
        var c3 = (await api.ObterClienteAsync("cli-a"))!;
        Assert.Equal("arte1", c3.Pilotos[Etapa.MOFU]);
        Assert.Equal("https://cliente.com.br/privacidade", c3.Conta.PoliticaUrl);
    }

    [Fact]
    public async Task Arte_reprovada_deixa_de_ser_piloto_e_continua_reprovada_na_releitura()
    {
        using var api = new ApiNetApi(Base, null, new ApiFalsa(req => Carteira(req, "cli-b")));
        var c1 = (await api.ListarClientesAsync()).Single();
        await api.EscolherPilotoAsync(c1, Etapa.MOFU, "arte1");
        await api.AprovarArteAsync(c1, "arte1", false);
        Assert.False(c1.Pilotos.ContainsKey(Etapa.MOFU));

        var c2 = (await api.ListarClientesAsync()).Single();   /* a carteira da API marca toda arte como aprovada */
        Assert.False(c2.Artes.Single().Aprovada);
        await api.AprovarArteAsync(c2, "arte1", true);
        var c3 = (await api.ListarClientesAsync()).Single();
        Assert.True(c3.Artes.Single().Aprovada);
    }

    [Fact]
    public async Task Publicar_recusado_com_error_traz_o_texto_da_API_e_o_status()
    {
        using var api = new ApiNetApi(Base, null, new ApiFalsa(_ => ApiFalsa.Json(HttpStatusCode.Forbidden, new { error = "publicar na Meta é de head pra cima" })));
        var r = await api.PublicarAsync(Cli("cli-c"), new PedidoDePublicacao("https://x/p", null, null, null));
        Assert.False(r.Ok);
        Assert.Equal("publicar na Meta é de head pra cima", r.Motivo);
        Assert.Equal("http_403", r.Codigo);
    }

    [Fact]
    public async Task Publicar_ok_credita_cada_anuncio_ao_piloto_da_etapa_do_conjunto()
    {
        var res = new ResultadoDoStart
        {
            Ok = true, CampanhaId = "c1", FormularioId = "f1", EstruturaHash = "h",
            Conjuntos = [new("MOFU 1", "MOFU", "aberto", "adset-m"), new("BOFU 1", "BOFU", "lista", "adset-b")],
            Anuncios = [new("ad MOFU", "adset-m", "ad-1", false, null), new("ad BOFU", "adset-b", "ad-2", false, null)],
        };
        using var api = new ApiNetApi(Base, null, new ApiFalsa(_ => ApiFalsa.Json(HttpStatusCode.OK, res)));
        var c = Cli("cli-d");
        c.Pilotos[Etapa.MOFU] = "arte-m"; c.Pilotos[Etapa.BOFU] = "arte-b";
        var r = await api.PublicarAsync(c, new PedidoDePublicacao("https://x/p", null, null, null));
        Assert.True(r.Ok);
        Assert.Equal("ok", r.Publicacao!.Estado);
        Assert.Equal("arte-m", r.Publicacao.Anuncios.Single(a => a.AdsetId == "adset-m").PecaId);
        Assert.Equal("arte-b", r.Publicacao.Anuncios.Single(a => a.AdsetId == "adset-b").PecaId);
    }

    [Fact]
    public async Task Publicar_sem_criativo_grava_o_estado_sem_criativo()
    {
        var res = new ResultadoDoStart { Ok = true, SemCriativo = true, CampanhaId = "c1", FormularioId = "f1", EstruturaHash = "h", Conjuntos = [new("MOFU 1", "MOFU", "aberto", "adset-m")] };
        using var api = new ApiNetApi(Base, null, new ApiFalsa(_ => ApiFalsa.Json(HttpStatusCode.OK, res)));
        var c = Cli("cli-e");
        var r = await api.PublicarAsync(c, new PedidoDePublicacao("https://x/p", null, null, null));
        Assert.True(r.Ok);
        Assert.Equal("sem_criativo", c.Publicacao!.Estado);
        Assert.True(Estagios.CampanhaPublicada(c));
    }

    [Fact]
    public async Task Timeout_ao_publicar_avisa_que_o_estado_na_Meta_e_incerto()
    {
        using var api = new ApiNetApi(Base, null, new ApiFalsa(_ => ApiFalsa.Timeout()));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => api.PublicarAsync(Cli("cli-f"), new PedidoDePublicacao("https://x/p", null, null, null)));
        Assert.Contains("INCERTO", e.Message);
        Assert.Contains("Gerenciador", e.Message);
    }

    [Fact]
    public async Task Timeout_ao_aceitar_decisao_avisa_que_o_estado_na_Meta_e_incerto()
    {
        using var api = new ApiNetApi(Base, null, new ApiFalsa(_ => ApiFalsa.Timeout()));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => api.DecidirAsync(Cli("cli-g"), "rec-1", true, null));
        Assert.Contains("INCERTO", e.Message);
    }

    [Fact]
    public async Task Timeout_numa_escrita_simples_vira_mensagem_e_nao_cancelamento()
    {
        using var api = new ApiNetApi(Base, null, new ApiFalsa(_ => ApiFalsa.Timeout()));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => api.MudarModoAsync(Cli("cli-h"), ModoDeExecucao.Sombra));
        Assert.Contains("não respondeu em 60 s", e.Message);
    }

    [Fact]
    public async Task Decisao_aplicada_que_nao_volta_na_lista_nao_lanca_depois_da_escrita()
    {
        using var api = new ApiNetApi(Base, null, new ApiFalsa(req => req.Method == HttpMethod.Post
            ? ApiFalsa.Json(HttpStatusCode.OK, new { ok = true, decisao = "aplicada", aplicou = true })
            : ApiFalsa.Json(HttpStatusCode.OK, new { ok = true, recomendacoes = Array.Empty<object>() })));
        var c = Cli("cli-i");
        var antes = new Recomendacao
        {
            Id = "rec-antiga", ClienteSlug = c.Slug, MetaId = "adset-9", Nome = "conjunto 9", Acao = Acao.PAUSAR,
            Decisao = new Decisao(Acao.PAUSAR, [], [], [], new NumerosDaDecisao(0, 0, null, null), false, null, false),
        };
        c.Recomendacoes.Add(antes);
        var rec = await api.DecidirAsync(c, "rec-antiga", true, null);
        Assert.Same(antes, rec);
        Assert.Equal(EstadoDaRecomendacao.Aplicada, rec.Estado);

        var semAntes = await api.DecidirAsync(Cli("cli-j"), "rec-sumida", true, null);
        Assert.Equal("rec-sumida", semAntes.Id);
        Assert.Equal(EstadoDaRecomendacao.Aplicada, semAntes.Estado);
    }
}
