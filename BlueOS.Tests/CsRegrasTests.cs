using System.Text.Json;
using BlueOS.Core.Cs;
using Xunit;

namespace BlueOS.Tests;

/* As regras do Agente de CS sem banco: nota e faixa, pilares, cargos, alcance, contrato por squad, prazos e decisões. */
public class CsRegrasTests
{
    [Theory]
    [InlineData(0, null, null, 50)]      // só o valor: (0+100)/2
    [InlineData(100, 0, "baixo", 100)]   // valor máximo, risco zero
    [InlineData(-100, 100, "critico", 0)]
    [InlineData(49, 15, "baixo", 81)]    // base 75 (74.5 arredonda pra cima); 75*0.4 + 85*0.6 = 81
    [InlineData(20, 50, "sem_dados", 60)] // rótulo sem_dados ignora o risco: fica na base
    public void A_nota_pesa_60_por_cento_de_retencao_e_40_de_valor(int valor, int? risco, string? rotulo, int esperado) =>
        Assert.Equal(esperado, CsRegras.Nota(valor, risco, rotulo));

    [Theory]
    [InlineData(80, "excelente")] [InlineData(79, "saudavel")] [InlineData(66, "saudavel")] [InlineData(65, "atencao")]
    [InlineData(50, "atencao")] [InlineData(49, "critico")] [InlineData(0, "critico")] [InlineData(null, "sem_analise")]
    public void A_regua_das_faixas(int? nota, string faixa) => Assert.Equal(faixa, CsRegras.FaixaDaNota(nota));

    [Fact]
    public void Os_apelidos_das_faixas_sao_as_cores_da_tela()
    {
        Assert.Equal("azul", CsRegras.ApelidoDaFaixa("excelente"));
        Assert.Equal("vermelho", CsRegras.ApelidoDaFaixa("critico"));
        Assert.Equal("cinza", CsRegras.ApelidoDaFaixa("sem_base"));
        Assert.Equal("cinza", CsRegras.ApelidoDaFaixa("sem_analise"));
    }

    const string ComFonte = """{"entrega":{"score":15,"sem_dados":false},"performance":{"score":0,"sem_dados":true},"relacionamento":{"sem_dados":true},"engajamento":{"sem_dados":true},"comercial":{"sem_dados":true}}""";
    const string SemFonte = """{"entrega":{"sem_dados":true},"performance":{"score":0,"sem_dados":true},"relacionamento":{"sem_dados":true},"engajamento":{"sem_dados":true},"comercial":{"sem_dados":true}}""";

    [Fact]
    public void Analise_sem_fonte_em_pilar_nenhum_nao_vira_nota()
    {
        Assert.Equal((null, "sem_base"), CsRegras.NotaDaAnalise(0, 10, "baixo", SemFonte));
        Assert.Equal((null, "sem_base"), CsRegras.NotaDaAnalise(null, 10, "baixo", ComFonte));
        Assert.Equal((null, "sem_base"), CsRegras.NotaDaAnalise(40, 10, "baixo", null));
        var (nota, faixa) = CsRegras.NotaDaAnalise(49, 15, "baixo", ComFonte);
        Assert.Equal(81, nota); Assert.Equal("excelente", faixa);
    }

    [Fact]
    public void Pilar_desligado_nao_conta_como_fonte_e_sub_scores_em_string_com_chave_sobrando_e_lido()
    {
        var desligado = """{"performance":{"score":80,"desligado":true},"entrega":{"sem_dados":true},"relacionamento":{"sem_dados":true},"engajamento":{"sem_dados":true},"comercial":{"sem_dados":true}}""";
        Assert.True(CsRegras.SemFonteNenhuma(CsRegras.LerSubScores(desligado)));
        Assert.False(CsRegras.SemFonteNenhuma(CsRegras.LerSubScores(ComFonte + "}")));
        Assert.False(CsRegras.SemFonteNenhuma(CsRegras.LerSubScores(JsonSerializer.Serialize(ComFonte))));
        Assert.Null(CsRegras.LerSubScores("[1,2]"));
    }

