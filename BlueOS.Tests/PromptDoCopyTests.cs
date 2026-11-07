using System.Text.Json;
using BlueOS.Core.Copy;
using BlueOS.Core.Modelos;
using Xunit;

namespace BlueOS.Tests;

/* O Agente de Copy sem rede: o vault lido das notas, os blocos do system, o pedido, o crivo e a divisão (lib/copy do V1). */
public class PromptDoCopyTests
{
    static SnapshotDoVault Vault() => PromptDoCopy.Snapshot("ebe4c4f7c4bc0cd371f47c2abc8e9eacd04c3644", new[]
    {
        ("TRF-CPY-002", JsonDocument.Parse("""{"id":"TRF-CPY-002","tipo":"proibicao","status":"fechada","titulo":"Nunca negação na headline","chave_copy":"nunca_negacao_headline","aplica_em":{}}""").RootElement, "Headline em negação é proibida em qualquer etapa."),
        ("TRF-CPY-003", JsonDocument.Parse("""{"id":"TRF-CPY-003","tipo":"proibicao","status":"fechada","titulo":"Nunca pedir reunião no CTA do MOFU","chave_copy":"nunca_reuniao_cta_mofu","aplica_em":{"etapa":["MOFU"]}}""").RootElement, "No MOFU o CTA não pede reunião."),
        ("TRF-CPY-004", JsonDocument.Parse("""{"id":"TRF-CPY-004","tipo":"proibicao","status":"fechada","titulo":"Nunca prova na sub do MOFU","chave_copy":"nunca_prova_sub_mofu","aplica_em":{"etapa":["MOFU"]}}""").RootElement, "No MOFU a sub não traz prova."),
        ("TRF-CPY-006", JsonDocument.Parse("""{"id":"TRF-CPY-006","tipo":"proibicao","status":"hipotese","titulo":"Pergunta só com dor concreta","chave_copy":"pergunta_so_com_dor_concreta","aplica_em":{}}""").RootElement, "Pergunta genérica queima."),
        ("NICHO-saas", JsonDocument.Parse("""{"id":"NICHO-saas","tipo":"nicho","estruturas":[{"base":"ampla","etapa":"MOFU","ordem":1,"parte":"headline","estrutura":"dor"},{"base":"ampla","etapa":"MOFU","ordem":1,"parte":"sub","estrutura":"explica_como_funciona"},{"base":"ampla","etapa":"MOFU","ordem":1,"parte":"cta","estrutura":"ver_conhecer"}],"evitar":[{"base":"ampla","etapa":"MOFU","ordem":1,"parte":"cta","estrutura":"saiba_mais"}]}""").RootElement, ""),
        ("NICHO-imobiliario", JsonDocument.Parse("""{"id":"NICHO-imobiliario","tipo":"nicho","estruturas":[{"base":"indicacao","etapa":"MOFU","ordem":1,"parte":"headline","estrutura":"dor"},{"base":"ampla","etapa":"MOFU","ordem":2,"parte":"headline","estrutura":"pergunta"}],"evitar":[]}""").RootElement, ""),
    });

    [Fact]
    public void O_vault_vem_das_notas_com_a_chave_de_copy_e_as_estruturas_do_nicho()
    {
        var v = Vault();
        Assert.Equal(4, v.Regras.Count);
        Assert.Equal(["MOFU"], v.RegraDaChave("nunca_reuniao_cta_mofu")!.Etapas);
        Assert.Equal(2, v.Nichos.Count);
        Assert.Equal(2, v.Nicho("imobiliario")!.Estruturas.Count);
        Assert.Null(v.Nicho("juridico"));
    }

