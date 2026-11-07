using System.Text.Json;
using BlueOS.Core.Api;
using BlueOS.Core.Modelos;
using BlueOS.Core.Publicar;
using Xunit;

namespace BlueOS.Tests;

/* As regras do Publicar do Start (lib/trafego/publicar/start.ts, pedido.ts, peca.ts, post-da-pagina.ts). */
public class StartRegrasTests
{
    [Fact]
    public void O_hash_da_estrutura_e_o_mesmo_JSON_do_V1_byte_a_byte()
    {
        /* Vetores calculados com JSON.stringify + sha256 (o que o V1 faz): a mesma estrutura tem de achar a mesma
           linha de trafego_start_publicacao, publicada por qualquer um dos dois lados. */
        Assert.Equal("20ddccfb7a66572412bd652fd716088c0a4c36db69e233893310c2e0c636e4b2",
            StartRegras.HashDaEstrutura("9715c94", null, "sem-criativo", null, [("1 · Advantage+ (amplo) · imobiliário", "MOFU", "advantage", 2500, null)]));
        Assert.Equal("264ff54b5a29399acde3a1eb9f9efd371d72b1beae920a666aa6f9f9eb4daf8f",
            StartRegras.HashDaEstrutura("9715c94", "a1,b2", "abc", "f9", [("1 · Advantage+ (amplo)", "MOFU", "advantage", 2500, null), ("2 · Interesses do nicho", "MOFU", "interesses_nicho", 2500, "6003+6004")]));
        Assert.Equal("fa5205d991d442804be3374a9eaecf3bf018de3ebead3ccb7ecc9e515d696229",
            StartRegras.HashDoTexto(new TextoDoAnuncio("Chega de planilha", "O sistema faz por você", "Ver como funciona")));
    }

