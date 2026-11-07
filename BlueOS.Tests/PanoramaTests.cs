using BlueOS.Core.Api;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Panorama;

namespace BlueOS.Tests;

/* O Panorama sem tela: a ordem de atenção, o diagnóstico de cada gravidade, o visto, a órbita e o resumo do squad. */
public class PanoramaTests
{
    static readonly ParametrosMotor P = ParametrosMotor.PadraoDoVault;
    static readonly DateTime Agora = new(2026, 11, 7, 8, 10, 0);

    static Cliente EmAutomacao(string nome = "Teste Ltda", decimal budget = 3000, decimal teto = 100, double gasto = 0.3, double mql = 0.9) =>
        DemoFabrica.EmAutomacao(nome, "#000", "saude", budget, teto, gasto, mql, P, Agora);

    [Fact]
    public void A_ordem_de_atencao_e_a_fechada_com_o_usuario()
    {
        Assert.Equal([Gravidade.AutomacaoParou, Gravidade.Dinheiro, Gravidade.Churn, Gravidade.Travado, Gravidade.Pedido, Gravidade.Frio], Gravidades.Lista.Select(g => g.Gravidade));
        Assert.True(Gravidade.Dinheiro < Gravidade.Churn);
        Assert.True(Gravidade.Travado < Gravidade.Pedido);
        Assert.True(Gravidades.Interrompe(Gravidade.AutomacaoParou));
        Assert.All(Gravidades.Lista.Skip(1), g => Assert.False(Gravidades.Interrompe(g.Gravidade)));
        Assert.Equal(1, Gravidades.Anel(Gravidade.Dinheiro));
        Assert.Equal(2, Gravidades.Anel(Gravidade.Travado));
        Assert.Equal(3, Gravidades.Anel(Gravidade.Frio));
    }

    [Fact]
    public void Cliente_em_dia_nao_tem_alerta()
    {
        var c = EmAutomacao();
        c.Sinais.DiasSemMensagem = 1;
        Assert.Empty(Diagnostico.DoCliente(c, P, Agora));
    }

    [Fact]
    public void Token_vencido_na_automacao_e_gravidade_1_com_o_que_custa_e_o_que_resolve()
    {
        var c = EmAutomacao();
        c.Conta.TokenValido = false; c.Conta.TokenConferidoEm = Agora.AddDays(-1).AddMinutes(-40);
        c.Sinais.ConjuntosAtivos = 3; c.Sinais.VerbaDiariaCentavos = 7700;
        var a = Diagnostico.DoCliente(c, P, Agora).First();
        Assert.Equal(Gravidade.AutomacaoParou, a.Gravidade);
        Assert.Contains("ontem", a.Curto);
        Assert.Contains("77", a.OQueCusta);
        Assert.Contains("token", a.OQueResolve);
        Assert.Equal(Estagio.Assets, a.EtapaDoStart);
        Assert.Equal(3, a.LinhaDoTempo.Count);
    }

    [Fact]
    public void Token_vencido_fora_da_automacao_nao_e_automacao_parada()
    {
        var c = new Cliente { Id = "1", Slug = "novo", Nome = "Novo", Desde = DateOnly.FromDateTime(Agora.AddDays(-3)) };
        c.Conta.TokenValido = false;
        var alertas = Diagnostico.DoCliente(c, P, Agora);
        Assert.DoesNotContain(alertas, a => a.Gravidade == Gravidade.AutomacaoParou);
        Assert.Equal(Gravidade.Travado, alertas.Single().Gravidade);
        Assert.Contains("chegou há 3 d", alertas.Single().Curto);
    }

    [Fact]
    public void Dinheiro_vem_antes_de_churn_e_o_custo_por_mql_acima_do_teto_dispara()
    {
        var c = EmAutomacao(budget: 6000, teto: 150, gasto: 0.8, mql: 0.5); /* 4.800 gastos para 20 MQL = 240 > 150 */
        c.Sinais.RiscoChurn = "alto"; c.Sinais.NotaCs = 52;
        var alertas = Diagnostico.DoCliente(c, P, Agora);
        Assert.Equal(Gravidade.Dinheiro, alertas[0].Gravidade);
        Assert.Contains("acima do teto", alertas[0].Titulo);
        Assert.Contains(alertas, a => a.Gravidade == Gravidade.Churn);
    }

