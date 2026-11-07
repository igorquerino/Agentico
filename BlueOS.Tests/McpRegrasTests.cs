using System.Text.Json;
using BlueOS.Core.Api;
using BlueOS.Core.Meta;
using BlueOS.Core.Modelos;
using BlueOS.Core.Publicar;
using Xunit;

namespace BlueOS.Tests;

/* O MCP oficial da Meta sem rede: os argumentos de cada ferramenta e a leitura do envelope JSON-RPC. */
public class McpRegrasTests
{
    static readonly Dictionary<string, object?> Comuns = McpRegras.Comuns("AbCdEfGhIjKlMnOpQrSt", McpRegras.Criacao, "publicar o Start");

    [Fact]
    public void A_conversa_tem_20_letras_ou_digitos_e_muda_a_cada_vez()
    {
        var a = McpRegras.NovaConversa(); var b = McpRegras.NovaConversa();
        Assert.True(McpRegras.ConversaValida(a));
        Assert.True(McpRegras.ConversaValida(b));
        Assert.NotEqual(a, b);
        Assert.False(McpRegras.ConversaValida("curta"));
        Assert.False(McpRegras.ConversaValida("AbCdEfGhIjKlMnOpQr-t"));
    }

    [Fact]
    public void O_squad_escreve_pelo_MCP_quando_esta_na_lista_sem_caixa_nem_espaco()
    {
        Assert.True(McpRegras.SquadPeloMcp("POSEIDON, piratas", "poseidon"));
        Assert.True(McpRegras.SquadPeloMcp("POSEIDON", " Poseidon "));
        Assert.False(McpRegras.SquadPeloMcp("POSEIDON", "GHOST"));
        Assert.False(McpRegras.SquadPeloMcp("", "POSEIDON"));
        Assert.False(McpRegras.SquadPeloMcp("POSEIDON", null));
    }

    [Fact]
    public void A_trava_de_escrita_abre_pela_conta_de_teste_OU_pelo_squad_no_MCP_nunca_sem_a_variavel()
    {
        Assert.False(McpRegras.EscritaLiberada(false, "123", "act_123", "POSEIDON", "POSEIDON"));
        Assert.True(McpRegras.EscritaLiberada(true, "123", "act_123", null, null));
        Assert.True(McpRegras.EscritaLiberada(true, "", "act_999", "POSEIDON", "POSEIDON"));
        Assert.False(McpRegras.EscritaLiberada(true, "", "act_999", "POSEIDON", "GHOST"));
        Assert.False(McpRegras.EscritaLiberada(true, "", "act_999", "", "POSEIDON"));
    }

    [Fact]
    public void A_conta_so_serve_no_token_habilitada_e_ativa()
    {
        Assert.Contains("não está no token", McpRegras.ContaServe(null, "act_1").Motivo);
        Assert.Contains("ainda não está habilitada", McpRegras.ContaServe(new ContaNoMcp("1", "CA", false, "ACTIVE", true, "Ads MCP is gradually being rolled out", null), "1").Motivo);
        Assert.Contains("está DISABLED", McpRegras.ContaServe(new ContaNoMcp("1", "CA", true, "DISABLED", false, null, "sinalizada"), "1").Motivo);
        Assert.True(McpRegras.ContaServe(new ContaNoMcp("1", "CA", true, "ACTIVE", true, null, null), "act_1").Ok);
    }

    [Fact]
    public void A_pagina_precisa_estar_na_conta_e_ter_aceitado_os_termos_de_lead()
    {
        var paginas = new List<PaginaNoMcp> { new("p1", "Leans Pay", false), new("p2", "AZ", true) };
        Assert.Contains("não aparece", McpRegras.PaginaServe(paginas, "p9").Motivo);
        var semTos = McpRegras.PaginaServe(paginas, "p1");
        Assert.False(semTos.Ok); Assert.Contains(McpRegras.LinkDoTosDeLead, semTos.Motivo);
        Assert.True(McpRegras.PaginaServe(paginas, "p2").Ok);
    }

