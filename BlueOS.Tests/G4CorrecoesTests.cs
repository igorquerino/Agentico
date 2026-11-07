using System.Globalization;
using System.Text;
using System.Text.Json;
using BlueOS.Api.Dominio;
using BlueOS.Api.Infra;
using BlueOS.Core.Cs;
using BlueOS.Core.Modelos;
using Xunit;

namespace BlueOS.Tests;

/* As correções da revisão G4 (núcleo da API), sem banco e sem rede: a trava de produção, o Sql, o escopo e os
   cargos, a sessão, o cargo que só lê e as regras puras das escritas e do setup. */
public class G4CorrecoesTests
{
    const string UrlHomologacao = "https://yurhgwqilbgtkvoymyab.supabase.co";

    /* ---- E04: poder decide pelo cargo, nunca pelo escopo global ------------------------------------ */

    [Theory]
    [InlineData("rh")]
    [InlineData("financeiro")]
    [InlineData("onboarding_manager")]
    public void Global_sem_cargo_de_escrita_ve_tudo_mas_nao_aplica_nem_edita_mql(string cargo)
    {
        var esc = new EscopoDaSessao("u", true, null, [cargo]);
        Assert.False(esc.PodeAplicarNaMeta);
        Assert.False(esc.PodeEditarMql);
    }

    [Theory]
    [InlineData("head", true, true)]
    [InlineData("head_departamento", true, true)]
    [InlineData("coordenador", true, true)]
    [InlineData("super_admin", true, true)]
    [InlineData("gestor", false, true)]
    [InlineData("operador", false, true)]
    [InlineData("designer", false, false)]
    public void Aplicar_na_meta_e_editar_mql_decidem_pelo_cargo(string cargo, bool aplica, bool editaMql)
    {
        foreach (var global in new[] { true, false })
        {
            var esc = new EscopoDaSessao("u", global, global ? null : ["s1"], [cargo]);
            Assert.Equal(aplica, esc.PodeAplicarNaMeta);
            Assert.Equal(editaMql, esc.PodeEditarMql);
        }
    }

    /* ---- E07: criar cliente é tier <= 2 ------------------------------------------------------------ */

    [Theory]
    [InlineData("coordenador", true)]
    [InlineData("diretor", true)]
    [InlineData("head", false)]
    [InlineData("head_departamento", false)]
    [InlineData("rh", false)]
    public void Criar_cliente_e_de_coordenador_pra_cima(string cargo, bool pode) =>
        Assert.Equal(pode, CsRegras.PodeCriarCliente([cargo]));

    /* ---- E05: o cargo que só lê ---------------------------------------------------------------------- */

    [Fact]
    public void Somente_leitura_pega_so_quem_nao_tem_outro_cargo()
    {
        Assert.True(Escopo.SomenteLeituraPelosPapeis(["rh"]));
        Assert.True(Escopo.SomenteLeituraPelosPapeis([" RH "]));
        Assert.False(Escopo.SomenteLeituraPelosPapeis(["rh", "gestor"]));
        Assert.False(Escopo.SomenteLeituraPelosPapeis([]));
        Assert.False(Escopo.SomenteLeituraPelosPapeis(null));
        Assert.True(new EscopoDaSessao("u", true, null, ["rh"]).SomenteLeitura);
    }

    [Theory]
    [InlineData("POST", true)] [InlineData("put", true)] [InlineData("PATCH", true)] [InlineData("DELETE", true)]
    [InlineData("GET", false)] [InlineData("HEAD", false)] [InlineData("OPTIONS", false)] [InlineData(null, false)]
    public void Escrita_e_post_put_patch_delete(string? metodo, bool escrita) => Assert.Equal(escrita, Escopo.EhMetodoDeEscrita(metodo));

    /* ---- E20: a resolução do escopo, pura ------------------------------------------------------------- */

    static readonly SquadDoEscopo[] Squads =
    [
        new("s1", "S1", "t1", false), new("s2", "S2", "t1", true), new("s3", "S3", "t2", false),
        new("s4", "S4", null, false), new("s5", "S5", null, true),
    ];

