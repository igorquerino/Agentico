using BlueOS.Core.Api;
using BlueOS.Core.Fluxo;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Panorama;

namespace BlueOS.Tests;

/* As correções da revisão G6b nas regras do Panorama: visto do conjunto inteiro, resumo pelo spec, limiar do
   "travado", churn fora do mapa, projeção pelos dias lidos, hash estável, visto no fuso de Brasília, sinais de vida
   que não fingem leitura e a geometria num lugar só. */
public class G6bCorrecoesTests
{
    static readonly ParametrosMotor P = ParametrosMotor.PadraoDoVault;
    static readonly DateTime Agora = new(2026, 11, 7, 8, 10, 0);
    static readonly Placar SemPlacar = new("Rafael", 0, 0, 0, 0, 0, [], [], [], 0, 0, 0);

    static Cliente EmAutomacao(string nome = "Teste Ltda", decimal budget = 3000, decimal teto = 100, double gasto = 0.3, double mql = 0.9) =>
        DemoFabrica.EmAutomacao(nome, "#000", "saude", budget, teto, gasto, mql, P, Agora);

    /* ---- E03 · o visto guarda o conjunto inteiro de alertas ------------------------------------ */

    [Fact]
    public void Uma_nova_causa_de_gravidade_1_derruba_o_visto_do_principal()
    {
        var c = EmAutomacao("Alfa");
        c.Conta.TokenValido = false; c.Conta.TokenConferidoEm = Agora.AddHours(-3);
        var antes = Montador.Montar([c], "POSEIDON", P, Agora, [], [], SemPlacar).Estrelas.Single();
        var vistos = new List<Visto> { new(c.Slug, antes.AssinaturaDoVisto, Agora.AddMinutes(-10), "Rafael") };
        var visto = Montador.Montar([c], "POSEIDON", P, Agora, vistos, [], SemPlacar);
        Assert.True(visto.Estrelas.Single().EstaVista);
        Assert.Empty(visto.Interrompem);

        /* no mesmo dia, a automação foi pausada: um 2º g1 atrás do principal (token), que continua o mesmo */
        c.Sinais.AutomacaoEstado = "pausada"; c.Sinais.AutomacaoDesde = Agora.AddMinutes(-2);
        var depois = Montador.Montar([c], "POSEIDON", P, Agora, vistos, [], SemPlacar);
        var e = depois.Estrelas.Single();
        Assert.Equal(antes.Principal!.Assinatura, e.Principal!.Assinatura);
        Assert.Equal(2, e.Alertas.Count(a => a.Gravidade == Gravidade.AutomacaoParou));
        Assert.False(e.EstaVista);
        Assert.Single(depois.Interrompem);
    }

    [Fact]
    public void A_assinatura_do_conjunto_nao_depende_da_ordem()
    {
        var c = EmAutomacao();
        c.Sinais.PedidoDoCliente = "e o relatório?"; c.Sinais.PedidoEm = Agora.AddHours(-2);
        c.Sinais.DiasSemMensagem = 5;
        var alertas = Diagnostico.DoCliente(c, P, Agora);
        Assert.Equal(2, alertas.Count);
        Assert.Equal(Diagnostico.AssinaturaDoConjunto(alertas), Diagnostico.AssinaturaDoConjunto(alertas.AsEnumerable().Reverse()));
        Assert.Contains("+", Diagnostico.AssinaturaDoConjunto(alertas));
    }

    /* ---- E07 · o resumo do squad segue as definições do spec ----------------------------------- */

    [Fact]
    public void Precisam_de_voce_nao_conta_os_vistos_e_squad_vazio_nao_tem_saude()
    {
        var frio = EmAutomacao("Charlie");
        frio.Sinais.DiasSemMensagem = 5;
        var assinatura = Diagnostico.AssinaturaDoConjunto(Diagnostico.DoCliente(frio, P, Agora));
        var pan = Montador.Montar([frio, EmAutomacao("Bravo")], "POSEIDON", P, Agora, [new(frio.Slug, assinatura, Agora, "Rafael")], [], SemPlacar);
        Assert.Equal(0, pan.Resumo.PrecisamDeVoce);
        Assert.Empty(pan.PrecisamDeVoce);
        Assert.Equal(1, pan.Resumo.EmDia);
        Assert.Equal(50, pan.Resumo.SaudePct); /* o visto não conta como em dia */

        var vazio = Montador.Montar([], "POSEIDON", P, Agora, [], [], SemPlacar);
        Assert.Equal(0, vazio.Resumo.Clientes);
        Assert.Null(vazio.Resumo.SaudePct);
    }