    [Fact]
    public void Budget_que_acaba_antes_do_mes_e_dinheiro()
    {
        var c = EmAutomacao(budget: 8000, teto: 150, gasto: 0.98, mql: 1.0);
        var a = Diagnostico.DoCliente(c, P, Agora).First(x => x.Gravidade == Gravidade.Dinheiro && x.Titulo.Contains("acaba"));
        Assert.Contains("acaba", a.Titulo);
        Assert.Contains("98%", a.Curto);
    }

    [Fact]
    public void Pedido_do_cliente_e_grupo_frio_sao_as_ultimas_gravidades()
    {
        var c = EmAutomacao();
        c.Sinais.PedidoDoCliente = "quando sobe a campanha?"; c.Sinais.PedidoEm = Agora.AddHours(-3); c.Sinais.PedidoOrigem = "cs";
        c.Sinais.DiasSemMensagem = 4;
        var alertas = Diagnostico.DoCliente(c, P, Agora);
        Assert.Equal([Gravidade.Pedido, Gravidade.Frio], alertas.Select(a => a.Gravidade));
        Assert.Contains("pediu pelo CS", alertas[0].Curto);
    }

    [Fact]
    public void Churn_absorve_o_frio_para_nao_contar_duas_vezes()
    {
        var c = EmAutomacao();
        c.Sinais.RiscoChurn = "critico"; c.Sinais.NotaCs = 40; c.Sinais.DiasSemMensagem = 11;
        var alertas = Diagnostico.DoCliente(c, P, Agora);
        Assert.Single(alertas);
        Assert.Equal(Gravidade.Churn, alertas[0].Gravidade);
        Assert.Contains("11 dias sem resposta", alertas[0].Curto);
    }

    [Fact]
    public void O_visto_vale_hoje_e_enquanto_o_estado_nao_mudar()
    {
        var v = new Visto("x", "token-vencido:202611060730", Agora.AddHours(-1), "Rafael");
        Assert.True(v.ValeAgora(Agora, "token-vencido:202611060730"));
        Assert.False(v.ValeAgora(Agora, "token-vencido:202611070730"));
        Assert.False(v.ValeAgora(Agora.AddDays(1), "token-vencido:202611060730"));
    }

    [Fact]
    public void A_orbita_espalha_as_estrelas_a_partir_do_topo_e_abre_segunda_volta_quando_nao_cabe()
    {
        var d = Orbita.Distribuir(4, 20);
        Assert.Equal(-90, d[0].Angulo);
        Assert.Equal(0, d[0].Angulo + 90 + 0, 6);
        Assert.Equal(4, d.Select(x => x.Angulo).Distinct().Count());
        Assert.All(d, x => Assert.Equal(0, x.Volta));
        var cheio = Orbita.Distribuir(30, 20);
        Assert.Equal(30, cheio.Count);
        Assert.Contains(cheio, x => x.Volta == 1);
        Assert.True(Orbita.Capacidade(4, 335) >= 41, "41 em dia cabem numa volta do anel de fora");
        Assert.Equal(-90, Orbita.AnguloDaBorda(0, 41));
    }