    [Fact]
    public void O_tier_e_o_melhor_cargo_e_coordenador_pra_cima_muda_o_sem_performance()
    {
        Assert.Equal(1, CsRegras.Tier(["gestor", "Super_Admin"]));
        Assert.Equal(CsRegras.TierSemHierarquia, CsRegras.Tier(["rh"]));
        Assert.True(CsRegras.PodeMudarSemPerformance(["coordenador"]));
        Assert.False(CsRegras.PodeMudarSemPerformance(["head"]));
        Assert.True(CsRegras.PodeAbrirSetupCs(["head"]));
        Assert.False(CsRegras.PodeAbrirSetupCs(["gestor"]));
        Assert.True(CsRegras.PodeAdministrar(["head_departamento"]));
        Assert.False(CsRegras.PodeAdministrar(["onboarding_manager"]));
        Assert.True(CsRegras.PodeCriarCliente(["engenheiro_ia"]));
    }

    [Fact]
    public void Alcance_por_qualquer_squad_e_fail_closed()
    {
        Assert.True(CsRegras.ClienteNoAlcance(null, null));
        Assert.True(CsRegras.ClienteNoAlcance(["a", "b"], ["c", "B"]));
        Assert.False(CsRegras.ClienteNoAlcance(["a"], ["c"]));
        Assert.False(CsRegras.ClienteNoAlcance(["a"], []));
        Assert.False(CsRegras.ClienteNoAlcance([], ["a"]));
    }

    [Fact]
    public void O_dinheiro_e_o_do_squad_que_olha_e_incluso_nunca_vira_zero()
    {
        var ghost = new CsRegras.Contrato("ghost", "mrr", 4000, null);
        var abyssal = new CsRegras.Contrato("abyssal", "tcv", null, 10000);
        var contratos = new[] { ghost, abyssal };
        Assert.Equal("proprio", CsRegras.LeituraDoSquad(contratos, "abyssal", "ghost").Situacao);
        Assert.Same(abyssal, CsRegras.LeituraDoSquad(contratos, "abyssal", "ghost").Contrato);
        var incluso = CsRegras.LeituraDoSquad([ghost], "abyssal", "ghost");
        Assert.Equal("incluso", incluso.Situacao); Assert.Equal("ghost", incluso.NoSquadId);
        Assert.Equal("sem_contrato", CsRegras.LeituraDoSquad([], "abyssal", null).Situacao);
        Assert.Same(ghost, CsRegras.LeituraDoSquad(contratos, null, "ghost").Contrato);
        Assert.Equal("R$10000 (contrato)", CsRegras.Dinheiro("tcv", null, 10000, null));
        Assert.Equal("R$4000/mês", CsRegras.Dinheiro("mrr", 4000, null, null));
        Assert.Equal("incluso no contrato do GHOST", CsRegras.Dinheiro(null, null, null, "GHOST"));
        Assert.Null(CsRegras.Dinheiro("mrr", 0, null, null));
    }

    [Fact]
    public void Ponto_de_vista_recorte_e_resolucao_do_squad_pedido()
    {
        Assert.Equal("a", CsRegras.PontoDeVista(["a"], null));
        Assert.Null(CsRegras.PontoDeVista(["a", "b"], null));
        Assert.Equal("b", CsRegras.PontoDeVista(["a", "b"], "b"));
        Assert.Null(CsRegras.PontoDeVista(["a"], "z"));
        Assert.Equal("z", CsRegras.PontoDeVista(null, "z"));
        Assert.Null(CsRegras.RecorteDeSquad(["a"], null));
        Assert.Null(CsRegras.RecorteDeSquad(["a"], "todos"));
        Assert.Equal("a", CsRegras.RecorteDeSquad(["a"], "a"));
        var squads = new[] { ("id-1", "GHOST"), ("id-2", "Abyssal") };
        Assert.Equal("id-2", CsRegras.ResolverSquadPedido(" abyssal ", squads));
        Assert.Equal("id-1", CsRegras.ResolverSquadPedido("ID-1", squads));
        Assert.Null(CsRegras.ResolverSquadPedido("poseidon", squads));
    }

