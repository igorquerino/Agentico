using BlueOS.Core.Executor;
using BlueOS.Core.Modelos;
using Xunit;

namespace BlueOS.Tests;

/* As travas de dinheiro do executor (lib/trafego/motor/executor.ts, Tráfego 3.4): todas falham FECHADO. */
public class ExecutorTests
{
    static EstadoAoVivo Vivo(string? status = "ACTIVE", long? verba = 3000, int? conta = 1, string? erro = null) =>
        new(DateTime.UtcNow, status is null && verba is null ? null : new ConjuntoAoVivo(status, verba), conta is null ? null : new ContaAoVivo(conta, 0), erro);
    static Folga ComFolga(long? folga) => new(300_000, 100_000, 5_000, null, 10, folga);

    [Fact]
    public void A_folga_do_mes_e_o_que_sobra_por_dia_menos_as_diarias_ativas()
    {
        Assert.Equal(15_000, Travas.FolgaDiaria(300_000, 100_000, 5_000, 10));
        Assert.Null(Travas.FolgaDiaria(null, 100_000, 5_000, 10));
        Assert.Equal(-5_000, Travas.FolgaDiaria(100_000, 100_000, 5_000, 0));
        Assert.Equal(2, Travas.DiasRestantesNoMes(new DateOnly(2026, 10, 30)));
        Assert.Equal(31, Travas.DiasRestantesNoMes(new DateOnly(2026, 10, 1)));
    }

    [Fact]
    public void Conta_nao_elegivel_nao_recebe_escrita_nem_para_pausar()
    {
        var t = Travas.TravasDeDinheiro(TipoNaMeta.PauseAdset, null, ComFolga(10_000), 0, 0, Vivo(conta: 2), null);
        Assert.False(t.Ok);
        Assert.Equal(CodigoDaTrava.ContaNaoElegivel, t.Codigo);
    }

    [Fact]
    public void Pausar_segue_mesmo_com_a_leitura_ao_vivo_falhando_e_avisa()
    {
        var t = Travas.TravasDeDinheiro(TipoNaMeta.PauseAdset, null, ComFolga(null), 0, 0, Vivo(null, null, null, "rede caiu"), null);
        Assert.True(t.Ok);
        Assert.Contains(t.Avisos, a => a.Contains("leitura ao vivo falhou"));
    }