    [Fact]
    public void A_campanha_do_Start_vira_ads_create_campaign_ABO_sem_verba()
    {
        var a = McpRegras.ArgsDaCampanha("act_12", StartRegras.CorpoDaCampanha("[🌊 Blue] x"), Comuns);
        Assert.Equal("12", a["ad_account_id"]);
        Assert.Equal("[🌊 Blue] x", a["campaign_name"]);
        Assert.Equal("OUTCOME_LEADS", a["objective"]);
        Assert.Equal("AUCTION", a["buying_type"]);
        Assert.Equal("[]", a["special_ad_categories"]);
        Assert.False(a.ContainsKey("campaign_daily_budget"));
        Assert.False(a.ContainsKey("is_adset_budget_sharing_enabled"));
        Assert.False(a.ContainsKey("status"));
        Assert.Equal("AbCdEfGhIjKlMnOpQrSt", a["client_conversation_id"]);
        Assert.Equal(true, a["hide_ui"]);
        Assert.Equal(McpRegras.Criacao, a["advertiser_job"]);
    }

    [Fact]
    public void O_conjunto_do_Start_vira_ads_create_ad_set_com_a_verba_inteira_e_o_targeting_em_texto()
    {
        var c = new ConjuntoProposto("MOFU 2", Etapa.MOFU, "interesses_nicho", "Interesses do nicho", 2500, "2 · Interesses do nicho", "AN · 2", new Idade(25, 55), 0);
        var corpo = StartRegras.CorpoDoConjunto(c, c.Nome, "camp1", "page1", [new InteresseDaMeta("6003", "Marketing")]);
        var a = McpRegras.ArgsDoConjunto("12", corpo, Comuns);
        Assert.Equal("camp1", a["campaign_id"]);
        Assert.Equal(c.Nome, a["ad_set_name"]);
        Assert.Equal(2500L, a["daily_budget"]);
        Assert.Equal("LEAD_GENERATION", a["optimization_goal"]);
        Assert.Equal("IMPRESSIONS", a["billing_event"]);
        Assert.Equal("ON_AD", a["destination_type"]);
        Assert.Equal("{\"page_id\":\"page1\"}", a["promoted_object"]);
        Assert.Equal(corpo["targeting"], a["targeting"]);
        Assert.False(a.ContainsKey("status"));
        Assert.False(a.ContainsKey("name"));
    }

    [Fact]
    public void O_anuncio_leva_o_criativo_inline_com_o_formulario_no_botao_e_as_UTMs()
    {
        var pub = new PublicacaoPreparada("AN · 2", new TextoDoAnuncio("Título", "Corpo", "Saiba mais"), "SIGN_UP", new LinhaDaPeca(null, null, Etapa.MOFU, "Título", "Corpo", "Saiba mais", "h", null, null));
        var corpo = StartRegras.CorpoDoCriativo("AN · 2", pub, "page1", "hash1", "form1", "https://site.com");
        var a = McpRegras.ArgsDoAnuncio("act_12", "adset1", "AN · 2", corpo, Comuns);
        Assert.Equal("adset1", a["ad_set_id"]); Assert.Equal("AN · 2", a["ad_name"]);
        var cr = JsonDocument.Parse((string)a["creative"]!).RootElement;
        Assert.Equal("AN · 2 — criativo", cr.GetProperty("name").GetString());
        Assert.Equal(StartRegras.UtmsDoAnuncio, cr.GetProperty("url_tags").GetString());
        var spec = cr.GetProperty("object_story_spec");
        Assert.Equal(JsonValueKind.Object, spec.ValueKind);
        Assert.Equal("page1", spec.GetProperty("page_id").GetString());
        Assert.Equal("form1", spec.GetProperty("link_data").GetProperty("call_to_action").GetProperty("value").GetProperty("lead_gen_form_id").GetString());
        Assert.Equal("hash1", spec.GetProperty("link_data").GetProperty("image_hash").GetString());

        var doPost = McpRegras.ArgsDoAnuncio("12", "adset1", "AN", StartRegras.CorpoDoCriativoDoPost("AN", "page1_post1", "LEARN_MORE", "form1"), Comuns);
        var cp = JsonDocument.Parse((string)doPost["creative"]!).RootElement;
        Assert.Equal("page1_post1", cp.GetProperty("object_story_id").GetString());
        Assert.Equal(JsonValueKind.Object, cp.GetProperty("call_to_action").ValueKind);
    }