    [Fact]
    public void Grupo_frio_conta_dias_de_calendario_em_brasilia_e_sem_data_nao_e_frio()
    {
        var agora = new DateTime(2026, 10, 30, 2, 30, 0, DateTimeKind.Utc); // 29/10 23:30 em Brasília
        Assert.Equal((false, null), CsRegras.GrupoFrio(null, agora));
        Assert.Equal((false, 0), CsRegras.GrupoFrio(new DateTime(2026, 10, 30, 1, 0, 0, DateTimeKind.Utc), agora)); // mesmo dia BR
        Assert.Equal((false, 2), CsRegras.GrupoFrio(new DateTime(2026, 10, 27, 23, 0, 0, DateTimeKind.Utc), agora));
        Assert.Equal((true, 3), CsRegras.GrupoFrio(new DateTime(2026, 10, 27, 1, 0, 0, DateTimeKind.Utc), agora)); // 26/10 22:00 BR
    }

    [Fact]
    public void Situacao_do_prazo_e_prazo_do_passo()
    {
        var hoje = new DateTime(2026, 10, 30, 15, 0, 0, DateTimeKind.Utc);
        Assert.Equal(("dentro_do_prazo", 0), CsRegras.SituacaoDoPrazo(false, new DateOnly(2026, 10, 30), null, true, hoje));
        Assert.Equal(("atrasada", 5), CsRegras.SituacaoDoPrazo(false, new DateOnly(2026, 10, 25), null, true, hoje));
        Assert.Equal(("nao_entregue", 6), CsRegras.SituacaoDoPrazo(false, new DateOnly(2026, 10, 24), null, true, hoje));
        Assert.Equal(("sem_prazo", null), CsRegras.SituacaoDoPrazo(false, null, null, true, hoje));
        Assert.Equal(("entregue", null), CsRegras.SituacaoDoPrazo(true, new DateOnly(2026, 10, 24), hoje, false, hoje));
        Assert.Equal(("entregue_atrasado", 6), CsRegras.SituacaoDoPrazo(true, new DateOnly(2026, 10, 24), hoje, true, hoje));
        var noite = new DateTime(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc); // 30/10 22:00 em Brasília
        Assert.Equal(new DateOnly(2026, 10, 30), CsRegras.PrazoDoPasso("imediato", noite));
        Assert.Equal(new DateOnly(2026, 11, 2), CsRegras.PrazoDoPasso("3d", noite));
        Assert.Equal(new DateOnly(2026, 12, 1), CsRegras.PrazoDoPasso("2026-12-01", noite));
        Assert.Null(CsRegras.PrazoDoPasso("quando der", noite));
    }

    [Fact]
    public void Titulo_cortado_na_palavra_e_nome_normalizado()
    {
        Assert.Equal("curto", CsRegras.CorteDoTitulo("curto"));
        var longo = "Executar e publicar a LP do cliente hoje ou postar no grupo até o fim do dia o reescopo formal";
        var corte = CsRegras.CorteDoTitulo(longo);
        Assert.EndsWith("…", corte); Assert.True(corte.Length <= 71); Assert.DoesNotContain("reescopo", corte);
        Assert.Equal("Fechar o alinhamento", CsRegras.TituloDaResposta("\"Fechar o alinhamento\""));
        Assert.Equal("Fechar o alinhamento", CsRegras.TituloDaResposta("Fechar o alinhamento."));
        Assert.Null(CsRegras.TituloDaResposta(new string('a', 91)));
        Assert.Equal("kaua", CsRegras.PrimeiroNome("Kauã Silva"));
        Assert.Equal("joao pedro", CsRegras.Normalizar("  João-Pedro! "));
    }