    [Fact]
    public void O_bloco_de_regras_lista_PROIBIDO_PREFERENCIA_e_o_que_falta()
    {
        var texto = PromptDoCopy.BlocoDeRegras(Vault(), out var desligadas);
        Assert.StartsWith("REGRAS FECHADAS (vault ebe4c4f)", texto);
        Assert.Contains("- PROIBIDO (TRF-CPY-002, nunca_negacao_headline): Nunca negação na headline. Headline em negação", texto);
        Assert.Contains("- PREFERÊNCIA, ainda hipótese (TRF-CPY-006, pergunta_so_com_dor_concreta)", texto);
        Assert.Contains("Sem regra viva nesta versão: prefira_contexto_ou_fragmento_sub, cta_pesa_pouco.", texto);
        Assert.Equal(2, desligadas.Count);
        Assert.Contains("vault sem versão", PromptDoCopy.BlocoDeRegras(null, out _));
    }

    [Fact]
    public void A_orientacao_do_lote_junta_a_geral_e_a_de_nicho_e_diz_a_origem_de_cada_etapa()
    {
        var b = PromptDoCopy.BlocosDoVaultPara(Vault(), "imobiliario", ["MOFU", "BOFU"]);
        Assert.Equal("NICHO-saas", b.GeralId);
        Assert.Equal("NICHO-imobiliario", b.NichoId);
        Assert.StartsWith("ORIENTAÇÃO DO LOTE (vault ebe4c4f)", b.Orientacao);
        Assert.Contains("ORIENTAÇÃO GERAL (NICHO-saas)", b.Orientacao);
        Assert.Contains("  HEADLINE (ângulo): 1. dor (indicacao) · 2. pergunta (ampla)", b.Orientacao);
        Assert.Contains("EVITAR: MOFU CTA (tipo): saiba_mais", b.Orientacao);
        Assert.Contains("BOFU: sem base na orientação geral; vale a régua.", b.Orientacao);
        Assert.Equal("geral", b.OrigemPorEtapa["MOFU"]);
        Assert.Equal("v8", b.OrigemPorEtapa["BOFU"]);
        Assert.Equal((true, true), b.FontesPorEtapa["MOFU"]);

        var semNicho = PromptDoCopy.BlocosDoVaultPara(Vault(), null, ["MOFU"]);
        Assert.Contains("sem nicho: preencha o card Tráfego do cliente. Até lá, todas as peças seguem a orientação geral.", semNicho.Orientacao);
    }

    [Fact]
    public void A_divisao_poe_uma_peca_em_cinco_no_nicho_e_o_pedido_diz_quem_segue_o_que()
    {
        var b = PromptDoCopy.BlocosDoVaultPara(Vault(), "imobiliario", ["MOFU"]);
        var f = PromptDoCopy.DivisaoDoGrupo(b, "MOFU", 5, 0, 0);
        Assert.Equal(1, f.Count(x => x == "nicho"));
        Assert.Equal("as peças 1 a 4 seguem a ORIENTAÇÃO GERAL; a peça 5 segue a ORIENTAÇÃO DE NICHO", PromptDoCopy.TextoDaDivisao(f));
        Assert.Equal("todas seguem a ORIENTAÇÃO GERAL", PromptDoCopy.TextoDaDivisao(["geral", "geral"]));
        Assert.Equal("", PromptDoCopy.TextoDaDivisao([null, null]));
        /* sem geral, tudo no nicho */
        var soNicho = PromptDoCopy.BlocosDoVaultPara(PromptDoCopy.Snapshot("abc1234567", new[] { ("NICHO-imobiliario", JsonDocument.Parse("""{"tipo":"nicho","estruturas":[{"etapa":"MOFU","parte":"headline","ordem":1,"estrutura":"dor","base":"ampla"}],"evitar":[]}""").RootElement, "") }), "imobiliario", ["MOFU"]);
        Assert.All(PromptDoCopy.DivisaoDoGrupo(soNicho, "MOFU", 3, 0, 0), x => Assert.Equal("nicho", x));
        Assert.Equal(("nicho", "NICHO-imobiliario"), PromptDoCopy.OrigemDaPeca(soNicho, "MOFU", null));
    }

