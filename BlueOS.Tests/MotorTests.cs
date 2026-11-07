using BlueOS.Core.Api;
using BlueOS.Core.Copy;
using BlueOS.Core.Fluxo;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Setup;

namespace BlueOS.Tests;

/* Os números esperados vêm do estudo e da validação do V1 de 11/10/2026
   (_bmad-output/architecture-motor-trafego/evidencias-2026-10-11/planejador_diferencial.md),
   já com as pontas das faixas decididas em 12/10 (abaixo do mínimo monta MOFU; R$ 7.500 é f2). */
public class PlanejadorTests
{
    static readonly ParametrosMotor P = ParametrosMotor.PadraoDoVault;
    static Plano Plan(decimal reais, bool lista = false, bool site = false) =>
        Planejador.Planejar(new EntradaDoPlano(Dinheiro.CentavosDe(reais), "estrategia", lista, site), P);

    [Theory]
    [InlineData(1499.99, FaixaId.AbaixoMinimo)]
    [InlineData(1500, FaixaId.F1Mofu)]
    [InlineData(3000, FaixaId.F1Mofu)]
    [InlineData(3000.01, FaixaId.F2MofuBofu)]
    [InlineData(7500, FaixaId.F2MofuBofu)]
    [InlineData(7500.01, FaixaId.F3Completo)]
    public void A_faixa_tem_os_limites_de_cima_exclusivos(decimal reais, FaixaId esperada)
        => Assert.Equal(esperada, Faixa.DoBudget(Dinheiro.CentavosDe(reais), P.Faixas));

    [Fact]
    public void Tres_mil_por_mes_monta_quatro_conjuntos_de_MOFU_a_25_por_dia()
    {
        var plano = Plan(3000);
        Assert.Equal(FaixaId.F1Mofu, plano.Faixa);
        Assert.Equal(100_00, plano.DiarioCentavos);
        Assert.Equal(4, plano.Conjuntos.Count);
        Assert.All(plano.Conjuntos, c => { Assert.Equal(Etapa.MOFU, c.Etapa); Assert.Equal(25_00, c.DiarioCentavos); });
        Assert.Equal(["advantage", "interesses_nicho", "interesses_alternativos", "interesses_decisor"], plano.Conjuntos.Select(c => c.Publico).ToArray());
        Assert.Equal(0, plano.NaoAlocadoCentavos);
        Assert.Empty(plano.Reservado);
    }

    [Fact]
    public void Sete_mil_e_quinhentos_e_f2_com_78_22_e_BOFU_reservado_sem_lista()
    {
        var plano = Plan(7500);
        Assert.Equal(FaixaId.F2MofuBofu, plano.Faixa);
        Assert.Equal(4, plano.Conjuntos.Count);
        Assert.All(plano.Conjuntos, c => Assert.Equal(48_75, c.DiarioCentavos));
        var bofu = Assert.Single(plano.Reservado);
        Assert.Equal(Etapa.BOFU, bofu.Etapa);
        Assert.Equal(55_00, bofu.DiarioCentavos);
    }

    [Fact]
    public void Doze_mil_monta_TOFU_de_20_e_MOFU_no_teto_de_50_com_sobra_de_100()
    {
        var plano = Plan(12000);
        Assert.Equal(FaixaId.F3Completo, plano.Faixa);
        var tofu = Assert.Single(plano.Conjuntos.Where(c => c.Etapa == Etapa.TOFU));
        Assert.Equal(20_00, tofu.DiarioCentavos);
        Assert.Equal("interesses_amplos", tofu.Publico);
        Assert.Equal(4, plano.Conjuntos.Count(c => c.Etapa == Etapa.MOFU));
        Assert.All(plano.Conjuntos.Where(c => c.Etapa == Etapa.MOFU), c => Assert.Equal(50_00, c.DiarioCentavos));
        Assert.Equal(80_00, Assert.Single(plano.Reservado).DiarioCentavos);
        Assert.Equal(100_00, plano.NaoAlocadoCentavos);
        Assert.Contains(plano.Avisos, a => a.Contains("bateu no teto"));
    }

