using System.Text.Json;
using BlueOS.Core.Executor;
using BlueOS.Core.Meta;
using BlueOS.Core.Modelos;
using BlueOS.Core.Publicar;
using Xunit;

namespace BlueOS.Tests;

/* As correções da revisão G2 (escrita na Meta): as regras de dinheiro e de retomada que moravam presas na Api, agora
   puras no Core e cobradas aqui. */
public class G2CorrecoesTests
{
    static readonly DateOnly Hoje = new(2026, 11, 21); // novembro tem 30 dias: faltam 10, contando hoje
    static EstadoAoVivo Vivo(string status = "ACTIVE", long? verba = 3000) => new(DateTime.UtcNow, new ConjuntoAoVivo(status, verba), new ContaAoVivo(1, 0), null);

    /* ---- G2-02 / G2-09: a folga do mês, com a estimativa CBO e as cópias que a foto ainda não tem ---- */

    static Folga FolgaCom(params CopiaDoMotor[] copias) => Travas.FolgaDoMes(3_000m,
        [new GastoDoDia("A", new DateOnly(2026, 11, 10), 1_000m)],
        [new FotoDoConjunto("A", "ACTIVE", 5_000), new FotoDoConjunto("B", "PAUSED", 3_000)], copias, Hoje);

    [Fact]
    public void A_folga_e_o_que_sobra_por_dia_menos_as_diarias_ativas_da_foto()
    {
        var f = FolgaCom();
        Assert.Equal(300_000, f.BudgetMensalCentavos);
        Assert.Equal(100_000, f.GastoMesCentavos);
        Assert.Equal(10, f.DiasRestantes);
        Assert.Equal(5_000, f.DiariasAtivasCentavos); // o PAUSED da foto não conta
        Assert.Equal(15_000, f.FolgaDiariaCentavos);
        Assert.Null(Travas.FolgaDoMes(null, [], [], [], Hoje).FolgaDiariaCentavos);
    }

    [Fact]
    public void A_copia_do_motor_fora_da_foto_desconta_a_folga_em_qualquer_status()
    {
        Assert.Equal(11_000, FolgaCom(new CopiaDoMotor("aplicada", "C1", 4_000, true, 3_000)).FolgaDiariaCentavos);
        /* já na foto: vale a regra da foto, não conta duas vezes */
        Assert.Equal(15_000, FolgaCom(new CopiaDoMotor("aplicada", "A", 9_999, true, 3_000)).FolgaDiariaCentavos);
        /* a verba não entrou: a cópia ficou com a diária do original, que é maior que a pedida */
        Assert.Equal(10_000, FolgaCom(new CopiaDoMotor("aplicada", "C2", 3_000, false, 5_000)).FolgaDiariaCentavos);
        /* 'incerto' pode ter criado a cópia (sem id, com a verba do original); 'em_voo' está sendo criada agora */
        Assert.Equal(9_000, FolgaCom(new CopiaDoMotor("incerto", null, null, false, 6_000)).FolgaDiariaCentavos);
        Assert.Equal(12_000, FolgaCom(new CopiaDoMotor("em_voo", null, 3_000, false, null)).FolgaDiariaCentavos);
        /* recusa limpa não criou nada */
        Assert.Equal(15_000, FolgaCom(new CopiaDoMotor("falhou", null, 4_000, false, 6_000)).FolgaDiariaCentavos);
    }

    [Fact]
    public void Duplicar_em_serie_nao_pega_a_folga_inteira_de_novo()
    {
        var fotos = new[] { new FotoDoConjunto("A", "ACTIVE", 15_000) };
        GastoDoDia[] gastos = [new("A", new DateOnly(2026, 11, 10), 1_000m)];
        var antes = Travas.FolgaDoMes(3_000m, gastos, fotos, [], Hoje);
        Assert.Equal(5_000, antes.FolgaDiariaCentavos);
        var primeira = Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 5_000, antes, 0, 0, Vivo(), null);
        Assert.True(primeira.Ok); Assert.Equal(5_000, primeira.CopiaCentavos);