    [Fact]
    public void Coordenador_de_tribo_ve_a_tribo_e_os_sem_tribo_mas_nunca_o_interno()
    {
        var esc = Escopo.Resolver("u", [new MembroDoEscopo(null, "t1", null, "tribo", "coordenador")], Squads);
        Assert.False(esc.Global);
        Assert.Equal(["s1", "s4"], esc.SquadIds!.OrderBy(x => x));
    }

    [Fact]
    public void O_interno_entra_so_por_vinculo_direto()
    {
        var esc = Escopo.Resolver("u", [new MembroDoEscopo(null, "t1", null, "tribo", "coordenador"), new MembroDoEscopo("s2", null, null, "squad", "head")], Squads);
        Assert.Equal(["s1", "s2", "s4"], esc.SquadIds!.OrderBy(x => x));
        Assert.Equal(["coordenador", "head"], esc.Papeis.OrderBy(x => x));
    }

    [Fact]
    public void Departamento_ve_os_operacionais_sem_o_interno()
    {
        var esc = Escopo.Resolver("u", [new MembroDoEscopo(null, null, "d1", "departamento", "head_departamento")], Squads);
        Assert.Equal(["s1", "s3", "s4"], esc.SquadIds!.OrderBy(x => x));
    }

    [Fact]
    public void Global_squad_e_sem_vinculo()
    {
        var global = Escopo.Resolver("u", [new MembroDoEscopo(null, null, null, "global", "rh")], []);
        Assert.True(global.Global);
        Assert.Null(global.SquadIds);

        Assert.Equal(["s3"], Escopo.Resolver("u", [new MembroDoEscopo("s3", null, null, "squad", "gestor")], Squads).SquadIds!);

        var nada = Escopo.Resolver("u", [], Squads);
        Assert.False(nada.Global);
        Assert.Empty(nada.SquadIds!);
    }

    /* ---- E01: a sessão não renova no acerto, respeita o exp e tira a conta banida --------------------- */