    [Fact]
    public void Com_lista_o_BOFU_monta_e_o_lookalike_entra_no_MOFU()
    {
        var plano = Plan(6000, lista: true, site: true);
        Assert.Equal(2, plano.Conjuntos.Count(c => c.Etapa == Etapa.BOFU));
        Assert.Contains(plano.Conjuntos, c => c.Publico == "lookalike");
        Assert.Empty(plano.Reservado);
        var soma = plano.Conjuntos.Sum(c => c.DiarioCentavos) + plano.Reservado.Sum(r => r.DiarioCentavos) + plano.NaoAlocadoCentavos;
        Assert.Equal(plano.DiarioCentavos, soma);
    }

    [Fact]
    public void Abaixo_do_minimo_monta_a_estrutura_minima_so_MOFU()
    {
        var plano = Plan(600);
        Assert.Equal(FaixaId.AbaixoMinimo, plano.Faixa);
        var unico = Assert.Single(plano.Conjuntos);
        Assert.Equal(20_00, unico.DiarioCentavos);
        Assert.Equal("advantage", unico.Publico);
    }

    [Fact]
    public void A_soma_nunca_passa_do_diario_em_nenhum_budget()
    {
        for (var reais = 500; reais <= 20000; reais += 137)
            foreach (var lista in new[] { false, true })
            {
                var plano = Plan(reais, lista, lista);
                var soma = plano.Conjuntos.Sum(c => c.DiarioCentavos) + plano.Reservado.Sum(r => r.DiarioCentavos) + plano.NaoAlocadoCentavos;
                Assert.Equal(plano.DiarioCentavos, soma);
                Assert.True(plano.NaoAlocadoCentavos >= 0);
            }
    }

    [Fact]
    public void A_estrutura_proposta_nomeia_como_o_Gerenciador_e_calcula_o_MQL_esperado()
    {
        var e = Estrutura.Montar(3000, false, false, 50, "imobiliario", P, new DateOnly(2026, 10, 6));
        Assert.Equal("[\U0001F30A Blue] [BR] [ABO] [MOFU] [LEADS] [FORM-L] 06 Outubro | Estáticos", e.NomeDaCampanha);
        Assert.Equal("1 · Advantage+ (amplo) · imobiliário", e.Conjuntos[0].Nome);
        Assert.Equal("AN · 1 · Advantage+ (amplo) · imobiliário", e.Conjuntos[0].Anuncio);
        Assert.Equal("1-4-1 · MOFU", e.Rotulo);
        Assert.Equal(60, e.MqlEsperado); // R$ 100/dia × 30 ÷ R$ 50
    }
}

public class ArvoreTests
{
    static readonly ParametrosMotor P = ParametrosMotor.PadraoDoVault;
    static EntradaConjunto Conj(long gasto, int a = 0, int b = 0, int c = 0, int avaliados = 0, int pendentes = 0, int dias = 12, Formato f = Formato.Imagem, Leitura l = Leitura.Medida, bool cobertura = true, int anuncios = 1, MemoriaDoConjunto? mem = null)
        => new("x", "adset", "MOFU 1", f, anuncios, dias, gasto, a + b + c + pendentes + Math.Max(0, avaliados - a - b - c), avaliados, pendentes, a, b, c, l, cobertura, mem);
    static readonly EntradaAoVivo Ativo = new(true, 39_00);

    [Fact]
    public void Video_fica_fora() => Assert.Equal(Acao.SEM_BASE, Arvore.Decidir(Conj(100_00, f: Formato.Video), Ativo, 60_00, true, P).Acao);

    [Fact]
    public void Leitura_falha_so_aguarda() => Assert.Equal(Acao.AGUARDAR, Arvore.Decidir(Conj(500_00, l: Leitura.SemAcesso), Ativo, 60_00, true, P).Acao);

    [Fact]
    public void Sem_teto_nao_decide_dinheiro()
    {
        var d = Arvore.Decidir(Conj(500_00), Ativo, null, true, P);
        Assert.Equal(Acao.MANTER, d.Acao);
        Assert.Contains("teto_ppl", d.Chaves);
    }

    [Fact]
    public void Queimou_uma_vez_e_meia_o_teto_sem_MQL_pausa_mesmo_dentro_dos_7_dias()
    {
        var d = Arvore.Decidir(Conj(95_00, dias: 3), Ativo, 60_00, true, P);
        Assert.Equal(Acao.PAUSAR, d.Acao);
        Assert.Equal(["corte_sem_mql"], d.Chaves);
    }