    [Fact]
    public void Budget_e_teto_do_resumo_somam_so_quem_tem_os_KPIs_lidos()
    {
        var lido = EmAutomacao("Lido", budget: 2000, teto: 100);
        var semKpis = EmAutomacao("Sem KPIs", budget: 9000, teto: 300);
        semKpis.Sinais.Kpis = null;
        var pan = Montador.Montar([lido, semKpis], "POSEIDON", P, Agora, [], [], SemPlacar);
        Assert.Equal(2000_00, pan.Resumo.BudgetCentavos);
        Assert.Equal(100_00, pan.Resumo.TetoMedioCentavos);
        Assert.Equal(lido.Sinais.Kpis!.GastoCentavos, pan.Resumo.GastoCentavos);
    }

    /* ---- E08 · gravidade 4 só depois de 3 dias, e data desconhecida não vira "739000 d" -------- */

    [Fact]
    public void Travado_so_dispara_a_partir_de_3_dias_corridos()
    {
        var novo = new Cliente { Id = "1", Slug = "novo", Nome = "Novo", Desde = DateOnly.FromDateTime(Agora.AddDays(-2)) };
        Assert.DoesNotContain(Diagnostico.DoCliente(novo, P, Agora), a => a.Gravidade == Gravidade.Travado);
        novo.Desde = DateOnly.FromDateTime(Agora.AddDays(-Diagnostico.DiasParaTravado));
        var a = Diagnostico.DoCliente(novo, P, Agora).Single(x => x.Gravidade == Gravidade.Travado);
        Assert.Contains("chegou há 3 d", a.Curto);
    }

    [Fact]
    public void Travado_sem_data_de_chegada_nao_conta_dias()
    {
        var c = new Cliente { Id = "1", Slug = "sem-data", Nome = "Sem Data" }; /* Desde = 01/01/0001: a listagem do V1 não manda */
        var a = Diagnostico.DoCliente(c, P, Agora).Single(x => x.Gravidade == Gravidade.Travado);
        Assert.DoesNotContain("há", a.Curto);
        Assert.DoesNotContain("739", a.Curto + a.Motivo);
        Assert.Contains("sem data", a.Motivo);
        Assert.Null(a.Desde);
        Assert.Empty(a.LinhaDoTempo);
    }

    /* ---- E09 · cliente em churn sai do mapa ----------------------------------------------------- */

    [Fact]
    public void Cliente_em_churn_nao_entra_no_mapa()
    {
        var foi = new Cliente { Id = "1", Slug = "foi-embora", Nome = "Foi Embora", Status = "churn", Squad = "POSEIDON", Desde = DateOnly.FromDateTime(Agora.AddDays(-90)) };
        var pan = Montador.Montar([foi, EmAutomacao("Fica")], "POSEIDON", P, Agora, [], [], SemPlacar);
        Assert.DoesNotContain(pan.Estrelas, e => e.Slug == "foi-embora");
        Assert.Equal(1, pan.Resumo.Clientes);
        Assert.Equal(100, pan.Resumo.SaudePct);
    }

    /* ---- E27 · a projeção do budget usa os dias lidos e só o mês corrente ---------------------- */

    [Fact]
    public void A_projecao_do_budget_usa_os_dias_lidos()
    {
        var c = EmAutomacao(budget: 3000);
        /* 60% em 3 dias lidos (a campanha entrou no dia 4): 20%/dia, acaba em 2 dias. Pelo dia 7 do calendário daria 4 dias. */
        c.Sinais.Kpis = c.Sinais.Kpis! with { GastoCentavos = 1800_00, DiasLidos = 3 };
        var a = Diagnostico.DoCliente(c, P, Agora).Single(x => x.Titulo.Contains("acaba"));
        Assert.Contains("acaba em 2 dia(s)", a.Curto);
        Assert.Contains("3 dia(s) lido(s)", a.Motivo);
    }

    [Fact]
    public void KPIs_de_outro_mes_nao_disparam_dinheiro()
    {
        var c = EmAutomacao(budget: 8000, teto: 150, gasto: 0.98, mql: 1.0);
        Assert.Contains(Diagnostico.DoCliente(c, P, Agora), x => x.Gravidade == Gravidade.Dinheiro);
        c.Sinais.Kpis = c.Sinais.Kpis! with { Mes = "2026-10" };
        Assert.DoesNotContain(Diagnostico.DoCliente(c, P, Agora), x => x.Gravidade == Gravidade.Dinheiro);
        Assert.True(Diagnostico.DoMesCorrente(c.Sinais.Kpis with { Mes = "" }, Agora));
    }