    [Fact]
    public void Decisao_humana_valida_o_pedido_e_faz_merge_por_indice()
    {
        var (_, r1) = CsRegras.ValidarDecisao("", 0, "fechado", "x", CsRegras.StatusPasso, true);
        Assert.Equal(400, r1!.Http);
        var (_, r2) = CsRegras.ValidarDecisao("id", 0, "fechado", " ", CsRegras.StatusPasso, true);
        Assert.Contains("justificativa", r2!.Erro);
        var (_, r3) = CsRegras.ValidarDecisao("id", 0, "cumprida", null, CsRegras.StatusPasso, true);
        Assert.Contains("status inválido", r3!.Erro);
        var (p, r4) = CsRegras.ValidarDecisao("id", 1, "atrasada", null, CsRegras.StatusPromessa, false);
        Assert.Null(r4); Assert.Equal(1, p!.Indice); Assert.Null(p.Justificativa);
        Assert.Equal(2, CsRegras.TamanhoDaLista("[{},{}]"));
        Assert.Equal(0, CsRegras.TamanhoDaLista("{}"));
        Assert.NotNull(CsRegras.ValidarIndice(2, 2));
        Assert.Null(CsRegras.ValidarIndice(1, 2));
        var mapa = CsRegras.AplicarDecisao("""{"0":{"status":"fechado"}}""", 1, new { status = "ignorado", por = "u" });
        using var d = JsonDocument.Parse(mapa);
        Assert.Equal("fechado", d.RootElement.GetProperty("0").GetProperty("status").GetString());
        Assert.Equal("ignorado", d.RootElement.GetProperty("1").GetProperty("status").GetString());
    }

    [Fact]
    public void Registro_do_time_recusa_texto_em_branco_e_valor_fora_da_forma()
    {
        Assert.NotNull(CsRegras.ValidarTexto("   ").Recusa);
        Assert.Equal("ok", CsRegras.ValidarTexto(" ok ").Texto);
        Assert.Equal(1234.5m, CsRegras.ValidarValor(JsonDocument.Parse("\"1234,50\"").RootElement).Valor);
        Assert.Equal(10m, CsRegras.ValidarValor(JsonDocument.Parse("10").RootElement).Valor);
        Assert.NotNull(CsRegras.ValidarValor(JsonDocument.Parse("\"1e3\"").RootElement).Recusa);
        Assert.NotNull(CsRegras.ValidarValor(JsonDocument.Parse("-1").RootElement).Recusa);
        Assert.NotNull(CsRegras.ValidarValor(null).Recusa);
        var (pedido, recusa) = CsRegras.ValidarPedidoUpsell(" seo ", "", JsonDocument.Parse("\"2500\"").RootElement, "2026-10-01", null);
        Assert.Null(recusa); Assert.Equal("seo", pedido!.Slug); Assert.Equal("seo", pedido.Nome); Assert.Equal(new DateOnly(2026, 10, 1), pedido.VendidoEm);
        Assert.NotNull(CsRegras.ValidarPedidoUpsell("", "", JsonDocument.Parse("1").RootElement, null, null).Recusa);
    }

    [Fact]
    public void Abyssal_e_divers_sao_fixos_e_os_alertas_saem_critico_primeiro_por_nota()
    {
        Assert.True(CsRegras.SquadSemPerformanceFixo(" divers "));
        Assert.False(CsRegras.SquadSemPerformanceFixo("GHOST"));
        var ordem = CsRegras.OrdenarAlertas([
            new("b", "B", "alto", 10, "critico", "vermelho", null, ""),
            new("a", "A", "critico", 40, "critico", "vermelho", null, ""),
            new("c", "C", "critico", 40, "critico", "vermelho", null, ""),
            new("d", "D", "critico", 5, "critico", "vermelho", null, ""),
        ]).Select(a => a.ClienteId).ToList();
        Assert.Equal(["d", "a", "c", "b"], ordem);
        Assert.Contains("mostrando 8 de 32", CsRegras.CorteDeclarado(8, 32));
    }
}