    [Fact]
    public void Entre_uma_e_uma_vez_e_meia_o_teto_sem_MQL_alerta()
        => Assert.Equal(Acao.ALERTAR, Arvore.Decidir(Conj(66_00, dias: 3), Ativo, 60_00, true, P).Acao);

    [Fact]
    public void Lead_pendente_segura_a_pausa_por_tres_dias_e_depois_pausa()
    {
        var segurando = Arvore.Decidir(Conj(200_00, pendentes: 2, mem: new(null, 1, 0)), Ativo, 60_00, true, P);
        Assert.Equal(Acao.ALERTAR, segurando.Acao);
        Assert.True(segurando.SegurouPorPendentes);
        var venceu = Arvore.Decidir(Conj(200_00, pendentes: 2, mem: new(null, 3, 0)), Ativo, 60_00, true, P);
        Assert.Equal(Acao.PAUSAR, venceu.Acao);
    }

    [Fact]
    public void Sem_cobertura_o_corte_nao_passa_de_alerta()
        => Assert.Equal(Acao.ALERTAR, Arvore.Decidir(Conj(200_00, cobertura: false), Ativo, 60_00, true, P).Acao);

    [Fact]
    public void Conjunto_que_ganha_duplica_com_mais_15_reais_ate_o_teto_de_montagem()
    {
        var d = Arvore.Decidir(Conj(468_00, a: 6, b: 4, c: 2, avaliados: 14), Ativo, 60_00, true, P);
        Assert.Equal(Acao.DUPLICAR, d.Acao);
        Assert.Equal(50_00, d.CopiaDiarioCentavos); // 39 + 15 = 54, teto 50
    }

    [Fact]
    public void Ganhador_pausado_na_Meta_nao_escala()
        => Assert.Equal(Acao.MANTER, Arvore.Decidir(Conj(468_00, a: 6, b: 4, c: 2, avaliados: 14), new EntradaAoVivo(false, 39_00), 60_00, true, P).Acao);

    [Fact]
    public void Uma_copia_viva_por_original()
        => Assert.Equal(Acao.MANTER, Arvore.Decidir(Conj(468_00, a: 6, b: 4, c: 2, avaliados: 14, mem: new(5, null, 1)), Ativo, 60_00, true, P).Acao);

    [Fact]
    public void Caro_demais_troca_o_criativo()
    {
        var d = Arvore.Decidir(Conj(468_00, c: 2, avaliados: 9), Ativo, 60_00, true, P);
        Assert.Equal(Acao.TROCAR_CRIATIVO, d.Acao);
        Assert.DoesNotContain(d.Avisos, a => a.Ref == "revisar_oferta");
        var semConversao = Arvore.Decidir(Conj(468_00, c: 2, avaliados: 9), Ativo, 60_00, false, P);
        Assert.Contains(semConversao.Avisos, a => a.Ref == "revisar_oferta");
    }

    [Fact]
    public void Dois_anuncios_no_conjunto_so_mantem()
        => Assert.Equal(Acao.MANTER, Arvore.Decidir(Conj(468_00, a: 6, b: 4, c: 2, avaliados: 14, anuncios: 2), Ativo, 60_00, true, P).Acao);

    [Fact]
    public void Dentro_dos_7_dias_sem_queimar_aguarda()
        => Assert.Equal(Acao.AGUARDAR, Arvore.Decidir(Conj(39_00, dias: 1), Ativo, 60_00, true, P).Acao);

    [Fact]
    public void O_alerta_de_budget_acende_em_20_por_cento()
    {
        Assert.Null(Arvore.AlertaBudget(1000_00, 6000_00, P));
        Assert.NotNull(Arvore.AlertaBudget(1200_00, 6000_00, P));
    }
}

public class ReguaTests
{
    static ReguaV2 R() => new()
    {
        Perguntas =
        [
            new() { Nome = "cargo", Forte = ["Diretor", "Sócio"], Media = ["Analista"], Elim = ["Estudante"] },
            new() { Nome = "frota", Chaves = ["quantos_veiculos"], Forte = ["Mais de 50"], Media = ["5 a 10"], Elim = ["Até 4"] },
        ],
    };
    static Dictionary<string, List<string>> L(params (string, string)[] r) => Regua.RespostasDoLead(r);