        /* a cópia nasceu PAUSED e a carga ainda não a viu: o segundo DUPLICAR já não tem folga (D2) */
        var depois = Travas.FolgaDoMes(3_000m, gastos, fotos, [new CopiaDoMotor("aplicada", "COPIA1", 5_000, true, 15_000)], Hoje);
        Assert.Equal(0, depois.FolgaDiariaCentavos);
        Assert.Equal(CodigoDaTrava.SemFolga, Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 5_000, depois, 1, 0, Vivo(), null).Codigo);
    }

    [Fact]
    public void O_conjunto_CBO_ativo_entra_pela_media_dos_dias_em_que_gastou_nos_ultimos_7()
    {
        GastoDoDia[] gastos =
        [
            new("X", new DateOnly(2026, 11, 13), 999m), // 8 dias atrás: fora da média, dentro do mês
            new("X", new DateOnly(2026, 11, 18), 30m),
            new("X", new DateOnly(2026, 11, 19), 0m),
            new("X", new DateOnly(2026, 11, 20), 60m),
            new("X", Hoje, 500m), // hoje: fora da média (o dia não acabou), dentro do gasto do mês
        ];
        var f = Travas.FolgaDoMes(3_000m, gastos, [new FotoDoConjunto("X", "ACTIVE", null)], [], Hoje);
        Assert.Equal(4_500, f.DiariasEstimadasCentavos); // (30 + 0 + 60) / 2 dias com gasto
        Assert.Equal(4_500, f.DiariasAtivasCentavos);
        Assert.Equal(158_900, f.GastoMesCentavos);
    }

    /* ---- G2-09: o desfecho (aplicada / falhou / incerto) e a marca `permanente` ---- */

    [Fact]
    public void O_desfecho_so_reabre_a_reserva_na_recusa_limpa()
    {
        var ok = Travas.Desfecho(TipoNaMeta.PauseAdset, true, false, false, false, false, null, 1_000);
        Assert.Equal(("aplicada", (ClasseDoErro?)null, 1_000), (ok.Estado, ok.Classe, ok.AguardarMs));

        var limpa = Travas.Desfecho(TipoNaMeta.PauseAdset, false, false, false, true, false, null, 0);
        Assert.Equal(("falhou", (ClasseDoErro?)ClasseDoErro.Desconhecido), (limpa.Estado, limpa.Classe));

        /* o /copies recusado não ensaia e pode ter criado a cópia: 'incerto', e nunca uma 2ª cópia */
        Assert.Equal("incerto", Travas.Desfecho(TipoNaMeta.DuplicateAdset, false, false, false, true, false, ClasseDoErro.Permanente, 0).Estado);
        /* o ensaio fora da produção não criou nada */
        Assert.Equal("falhou", Travas.Desfecho(TipoNaMeta.DuplicateAdset, false, false, false, true, true, null, 0).Estado);
        /* a trava que recusa sem falar com a Meta (anti-ban) não criou nada */
        var semVerba = Travas.Desfecho(TipoNaMeta.DuplicateAdset, false, false, false, false, false, ClasseDoErro.Permanente, 0);
        Assert.Equal("falhou", semVerba.Estado); Assert.True(semVerba.Permanente);

        var semResposta = Travas.Desfecho(TipoNaMeta.PauseAdset, false, true, true, false, false, null, 0);
        Assert.Equal(("incerto", (ClasseDoErro?)ClasseDoErro.Temporario, 60_000), (semResposta.Estado, semResposta.Classe, semResposta.AguardarMs));
        /* passou no ensaio e falhou valendo: não dá para afirmar que nada mudou */
        Assert.Equal("incerto", Travas.Desfecho(TipoNaMeta.PauseAdset, false, false, true, true, false, ClasseDoErro.Permanente, 0).Estado);
        Assert.False(Travas.Desfecho(TipoNaMeta.PauseAdset, false, false, false, true, false, ClasseDoErro.Temporario, 0).Permanente);
    }

    /* ---- G2-06 / G2-09: o anti-ban de 5×, com a verba do mesmo tipo e falha fechada ---- */

    static Dictionary<string, object?> Diaria(long c) => new() { ["daily_budget_cents"] = c };

    [Fact]
    public void O_salto_de_verba_acima_de_5x_nao_passa_e_sem_leitura_tambem_nao()
    {
        Assert.Equal(GraphRegras.VereditoDaVerba.Passa, GraphRegras.SaltoDeVerba(new Dictionary<string, object?>(), false, null, null).Veredito);
        Assert.Equal(GraphRegras.VereditoDaVerba.SemLeitura, GraphRegras.SaltoDeVerba(Diaria(2_000), false, null, null).Veredito);
        Assert.Equal(GraphRegras.VereditoDaVerba.Passa, GraphRegras.SaltoDeVerba(Diaria(5_000), true, "1000", null).Veredito);
        var salto = GraphRegras.SaltoDeVerba(Diaria(5_001), true, "1000", null);
        Assert.Equal(GraphRegras.VereditoDaVerba.SaltoGrande, salto.Veredito);
        Assert.Contains("+400%", salto.Frase);
    }

    [Fact]
    public void Diaria_pedida_em_conjunto_CBO_ou_vitalicio_e_recusa_que_nao_reabre()
    {
        /* vitalício: a Graph devolve "0" na diária, e o `??` antigo nunca chegava ao vitalício */
        Assert.Equal(GraphRegras.VereditoDaVerba.SemVerbaDoMesmoTipo, GraphRegras.SaltoDeVerba(Diaria(3_000), true, "0", "100000").Veredito);
        /* CBO: nenhuma verba no conjunto */
        Assert.Equal(GraphRegras.VereditoDaVerba.SemVerbaDoMesmoTipo, GraphRegras.SaltoDeVerba(Diaria(3_000), true, null, null).Veredito);
        /* vitalícia pedida compara com a vitalícia */
        var vitalicia = new Dictionary<string, object?> { ["lifetime_budget"] = "40000" };
        Assert.Equal(GraphRegras.VereditoDaVerba.Passa, GraphRegras.SaltoDeVerba(vitalicia, true, "0", "10000").Veredito);
        Assert.Equal(GraphRegras.VereditoDaVerba.SaltoGrande, GraphRegras.SaltoDeVerba(new Dictionary<string, object?> { ["lifetime_budget"] = "60000" }, true, "0", "10000").Veredito);
        Assert.Equal((3_000L, true), GraphRegras.OrcamentoPedidoComTipo(Diaria(3_000))!.Value);
        Assert.Equal((40_000L, false), GraphRegras.OrcamentoPedidoComTipo(vitalicia)!.Value);
    }

    /* ---- G2-09: a composição do hash do pedido do Publicar ---- */

    static ConjuntoProposto Cj(string nome, string publico) => new("MOFU", Etapa.MOFU, publico, publico, 2500, nome, "AN · " + nome, new Idade(25, 55), 0);

    [Fact]
    public void O_hash_do_pedido_compoe_artes_texto_formulario_e_interesses_como_o_V1()
    {
        var conjuntos = new[] { Cj("1 · Advantage+ (amplo)", "advantage"), Cj("2 · Interesses do nicho", "interesses_nicho") };
        IReadOnlyList<IReadOnlyList<InteresseDaMeta>> interesses = [[], [new("6003", "A"), new("6004", "B")]];
        var paraHash = StartRegras.ConjuntosParaHash(conjuntos, interesses);
        Assert.Null(paraHash[0].Interesses);
        Assert.Equal("6003+6004", paraHash[1].Interesses);
        /* o mesmo vetor do V1 de StartRegrasTests: artes "a1,b2", texto "abc", formulário "f9" */
        Assert.Equal("264ff54b5a29399acde3a1eb9f9efd371d72b1beae920a666aa6f9f9eb4daf8f", StartRegras.HashDoPedido("9715c94", ["a1", "b2"], "abc", "f9", paraHash));
        /* sem arte e sem texto: null e "sem-criativo" */
        var umSo = StartRegras.ConjuntosParaHash([Cj("1 · Advantage+ (amplo) · imobiliário", "advantage")], [[]]);
        Assert.Equal("20ddccfb7a66572412bd652fd716088c0a4c36db69e233893310c2e0c636e4b2", StartRegras.HashDoPedido("9715c94", [], null, null, umSo));
        /* a ordem das artes faz parte do pedido */
        Assert.NotEqual(StartRegras.HashDoPedido("v", ["a1", "b2"], "t", null, paraHash), StartRegras.HashDoPedido("v", ["b2", "a1"], "t", null, paraHash));
    }

    /* ---- G2-05: a ativação do anúncio da cópia pelo MCP vai como "ad" ---- */

    [Fact]
    public void A_ativacao_do_anuncio_da_copia_e_entidade_ad_e_a_do_conjunto_ad_set()
    {
        var vazio = new Dictionary<string, object?>();
        Assert.Equal(("ads_activate_entity", "ad"), McpRegras.TraduzirAcao("ativar_copia_ad", vazio) is { } a ? (a.Ferramenta, a.TipoDaEntidade) : default);
        Assert.Equal("ad_set", McpRegras.TraduzirAcao("ativar_copia", vazio)!.Value.TipoDaEntidade);
        var g = GraphRegras.Montar("ativar_copia_ad", "123456789", vazio, "act_1")!.Value;
        Assert.Equal("123456789", g.Caminho); Assert.Equal("ACTIVE", g.Corpo["status"]);
    }

    /* ---- G2-07: resposta da Graph sem corpo de erro e fora de 2xx não é sucesso ---- */

    [Fact]
    public void Status_fora_de_2xx_sem_error_vira_ErroDaMeta()
    {
        Assert.Equal(JsonValueKind.Object, GraphRegras.LerResposta("", 200).ValueKind);
        Assert.True(GraphRegras.LerResposta("{\"success\":true}", 200).GetProperty("success").GetBoolean());
        var vazio5xx = Assert.Throws<ErroDaMeta>(() => GraphRegras.LerResposta("", 503));
        Assert.Equal(ClasseDoErro.Temporario, vazio5xx.Classe);
        Assert.Contains("503", vazio5xx.MensagemAoUsuario);
        Assert.Throws<ErroDaMeta>(() => GraphRegras.LerResposta("{}", 404));
        Assert.Equal(ClasseDoErro.Temporario, Assert.Throws<ErroDaMeta>(() => GraphRegras.LerResposta("<html>bad gateway</html>", 502)).Classe);
        var comErro = Assert.Throws<ErroDaMeta>(() => GraphRegras.LerResposta("{\"error\":{\"message\":\"Invalid parameter\",\"code\":100}}", 400));
        Assert.Equal(100, comErro.Codigo);
    }

    /* ---- G2-10: o envelope SSE do MCP ---- */

    [Fact]
    public void O_envelope_le_JSON_puro_e_o_ultimo_evento_SSE()
    {
        Assert.Equal(7, McpRegras.Envelope("{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}")!.Value.GetProperty("id").GetInt32());
        var dois = "event: message\ndata: {\"id\":1}\n\nevent: message\ndata: {\"id\":2,\"result\":{}}\n\n";
        Assert.Equal(2, McpRegras.Envelope(dois)!.Value.GetProperty("id").GetInt32());
        Assert.Equal(3, McpRegras.Envelope("event: message\r\ndata:{\"id\":3}\r\n\r\n")!.Value.GetProperty("id").GetInt32());
        Assert.Null(McpRegras.Envelope("event: ping\n: comentário\n\n"));
        Assert.Null(McpRegras.Envelope(""));
    }

    [Fact]
    public void O_data_de_varias_linhas_do_mesmo_evento_e_juntado()
    {
        var quebrado = "event: message\ndata: {\"jsonrpc\":\"2.0\",\ndata: \"id\":9,\ndata: \"result\":{\"isError\":false}}\n\n";
        var e = McpRegras.Envelope(quebrado)!.Value;
        Assert.Equal(9, e.GetProperty("id").GetInt32());
        Assert.False(e.GetProperty("result").GetProperty("isError").GetBoolean());
        /* sem a linha em branco final também fecha o evento */
        Assert.Equal(9, McpRegras.Envelope("data: {\"id\":\ndata: 9}")!.Value.GetProperty("id").GetInt32());
        /* um JSON por linha no mesmo evento (fora da especificação): vale o último */
        Assert.Equal(5, McpRegras.Envelope("data: {\"id\":4}\ndata: {\"id\":5}\n\n")!.Value.GetProperty("id").GetInt32());
    }

    /* ---- G2-11: a trava da publicação em voo, por cliente ---- */

    static RegistroDoStart Reg(string estado, string hash, string atualizado, string? campanha = null) =>
        new() { Estado = estado, EstruturaHash = hash, AtualizadoEm = atualizado, CampanhaId = campanha };

    [Fact]
    public void Outra_estrutura_em_curso_ou_pela_metade_barra_a_nova_publicacao()
    {
        var agora = new DateTime(2026, 11, 7, 10, 5, 0, DateTimeKind.Utc);
        const string recente = "2026-11-07 10:00:00.123456+00", velho = "2026-11-07 09:50:00.1+00";
        Assert.Equal(StartRegras.OutraPublicacao.Ignora, StartRegras.SituacaoDeOutra(Reg("criando", "h1", recente), "h1", agora));
        Assert.Equal(StartRegras.OutraPublicacao.EmCurso, StartRegras.SituacaoDeOutra(Reg("criando", "h2", recente), "h1", agora));
        Assert.Equal(StartRegras.OutraPublicacao.PelaMetade, StartRegras.SituacaoDeOutra(Reg("criando", "h2", velho, "c9"), "h1", agora));
        Assert.Equal(StartRegras.OutraPublicacao.Ignora, StartRegras.SituacaoDeOutra(Reg("criando", "h2", velho), "h1", agora));
        Assert.Equal(StartRegras.OutraPublicacao.PelaMetade, StartRegras.SituacaoDeOutra(Reg("parcial", "h2", velho, "c9"), "h1", agora));
        Assert.Equal(StartRegras.OutraPublicacao.Ignora, StartRegras.SituacaoDeOutra(Reg("ok", "h2", recente, "c9"), "h1", agora));
        Assert.True(StartRegras.EmCurso(Reg("criando", "h1", recente), agora));
        Assert.False(StartRegras.EmCurso(Reg("criando", "h1", velho), agora));
        Assert.False(StartRegras.EmCurso(Reg("parcial", "h1", recente), agora));
    }

    /* ---- G2-24: a reconferência compara também o vault ---- */

    [Fact]
    public void A_recomendacao_de_outro_vault_e_obsoleta()
    {
        Assert.False(Travas.Obsoleta("DUPLICAR", ["b", "a"], "abc", "DUPLICAR", ["a", "b"], "abc"));
        Assert.True(Travas.Obsoleta("DUPLICAR", ["a"], "abc", "DUPLICAR", ["a"], "def"));
        Assert.True(Travas.Obsoleta("DUPLICAR", ["a"], null, "DUPLICAR", ["a"], "abc"));
        Assert.True(Travas.Obsoleta("DUPLICAR", ["a"], "abc", "PAUSAR", ["a"], "abc"));
        Assert.True(Travas.Obsoleta("DUPLICAR", ["a"], "abc", "DUPLICAR", ["a", "c"], "abc"));
    }

    /* ---- G2-25: o fuso que caiu no local avisa ---- */

    [Fact]
    public void O_aviso_do_fuso_so_existe_quando_caiu_no_local() =>
        Assert.Equal(Fuso.CaiuNoLocal, Fuso.AvisoDoFuso() is not null);

    /* ---- G2-27: a classe do erro ---- */

    [Fact]
    public void Limites_do_BUC_e_is_transient_sao_temporarios_e_permissao_do_MCP_nao_e_token()
    {
        Assert.Equal(ClasseDoErro.Temporario, GraphRegras.ClasseDe(80001, null));
        Assert.Equal(ClasseDoErro.Temporario, GraphRegras.ClasseDe(80005, null));
        Assert.Equal(ClasseDoErro.Temporario, GraphRegras.ClasseDe(100, "x", transitorio: true));
        Assert.Equal(ClasseDoErro.Credencial, GraphRegras.ClasseDe(190, "x", transitorio: true));
        var transitorio = ErroDaMeta.De(JsonDocument.Parse("{\"message\":\"x\",\"code\":1487000,\"is_transient\":true}").RootElement);
        Assert.Equal(ClasseDoErro.Temporario, transitorio.Classe);
        Assert.Equal(ClasseDoErro.Permanente, McpRegras.ClasseDoErroDoMcp("PERMISSION_DENIED", false, "no access"));
        Assert.Equal(ClasseDoErro.Credencial, McpRegras.ClasseDoErroDoMcp("AUTH_ERROR", false, "expired"));
        Assert.Equal(ClasseDoErro.Credencial, McpRegras.ClasseDoErroDoMcp("INVALID_TOKEN", false, "x"));
    }

    /* ---- G2-31 / G2-37: o código do conjunto pausado e o piso da cópia ---- */

    [Fact]
    public void Conjunto_PAUSED_tem_codigo_proprio()
    {
        var t = Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 3_000, FolgaCom(), 0, 0, Vivo("PAUSED"), null);
        Assert.Equal(CodigoDaTrava.ConjuntoNaoAtivo, t.Codigo);
        Assert.Equal("conjunto_nao_ativo", t.Codigo!.Value.Slug());
    }

    [Fact]
    public void A_copia_abaixo_de_R_20_nao_sai()
    {
        var pedidaBaixa = Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 1_500, FolgaCom(), 0, 0, Vivo(), null);
        Assert.Equal(CodigoDaTrava.CopiaAbaixoDoMinimo, pedidaBaixa.Codigo);
        Assert.Equal("copia_abaixo_do_minimo", pedidaBaixa.Codigo!.Value.Slug());
        Assert.Equal(CodigoDaTrava.CopiaAbaixoDoMinimo, Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, null, FolgaCom(), 0, 0, Vivo(verba: 1_500), null).Codigo);
        var noPiso = Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 2_000, FolgaCom(), 0, 0, Vivo(), null);
        Assert.True(noPiso.Ok); Assert.Equal(2_000, noPiso.CopiaCentavos);
    }

    /* ---- G2-41: a pergunta da régua sem opções é recusada antes da Meta ---- */

    [Fact]
    public void Pergunta_da_regua_sem_opcoes_e_recusada_antes_de_criar_o_formulario()
    {
        var ok = new ReguaV2 { Perguntas = [new PerguntaDaRegua { Nome = "cargo", Rotulo = "Cargo?", Forte = ["Diretor"] }] };
        Assert.Null(StartRegras.FaltaNaRegua(ok));
        var vazia = new ReguaV2 { Perguntas = [ok.Perguntas[0], new PerguntaDaRegua { Nome = "porte", Rotulo = "Porte da empresa?", Forte = ["  "], Media = [], Elim = [""] }] };
        var falta = StartRegras.FaltaNaRegua(vazia);
        Assert.Equal("regua_sem_opcoes", falta?.Codigo);
        Assert.Contains("Porte da empresa?", falta?.Motivo);
    }
}