    [Fact]
    public void O_montador_poe_os_graves_no_centro_os_saudaveis_na_borda_e_soma_o_squad()
    {
        var em_dia = EmAutomacao("Bravo", 2000, 100, 0.3, 0.9);
        var parou = EmAutomacao("Alfa", 3000, 100, 0.3, 0.9);
        parou.Conta.TokenValido = false; parou.Conta.TokenConferidoEm = Agora.AddHours(-2);
        var frio = EmAutomacao("Charlie", 2000, 100, 0.3, 0.9);
        frio.Sinais.DiasSemMensagem = 5;
        var vistos = new List<Visto> { new("charlie", Diagnostico.DoCliente(frio, P, Agora)[0].Assinatura, Agora.AddMinutes(-5), "Rafael") };
        var placar = new Placar("Rafael", 0, 0, 0, 0, 0, [], [], [], 0, 0, 0);
        var pan = Montador.Montar([em_dia, parou, frio], "POSEIDON", P, Agora, vistos, [], placar);

        Assert.Equal(3, pan.Resumo.Clientes);
        Assert.Equal(2, pan.Resumo.PrecisamDeVoce);
        Assert.Equal(1, pan.Resumo.EmDia);
        Assert.Equal(33, pan.Resumo.SaudePct);
        Assert.Equal(1, pan.Estrelas.Single(e => e.Slug == "alfa").Posicao.Anel);
        Assert.Equal(3, pan.Estrelas.Single(e => e.Slug == "charlie").Posicao.Anel);
        Assert.Equal(Orbita.AnelDosSaudaveis, pan.Estrelas.Single(e => e.Slug == "bravo").Posicao.Anel);
        Assert.True(pan.Estrelas.Single(e => e.Slug == "charlie").EstaVista);
        Assert.Single(pan.Interrompem);
        Assert.Equal(7000_00, pan.Resumo.BudgetCentavos);
        Assert.True(pan.Resumo.Mql > 0);
    }

    [Fact]
    public void A_demonstracao_tem_50_estrelas_e_conta_a_historia_do_mapa()
    {
        var api = new DemoApi();
        var pan = api.LerPanoramaAsync("POSEIDON").GetAwaiter().GetResult();
        Assert.Equal(50, pan.Estrelas.Count);
        Assert.Equal(Gravidade.AutomacaoParou, pan.Estrelas.Single(e => e.Slug == "ferraz-contabil").Principal!.Gravidade);
        Assert.Equal(Gravidade.Dinheiro, pan.Estrelas.Single(e => e.Slug == "batcar").Principal!.Gravidade);
        Assert.Equal(Gravidade.Churn, pan.Estrelas.Single(e => e.Slug == "escola-aurora").Principal!.Gravidade);
        Assert.Equal(Gravidade.Pedido, pan.Estrelas.Single(e => e.Slug == "leanspay").Principal!.Gravidade);
        Assert.Equal(Gravidade.Frio, pan.Estrelas.Single(e => e.Slug == "az-collections").Principal!.Gravidade);
        Assert.True(pan.Estrelas.Single(e => e.Slug == "bonifq").Saudavel);
        Assert.True(pan.Resumo.EmDia >= 40, $"{pan.Resumo.EmDia} em dia");
        Assert.Single(pan.Interrompem);
    }

    [Fact]
    public void Marcar_como_visto_silencia_e_resolver_da_os_pontos_da_gravidade()
    {
        var api = new DemoApi();
        var antes = api.LerPanoramaAsync(null).GetAwaiter().GetResult();
        var ferraz = antes.Estrelas.Single(e => e.Slug == "ferraz-contabil");
        api.MarcarVistoAsync("ferraz-contabil", ferraz.Principal!.Assinatura, true).GetAwaiter().GetResult();
        var visto = api.LerPanoramaAsync(null).GetAwaiter().GetResult();
        Assert.True(visto.Estrelas.Single(e => e.Slug == "ferraz-contabil").EstaVista);
        Assert.Empty(visto.Interrompem);

        /* o head renovou o token: a estrela acende e quem olha ganha +60 */
        var c = api.ObterClienteAsync("ferraz-contabil").GetAwaiter().GetResult()!;
        c.Conta.TokenValido = true;
        var depois = api.LerPanoramaAsync(null).GetAwaiter().GetResult();
        Assert.True(depois.Estrelas.Single(e => e.Slug == "ferraz-contabil").Saudavel);
        Assert.Equal(antes.Placar.Pontos + 60, depois.Placar.Pontos);
        Assert.Equal("Ferraz Contábil", depois.Placar.AcendeuHoje[0].Cliente);
    }
}