    [Fact]
    public void Duas_fortes_e_tier_A() => Assert.Equal(Tier.A, Regua.AvaliarLead(L(("cargo", "diretor"), ("frota", "MAIS DE 50")), R()).Tier);

    [Fact]
    public void Uma_forte_e_tier_B() => Assert.Equal(Tier.B, Regua.AvaliarLead(L(("cargo", "Sócio"), ("frota", "5 a 10")), R()).Tier);

    [Fact]
    public void Nenhuma_forte_e_tier_C() => Assert.Equal(Tier.C, Regua.AvaliarLead(L(("cargo", "Analista"), ("frota", "5 a 10")), R()).Tier);

    [Fact]
    public void Eliminatoria_derruba_mesmo_com_forte()
    {
        var r = Regua.AvaliarLead(L(("cargo", "Diretor"), ("frota", "Até 4")), R());
        Assert.Equal(Situacao.NaoMql, r.Situacao);
        Assert.Contains("frota", r.Eliminatorias);
    }

    [Fact]
    public void Resposta_fora_das_listas_fica_pendente()
        => Assert.Equal(Situacao.Pendente, Regua.AvaliarLead(L(("cargo", "Consultor"), ("frota", "5 a 10")), R()).Situacao);

    [Fact]
    public void O_apelido_do_campo_vale_como_o_nome()
        => Assert.Equal(Tier.A, Regua.AvaliarLead(L(("cargo", "Diretor"), ("quantos_veiculos", "Mais de 50")), R()).Tier);

    [Fact]
    public void Lead_de_vaga_nao_e_MQL()
        => Assert.Equal(Situacao.NaoMql, Regua.AvaliarLead(L(("cargo", "Diretor"), ("frota", "Mais de 50"), ("pretensao_salarial", "R$ 5 mil")), R()).Situacao);

    [Fact]
    public void A_contagem_separa_avaliados_de_pendentes()
    {
        var c = Regua.Contar([L(("cargo", "Diretor"), ("frota", "Mais de 50")), L(("cargo", "Consultor"), ("frota", "5 a 10")), L(("cargo", "Estudante"), ("frota", "5 a 10"))], R());
        Assert.Equal(1, c.MqlA); Assert.Equal(1, c.Mql); Assert.Equal(2, c.Avaliados); Assert.Equal(1, c.Pendentes);
    }
}

public class FluxoDemoTests
{
    [Fact]
    public async Task O_start_automatico_leva_o_cliente_da_copy_ate_a_automacao()
    {
        var api = new DemoApi();
        var c = (await api.ObterClienteAsync("horizonte-imoveis"))!;
        Assert.Equal(Estagio.Copy, Estagios.DoCliente(c, api.Parametros));
        var s = Condutor.Sugerir(c, null);
        Assert.Equal("imobiliario", s.NichoSlug);
        Assert.NotNull(s.Regua);
        var condutor = new Condutor(api);
        var passos = new List<PassoDoStart>();
        await condutor.ConduzirAsync(c, new PlanoDoStart(s.NichoSlug, s.BudgetMensal, s.Regua, s.TetoMql, ModoDeExecucao.Sombra), atual => passos.Add(atual.Automacao.Passo));
        Assert.Null(c.Automacao.PrecisaDeVoce);
        Assert.Equal(PassoDoStart.Pronto, c.Automacao.Passo);
        Assert.Contains(PassoDoStart.Copy, passos);
        Assert.Contains(PassoDoStart.Design, passos);
        Assert.Contains(PassoDoStart.Campanha, passos);
        Assert.Equal(Estagio.Automacao, Estagios.DoCliente(c, api.Parametros));
        Assert.Equal(ModoDeExecucao.Sombra, c.Modo);
        Assert.All(Estagios.ValidarAssentos(c, api.Parametros), a => Assert.True(a.Ok, a.Motivo));
        Assert.False(c.Automacao.EmCurso);
    }