    /* ---- E28 · assinatura do pedido estável entre processos ------------------------------------ */

    [Fact]
    public void O_hash_do_pedido_e_estavel_entre_processos()
    {
        Assert.Equal("a9993e3647", Diagnostico.HashCurto("abc")); /* SHA-1("abc") = a9993e36 47068 16aba…: não muda de um processo para outro */
        var c = EmAutomacao();
        c.Sinais.PedidoDoCliente = "quando sobe a campanha?"; c.Sinais.PedidoEm = Agora.AddHours(-1);
        var a = Diagnostico.DoCliente(c, P, Agora).Single(x => x.Gravidade == Gravidade.Pedido);
        Assert.EndsWith(":" + Diagnostico.HashCurto("quando sobe a campanha?"), a.Assinatura);
    }

    /* ---- E29 · o visto expira à meia-noite de Brasília, qualquer que seja o fuso do host ------- */

    [Fact]
    public void O_visto_vira_a_meia_noite_de_Brasilia()
    {
        var em = new DateTime(2026, 11, 7, 23, 0, 0, DateTimeKind.Utc);        /* 20:00 de 07/11 em Brasília */
        var v = new Visto("x", "a", em, "Rafael");
        Assert.True(v.ValeAgora(new DateTime(2026, 11, 8, 2, 30, 0, DateTimeKind.Utc), "a"));  /* 23:30 de 07/11 em Brasília: já é 08/11 em UTC */
        Assert.False(v.ValeAgora(new DateTime(2026, 11, 8, 3, 30, 0, DateTimeKind.Utc), "a")); /* 00:30 de 08/11 em Brasília */
        Assert.Equal(new DateOnly(2026, 11, 7), Relogio.DiaEmBrasilia(new DateTime(2026, 11, 8, 2, 59, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void O_relogio_fixado_vale_ate_o_dispose()
    {
        var fixo = new DateTime(2026, 11, 1, 9, 0, 0);
        using (Relogio.Fixar(fixo)) Assert.Equal(fixo, Relogio.Agora);
        Assert.NotEqual(fixo, Relogio.Agora);
    }

    /* ---- E30 · "Abrir no Start" de churn, pedido e frio vai à etapa atual ---------------------- */

    [Fact]
    public void Churn_pedido_e_frio_abrem_o_Start_na_etapa_atual_do_cliente()
    {
        var c = EmAutomacao();
        c.Sinais.RiscoChurn = "alto"; c.Sinais.NotaCs = 50;
        c.Sinais.PedidoDoCliente = "e o relatório?"; c.Sinais.PedidoEm = Agora.AddHours(-1);
        var alertas = Diagnostico.DoCliente(c, P, Agora);
        var atual = Estagios.DoCliente(c, P);
        Assert.Equal(Estagio.Automacao, atual);
        Assert.All(alertas.Where(a => a.Gravidade is Gravidade.Churn or Gravidade.Pedido), a => Assert.Equal(atual, a.EtapaDoStart));

        var frio = EmAutomacao("Frio");
        frio.Sinais.DiasSemMensagem = 6;
        Assert.Equal(atual, Diagnostico.DoCliente(frio, P, Agora).Single(a => a.Gravidade == Gravidade.Frio).EtapaDoStart);
    }

    /* ---- E34 · nada de pedaço nulo no texto ----------------------------------------------------- */

    [Fact]
    public void Textos_sem_dado_nao_deixam_buraco()
    {
        var c = EmAutomacao();
        c.Conta.TokenValido = false; c.Conta.TokenConferidoEm = null;
        Assert.Equal("parou · token vencido", Diagnostico.DoCliente(c, P, Agora).First().Curto);

        var semNota = EmAutomacao("Sem Nota");
        semNota.Sinais.RiscoChurn = "critico"; semNota.Sinais.NotaCs = null; semNota.Sinais.TendenciaD7 = -10; semNota.Sinais.DiasSemMensagem = 0;
        var churn = Diagnostico.DoCliente(semNota, P, Agora).Single(a => a.Gravidade == Gravidade.Churn);
        Assert.Equal("churn critico", churn.Curto);
        Assert.DoesNotContain("nota  ", churn.Motivo);
        Assert.DoesNotContain("→", churn.Motivo);
        Assert.StartsWith("Sem nota do CS.", churn.Motivo);
    }

    /* ---- E23 · a faixa mostra o g1 mais antigo -------------------------------------------------- */

    [Fact]
    public void Interrompem_vem_do_mais_antigo_para_o_mais_novo()
    {
        var novo = EmAutomacao("Alfa"); novo.Conta.TokenValido = false; novo.Conta.TokenConferidoEm = Agora.AddHours(-1);
        var velho = EmAutomacao("Zeta"); velho.Conta.TokenValido = false; velho.Conta.TokenConferidoEm = Agora.AddDays(-2);
        var pan = Montador.Montar([novo, velho], "POSEIDON", P, Agora, [], [], SemPlacar);
        Assert.Equal(["zeta", "alfa"], pan.Interrompem.Select(e => e.Slug));
    }

    /* ---- E35 · a geometria do mapa num lugar só ------------------------------------------------- */

    [Fact]
    public void O_fator_dos_aneis_sai_dos_raios_do_mock()
    {
        double[] esperado = [0, .193, .279, .364, .479];
        for (var anel = 1; anel <= 4; anel++) Assert.Equal(esperado[anel], Math.Round(Orbita.Fator(anel), 3));
    }

    /* ---- E02 · sinais de vida que não fingem leitura -------------------------------------------- */

    [Fact]
    public void Fonte_sem_dado_vira_ainda_nao_vigiado_e_o_ao_vivo_fala_da_carteira()
    {
        var c = EmAutomacao();
        c.Sinais.Kpis = null; c.Sinais.NotaCs = null; c.Sinais.RiscoChurn = null; c.Sinais.DiasSemMensagem = null;
        IReadOnlyList<SinalDeVida> sinais =
        [
            new("carteira", "lida agora", false),
            new("Meta (gasto, leads)", "pela carga diária do V1 (07:30 UTC)", false),
            new("grupos do CS", "ainda não chega ao app", false),
        ];
        var pan = Montador.Montar([c], "POSEIDON", P, Agora, [], sinais, SemPlacar);
        var meta = Vida.DaFonte(pan.SinaisDeVida, Vida.Meta)!;
        Assert.False(meta.Vigiado);
        Assert.Equal(Vida.NaoVigiado, meta.Texto);
        Assert.False(Vida.DaFonte(pan.SinaisDeVida, Vida.GruposDoCs)!.Vigiado);
        Assert.False(Vida.DaFonte(pan.SinaisDeVida, Vida.Publicacoes)!.Vigiado);
        Assert.True(Vida.DaFonte(pan.SinaisDeVida, "carteira")!.Vigiado);

        /* o "ao vivo" acha a Meta pelo nome: sem leitura da Meta, fala da carteira (nunca "Meta lida agora") */
        var texto = Vida.AoVivo(pan.SinaisDeVida, Agora);
        Assert.Equal("ao vivo · carteira lida 08:10", texto);
        Assert.DoesNotContain("Meta", texto);
    }

    [Fact]
    public void Com_a_Meta_lida_o_ao_vivo_mostra_a_leitura_dela_e_fonte_ruim_nao_e_mascarada()
    {
        var c = EmAutomacao();
        IReadOnlyList<SinalDeVida> sinais = [new("token do squad", "conferido 08:02", false), new("Meta (gasto, leads)", "lida há 4 min", false)];
        var pan = Montador.Montar([c], "POSEIDON", P, Agora, [], sinais, SemPlacar);
        Assert.Equal("ao vivo · Meta lida há 4 min", Vida.AoVivo(pan.SinaisDeVida, Agora));

        var semKpis = EmAutomacao("Sem KPIs"); semKpis.Sinais.Kpis = null;
        IReadOnlyList<SinalDeVida> ruim = [new("Meta (gasto, leads)", "sem carga este mês", true)];
        var pan2 = Montador.Montar([semKpis], "POSEIDON", P, Agora, [], ruim, SemPlacar);
        Assert.Equal("sem carga este mês", Vida.DaFonte(pan2.SinaisDeVida, Vida.Meta)!.Texto);
    }

    [Fact]
    public void Sem_leitura_diz_ha_quanto_tempo()
    {
        Assert.Equal("sem leitura", Vida.SemLeitura(null, Agora));
        Assert.Equal("sem leitura há 12 min", Vida.SemLeitura(Agora.AddMinutes(-12), Agora));
        Assert.Equal("sem leitura há 2 h", Vida.SemLeitura(Agora.AddHours(-2).AddMinutes(-5), Agora));
    }
}