    [Fact]
    public void Nada_que_mude_com_o_relogio_entra_no_hash_mas_o_interesse_entra()
    {
        var a = StartRegras.HashDaEstrutura("v", null, "t", null, [("1 · X", "MOFU", "advantage", 2500, null)]);
        var b = StartRegras.HashDaEstrutura("v", null, "t", null, [("1 · X", "MOFU", "advantage", 2500, "")]);
        var c = StartRegras.HashDaEstrutura("v", null, "t", null, [("1 · X", "MOFU", "advantage", 2500, "6003")]);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void O_pedido_recusa_na_ordem_politica_link()
    {
        Assert.Equal("sem_politica", StartRegras.FaltaNoPedido(null, null, null)?.Codigo);
        Assert.Equal("sem_link", StartRegras.FaltaNoPedido("cliente.com.br", "https://cliente.com.br/privacidade", null)?.Codigo);
        Assert.Null(StartRegras.FaltaNoPedido(null, "https://cliente.com.br/privacidade", null));
        Assert.Equal("sem_link", StartRegras.FaltaNoPedido(null, "https://www.facebook.com/pagina", null)?.Codigo);
        Assert.Null(StartRegras.FaltaNoPedido(null, null, "123456"));
    }

    [Theory]
    [InlineData("https://cliente.com.br", true)]
    [InlineData("https://m.facebook.com/x", false)]
    [InlineData("https://fb.me/x", false)]
    [InlineData("https://business.facebook.com/x", false)]
    [InlineData("http://www.facebook.com", false)]
    [InlineData("cliente.com.br", false)]
    [InlineData(null, false)]
    public void Todo_dominio_da_Meta_conta_como_a_propria_pagina(string? url, bool externo) => Assert.Equal(externo, StartRegras.EhLinkExterno(url));

    [Fact]
    public void O_destino_do_anuncio_e_o_link_senao_a_politica_senao_a_pagina()
    {
        Assert.Equal("https://a.com", StartRegras.DestinoDoAnuncio("https://a.com", "https://b.com", "1"));
        Assert.Equal("https://b.com", StartRegras.DestinoDoAnuncio(null, "https://b.com", "1"));
        Assert.Equal("https://www.facebook.com/1", StartRegras.DestinoDoAnuncio(null, null, "1"));
    }

    [Fact]
    public void Os_nomes_editados_entram_por_cima_e_o_repetido_nao_passa()
    {
        var rec = new List<(string, string)> { ("1 · A", "AN · 1 · A"), ("2 · B", "AN · 2 · B") };
        var f = StartRegras.NomesFinais("[Blue] X", rec, new NomesEditados(" Minha ", ["  Um  ", null], null));
        Assert.Equal("Minha", f.Campanha);
        Assert.Equal(["Um", "2 · B"], f.Conjuntos);
        Assert.Equal(["AN · 1 · A", "AN · 2 · B"], f.Anuncios);
        Assert.Null(StartRegras.FaltaNosNomes(f));
        Assert.Equal("nome_invalido", StartRegras.FaltaNosNomes(new NomesFinais("c", ["x", "X "], ["a", "b"]))?.Codigo);
        Assert.Contains("vazio", StartRegras.FaltaNosNomes(new NomesFinais("c", ["x", ""], ["a", "b"]))?.Motivo);
        Assert.Contains("passa de 200", StartRegras.FaltaNosNomes(new NomesFinais(new string('x', 201), ["x"], ["a"]))?.Motivo);
    }

    [Fact]
    public void O_nome_do_anuncio_leva_a_etiqueta_e_nunca_passa_de_200()
    {
        var d = StartRegras.DeclaracaoDe("dor", ["negacao"], "fragmento", "saiba_mais");
        Assert.NotNull(d);
        Assert.Equal("AN · 1 · A · H:dor+negacao S:fragmento C:saiba_mais", StartRegras.NomeComEtiqueta("AN · 1 · A", d));
        var longo = StartRegras.NomeComEtiqueta(new string('a', 300), d);
        Assert.True(longo.Length <= 200);
        Assert.EndsWith(" · H:dor+negacao S:fragmento C:saiba_mais", longo);
        Assert.Null(StartRegras.DeclaracaoDe("dor", ["inventada"], "fragmento", "saiba_mais"));
        Assert.Null(StartRegras.DeclaracaoDe("dor", [], "fragmento", null));
        Assert.Equal(200, StartRegras.NomeComEtiqueta(new string('b', 250), null).Length);
    }

    [Fact]
    public void O_formulario_da_regua_tem_os_tres_campos_de_contato_e_uma_pergunta_por_campo_da_regua()
    {
        var regua = new ReguaV2 { Perguntas = [new PerguntaDaRegua { Nome = "cargo", Rotulo = "Qual o seu cargo?", Forte = ["Diretor", "Sócio"], Media = ["Gerente"], Elim = ["Estudante", "Diretor"] }] };
        var perguntas = StartRegras.PerguntasDoFormulario(regua);
        Assert.Equal(4, perguntas.Count);
        Assert.Equal("FULL_NAME", perguntas[0]["type"]);
        Assert.Equal("CUSTOM", perguntas[3]["type"]);
        Assert.Equal("cargo", perguntas[3]["key"]);
        var opcoes = (List<Dictionary<string, string>>)perguntas[3]["options"];
        Assert.Equal(["Diretor", "Sócio", "Gerente", "Estudante"], opcoes.Select(o => o["key"]));
        Assert.Equal("[\U0001F30A Blue] [FORM-L] 30/10 | régua v2", StartRegras.NomeDoFormulario(new DateOnly(2026, 10, 30)));
    }

    [Fact]
    public void O_conjunto_leva_so_a_pagina_no_promoted_object_e_os_interesses_num_grupo_so()
    {
        var c = new ConjuntoProposto("MOFU 2", Etapa.MOFU, "interesses_nicho", "Interesses do nicho", 2500, "2 · Interesses do nicho", "AN · 2", new Idade(25, 55), 0);
        var corpo = StartRegras.CorpoDoConjunto(c, c.Nome, "camp1", "page1", [new InteresseDaMeta("6003", "Marketing")]);
        Assert.Equal("PAUSED", corpo["status"]);
        Assert.Equal("LEAD_GENERATION", corpo["optimization_goal"]);
        Assert.Equal("ON_AD", corpo["destination_type"]);
        Assert.Equal("{\"page_id\":\"page1\"}", corpo["promoted_object"]);
        var t = JsonDocument.Parse(corpo["targeting"]).RootElement;
        Assert.Equal(25, t.GetProperty("age_min").GetInt32());
        Assert.Equal(0, t.GetProperty("targeting_automation").GetProperty("advantage_audience").GetInt32());
        Assert.Equal("6003", t.GetProperty("flexible_spec")[0].GetProperty("interests")[0].GetProperty("id").GetString());
        var semInteresse = StartRegras.CorpoDoConjunto(c, c.Nome, "camp1", "page1", []);
        Assert.False(JsonDocument.Parse(semInteresse["targeting"]).RootElement.TryGetProperty("flexible_spec", out _));
    }

    [Fact]
    public void O_criativo_leva_o_formulario_no_botao_e_as_UTMs_em_url_tags()
    {
        var pub = new PublicacaoPreparada("AN · 1", new TextoDoAnuncio("Head", "Corpo", "Ver"), "SIGN_UP", new LinhaDaPeca(null, null, Etapa.MOFU, "Head", "Corpo", "Ver", "h", null, null));
        var corpo = StartRegras.CorpoDoCriativo("AN · 1", pub, "page1", "hash1", "form1", "https://a.com");
        Assert.Equal(StartRegras.UtmsDoAnuncio, corpo["url_tags"]);
        Assert.Contains("utm_campaign={{campaign.name}}", corpo["url_tags"]);
        var spec = JsonDocument.Parse(corpo["object_story_spec"]).RootElement;
        Assert.Equal("page1", spec.GetProperty("page_id").GetString());
        var ld = spec.GetProperty("link_data");
        Assert.Equal("hash1", ld.GetProperty("image_hash").GetString());
        Assert.Equal("SIGN_UP", ld.GetProperty("call_to_action").GetProperty("type").GetString());
        Assert.Equal("form1", ld.GetProperty("call_to_action").GetProperty("value").GetProperty("lead_gen_form_id").GetString());

        var doPost = StartRegras.CorpoDoCriativoDoPost("AN · 1", "page1_777", "LEARN_MORE", "form1");
        Assert.Equal("page1_777", doPost["object_story_id"]);
        Assert.Contains("\"lead_gen_form_id\":\"form1\"", doPost["call_to_action"]);
    }

    [Fact]
    public void O_post_da_foto_vira_pagina_underline_foto_quando_nao_publicado()
    {
        Assert.Equal("p1_f1", StartRegras.IdDoPostDaFoto("p1", "f1", null));
        Assert.Equal("p1_99", StartRegras.IdDoPostDaFoto("p1", "f1", "p1_99"));
        Assert.Equal("", StartRegras.IdDoPostDaFoto("p1", null, null));
        Assert.Equal("Head\n\nCorpo", StartRegras.LegendaDoPost(" Head ", "Corpo "));
    }

    [Fact]
    public void A_campanha_e_ABO_de_cadastro_pausada_e_o_link_do_Gerenciador_tira_o_act()
    {
        var c = StartRegras.CorpoDaCampanha("[Blue] X");
        Assert.Equal("OUTCOME_LEADS", c["objective"]);
        Assert.Equal("PAUSED", c["status"]);
        Assert.Equal("false", c["is_adset_budget_sharing_enabled"]);
        Assert.Equal("https://adsmanager.facebook.com/adsmanager/manage/campaigns?act=123&selected_campaign_ids=9", StartRegras.LinkDoGerenciador("act_123", "9"));
    }

    [Fact]
    public void A_cola_do_gestor_sai_dos_mesmos_textos_que_subiriam()
    {
        var reg = new RegistroDoStart { ImagemHash = "h0", FormularioId = "f1", Conjuntos = [new ConjuntoCriadoNoStart("1 · A", "MOFU", "advantage", "ad1")] };
        var pub = new PublicacaoPreparada("AN · 1", new TextoDoAnuncio("Head", "Corpo", "Ver"), null, new LinhaDaPeca(null, null, Etapa.MOFU, "Head", "Corpo", "Ver", "h", null, null));
        var cola = StartRegras.CriativoNaMao0(reg, [pub], [("1 · A", "MOFU", "advantage", 2500, null)], "https://a.com", ["h1"]);
        Assert.Equal(StartRegras.AvisoDoCriativoNaMao, cola.Motivo);
        Assert.Single(cola.Anuncios);
        Assert.Equal("ad1", cola.Anuncios[0].AdsetId);
        Assert.Equal("LEARN_MORE", cola.Anuncios[0].Cta);
        Assert.Equal("h1", cola.Anuncios[0].ImagemHash);
        Assert.Equal("Corpo", cola.Anuncios[0].Corpo);
    }
}