    [Fact]
    public void A_regua_proposta_pelo_formulario_elimina_as_respostas_negativas()
    {
        var f = new FormularioDaMeta("1", "Leads", "ACTIVE", 10, null,
        [
            new PerguntaDoFormulario("qual_o_seu_cargo?", "Qual o seu cargo?", "CUSTOM", [new("dono", "Dono"), new("gerente", "Gerente"), new("estudante", "Estudante"), new("outro", "Outro")]),
            new PerguntaDoFormulario("email", "Email", "EMAIL", []),
        ]);
        var r = Condutor.ProporRegua(f);
        var q = Assert.Single(r.Perguntas);
        Assert.Equal("qual_o_seu_cargo?", q.Nome);
        Assert.Equal(["dono", "gerente"], q.Forte);
        Assert.Equal(["estudante", "outro"], q.Elim);
        Assert.Equal("imobiliario", Nichos.Sugerir("Corretora de imóveis em Goiânia"));
        Assert.Equal("saude", Nichos.Sugerir("Clínica odontológica"));
        Assert.Equal("outro", Nichos.Sugerir(""));
    }

    [Fact]
    public async Task O_cliente_de_referencia_esta_em_automacao_e_o_motor_recomenda()
    {
        var api = new DemoApi();
        var c = (await api.ObterClienteAsync("nautilus-seguros"))!;
        Assert.Equal(Estagio.Automacao, Estagios.DoCliente(c, api.Parametros));
        /* A semente já rodou o cron: as decisões estão pendentes; rodar de novo só repete. */
        var pendentes = c.Recomendacoes.Where(r => r.Estado == EstadoDaRecomendacao.Pendente).ToList();
        Assert.NotEmpty(pendentes);
        Assert.Contains(pendentes, r => r.Acao == Acao.PAUSAR);
        Assert.Contains(pendentes, r => r.Acao == Acao.DUPLICAR);
        var rodada = await api.RodarMotorAsync(c);
        Assert.Equal(0, rodada.Gravadas);
        Assert.True(rodada.Repetidas > 0);
        var dup = pendentes.First(r => r.Acao == Acao.DUPLICAR);
        var antes = c.Conjuntos.Count;
        await api.DecidirAsync(c, dup.Id, true, null);
        Assert.Equal(EstadoDaRecomendacao.Aplicada, dup.Estado);
        Assert.Equal(antes + 1, c.Conjuntos.Count);
        Assert.False(c.Conjuntos.Last().Ativo);
        await Assert.ThrowsAsync<ArgumentException>(() => api.DecidirAsync(c, pendentes.First(r => r.Acao == Acao.PAUSAR).Id, false, null));
    }

