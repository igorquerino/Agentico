using System.Text.Json;
using BlueOS.Api.Dominio;
using BlueOS.Api.Infra;
using BlueOS.Core.Copy;
using BlueOS.Core.Design;
using BlueOS.Core.Modelos;
using SkiaSharp;
using Xunit;

namespace BlueOS.Tests;

/* As correções da revisão G3 (Geração: Copy e Designer), com o esperado tirado do V1 quando a entrada é de paridade:
   o portão do crivo, os dois crivos iguais, a divisão (divisao.ts), a consulta ao vault (consulta.ts, contrato.ts), a
   normalização do pedido (etapas.ts), o mapa do CTA (estruturas.ts), a diagramação no topo (texto-zonas.ts), o degradê
   (camada-degrade.ts), o custo (precos.ts, custo.ts) e as guardas do Designer e do ajuste. */
public class G3CorrecoesTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    static SnapshotDoVault Vault(params (string Id, string Fm, string Corpo)[] extras)
    {
        var notas = new List<(string, JsonElement, string)>
        {
            ("TRF-CPY-002", J("""{"tipo":"proibicao","status":"fechada","titulo":"Nunca negação na headline","chave_copy":"nunca_negacao_headline","aplica_em":{}}"""), "x"),
            ("TRF-CPY-003", J("""{"tipo":"proibicao","status":"fechada","titulo":"Nunca reunião no CTA do MOFU","chave_copy":"nunca_reuniao_cta_mofu","aplica_em":{"etapa":["MOFU"]}}"""), "x"),
            ("TRF-CPY-004", J("""{"tipo":"proibicao","status":"fechada","titulo":"Nunca prova na sub do MOFU","chave_copy":"nunca_prova_sub_mofu","aplica_em":{"etapa":["MOFU"]}}"""), "x"),
            ("NICHO-saas", J("""{"tipo":"nicho","estruturas":[{"base":"ampla","etapa":"MOFU","ordem":1,"parte":"headline","estrutura":"dor"}],"evitar":[]}"""), ""),
            ("NICHO-imobiliario", J("""{"tipo":"nicho","estruturas":[{"base":"ampla","etapa":"MOFU","ordem":1,"parte":"headline","estrutura":"pergunta"}],"evitar":[]}"""), ""),
        };
        notas.AddRange(extras.Select(e => (e.Id, J(e.Fm), e.Corpo)));
        return PromptDoCopy.Snapshot("ebe4c4f7c4bc0cd371f47c2abc8e9eacd04c3644", notas);
    }

    static PecaDoModelo Peca(string h, string c, string cta, string angulo = "dor", string papel = "contexto_da_dor", string tipoCta = "ver_conhecer", params string[] formas) =>
        new(h, c, cta, angulo, formas, papel, tipoCta);

    static PecaDeCopy PecaPub(Etapa etapa, string h, string sub, string cta, string papel = "contexto_da_dor", string tipoCta = "ver_conhecer", TipoPeca tipo = TipoPeca.Estatico) =>
        new() { Id = "p", Etapa = etapa, Tipo = tipo, Headline = h, Sub = sub, Cta = cta, AnguloHeadline = "dor", PapelSub = papel, TipoCta = tipoCta };

    /* ---- G3-01: crivo_ok nulo não é aprovação -------------------------------------- */

    [Fact]
    public void G3_01_crivo_que_nao_rodou_nao_libera_a_aprovacao()
    {
        Assert.False(PromptDoCopy.CrivoLibera(null));
        Assert.False(PromptDoCopy.CrivoLibera(false));
        Assert.True(PromptDoCopy.CrivoLibera(true));

        var ok = Peca("Fechamento em um dia, sem planilha", "O ERP manda, o Acme concilia.", "Ver como funciona", papel: "explica_como_funciona");
        var semVault = PromptDoCopy.Avaliar(ok, "MOFU", null, null, "estatico", "Acme");
        Assert.Null(semVault.Crivo.Ok);              /* o crivo não rodou */
        Assert.Null(semVault.Motivo);                /* não regera à toa */
        Assert.False(PromptDoCopy.CrivoLibera(semVault.Crivo.Ok));
        /* reprovação certa (régua em código) é false mesmo sem vault */
        var comTravessao = PromptDoCopy.Avaliar(Peca("Fechamento — em um dia", "corpo", "Ver como funciona"), "MOFU", null, null, "estatico", "Acme");
        Assert.False(comTravessao.Crivo.Ok);
        Assert.Contains("R10", comTravessao.Motivo);
    }

    /* ---- G3-02 / EC-4a: nota ruim não derruba o vault ------------------------------ */

    [Fact]
    public void G3_02_nota_com_tipo_inesperado_nao_derruba_o_snapshot()
    {
        var v = Vault(
            ("NICHO-saude", """{"tipo":"nicho","estruturas":[{"etapa":"MOFU","parte":"headline","ordem":1.5,"estrutura":"dor","base":"ampla"}],"evitar":[]}""", ""),
            ("TRF-CPY-009", """{"tipo":"orientacao","status":"fechada","titulo":7,"chave_copy":"cta_pesa_pouco","aplica_em":{"etapa":[1,"MOFU"]},"parametros":{"papeis":"nao-e-lista"}}""", "c"));
        Assert.NotNull(v.RegraDaChave("nunca_negacao_headline"));
        Assert.Equal(["MOFU"], v.RegraDaChave("cta_pesa_pouco")!.Etapas);
        Assert.Equal(0, v.Nicho("saude")!.Estruturas[0].Ordem);
        Assert.Empty(v.Ignoradas);
    }

    /* ---- G3-06: o crivo da geração e o da publicação dizem a mesma coisa ------------- */

    [Fact]
    public void G3_06_os_dois_crivos_concordam_nos_marcadores_do_vault_e_na_regua()
    {
        var v = Vault();
        (bool Geracao, bool Publicacao) Os2(Etapa etapa, string h, string sub, string cta, string papel = "contexto_da_dor", string tipoCta = "ver_conhecer")
        {
            var g = PromptDoCopy.Avaliar(Peca(h, sub, cta, papel: papel, tipoCta: tipoCta), etapa.ToString(), v, null, "estatico", "Acme").Motivo is null;
            var p = Crivo.Conferir(PecaPub(etapa, h, sub, cta, papel, tipoCta), "Acme").Count == 0;
            return (g, p);
        }
        /* "Sem…" no começo não é negação do vault: os dois aprovam (antes a publicação recusava) */
        Assert.Equal((true, true), Os2(Etapa.MOFU, "Sem planilha, o fechamento sai em um dia", "O ERP manda e a conciliação sai sozinha.", "Ver como funciona"));
        /* pedido de demonstração no CTA do MOFU: os dois recusam (antes a publicação deixava passar) */
        Assert.Equal((false, false), Os2(Etapa.MOFU, "Fechamento em um dia", "O ERP manda e a conciliação sai sozinha.", "Solicite uma demonstração"));
        /* prova escrita na sub, sem declarar: os dois recusam */
        Assert.Equal((false, false), Os2(Etapa.MOFU, "Fechamento em um dia", "Mais de 500 empresas já usam", "Ver como funciona"));
        /* negação no meio da headline */
        Assert.Equal((false, false), Os2(Etapa.MOFU, "Vende quem responde primeiro. Não quem responde melhor.", "O ERP manda e a conciliação sai sozinha.", "Ver como funciona"));
        /* a régua em código: travessão, CTA longo, peça incompleta, TOFU com a marca */
        Assert.Equal((false, false), Os2(Etapa.MOFU, "Fechamento em um dia — sem susto", "corpo", "Ver como funciona"));
        Assert.Equal((false, false), Os2(Etapa.MOFU, "Fechamento em um dia", "corpo", "Clique aqui e veja como funciona"));
        Assert.Equal((false, false), Os2(Etapa.MOFU, "Fechamento em um dia", "", "Ver como funciona"));
        Assert.Equal((false, false), Os2(Etapa.TOFU, "O fechamento que a Acme resolve", "corpo", "Saiba mais"));
        /* no BOFU a reunião e a prova não são proibidas: os dois aprovam */
        Assert.Equal((true, true), Os2(Etapa.BOFU, "Fechamento em um dia", "Mais de 500 empresas já usam", "Solicite uma demonstração", tipoCta: "falar"));
    }

    [Fact]
    public void G3_17_crivo_da_publicacao_r10_r1_r3_incompleta_e_proibicoes()
    {
        Assert.Empty(Crivo.Conferir(PecaPub(Etapa.MOFU, "Fechamento em um dia", "O ERP manda.", "Ver como funciona"), "Acme"));
        Assert.Contains("R10: travessão na peça", Crivo.Conferir(PecaPub(Etapa.MOFU, "Fechamento - em um dia", "O ERP manda.", "Ver como funciona"), "Acme"));
        Assert.Contains("R1: o TOFU cita a marca", Crivo.Conferir(PecaPub(Etapa.TOFU, "Fechamento em um dia", "a acme concilia", "Saiba mais"), "Acme"));
        Assert.Empty(Crivo.Conferir(PecaPub(Etapa.MOFU, "Fechamento com a Acme", "O ERP manda.", "Ver como funciona"), "Acme")); /* R1 é só TOFU */
        Assert.Contains("R3: CTA de estático com mais de 4 palavras", Crivo.Conferir(PecaPub(Etapa.MOFU, "Fechamento em um dia", "O ERP manda.", "Veja agora como isso funciona"), "Acme"));
        Assert.Empty(Crivo.Conferir(PecaPub(Etapa.MOFU, "Fechamento em um dia", "O ERP manda.", "Veja agora como isso funciona", tipo: TipoPeca.Video), "Acme")); /* vídeo fala o CTA */
        Assert.Contains("peça incompleta: abertura, fecho e CTA são obrigatórios", Crivo.Conferir(PecaPub(Etapa.MOFU, "Fechamento", "O ERP manda.", " "), "Acme"));
        Assert.Contains(Crivo.Conferir(PecaPub(Etapa.MOFU, "Fechamento", "O ERP manda.", "Ver", papel: "prova"), "Acme"), x => x.StartsWith("nunca_prova_sub_mofu"));
        Assert.Contains(Crivo.Conferir(PecaPub(Etapa.MOFU, "Fechamento", "O ERP manda.", "Ver", tipoCta: "agendar_solicitar"), "Acme"), x => x.StartsWith("nunca_reuniao_cta_mofu"));
        Assert.Contains(Crivo.Conferir(PecaPub(Etapa.BOFU, "Chega de planilha", "O ERP manda.", "Ver"), "Acme"), x => x.StartsWith("nunca_negacao_headline"));
    }

    /* ---- G3-07 / G3-27: o fim da resposta e a troca de modelo ---------------------- */

    [Fact]
    public void G3_07_recusa_e_json_cortado_viram_erro_claro_e_429_passa_ao_seguinte()
    {
        Assert.Contains("recusou", Ia.ProblemaDoFim("refusal", 1000, "cyber"));
        Assert.Contains("(cyber)", Ia.ProblemaDoFim("refusal", 1000, "cyber"));
        Assert.Contains("teto de 4000 tokens", Ia.ProblemaDoFim("max_tokens", 4000));
        Assert.Null(Ia.ProblemaDoFim("end_turn", 4000));
        Assert.Null(Ia.ProblemaDoFim(null, 4000));
        Assert.True(Ia.PassaAoSeguinte(429)); Assert.True(Ia.PassaAoSeguinte(529)); Assert.True(Ia.PassaAoSeguinte(503));
        Assert.False(Ia.PassaAoSeguinte(400)); Assert.False(Ia.PassaAoSeguinte(500));
        Assert.True(Ia.TempoDeEspera < TimeSpan.FromMinutes(5)); /* o servidor desiste antes do desktop */
    }

    /* ---- G3-09 / G3-16: o custo e o ia_uso ----------------------------------------- */

    [Fact]
    public void G3_09_o_cache_e_cobrado_pelo_preco_do_cache_e_o_ambiente_e_derivado()
    {
        Assert.Equal(4m + 20m, CopyAgente.CustoUsd("claude-opus-5-5", 1_000_000, 1_000_000));
        /* 1M lidos do cache: 10% da entrada; 1M gravados: 125% */
        Assert.Equal(0.4m, CopyAgente.CustoUsd("claude-opus-5-5", 1_000_000, 0, cacheLidos: 1_000_000));
        Assert.Equal(5m, CopyAgente.CustoUsd("claude-opus-5-5", 1_000_000, 0, cacheGravados: 1_000_000));
        Assert.Equal(2m * 0.5m + 2m * 0.5m * 0.1m, CopyAgente.CustoUsd("claude-sonnet-5-5", 1_000_000, 0, cacheLidos: 500_000));
        Assert.Equal(5m + 25m, CopyAgente.CustoUsd("claude-opus-5", 1_000_000, 1_000_000));
        Assert.Equal(0m, CopyAgente.CustoUsd("modelo-que-nao-existe", 1_000_000, 1_000_000)); /* fora da tabela: 0, visível */
        Assert.Equal("homologacao", UsoDaIa.Ambiente("yurhgwqilbgtkvoymyab"));
        Assert.Equal("producao", UsoDaIa.Ambiente(Db.RefProducao));
        Assert.Equal("desconhecido", UsoDaIa.Ambiente("outro"));
        Assert.Equal("wxyz", UsoDaIa.ChaveRef("sk-abcwxyz"));
        Assert.Null(UsoDaIa.ChaveRef("abc"));
    }

    /* ---- G3-03 / G3-16: o texto no topo, sob a faixa da logo, e dentro da área ---------- */

    [Theory]
    [InlineData("feed")]
    [InlineData("story")]
    public void G3_03_o_bloco_comeca_abaixo_da_faixa_da_logo_centralizado_e_cabe_na_area(string formato)
    {
        foreach (var head in new[] { "Feche o mês em um dia", string.Join(" ", Enumerable.Repeat("Conciliação automática do extrato bancário", 4)) })
        {
            var estimado = Diagramacao.Centralizado(formato, head, "O ERP manda, o Acme concilia cada recebível antes do fechamento.", "Ver como funciona", "#ffffff", "#8dae24", "#8dae24", "#0b1220");
            foreach (var l in new[] { estimado, Compositor.Ajustar(estimado) })
            {
                var h = l.Altura;
                var topo = Diagramacao.TopoDoTexto(formato);
                Assert.Equal(formato == "story" ? 230 + 56 : 150 + 56, topo);
                Assert.InRange(l.Caixa("headline")!.Y * h, topo - 0.5, topo + 0.5);          /* começa logo abaixo da faixa (BH-2a) */
                var (y, alt) = Diagramacao.Bloco(l);
                Assert.True((y + alt) * h <= Diagramacao.FimDaAreaDoTexto * h + 1, $"o bloco passou da área do texto: {(y + alt) * h} px");
                foreach (var c in l.Caixas)
                {
                    Assert.InRange(c.X, 0, 1); Assert.InRange(c.X + c.W, 0, 1.0001);
                    Assert.True(c.Y * h >= Diagramacao.FaixaDoLogo(formato));                 /* nada sob a logo */
                }
                /* headline normal cabe em 3 linhas; a enorme passa de 3 (sem política de corte, BH-2c), mas o bloco continua na área */
                if (head.Length < 60) Assert.True(l.Caixa("headline")!.Linhas.Count <= Diagramacao.MaxLinhas);
                else Assert.True(l.Caixa("headline")!.Tamanho < Diagramacao.TamanhoHeadline * (formato == "story" ? 0.78 : 1)); /* encolheu */
                var cta = l.Caixa("cta")!;
                Assert.InRange(cta.X + cta.W / 2, 0.499, 0.501);                               /* o botão no eixo central (BH-2b) */
                Assert.True(cta.Y > l.Caixa("sub")!.Y && l.Caixa("sub")!.Y > l.Caixa("headline")!.Y);
            }
        }
    }

    [Fact]
    public void G3_16_compor_desenha_a_peca_no_tamanho_da_Meta_com_o_degrade_atras_do_texto()
    {
        byte[] cena;
        using (var bmp = new SKBitmap(1024, 1280))
        {
            using (var cv = new SKCanvas(bmp)) cv.Clear(new SKColor(200, 210, 220));
            using var img = SKImage.FromBitmap(bmp); using var d = img.Encode(SKEncodedImageFormat.Png, 100); cena = d.ToArray();
        }
        var estimado = Diagramacao.Centralizado("feed", "Feche o mês em um dia", "O ERP manda, o Acme concilia.", "Ver como funciona", "#ffffff", "#8dae24", "#8dae24", "#0b1220");
        var png = Compositor.Compor(cena, estimado, out var layout, out var degrade);
        using var final = SKBitmap.Decode(png);
        Assert.Equal((1080, 1350), (final.Width, final.Height));
        /* texto claro no topo: degradê preto, do topo até 20% abaixo do bloco, denso em cima */
        Assert.Equal("#000000", degrade.Cor);
        Assert.Equal(0, degrade.Y);
        var (y, alt) = Diagramacao.Bloco(layout);
        Assert.InRange(degrade.H, y + alt + 0.2 - 1e-9, y + alt + 0.2 + 1e-9);
        Assert.Equal(1, degrade.InicioY); Assert.InRange(degrade.FimY, y / degrade.H - 1e-9, y / degrade.H + 1e-9);
        /* o topo escureceu e a base (onde fica o objeto) ficou como veio */
        var topo = final.GetPixel(540, 40); var baseDaPeca = final.GetPixel(540, 1330);
        Assert.True(topo.Red < 120, $"o topo não escureceu: {topo}");
        Assert.Equal((byte)200, baseDaPeca.Red);
    }

    [Fact]
    public void G3_03_degrade_segue_o_lado_e_a_cor_do_texto_como_o_V1()
    {
        var embaixo = Diagramacao.DegradeAtrasDoTexto(0.6, 0.3, "#ffffff");
        Assert.Equal(("#000000", 0.4, 0.6), (embaixo.Cor, Math.Round(embaixo.Y, 9), Math.Round(embaixo.H, 9)));
        /* denso no FIM do texto (0,9), transparente 20% acima do topo dele (0,4) */
        Assert.Equal((0.0, Math.Round((0.9 - 0.4) / 0.6, 6)), (embaixo.InicioY, Math.Round(embaixo.FimY, 6)));
        var escuro = Diagramacao.DegradeAtrasDoTexto(0.1, 0.2, "#0b1220");
        Assert.Equal("#ffffff", escuro.Cor);
        Assert.Equal(0.8, escuro.OpacidadePara); Assert.Equal(0, escuro.OpacidadeDe);
    }

    [Fact]
    public void G3_03_a_area_reservada_do_prompt_e_a_que_a_diagramacao_ocupa()
    {
        var copy = new CopyDaArte(1, "MOFU", "Feche o mês em um dia, sem planilha e sem susto no caixa", "O ERP manda, o Acme concilia cada recebível.", "Ver como funciona");
        var (inicio, altura) = PromptDaArte.AreaReservada(copy, "feed");
        var l = Diagramacao.Centralizado("feed", copy.Head, copy.Sub, copy.Cta, "#fff", "#fff", "#fff", "#000");
        var (y, h) = Diagramacao.Bloco(l);
        Assert.Equal(206, inicio);
        Assert.InRange(altura, h * 1350 - 1, h * 1350 + 1);
        Assert.True(altura > 51 + 27 + 31); /* maior que a área antiga, que era menor que o texto real */
        var p = PromptDaArte.Montar(copy, new Cliente { Id = "1", Slug = "a", Nome = "Acme" }, null, "feed", "laptop", null);
        Assert.Contains($"Altura aproximada a manter limpa: {altura} px", p);
        Assert.Contains("a partir de 206 px", p);
    }

    /* ---- G3-10: o texto_layout que o Estúdio do V1 lê ------------------------------- */

    [Fact]
    public void G3_10_tamanho_em_fracao_da_largura_e_o_degrade_como_camada()
    {
        var l = Diagramacao.Centralizado("story", "Feche o mês em um dia", "O ERP manda.", "Ver como funciona", "#ffffff", "#8dae24", "#8dae24", "#0b1220");
        var d = Diagramacao.DegradeDoLayout(l);
        var itens = J(PromptDaArte.TextoLayout(l, "#8dae24", "#8dae24", d));
        Assert.Equal(5, itens.GetArrayLength());
        var head = l.Caixa("headline")!;
        Assert.Equal(Math.Round(head.Tamanho * 1920 / 1080, 5), itens[0].GetProperty("tamanho").GetDouble());
        var camada = itens.EnumerateArray().Single(x => x.TryGetProperty("tipo", out var t) && t.GetString() == "degrade");
        Assert.Equal("#000000", camada.GetProperty("de").GetProperty("cor").GetString());
        Assert.Equal(0.8, camada.GetProperty("para").GetProperty("opacidade").GetDouble());
        Assert.False(camada.GetProperty("oculto").GetBoolean());
        Assert.Equal(1, camada.GetProperty("inicio").GetProperty("y").GetDouble());
    }

    /* ---- G3-11: o ICP padrão não vira dado ----------------------------------------- */

    [Fact]
    public void G3_11_o_padrao_do_icp_nao_liga_o_temIcp_nem_entra_no_contexto()
    {
        var vazio = new Cliente { Id = "2", Slug = "x", Nome = "X" }; /* Tipo "B2B", nível "3", "morno": o padrão da leitura */
        var (texto, temIcp) = PromptDoCopy.ContextoDoCliente(vazio);
        Assert.False(temIcp);
        Assert.DoesNotContain("- Tipo: B2B", texto);
        Assert.DoesNotContain("morno", texto);
        Assert.DoesNotContain("B2B", PromptDaArte.DescreverIcp(vazio));

        var real = new Cliente { Id = "3", Slug = "y", Nome = "Y" }; real.Icp.CargoDecisor = "CFO";
        var (t2, tem2) = PromptDoCopy.ContextoDoCliente(real);
        Assert.True(tem2);                      /* tipo + outro dado do ICP: a régua do V1 (dores, tipo ou segmento) */
        Assert.Contains("- Tipo: B2B", t2);
    }

    /* ---- G3-12: a divisão do divisao.ts, com histórico ------------------------------ */

    [Theory]
    [InlineData(3, 0, 0, 0)]   /* o V1 não manda a 1ª peça ao nicho (o porte antigo mandava 1) */
    [InlineData(5, 0, 0, 1)]
    [InlineData(3, 4, 0, 1)]
    [InlineData(10, 0, 0, 2)]
    [InlineData(3, 20, 0, 1)]  /* teto ceil(n/5): o lote não recupera o atraso inteiro */
    [InlineData(5, 3, 2, 0)]
    [InlineData(0, 0, 0, 0)]
    public void G3_12_pecas_do_nicho_como_o_V1(int n, int geral, int nicho, int esperado) =>
        Assert.Equal(esperado, PromptDoCopy.PecasDoNicho(n, new PromptDoCopy.Historico(geral, nicho)));

    [Fact]
    public void G3_12_fontes_do_grupo_e_o_historico_desde_a_primeira_geral()
    {
        var (f, depois) = PromptDoCopy.FontesDoGrupo(6, (true, true), new PromptDoCopy.Historico(3, 0));
        Assert.Equal(["geral", "geral", "geral", "geral", "geral", "nicho"], f);
        Assert.Equal(new PromptDoCopy.Historico(8, 1), depois);
        var (soNicho, h2) = PromptDoCopy.FontesDoGrupo(2, (false, true), new PromptDoCopy.Historico(1, 1));
        Assert.Equal(["nicho", "nicho"], soNicho); Assert.Equal(new PromptDoCopy.Historico(1, 1), h2); /* sem a geral, o histórico não anda */
        var (soGeral, h3) = PromptDoCopy.FontesDoGrupo(2, (true, false), PromptDoCopy.Historico.Zero);
        Assert.Equal(["geral", "geral"], soGeral); Assert.Equal(new PromptDoCopy.Historico(2, 0), h3);
        Assert.Equal([null, null], PromptDoCopy.FontesDoGrupo(2, (false, false), PromptDoCopy.Historico.Zero).Fontes);

        Assert.Equal(new PromptDoCopy.Historico(2, 1), PromptDoCopy.HistoricoDasPecas(["nicho", "nicho", "geral", "nicho", "geral", "v8"]));
        Assert.Equal(PromptDoCopy.Historico.Zero, PromptDoCopy.HistoricoDasPecas(["nicho", "nicho"]));

        var b = PromptDoCopy.BlocosDoVaultPara(Vault(), "imobiliario", ["MOFU"]);
        Assert.All(PromptDoCopy.DivisaoDoGrupo(b, "MOFU", 3, 0, 0), x => Assert.Equal("geral", x));
        Assert.Equal(["geral", "geral", "nicho"], PromptDoCopy.DivisaoDoGrupo(b, "MOFU", 3, new PromptDoCopy.Historico(4, 0)).Fontes);
    }

    /* ---- G3-13: o vault lido como o V1 ---------------------------------------------- */

    [Fact]
    public void G3_13_chave_repetida_desliga_refutada_nao_entra_e_papeis_vem_de_parametros_papeis()
    {
        var v = Vault(
            ("TRF-CPY-012", """{"tipo":"proibicao","status":"fechada","titulo":"Outra negação","chave_copy":"nunca_negacao_headline","aplica_em":{}}""", "dup"),
            ("TRF-CPY-020", """{"tipo":"orientacao","status":"refutada","titulo":"Caiu","chave_copy":"cta_pesa_pouco","aplica_em":{}}""", "c"),
            ("TRF-CPY-021", """{"tipo":"orientacao","status":"aberta","titulo":"Aberta","chave_copy":"pergunta_so_com_dor_concreta","aplica_em":{}}""", "c"),
            ("TRF-CPY-005", """{"tipo":"orientacao","status":"fechada","titulo":"Prefira contexto","chave_copy":"prefira_contexto_ou_fragmento_sub","aplica_em":{},"parametros":{"papeis":["contexto_da_dor","inventado"],"papeis_sub":["fragmento"]}}""", "c"));
        Assert.Null(v.RegraDaChave("nunca_negacao_headline"));
        Assert.Equal("repetida", v.RegraDaChaveCopy("nunca_negacao_headline").Motivo);
        Assert.Equal("ausente", v.RegraDaChaveCopy("cta_pesa_pouco").Motivo);
        Assert.Null(v.RegraDaChave("pergunta_so_com_dor_concreta"));
        Assert.Equal(["contexto_da_dor"], v.RegraDaChave("prefira_contexto_ou_fragmento_sub")!.PapeisPreferidos);

        var texto = PromptDoCopy.BlocoDeRegras(v, out var desligadas);
        Assert.Contains(("nunca_negacao_headline", "repetida"), desligadas);
        Assert.DoesNotContain("Caiu", texto); Assert.DoesNotContain("Aberta", texto);
        Assert.Contains("Papéis de SUB preferidos: contexto da dor.", texto);

        /* a negação repetida fica desligada: não reprova, e o crivo diz por quê */
        var r = PromptDoCopy.Crivar(Peca("Chega de planilha", "corpo", "Ver"), PromptDoCopy.DeclaracaoDe(Peca("Chega de planilha", "corpo", "Ver")), "MOFU", v);
        Assert.True(r.Ok);
        Assert.Contains(("nunca_negacao_headline", "repetida"), r.Desligadas);
    }

    [Fact]
    public void G3_13_casa_completo_nicho_faixa_e_nivel()
    {
        var v = Vault(("TRF-CPY-030", """{"tipo":"proibicao","status":"fechada","titulo":"Prova só fora da saúde","chave_copy":"nunca_prova_sub_mofu","aplica_em":{"etapa":["MOFU"],"nicho":["saude"]}}""", "x"));
        /* duas regras com a chave: repetida. Monta um vault só com a do nicho */
        var soNicho = PromptDoCopy.Snapshot("abc1234", [("TRF-CPY-030", J("""{"tipo":"proibicao","status":"fechada","titulo":"t","chave_copy":"nunca_prova_sub_mofu","aplica_em":{"etapa":["MOFU"],"nicho":["saude"]}}"""), "x")]);
        var prova = Peca("Fechamento em um dia", "Mais de 500 empresas já usam", "Ver");
        var d = PromptDoCopy.DeclaracaoDe(prova);
        Assert.False(PromptDoCopy.Crivar(prova, d, "MOFU", soNicho, "saude").Ok);
        Assert.True(PromptDoCopy.Crivar(prova, d, "MOFU", soNicho, "imobiliario").Ok);  /* outro nicho: não vale */
        Assert.True(PromptDoCopy.Crivar(prova, d, "MOFU", soNicho, null).Ok);           /* nicho desconhecido: não casa */
        Assert.Equal("repetida", v.RegraDaChaveCopy("nunca_prova_sub_mofu").Motivo);

        var comFaixa = PromptDoCopy.Snapshot("abc1234", [("T", J("""{"tipo":"proibicao","status":"fechada","titulo":"t","chave_copy":"nunca_negacao_headline","aplica_em":{"faixa":["start"],"nivel":"anuncio"}}"""), "x")]);
        var regra = comFaixa.RegraDaChave("nunca_negacao_headline")!;
        Assert.False(PromptDoCopy.Casa(regra, "MOFU", null));
        Assert.True(PromptDoCopy.Casa(regra, "MOFU", null, "start", "anuncio"));
        Assert.False(PromptDoCopy.Casa(regra, "MOFU", null, "start", "conjunto"));
    }

    [Fact]
    public void G3_17_hipotese_avisa_e_nao_reprova()
    {
        var hip = PromptDoCopy.Snapshot("abc1234", [("T", J("""{"tipo":"proibicao","status":"hipotese","titulo":"t","chave_copy":"nunca_negacao_headline","aplica_em":{}}"""), "x")]);
        var neg = Peca("Não perca mais clientes no fechamento", "corpo", "Ver");
        var r = PromptDoCopy.Crivar(neg, PromptDoCopy.DeclaracaoDe(neg), "MOFU", hip);
        Assert.True(r.Ok);
        Assert.StartsWith("aviso (hipotese): headline em negação", r.Regras.Single().Motivo);
        Assert.Null(r.MotivoDaReprovacao);
    }

    /* ---- G3-16: as guardas do ajuste ------------------------------------------------ */

    [Fact]
    public void G3_16_guardas_do_ajuste_e_campos()
    {
        Assert.Contains("aprovada", PromptDoCopy.GuardaDoAjuste(true, false));
        Assert.Contains("publicada", PromptDoCopy.GuardaDoAjuste(false, true));
        Assert.Null(PromptDoCopy.GuardaDoAjuste(false, false));
        Assert.Equal(["headline", "corpo", "cta"], PromptDoCopy.CamposDoAjuste(null).Campos);
        Assert.NotNull(PromptDoCopy.CamposDoAjuste([]).Erro);
        Assert.Contains("titulo", PromptDoCopy.CamposDoAjuste(["titulo", "cta"]).Erro);
        Assert.Equal(["cta"], PromptDoCopy.CamposDoAjuste(["cta", "cta"]).Campos);
        Assert.True(PromptDoCopy.ClientePodeGerar("ativo")); Assert.True(PromptDoCopy.ClientePodeGerar("onboarding"));
        Assert.False(PromptDoCopy.ClientePodeGerar("pausado")); Assert.False(PromptDoCopy.ClientePodeGerar(null));
    }

    /* ---- G3-23: o pedido de copy não coage em silêncio ------------------------------- */

    [Fact]
    public void G3_23_item_invalido_e_ignorado_com_aviso_e_o_misto_reparte_como_o_V1()
    {
        var ajustes = new List<string>();
        var n = PromptDoCopy.Normalizar([new("carrossel", "mofu", 2), new("estatico", "meio", 2), new("estatico", "mofu", 0), new("estatico", "bofu", 20), new("video", "misto", 5)], ajustes);
        Assert.Equal(3, ajustes.Count(a => a.StartsWith("Item ignorado")));
        Assert.Contains("BOFU: pedi 20, o teto por item é 15.", ajustes);
        Assert.DoesNotContain(n, i => i.Tipo == "estatico" && i.Funil == "mofu");
        Assert.Contains(n, i => i is { Tipo: "estatico", Funil: "bofu", NPecas: 15 });
        /* repartirMisto: 5 vídeos = 2 TOFU, 2 MOFU, 1 BOFU (o resto, uma peça por etapa a partir da primeira) */
        Assert.Contains(n, i => i is { Tipo: "video", Funil: "tofu", NPecas: 2 });
        Assert.Contains(n, i => i is { Tipo: "video", Funil: "mofu", NPecas: 2 });
        Assert.Contains(n, i => i is { Tipo: "video", Funil: "bofu", NPecas: 1 });
        Assert.Equal(20000, PromptDoCopy.LimiteReferencia);
    }

    /* ---- G3-19 a G3-22, G3-26: as guardas do Designer -------------------------------- */

    [Fact]
    public void G3_21_template_validado_livre_vira_auto_e_os_tetos()
    {
        Assert.Equal(("auto", (string?)null, (string?)null), TemplatesDoDesigner.Validar(null));
        Assert.Equal("laptop", TemplatesDoDesigner.Validar("laptop").Efetivo);
        Assert.Equal("centralizado", TemplatesDoDesigner.Validar("centralizado").Efetivo);
        var livre = TemplatesDoDesigner.Validar("livre");
        Assert.Equal("auto", livre.Efetivo); Assert.NotNull(livre.Aviso);
        var ruim = TemplatesDoDesigner.Validar("qualquer-coisa");
        Assert.Null(ruim.Efetivo); Assert.Contains("template desconhecido", ruim.Erro);
        Assert.True(TemplatesDoDesigner.TetoDeImagensPorChamada >= 1);
        Assert.True(TemplatesDoDesigner.LimiteDoAjuste > 0);
    }

    [Fact]
    public void G3_19_desfecho_do_lote()
    {
        Assert.Equal("falha", TemplatesDoDesigner.DesfechoDoLote(0, 1));
        Assert.Equal("parado", TemplatesDoDesigner.DesfechoDoLote(1, 2));
        Assert.Equal("pronto", TemplatesDoDesigner.DesfechoDoLote(2, 2));
    }

    [Fact]
    public void G3_26_provedor_pedido_sem_chave_e_config_vazia_sao_ausentes()
    {
        Assert.Null(ImagemIa.Escolher("gemini", temOpenAi: true, temGemini: false));
        Assert.Null(ImagemIa.Escolher("openai", temOpenAi: false, temGemini: true));
        Assert.Equal("openai", ImagemIa.Escolher(null, true, true));
        Assert.Equal("gemini", ImagemIa.Escolher("", false, true));
        Assert.Equal("gemini", ImagemIa.Escolher(" Gemini ", true, true));
        Assert.Null(ImagemIa.Escolher(null, false, false));
        Assert.Null(ImagemIa.Valor("")); Assert.Null(ImagemIa.Valor("  ")); Assert.Equal("gpt", ImagemIa.Valor(" gpt "));
    }

    /* ---- G3-31: o CTA para a Meta, o mapa do V1 ------------------------------------- */

    [Theory]
    [InlineData("saiba_mais", "LEARN_MORE")]
    [InlineData("ver_conhecer", "LEARN_MORE")]
    [InlineData("testar", "SIGN_UP")]
    [InlineData("falar", "CONTACT_US")]
    [InlineData("agendar_solicitar", "GET_QUOTE")]
    [InlineData("comecar", "SIGN_UP")]
    [InlineData("inscrever", "SIGN_UP")]
    [InlineData("baixar", "DOWNLOAD")]
    [InlineData("comprar", "SHOP_NOW")]
    [InlineData("outro", "LEARN_MORE")]
    public void G3_31_cta_para_meta_e_a_META_CTA_DE_do_V1(string tipo, string meta) => Assert.Equal(meta, Estruturas.CtaParaMeta(tipo));

    /* ---- G3-32: o filtro de objeto não é neutralizado pela própria lista ------------------- */

    [Fact]
    public void G3_32_o_filtro_le_so_as_linhas_do_cliente()
    {
        var c = new Cliente { Id = "1", Slug = "acme", Nome = "Acme" };
        c.Produto.FuncionalidadesChave = "concilia o extrato; avisa a divergência";
        var comApoio = PromptDaArte.ContextoVisual(c);
        var semApoio = PromptDaArte.ContextoVisual(c, comApoio: false);
        Assert.Contains("ELEMENTOS DE APOIO", comApoio);
        Assert.DoesNotContain("ELEMENTOS DE APOIO", semApoio);
        Assert.Null(PromptDaArte.ObjetoProibido("um cofre aberto", comApoio));          /* o defeito: a lista proibida "liberava" o cofre */
        Assert.Equal("objeto genérico: cofre", PromptDaArte.ObjetoProibido("um cofre aberto", semApoio));
    }

    /* ---- G3-33: o demo não inventa número, prazo nem vaga, e escreve certo ---------------- */

    [Fact]
    public void G3_33_o_demo_nao_inventa_prova_nem_escassez_e_respeita_siglas()
    {
        var c = new Cliente { Id = "1", Slug = "demo", Nome = "Acme", NichoSlug = null };
        c.Icp.DoresPrincipais.Add("CRM desatualizado trava o time");
        var lote = GeradorDemo.Gerar(c, [new(Etapa.MOFU, TipoPeca.Estatico, 4), new(Etapa.BOFU, TipoPeca.Estatico, 4)], null, 1, "abc1234");
        foreach (var p in lote.Pecas)
        {
            var tudo = $"{p.Headline} {p.Sub} {p.Cta}";
            Assert.DoesNotContain("%", tudo); Assert.DoesNotContain("vagas", tudo); Assert.DoesNotContain("15 dias", tudo);
            Assert.DoesNotContain("çãos", tudo); Assert.DoesNotContain("crm ", tudo); Assert.DoesNotContain("whatsapp", tudo);
        }
        Assert.Contains(lote.Pecas, p => p.Headline.Contains("CRM desatualizado"));
        Assert.Contains(lote.Pecas, p => p.Etapa == Etapa.MOFU && p.CrivoOk);
    }
}