    [Fact]
    public void O_pedido_de_geracao_e_o_do_V1_com_o_briefing_por_ultimo()
    {
        var p = PromptDoCopy.PedidoDeGeracao([(new ItemDoPedido("estatico", "mofu", 3), ["geral", "geral", "nicho"])], "fale do fechamento do mês", null);
        Assert.StartsWith("Gere EXATAMENTE este pedido (um grupo por linha, com a quantidade pedida):\n- 3 peça(s) do tipo ESTÁTICO para o funil MOFU: as peças 1 a 2 seguem a ORIENTAÇÃO GERAL; a peça 3 segue a ORIENTAÇÃO DE NICHO", p);
        Assert.Contains(PromptDoCopy.InstrucaoDaDeclaracao, p);
        Assert.EndsWith("BRIEFING DO GESTOR (prioridade sobre o contexto e sobre as orientações geral e de nicho; nunca sobre um PROIBIDO):\nfale do fechamento do mês", p);
    }

    [Fact]
    public void O_contexto_do_cliente_sai_em_markdown_com_ICP_produto_e_estrategia()
    {
        var c = new Cliente { Id = "1", Slug = "acme", Nome = "Acme" };
        c.Icp.CargoDecisor = "CFO"; c.Icp.Segmento = "SaaS de cobrança"; c.Icp.DoresPrincipais.Add("fecha o mês na planilha");
        c.Produto.NomeCorreto = "Acme Pay"; c.Produto.Descricao = "conciliação automática"; c.Produto.Diferenciais = "integra o ERP; concilia em 1 dia";
        c.Estrategia.PromessaPrincipal = "fechar o mês em um dia";
        var (texto, temIcp) = PromptDoCopy.ContextoDoCliente(c, new PromptDoCopy.Inteligencia("direto", null, null, []));
        Assert.True(temIcp);
        Assert.StartsWith("# CLIENTE: Acme\n\n## ICP 1 (público-alvo)\n- Segmento: SaaS de cobrança\n", texto);
        Assert.Contains("- Cargo decisor (a copy é DIRECIONADA a ele): CFO", texto);
        Assert.Contains("- Dores principais:\n  - fecha o mês na planilha", texto);
        Assert.Contains("## Produto\n- Nome correto: Acme Pay", texto);
        Assert.Contains("- Diferenciais:\n  - integra o ERP\n  - concilia em 1 dia", texto);
        Assert.Contains("## Estratégia\n- Promessa principal: fechar o mês em um dia", texto);
        Assert.Contains("## Inteligência\n- Voz do cliente: direto", texto);
        var vazio = new Cliente { Id = "2", Slug = "x", Nome = "X" }; vazio.Icp.Tipo = "";
        Assert.False(PromptDoCopy.ContextoDoCliente(vazio).TemIcp);
    }

    [Fact]
    public void O_system_tem_a_ordem_fixa_regua_regras_orientacao_cliente()
    {
        var b = PromptDoCopy.BlocosDoVaultPara(Vault(), "imobiliario", ["MOFU"]);
        var s = PromptDoCopy.SystemDoCopy("R1 ...", b, "# CLIENTE: Acme");
        Assert.Equal(4, s.Count);
        Assert.StartsWith("Você é o Agente de Copy da Blue Ocean.", s[0]); Assert.EndsWith("R1 ...", s[0]);
        Assert.StartsWith("REGRAS FECHADAS", s[1]); Assert.StartsWith("ORIENTAÇÃO DO LOTE", s[2]); Assert.StartsWith("# CLIENTE", s[3]);
        Assert.Equal(3, PromptDoCopy.SystemDoCopy("R1", b, "  ").Count);
    }