    [Fact]
    public void Duplicar_exige_o_conjunto_ACTIVE_lido_na_hora()
    {
        Assert.Equal(CodigoDaTrava.ConjuntoNaoLido, Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 3000, ComFolga(10_000), 0, 0, Vivo(null, null, 1, "sem resposta"), null).Codigo);
        Assert.Equal(CodigoDaTrava.ConjuntoNaoLido, Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 3000, ComFolga(10_000), 0, 0, Vivo("PAUSED"), null).Codigo);
        Assert.Equal(CodigoDaTrava.ConjuntoComProblema, Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 3000, ComFolga(10_000), 0, 0, Vivo("WITH_ISSUES"), null).Codigo);
    }

    [Fact]
    public void Quatro_mudancas_de_verba_na_hora_fecham_a_porta()
    {
        var t = Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 3000, ComFolga(10_000), 4, 0, Vivo(), null);
        Assert.Equal(CodigoDaTrava.VerbaPorHora, t.Codigo);
    }

    [Fact]
    public void Vinte_escritas_na_rodada_fecham_a_porta_antes_de_tudo()
    {
        var t = Travas.TravasDeDinheiro(TipoNaMeta.PauseAdset, null, ComFolga(10_000), 0, 20, Vivo(), null);
        Assert.Equal(CodigoDaTrava.EscritasPorRodada, t.Codigo);
    }

    [Fact]
    public void Sem_budget_nao_abre_verba_e_sem_folga_nao_duplica()
    {
        Assert.Equal(CodigoDaTrava.SemBudget, Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 3000, new Folga(null, 0, 0, null, 10, null), 0, 0, Vivo(), null).Codigo);
        var semFolga = Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 3000, ComFolga(1_500), 0, 0, Vivo(), null);
        Assert.Equal(CodigoDaTrava.SemFolga, semFolga.Codigo);
        Assert.Contains("D2", semFolga.Motivo);
    }

    [Fact]
    public void A_verba_da_copia_desce_ate_a_folga_e_avisa()
    {
        var t = Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 5000, ComFolga(3000), 0, 0, Vivo(), null);
        Assert.True(t.Ok);
        Assert.Equal(3000, t.CopiaCentavos);
        Assert.Contains(t.Avisos, a => a.Contains("desceu de R$ 50,00 para R$ 30,00/dia"));
    }

    [Fact]
    public void Sem_verba_pedida_vale_a_do_original_lida_ao_vivo()
    {
        var t = Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, null, ComFolga(10_000), 0, 0, Vivo(verba: 2500), null);
        Assert.Equal(2500, t.CopiaCentavos);
    }

    [Fact]
    public void O_vault_so_aperta_os_limites_nunca_afrouxa()
    {
        var aperta = new LimitesDoAutomatico(2, 5, null);
        Assert.Equal(CodigoDaTrava.EscritasPorRodada, Travas.TravasDeDinheiro(TipoNaMeta.PauseAdset, null, ComFolga(null), 0, 5, Vivo(), aperta).Codigo);
        Assert.Equal(CodigoDaTrava.VerbaPorHora, Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 3000, ComFolga(10_000), 2, 0, Vivo(), aperta).Codigo);
        var afrouxa = new LimitesDoAutomatico(40, 50, null);
        Assert.Equal(CodigoDaTrava.EscritasPorRodada, Travas.TravasDeDinheiro(TipoNaMeta.PauseAdset, null, ComFolga(null), 0, 20, Vivo(), afrouxa).Codigo);
        Assert.Equal(CodigoDaTrava.VerbaPorHora, Travas.TravasDeDinheiro(TipoNaMeta.DuplicateAdset, 3000, ComFolga(10_000), 4, 0, Vivo(), afrouxa).Codigo);
    }

    [Fact]
    public void O_texto_da_copia_diz_em_que_estado_ela_esta_agora()
    {
        Assert.Contains("não foi aplicada", Travas.TextoDaCopia(new VerbaDaCopia(3000, false, "recusou")));
        Assert.Equal("A cópia nasceu pausada com R$ 30,00/dia. Ativar é no Gerenciador.", Travas.TextoDaCopia(new VerbaDaCopia(3000, true, null)));
        Assert.Equal("A cópia nasceu pausada. Ativar é no Gerenciador.", Travas.TextoDaCopia(null));
        Assert.Contains("ativação não foi tentada", Travas.TextoDaCopia(new VerbaDaCopia(3000, true, null), "ACTIVE", null));
        Assert.Contains("ATIVAR falhou", Travas.TextoDaCopia(new VerbaDaCopia(3000, true, null), "ACTIVE", new AtivacaoDaCopia { Conjunto = false, Erro = "x" }));
        Assert.Contains("está ATIVA com R$ 30,00/dia (e 2 anúncio(s) dela)", Travas.TextoDaCopia(new VerbaDaCopia(3000, true, null), "ACTIVE", new AtivacaoDaCopia { Conjunto = true, Ativados = 2, MantidosPausados = 1 }));
    }

    [Fact]
    public void So_PAUSAR_e_DUPLICAR_viram_escrita_na_Meta()
    {
        Assert.Equal(TipoNaMeta.PauseAdset, TipoNaMetaExt.DaAcao(Acao.PAUSAR));
        Assert.Equal(TipoNaMeta.DuplicateAdset, TipoNaMetaExt.DaAcao(Acao.DUPLICAR));
        Assert.Null(TipoNaMetaExt.DaAcao(Acao.ALERTAR));
        Assert.Null(TipoNaMetaExt.DaAcao(Acao.TROCAR_CRIATIVO));
        Assert.Equal("pausou_conjunto", TipoNaMeta.PauseAdset.VerboNoLog());
        Assert.Equal("duplicate_adset", TipoNaMeta.DuplicateAdset.NaGraph());
    }

    [Fact]
    public void O_dia_da_conta_e_o_de_Brasilia()
    {
        /* 23h de 19/09 em Brasília é 02h de 20/09 em UTC: o dia da conta tem de continuar 19/09. */
        var instante = new DateTime(2026, 9, 20, 2, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 9, 19), Fuso.DiaDaConta(instante));
    }
}