    [Fact]
    public void A_atualizacao_usa_os_nomes_da_Graph_e_a_verba_vira_numero()
    {
        var a = McpRegras.ArgsDeAtualizacao("act_12", "ad_set", "999", new Dictionary<string, string> { ["daily_budget"] = "3000", ["status"] = "PAUSED" }, Comuns);
        Assert.Equal("ad_set", a["entity_type"]); Assert.Equal("999", a["entity_id"]); Assert.Equal("12", a["ad_account_id"]);
        var f = JsonDocument.Parse((string)a["fields"]!).RootElement;
        Assert.Equal(3000, f.GetProperty("daily_budget").GetInt64());
        Assert.Equal("PAUSED", f.GetProperty("status").GetString());

        var at = McpRegras.ArgsDeAtivacao("12", "campaign", "c1", ["c1", "s1", "a1"], false, Comuns);
        Assert.Equal(new List<string> { "c1", "s1", "a1" }, at["object_ids"]);
        Assert.Equal(false, at["publish_as_active"]);
        Assert.Equal(false, at["allow_partial_publish"]);
        var aoVivo = McpRegras.ArgsDeAtivacao("12", "ad_set", "s1", null, false, Comuns);
        Assert.False(aoVivo.ContainsKey("object_ids"));
        /* o MCP recusa argumento fora do esquema: hide_ui só existe nas criações e na atualização */
        Assert.False(aoVivo.ContainsKey("hide_ui"));
        Assert.False(McpRegras.ArgsDasContas(null, Comuns).ContainsKey("hide_ui"));
        Assert.False(McpRegras.ArgsDasPaginas("12", Comuns).ContainsKey("hide_ui"));
        Assert.False(McpRegras.ArgsDaImagem("12", "https://x/a.png", "a", Comuns).ContainsKey("hide_ui"));
        Assert.Equal(true, a["hide_ui"]);
    }

    [Fact]
    public void So_pausar_verba_atualizar_e_ativar_vao_pelo_MCP_a_copia_fica_na_Graph()
    {
        var vazio = new Dictionary<string, object?>();
        var pausa = McpRegras.TraduzirAcao("pause_adset", vazio)!.Value;
        Assert.Equal(("ads_update_entity", "ad_set"), (pausa.Ferramenta, pausa.TipoDaEntidade)); Assert.Equal("PAUSED", pausa.Campos["status"]);
        Assert.Equal("campaign", McpRegras.TraduzirAcao("pause_campaign", vazio)!.Value.TipoDaEntidade);
        Assert.Equal("ad", McpRegras.TraduzirAcao("pause_ad", vazio)!.Value.TipoDaEntidade);
        var verba = McpRegras.TraduzirAcao("set_daily_budget", new Dictionary<string, object?> { ["daily_budget_cents"] = 2500 })!.Value;
        Assert.Equal("2500", verba.Campos["daily_budget"]);
        var up = McpRegras.TraduzirAcao("update_adset", new Dictionary<string, object?> { ["daily_budget"] = "3000" })!.Value;
        Assert.Equal("ads_update_entity", up.Ferramenta); Assert.Equal("3000", up.Campos["daily_budget"]);
        Assert.Equal("ads_activate_entity", McpRegras.TraduzirAcao("ativar_copia", vazio)!.Value.Ferramenta);
        Assert.Null(McpRegras.TraduzirAcao("duplicate_adset", vazio));
        Assert.Null(McpRegras.TraduzirAcao("create_adset", vazio));
    }

    [Fact]
    public void O_envelope_de_sucesso_traz_o_JSON_do_texto_ou_o_structuredContent()
    {
        var soTexto = JsonDocument.Parse("""{"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"{\"campaign_id\":\"c1\",\"status\":\"DRAFT\",\"client_conversation_id\":\"AbCdEfGhIjKlMnOpQrSt\"}"}],"isError":false}}""").RootElement;
        var r = McpRegras.Ler(soTexto);
        Assert.True(r.Ok); Assert.Equal("c1", r.S("campaign_id")); Assert.Equal("AbCdEfGhIjKlMnOpQrSt", r.ConversaId);
        var criado = McpRegras.ObjetoCriado(r, "campaign_id");
        Assert.True(criado.Rascunho); Assert.Null(criado.ErrosAtivos);

        var estruturado = JsonDocument.Parse("""{"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"ok"}],"isError":false,"structuredContent":{"ad_id":"a1","status":"PAUSED","active_errors":"[]","ads_manager_url":"https://x"}}}""").RootElement;
        var r2 = McpRegras.Ler(estruturado);
        var ad = McpRegras.ObjetoCriado(r2, "ad_id");
        Assert.Equal("a1", ad.Id); Assert.False(ad.Rascunho); Assert.Null(ad.ErrosAtivos); Assert.Equal("https://x", ad.LinkDoGerenciador);
    }