    [Fact]
    public void O_crivo_reprova_pela_declaracao_e_pelo_texto_so_nas_proibicoes_fechadas_da_etapa()
    {
        var v = Vault();
        var neg = new PecaDoModelo("Não perca mais clientes no fechamento", "corpo", "Ver como funciona", "dor", [], "contexto_da_dor", "ver_conhecer");
        var a = PromptDoCopy.Avaliar(neg, "MOFU", v);
        Assert.NotNull(a.Declaracao); Assert.False(a.Crivo.Ok); Assert.Contains("nunca_negacao_headline: headline em negação", a.Motivo);

        var declarada = new PecaDoModelo("Fechamento sem susto", "corpo", "Ver como funciona", "dor", ["negacao"], "contexto_da_dor", "ver_conhecer");
        Assert.Contains("a declaração traz negacao", PromptDoCopy.Avaliar(declarada, "MOFU", v).Motivo);

        var reuniao = new PecaDoModelo("Fechamento em um dia", "corpo", "Agende uma demonstração", "dor", [], "contexto_da_dor", "falar");
        Assert.Contains("nunca_reuniao_cta_mofu", PromptDoCopy.Avaliar(reuniao, "MOFU", v).Motivo);
        /* no BOFU a regra do CTA não se aplica */
        Assert.Null(PromptDoCopy.Avaliar(reuniao, "BOFU", v).Motivo);

        var prova = new PecaDoModelo("Fechamento em um dia", "Mais de 500 empresas já usam", "Ver como funciona", "dor", [], "explica_como_funciona", "ver_conhecer");
        Assert.Contains("prova na sub", PromptDoCopy.Avaliar(prova, "MOFU", v).Motivo);

        var foraDoVocabulario = new PecaDoModelo("x", "y", "z", "piada", [], "contexto_da_dor", "ver_conhecer");
        Assert.Equal("a peça veio sem a estrutura declarada no vocabulário", PromptDoCopy.Avaliar(foraDoVocabulario, "MOFU", v).Motivo);

        var ok = new PecaDoModelo("Fechamento em um dia, sem planilha", "O ERP manda, o Acme concilia.", "Ver como funciona", "dor", [], "explica_como_funciona", "ver_conhecer");
        var r = PromptDoCopy.Avaliar(ok, "MOFU", v);
        Assert.Null(r.Motivo); Assert.True(r.Crivo.Ok); Assert.Equal(3, r.Crivo.Regras.Count);
        Assert.Null(PromptDoCopy.Crivar(ok, r.Declaracao, "MOFU", null).Ok);
        var json = PromptDoCopy.CrivoParaGravar(r.Crivo, "geracao");
        Assert.Contains("\"fase\":\"geracao\"", json); Assert.Contains("\"chave\":\"nunca_negacao_headline\"", json);
    }

    [Fact]
    public void O_misto_se_reparte_e_o_esquema_tem_a_declaracao_obrigatoria()
    {
        var n = PromptDoCopy.Normalizar([new("estatico", "misto", 5), new("video", "misto", 3), new("estatico", "mofu", 2)]);
        Assert.Contains(n, i => i is { Tipo: "estatico", Funil: "mofu", NPecas: 5 }); /* 3 do misto + 2 do pedido */
        Assert.Contains(n, i => i is { Tipo: "estatico", Funil: "bofu", NPecas: 2 });
        Assert.Equal(3, n.Count(i => i.Tipo == "video"));
        Assert.Equal("TOFU", PromptDoCopy.EtapaDoFunil("tofu")); Assert.Equal("MOFU", PromptDoCopy.EtapaDoFunil("misto"));

        var schema = JsonSerializer.Serialize(PromptDoCopy.SchemaDoLote());
        Assert.Contains("\"grupos\"", schema); Assert.Contains("\"angulo_headline\"", schema); Assert.Contains("\"additionalProperties\":false", schema);
        var so = PromptDoCopy.SchemaDaPeca(["cta"]);
        var props = (Dictionary<string, object>)so["properties"];
        Assert.True(props.ContainsKey("cta")); Assert.False(props.ContainsKey("headline"));

        var grupos = PromptDoCopy.LerGrupos("""{"grupos":[{"tipo":"estatico","funil":"mofu","pecas":[{"headline":"H","corpo":"C","cta":"T","angulo_headline":"dor","forma_headline":[],"papel_sub":"fragmento","tipo_cta":"testar"}]}]}""");
        Assert.Single(grupos); Assert.Equal("H", grupos[0].Pecas[0].Headline); Assert.Equal("testar", grupos[0].Pecas[0].TipoCta);
    }
}