    static string Jwt(DateTime expUtc)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64("{\"alg\":\"HS256\"}") + "." + B64($"{{\"sub\":\"u\",\"exp\":{new DateTimeOffset(expUtc).ToUnixTimeSeconds()}}}") + ".assinatura";
    }

    [Fact]
    public void O_acerto_do_cache_nao_renova_a_sessao()
    {
        var agora = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var s = new Sessoes(() => agora);
        s.Guardar("tok", "u1", agora + Sessoes.Janela);
        for (var i = 0; i < 4; i++) { agora = agora.AddMinutes(1); Assert.Equal("u1", s.UserIdDe("tok")); }
        agora = agora.AddMinutes(1).AddSeconds(1);
        Assert.Null(s.UserIdDe("tok"));
    }

    [Fact]
    public void A_sessao_nunca_vale_alem_do_exp_do_jwt_nem_de_5_minutos()
    {
        var agora = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var s = new Sessoes(() => agora);
        var token = Jwt(agora.AddMinutes(2));
        s.Guardar(token, "u1", agora.AddHours(1));
        agora = agora.AddMinutes(2).AddSeconds(1);
        Assert.Null(s.UserIdDe(token));

        var longo = Jwt(agora.AddHours(1));
        s.Guardar(longo, "u1", agora.AddHours(1));
        agora = agora.AddMinutes(5).AddSeconds(1);
        Assert.Null(s.UserIdDe(longo));

        var vencido = Jwt(agora.AddMinutes(-1));
        s.Guardar(vencido, "u1", agora.AddMinutes(5));
        Assert.Null(s.UserIdDe(vencido));
    }

    [Fact]
    public void As_sessoes_vencidas_saem_do_mapa()
    {
        var agora = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var s = new Sessoes(() => agora);
        s.Guardar("a", "u1", agora.AddMinutes(5));
        s.Guardar("b", "u2", agora.AddMinutes(5));
        agora = agora.AddMinutes(10);
        s.Guardar("c", "u3", agora.AddMinutes(5));
        Assert.Equal(1, s.Quantas);
    }

    [Fact]
    public void Exp_do_jwt_e_lido_do_payload()
    {
        var exp = new DateTime(2026, 11, 7, 13, 0, 0, DateTimeKind.Utc);
        Assert.Equal(exp, Sessoes.ExpiraDoJwt(Jwt(exp)));
        Assert.Null(Sessoes.ExpiraDoJwt("nao-e-jwt"));
        Assert.Null(Sessoes.ExpiraDoJwt("a.###.c"));
    }

    [Theory]
    [InlineData("{\"id\":\"u1\"}", "u1")]
    [InlineData("{\"id\":\"u1\",\"banned_until\":null}", "u1")]
    [InlineData("{\"id\":\"u1\",\"banned_until\":\"2026-01-01T00:00:00Z\"}", "u1")]
    [InlineData("{\"id\":\"u1\",\"banned_until\":\"2126-01-01T00:00:00Z\"}", null)]
    [InlineData("{\"id\":\"u1\",\"banned_until\":\"nao e data\"}", null)]
    public void Conta_banida_nao_tem_sessao(string json, string? esperado)
    {
        using var d = JsonDocument.Parse(json);
        Assert.Equal(esperado, SupabaseAuth.UserIdDoUsuario(d.RootElement, new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)));
    }

    /* ---- E02, E18, E23, E24: a trava de produção e a escolha do transporte ----------------------------- */

    [Fact]
    public void A_producao_e_recusada_pelo_url()
    {
        var e = Db.Escolher("https://oaeqvfmvjahmlgdhpsrv.supabase.co", "", "sbp_token");
        Assert.Equal(Db.MotivoProducao, e.Erro);
    }

    [Theory]
    [InlineData("Host=aws-0-sa-east-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.oaeqvfmvjahmlgdhpsrv;Password=segredo")]
    [InlineData("Host=db.oaeqvfmvjahmlgdhpsrv.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=segredo")]
    [InlineData("Host=db.oaeqvfmvjahmlgdhpsrv.supabase.co;Username=postgres;Password=SENHA_DO_BANCO")]
    public void A_producao_e_recusada_pela_connection_string_mesmo_com_url_da_homologacao(string cs)
    {
        var e = Db.Escolher(UrlHomologacao, cs, "sbp_token");
        Assert.Equal(Db.MotivoProducao, e.Erro);
        Assert.False(e.PeloNpgsql);
    }

    [Fact]
    public void Connection_string_de_outro_projeto_e_recusada()
    {
        var e = Db.Escolher(UrlHomologacao, "Host=aws-0-sa-east-1.pooler.supabase.com;Username=postgres.abcdefghijklmnopqrst;Password=segredo", "");
        Assert.NotNull(e.Erro);
        Assert.Contains("mesmo projeto", e.Erro);
    }

    [Theory]
    [InlineData("Host=aws-0-sa-east-1.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.yurhgwqilbgtkvoymyab;Password=segredo;SSL Mode=Require;No Reset On Close=true;Maximum Pool Size=5")]
    [InlineData("host=aws-0-sa-east-1.pooler.supabase.com;username=postgres.yurhgwqilbgtkvoymyab;password=segredo")]
    [InlineData("Host=aws-0-sa-east-1.pooler.supabase.com;Username=postgres.yurhgwqilbgtkvoymyab;Pwd=segredo")]
    [InlineData("Host=db.yurhgwqilbgtkvoymyab.supabase.co;Username=postgres;Password=segredo")]
    public void A_homologacao_com_senha_vai_pelo_npgsql(string cs)
    {
        var e = Db.Escolher(UrlHomologacao, cs, "");
        Assert.Null(e.Erro);
        Assert.True(e.PeloNpgsql);
        Assert.DoesNotContain("segredo", e.Assinatura);
    }

    [Fact]
    public void Sem_senha_de_verdade_vai_pela_management_api_e_sem_nada_e_erro()
    {
        var placeholder = Db.Escolher(UrlHomologacao, "Host=aws-0-sa-east-1.pooler.supabase.com;Username=postgres.yurhgwqilbgtkvoymyab;Password=SENHA_DO_BANCO", "sbp_token_de_teste");
        Assert.Null(placeholder.Erro);
        Assert.False(placeholder.PeloNpgsql);
        Assert.StartsWith("mgmt:", placeholder.Assinatura);
        Assert.DoesNotContain("sbp_token_de_teste", placeholder.Assinatura);

        Assert.Contains("falta", Db.Escolher(UrlHomologacao, "", "").Erro);
        Assert.NotNull(Db.Escolher(UrlHomologacao, "isto nao e uma connection string", "sbp_x").Erro);
    }

    [Fact]
    public void Trocar_o_token_pelo_de_mesmo_tamanho_muda_a_assinatura()
    {
        var a = Db.Escolher(UrlHomologacao, "", "sbp_aaaaaaaaaaaaaaaa");
        var b = Db.Escolher(UrlHomologacao, "", "sbp_bbbbbbbbbbbbbbbb");
        Assert.NotEqual(a.Assinatura, b.Assinatura);
    }

    /* ---- E19: Sql.Lit e Sql.Fmt, a barreira contra injeção --------------------------------------------- */

    [Fact]
    public void Texto_dobra_a_aspa_e_nulo_vira_null()
    {
        Assert.Equal("'O''Brien'", Sql.Lit("O'Brien"));
        Assert.Equal("null", Sql.Lit(null));
        Assert.Equal("where slug = 'x''; drop table clientes; --'", Sql.Fmt($"where slug = {"x'; drop table clientes; --"}"));
    }

    [Fact]
    public void Numero_e_data_saem_na_cultura_invariante_mesmo_em_pt_BR()
    {
        var antes = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
            Assert.Equal("1234.5", Sql.Lit(1234.5m));
            Assert.Equal("0.1", Sql.Lit(0.1));
            Assert.Equal("1234567", Sql.Lit(1234567L));
            Assert.Equal("x = 1.5 and y = 2", Sql.Fmt($"x = {1.5m} and y = {2}"));
            Assert.Equal("'2026-11-07'::date", Sql.Lit(new DateOnly(2026, 11, 7)));
            Assert.Equal("'2026-11-07T12:00:00.000Z'::timestamptz", Sql.Lit(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc)));
        }
        finally { CultureInfo.CurrentCulture = antes; }
    }

    [Fact]
    public void Arrays_jsonb_raw_e_guid()
    {
        Assert.Equal("'{}'::text[]", Sql.Lit(Array.Empty<string>()));
        Assert.Equal("array['a','b''c']::text[]", Sql.Lit(new[] { "a", "b'c" }));
        Assert.Equal("array['a']::text[]", Sql.Lit(new List<string> { "a" }));
        var g = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("'11111111-2222-3333-4444-555555555555'::uuid", Sql.Lit(g));
        Assert.Equal("array['11111111-2222-3333-4444-555555555555']::uuid[]", Sql.Lit(new[] { g }));
        Assert.Equal("'{}'::uuid[]", Sql.Lit(Array.Empty<Guid>()));
        Assert.Equal("'{\"a\":\"it''s\"}'::jsonb", Sql.Lit(new Jsonb("{\"a\":\"it's\"}")));
        Assert.Equal("now()", Sql.Lit(new Raw("now()")));
        Assert.Equal("true", Sql.Lit(true));
        Assert.Equal("select '{}'::text[] where a = 1", Sql.Fmt($"select '{{}}'::text[] where a = {1}"));
        Assert.Throws<ArgumentException>(() => Sql.Lit(new object()));
    }

    /* ---- E03, E09, E10, E13, E39, E41: as escritas --------------------------------------------------- */

    [Fact]
    public void O_recorte_do_icp_so_muda_se_for_do_cliente()
    {
        var cid = Guid.NewGuid(); var sid = Guid.NewGuid();
        var sql = Escritas.RecorteSql(cid, sid, new Icp { Setor = "x" });
        Assert.Contains($"segmento_id = '{sid}'::uuid and cliente_id = '{cid}'::uuid", sql);
    }

    static JsonElement Corpo(string json, string campo) =>
        JsonDocument.Parse(json).RootElement.TryGetProperty(campo, out var v) ? v.Clone() : default;

    [Fact]
    public void Campo_ausente_nao_mexe_e_null_limpa()
    {
        var (ausente, e1) = Escritas.TextoDoCorpo(Corpo("{}", "nichoSlug"), "nicho");
        Assert.Null(e1); Assert.False(ausente.Veio);
        var (nulo, _) = Escritas.TextoDoCorpo(Corpo("{\"nichoSlug\":null}", "nichoSlug"), "nicho");
        Assert.True(nulo.Veio); Assert.Null(nulo.Valor);
        var (texto, _) = Escritas.TextoDoCorpo(Corpo("{\"nichoSlug\":\"saude\"}", "nichoSlug"), "nicho");
        Assert.Equal(Campo<string?>.De("saude"), texto);
        Assert.NotNull(Escritas.TextoDoCorpo(Corpo("{\"nichoSlug\":3}", "nichoSlug"), "nicho").Erro);

        Assert.False(Escritas.ReaisDoCorpo(default, "budget").Campo.Veio);
        Assert.Equal(Campo<decimal?>.De(null), Escritas.ReaisDoCorpo(Corpo("{\"b\":null}", "b"), "budget").Campo);
        Assert.Equal(Campo<decimal?>.De(2500.5m), Escritas.ReaisDoCorpo(Corpo("{\"b\":2500.5}", "b"), "budget").Campo);
        Assert.Equal(Campo<decimal?>.De(80m), Escritas.ReaisDoCorpo(Corpo("{\"b\":\"80\"}", "b"), "budget").Campo);
        Assert.NotNull(Escritas.ReaisDoCorpo(Corpo("{\"b\":\"oitenta\"}", "b"), "budget").Erro);

        Assert.False(Escritas.ReguaDoCorpo(default).Campo.Veio);
        Assert.Equal(Campo<ReguaV2?>.De(null), Escritas.ReguaDoCorpo(Corpo("{\"r\":null}", "r")).Campo);
        var regua = Escritas.ReguaDoCorpo(Corpo("{\"r\":{\"versao\":2,\"perguntas\":[{\"nome\":\"faturamento\",\"forte\":[\"alto\"]}]}}", "r")).Campo;
        Assert.True(regua.Veio);
        Assert.Equal("faturamento", regua.Valor!.Perguntas.Single().Nome);
        Assert.NotNull(Escritas.ReguaDoCorpo(Corpo("{\"r\":\"x\"}", "r")).Erro);
    }

    [Fact]
    public void O_upsert_da_conta_cria_a_linha_com_o_squad_principal_e_so_mexe_nas_colunas_dadas()
    {
        var sql = Escritas.UpsertDaContaSql("acme", Guid.NewGuid(), [("nicho_slug", "saude")]);
        Assert.Contains("insert into public.trafego_contas (cliente_slug, nicho_slug, squad_nome, atualizado_em, atualizado_por, origem)", sql);
        Assert.Contains("cliente_squad_tipo = 'principal'", sql);
        Assert.Contains("on conflict (cliente_slug) do update set nicho_slug = excluded.nicho_slug, atualizado_em = now()", sql);
        Assert.DoesNotContain("regua_mql", sql);
        Assert.DoesNotContain("teto_mql", sql);
        Assert.DoesNotContain("squad_nome = excluded", sql);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("50", true)]
    [InlineData("100000", true)]
    [InlineData("100000.01", false)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    public void O_teto_por_mql_vai_ate_100_mil(string? teto, bool ok) =>
        Assert.Equal(ok, Escritas.ProblemaDoTeto(teto is null ? null : decimal.Parse(teto, CultureInfo.InvariantCulture)) is null);

    [Theory]
    [InlineData("(61) 99582-1272", "5561995821272")]
    [InlineData("+55 61 99582-1272", "5561995821272")]
    [InlineData("6133334444", "556133334444")]
    [InlineData("5561995821272", "5561995821272")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void O_whatsapp_vira_e164(string? bruto, string? esperado)
    {
        var r = Escritas.WhatsappE164(bruto);
        Assert.True(r.Ok);
        Assert.Equal(esperado, r.Valor);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567890123456")]
    [InlineData("ramal 12")]
    public void O_whatsapp_fora_do_e164_e_recusado(string bruto) => Assert.False(Escritas.WhatsappE164(bruto).Ok);

    [Fact]
    public void A_trilha_do_setup_grava_na_aba_setup_com_antes_e_depois()
    {
        var sql = TrilhaDoSetup.InsertSql("mudou_setup_do_motor", Guid.NewGuid(), "acme", "ok", null, null, null, null,
            new { modo = "recomenda" }, new { modo = "executa" }, new { o_que = "modo" }, "POSEIDON");
        Assert.Contains("values ('setup', 'mudou_setup_do_motor'", sql);
        Assert.Contains("'{\"modo\":\"recomenda\"}'::jsonb", sql);
        Assert.Contains("'{\"modo\":\"executa\"}'::jsonb", sql);
        Assert.Contains("'POSEIDON'", sql);
    }

    /* ---- E13 e E37: o setup -------------------------------------------------------------------------- */

    [Theory]
    [InlineData("..", "arquivo")]
    [InlineData(".", "arquivo")]
    [InlineData("...", "arquivo")]
    [InlineData("", "arquivo")]
    [InlineData("Logo Ação.PNG", "logo-acao.png")]
    [InlineData("../../x.png", "..-..-x.png")]
    public void O_nome_do_asset_nunca_vira_segmento_de_pontos(string nome, string esperado) =>
        Assert.Equal($"cid/tela/{esperado}", Setup.ChaveDoAsset("cid", "tela", nome));

    [Fact]
    public void O_token_entra_na_trilha_mascarado_e_com_hash_curto()
    {
        var token = "EAAfSUPERLONGO1234567890abcdefkCZBte";
        Assert.Equal("EAAf••••kCZBte", Setup.Mascarar(token));
        Assert.Equal("••••", Setup.Mascarar("curto"));
        Assert.Equal("", Setup.Mascarar(null));
        Assert.Equal(12, Setup.Impressao(token).Length);
        Assert.NotEqual(Setup.Impressao(token), Setup.Impressao(token + "x"));
        Assert.Equal("", Setup.Impressao(""));
    }

    /* ---- E32: o número do lote de copy é o gravado ---------------------------------------------------- */

    sealed class LinhaFalsa(Dictionary<string, object?> v) : ILinha
    {
        object? V(string c) => v.GetValueOrDefault(c);
        public string? S(string c) => V(c)?.ToString();
        public decimal? Dec(string c) => V(c) as decimal?;
        public long L(string c) => V(c) is long l ? l : 0;
        public bool? B(string c) => V(c) as bool?;
        public DateTime? Dt(string c) => V(c) as DateTime?;
        public DateOnly? Data(string c) => V(c) as DateOnly?;
        public List<string> Textos(string c) => V(c) is string[] a ? a.ToList() : new();
        public string? Json(string c) => V(c)?.ToString();
    }

    [Fact]
    public void O_numero_do_lote_e_o_do_banco_e_nao_a_posicao_na_janela()
    {
        var lotes = """
            [{"lote_id":"a","numero":21,"modelo":"m","criado_em":"2026-11-01T00:00:00","pecas":[]},
             {"lote_id":"b","numero":22,"modelo":"m","criado_em":"2026-11-02T00:00:00","pecas":[]},
             {"lote_id":"c","modelo":"m","criado_em":"2026-11-03T00:00:00","pecas":[]}]
            """;
        var c = Carteira.Ler(new LinhaFalsa(new() { ["cliente_id"] = Guid.NewGuid().ToString(), ["cliente_slug"] = "acme", ["lotes"] = lotes }));
        Assert.Equal(["c", "b", "a"], c.Lotes.Select(l => l.Id));
        Assert.Equal(21, c.Lotes.Single(l => l.Id == "a").Numero);
        Assert.Equal(22, c.Lotes.Single(l => l.Id == "b").Numero);
        Assert.Equal(3, c.Lotes.Single(l => l.Id == "c").Numero);
    }
}