    [Fact]
    public void O_envelope_de_recusa_vira_erro_com_a_frase_e_a_categoria()
    {
        var isError = JsonDocument.Parse("""{"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"This tool is new and is being gradually rolled out across ad accounts. Please check back at a later date."}],"isError":true}}""").RootElement;
        var r = McpRegras.Ler(isError);
        Assert.False(r.Ok); Assert.Contains("gradually", r.Erro);
        Assert.Equal(ClasseDoErro.Permanente, McpRegras.ClasseDoErroDoMcp(r.Categoria, r.Repetivel, r.Erro));
        Assert.Contains("lançamento gradual", McpRegras.FraseDoMcp(r.Erro));

        var comCategoria = JsonDocument.Parse("""{"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"x"}],"isError":true,"structuredContent":{"success":false,"error_message":"Invalid parameter: daily_budget","error_category":"VALIDATION","is_retryable":false}}}""").RootElement;
        var r2 = McpRegras.Ler(comCategoria);
        Assert.False(r2.Ok); Assert.Equal("VALIDATION", r2.Categoria); Assert.Equal("Invalid parameter: daily_budget", r2.Erro);
        Assert.Equal(ClasseDoErro.Permanente, McpRegras.ClasseDoErroDoMcp(r2.Categoria, r2.Repetivel, r2.Erro));

        var rpc = JsonDocument.Parse("""{"jsonrpc":"2.0","id":7,"error":{"code":-32602,"message":"Unknown tool"}}""").RootElement;
        Assert.Contains("Unknown tool", McpRegras.Ler(rpc).Erro);

        Assert.Equal(ClasseDoErro.Temporario, McpRegras.ClasseDoErroDoMcp("TRANSIENT", true, "try again"));
        Assert.Equal(ClasseDoErro.Credencial, McpRegras.ClasseDoErroDoMcp("PERMISSION", false, "no access"));
        Assert.Equal(ClasseDoErro.Temporario, McpRegras.ClasseDoErroDoMcp(null, false, "User request limit reached: too many calls"));
    }

    [Fact]
    public void As_contas_as_paginas_e_o_hash_da_imagem_sao_lidos_do_que_o_MCP_devolve()
    {
        var contas = JsonDocument.Parse("""{"ad_accounts":[{"ad_account_id":"1","ad_account_name":"CA","is_ads_mcp_enabled":true,"account_status":"ACTIVE","is_queryable":true,"is_ads_mcp_disabled_reason":null,"not_queryable_reason":null},{"ad_account_id":"2","ad_account_name":"CB","is_ads_mcp_enabled":false,"account_status":"DISABLED","is_queryable":false,"is_ads_mcp_disabled_reason":"rollout","not_queryable_reason":"flag"}],"next_cursor":"abc"}""").RootElement;
        var l = McpRegras.Contas(contas);
        Assert.Equal(2, l.Count); Assert.True(l[0].Habilitada); Assert.False(l[1].Habilitada); Assert.Equal("rollout", l[1].MotivoDesabilitada);
        Assert.Equal("abc", McpRegras.ProximoCursor(contas));
        Assert.Null(McpRegras.ProximoCursor(JsonDocument.Parse("""{"ad_accounts":[]}""").RootElement));

        var paginas = McpRegras.Paginas(JsonDocument.Parse("""{"pages":[{"page_id":"p1","page_name":"Leans Pay","leadgen_tos_accepted":false}]}""").RootElement);
        Assert.Single(paginas); Assert.False(paginas[0].TosDeLeadAceito);

        Assert.Equal("h1", McpRegras.HashDaImagem(JsonDocument.Parse("""{"result_kind":"URL_IMAGE","image":{"hash":"h1","url":"u"}}""").RootElement));
        Assert.Equal("h2", McpRegras.HashDaImagem(JsonDocument.Parse("""{"image_hash":"h2"}""").RootElement));
        Assert.Equal("h3", McpRegras.HashDaImagem(JsonDocument.Parse("""{"images":[{"image_hash":"h3"}]}""").RootElement));
        Assert.Null(McpRegras.HashDaImagem(JsonDocument.Parse("""{"result_kind":"LOCAL_FILE"}""").RootElement));
    }
}