    [Fact]
    public async Task O_fluxo_inteiro_anda_de_ponta_a_ponta_na_demonstracao()
    {
        var api = new DemoApi();
        var c = await api.CriarClienteAsync("Teste Ponta a Ponta", "POSEIDON");
        Assert.Equal(Estagio.Chegada, Estagios.DoCliente(c, api.Parametros));
        await api.SubirAssetAsync(c, TipoDeAsset.LogoPrincipal, "logo.png", "image/png", new byte[10]);
        Assert.True((await api.ConectarContaAsync(c, "act_123456789012", "987654321", "Página")).Ok);
        await api.ConferirTokenAsync(c);
        Assert.Equal(Estagio.Ficha, Estagios.DoCliente(c, api.Parametros));
        c.Ficha.Responsavel = "Alguém"; c.Ficha.Site = "https://x.com"; c.Icp.CargoDecisor = "CTO"; c.Icp.DoresPrincipais.Add("dor");
        c.Produto.NomeCorreto = "Produto X"; c.Estrategia.PromessaPrincipal = "promessa";
        await api.SalvarCardTrafegoAsync(c, "tecnologia-infra-dev", 4000);
        await api.SalvarCriterioMqlAsync(c, new ReguaV2 { Perguntas = [new() { Nome = "cargo", Forte = ["CTO"], Media = ["Dev"], Elim = ["Estudante"] }] }, 80);
        Assert.Equal(Estagio.Copy, Estagios.DoCliente(c, api.Parametros));
        var lote = await api.GerarCopyAsync(c, [new(Etapa.MOFU, TipoPeca.Estatico, 3), new(Etapa.BOFU, TipoPeca.Estatico, 2)], null);
        Assert.Equal(5, lote.Pecas.Count);
        Assert.All(lote.Pecas, p => Assert.True(p.CrivoOk, p.MotivoDoCrivo));
        foreach (var p in lote.Pecas) await api.AprovarPecaAsync(c, p.Id, true);
        Assert.Equal(Estagio.Design, Estagios.DoCliente(c, api.Parametros));
        /* faixa 2 SEM lista: o Planejador reserva o BOFU, então o fluxo não pede copy nem piloto de BOFU (não vai ao ar) */
        Assert.Equal([Etapa.MOFU], Estagios.EtapasExigidas(c, api.Parametros));
        c.Conta.TemLista = true;
        Assert.Equal([Etapa.MOFU, Etapa.BOFU], Estagios.EtapasExigidas(c, api.Parametros));
        var artes = await api.GerarArtesAsync(c, [lote.Pecas[0].Id], ["feed", "story"]);
        Assert.Equal(2, artes.Count);
        /* faixa 2 (R$ 4.000) COM lista monta MOFU e BOFU: só com o piloto de MOFU o Design não fecha */
        await api.EscolherPilotoAsync(c, Etapa.MOFU, artes[0].Id);
        Assert.Equal(Estagio.Design, Estagios.DoCliente(c, api.Parametros));
        Assert.Contains("criativo piloto de BOFU", Estagios.DesignPronto(c, api.Parametros).Pendencias);
        /* validador de assento: a arte de MOFU na vaga de BOFU é pendência do Design e recusa do Publicar */
        await api.EscolherPilotoAsync(c, Etapa.BOFU, artes[1].Id);
        Assert.Equal(Estagio.Design, Estagios.DoCliente(c, api.Parametros));
        Assert.Contains(Estagios.DesignPronto(c, api.Parametros).Pendencias, s => s.Contains("assento errado"));
        var recusada = await api.PublicarAsync(c, new PedidoDePublicacao("https://x.com/privacidade", "https://x.com", null, null));
        Assert.False(recusada.Ok);
        Assert.Equal("assento_errado", recusada.Codigo);
        var artesBofu = await api.GerarArtesAsync(c, [lote.Pecas.First(p => p.Etapa == Etapa.BOFU).Id], ["feed"]);
        await api.EscolherPilotoAsync(c, Etapa.BOFU, artesBofu[0].Id);
        Assert.All(Estagios.ValidarAssentos(c, api.Parametros), a => Assert.True(a.Ok, a.Motivo));
        Assert.Equal(Estagio.Campanha, Estagios.DoCliente(c, api.Parametros));
        var est = await api.MontarEstruturaAsync(c);
        Assert.True(est.Ok);
        Assert.Equal(FaixaId.F2MofuBofu, est.Estrutura!.FaixaId);
        var pub = await api.PublicarAsync(c, new PedidoDePublicacao("https://x.com/privacidade", "https://x.com", null, null));
        Assert.True(pub.Ok, pub.Motivo);
        Assert.True(pub.Publicacao!.Pausado);
        Assert.Equal(Estagio.Automacao, Estagios.DoCliente(c, api.Parametros));
        await api.SimularVeiculacaoAsync(c, 12);
        var ongoing = await api.LerOngoingAsync(c);
        Assert.True(ongoing.Kpis.GastoCentavos > 0);
        var rodada = await api.RodarMotorAsync(c);
        Assert.NotEmpty(rodada.Avaliados);
    }

    [Fact]
    public void O_crivo_reprova_negacao_na_headline_e_reuniao_no_CTA_do_MOFU()
    {
        var p = new PecaDeCopy { Id = "1", Etapa = Etapa.MOFU, Tipo = TipoPeca.Estatico, Headline = "Não perca mais leads", Sub = "x", Cta = "Agendar reunião", PapelSub = "prova" };
        var problemas = Crivo.Conferir(p, "Marca");
        Assert.Contains(problemas, x => x.StartsWith("nunca_negacao_headline"));
        Assert.Contains(problemas, x => x.StartsWith("nunca_reuniao_cta_mofu"));
        Assert.Contains(problemas, x => x.StartsWith("nunca_prova_sub_mofu"));
    }

    [Fact]
    public void Dinheiro_le_o_que_a_pessoa_digita_como_o_V1()
    {
        Assert.Equal(3000m, Dinheiro.ReaisDoTexto("3.000"));
        Assert.Equal(3000.5m, Dinheiro.ReaisDoTexto("3.000,50"));
        Assert.Equal(1500.5m, Dinheiro.ReaisDoTexto("1500.50"));
        Assert.Equal("R$ 1.234,56", Dinheiro.Reais(123456));
        Assert.Equal("R$ 3.000", Dinheiro.ReaisCurto(300000));
    }
}
