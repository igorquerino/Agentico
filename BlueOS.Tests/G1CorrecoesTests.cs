using System.Text.Json;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Setup;

namespace BlueOS.Tests;

/* As correções da revisão G1 (motor): a entrada da árvore sai do estado REAL da carga e da memória do motor, e o que
   não foi lido segura a árvore (AGUARDAR/SEM_BASE), nunca a deixa agir. Os casos seguem o V1 (lib/trafego/motor). */

public class CargaDoMotorTests
{
    static readonly ParametrosMotor P = ParametrosMotor.PadraoDoVault;
    /* 10:00 em Brasília: o dia da conta é 07/11; a janela começa em 08/10; o mês em 01/11 */
    static readonly DateTime Agora = new(2026, 11, 7, 13, 0, 0, DateTimeKind.Utc);
    static readonly DateOnly Hoje = new(2026, 11, 7);

    static ReguaV2 R() => new() { Perguntas = [new() { Nome = "cargo", Forte = ["Diretor"], Media = ["Analista"], Elim = ["Estudante"] }] };

    /// <summary>`dias` dias de gasto, do `ultimo` dia atrás para trás.</summary>
    static IEnumerable<LinhaDeInsight> Gastou(string adset, decimal porDia, int dias, string ad = "ad1", string? camp = "c1", int ultimo = 1) =>
        Enumerable.Range(0, dias).Select(k => new LinhaDeInsight(ad, adset, camp, Hoje.AddDays(-(ultimo + k)), porDia, 0));

    /// <summary>Uma rodada da carga que releu os dias de `de` até `ate` dias atrás.</summary>
    static MarcaDaCarga Lidos(int de, int ate, string estado = "ok", int recusas = 0, IReadOnlyList<DateOnly>? diasOk = null) =>
        new(Hoje, estado, Enumerable.Range(ate, de - ate + 1).Select(k => Hoje.AddDays(-k)).ToList(), diasOk ?? [], recusas);

    static FotoDoConjunto Foto(string adset, string? formato = "imagem", long? orcamento = 39_00, string? status = "ACTIVE", double horas = 3, string? camp = "c1") =>
        new(adset, camp, $"MOFU {adset}", status, orcamento, formato, Agora.AddHours(-horas));

    static LinhaDeLead Lead(string id, string adset, double diasAtras, string? cargo, string ad = "ad1") =>
        new(id, adset, ad, Agora.AddDays(-diasAtras), cargo is null ? [new Resposta("email", "x@y.com")] : [new Resposta("cargo", cargo)]);

    static DadosDaCarga Dados(IEnumerable<LinhaDeInsight> insights, IEnumerable<FotoDoConjunto> fotos, IEnumerable<MarcaDaCarga> marcas,
        IEnumerable<LinhaDeLead>? leads = null, IEnumerable<RecomendacaoNaMemoria>? registro = null, string? campanha = null,
        bool semRegua = false, long? budget = null, DateTime? agora = null) =>
        new(agora ?? Agora, campanha, semRegua ? null : R(), budget, 60_00, insights.ToList(), (leads ?? []).ToList(), fotos.ToList(), marcas.ToList(), (registro ?? []).ToList());

    static ConjuntoEmVeiculacao Um(DadosDaCarga d, string adset = "a1") => Carga.Ler(d, P).Conjuntos.Single(c => c.AdsetId == adset);

    static Decisao Decidir(ConjuntoEmVeiculacao cj, bool converte = true) =>
        Arvore.Decidir(Rodada.EntradaDe(cj, "x", cj.Formato, Hoje), new EntradaAoVivo(cj.Ativo, cj.DiarioCentavos), 60_00, converte, P);

    /* o que queima: R$ 10/dia por 12 dias = R$ 120, 2× o teto de R$ 60, sem MQL */
    static DadosDaCarga Queima(IEnumerable<MarcaDaCarga>? marcas = null, FotoDoConjunto? foto = null, IEnumerable<LinhaDeLead>? leads = null, IEnumerable<RecomendacaoNaMemoria>? registro = null) =>
        Dados(Gastou("a1", 10m, 12), [foto ?? Foto("a1")], marcas ?? [Lidos(20, 1)], leads, registro);

    /* o que ganha: R$ 30/dia por 12 dias e 12 leads "Diretor" (tier B): taxa 80%, R$ 37,50 por MQL ponderado */
    static IEnumerable<LinhaDeLead> Ganhadores(string adset = "a1") => Enumerable.Range(1, 12).Select(k => Lead($"l{k}", adset, k - 0.5, "Diretor"));
    static DadosDaCarga Ganha(IEnumerable<LinhaDeInsight>? extra = null, FotoDoConjunto? foto = null, IEnumerable<RecomendacaoNaMemoria>? registro = null) =>
        Dados(Gastou("a1", 30m, 12).Concat(extra ?? []), [foto ?? Foto("a1")], [Lidos(20, 1)], Ganhadores(), registro);

    /* ---- E01: a leitura, a foto, o formato, a cobertura e os anúncios ---------------------------- */

    [Fact]
    public void Com_a_carga_lida_inteira_o_conjunto_que_queima_pausa()
    {
        var cj = Um(Queima());
        Assert.Equal(Leitura.Medida, cj.Leitura);
        Assert.True(cj.CoberturaCompleta);
        Assert.Equal(1, cj.AnunciosAtivos);
        Assert.True(cj.Ativo);
        Assert.Equal(39_00, cj.DiarioCentavos);
        Assert.Equal(Formato.Imagem, cj.Formato);
        Assert.Equal(Acao.PAUSAR, Decidir(cj).Acao);
    }

    [Fact]
    public void Dia_de_entrega_que_nenhuma_rodada_da_carga_leu_deixa_a_leitura_incompleta_e_a_arvore_aguarda()
    {
        var cj = Um(Queima(marcas: [Lidos(20, 4)]));
        Assert.Equal(Leitura.Incompleta, cj.Leitura);
        Assert.Equal(Acao.AGUARDAR, Decidir(cj).Acao);
    }

    [Fact]
    public void Token_que_perdeu_os_leads_e_sem_acesso_e_nao_pausa()
    {
        var cj = Um(Queima(marcas: [Lidos(20, 4), Lidos(3, 1, "parcial", recusas: 1)]));
        Assert.Equal(Leitura.SemAcesso, cj.Leitura);
        Assert.Equal(Acao.AGUARDAR, Decidir(cj).Acao);
    }

    [Fact]
    public void O_dia_que_alguma_rodada_leu_com_sucesso_vale_mesmo_com_a_rodada_parcial()
    {
        var ok = new[] { Hoje.AddDays(-1), Hoje.AddDays(-2), Hoje.AddDays(-3) };
        var cj = Um(Queima(marcas: [Lidos(20, 4), Lidos(3, 1, "parcial", diasOk: ok)]));
        Assert.Equal(Leitura.Medida, cj.Leitura);
        Assert.Equal(Acao.PAUSAR, Decidir(cj).Acao);
    }

    [Fact]
    public void Foto_de_mais_de_36_horas_anula_status_e_verba_e_a_leitura_vence()
    {
        var cj = Um(Queima(foto: Foto("a1", horas: 40)));
        Assert.Equal(Leitura.Vencida, cj.Leitura);
        Assert.Null(cj.Ativo);
        Assert.Null(cj.DiarioCentavos);
        Assert.Equal(Acao.AGUARDAR, Decidir(cj).Acao);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Imagem")]
    [InlineData("carrossel")]
    public void Formato_que_a_carga_nao_leu_e_desconhecido_e_da_sem_base(string? formato)
    {
        var cj = Um(Queima(foto: Foto("a1", formato: formato)));
        Assert.Equal(Formato.Desconhecido, cj.Formato);
        Assert.Equal(Acao.SEM_BASE, Decidir(cj).Acao);
    }

    [Fact]
    public void Conjunto_sem_foto_nao_tem_status_nem_verba_nem_formato()
    {
        var cj = Um(Dados(Gastou("a1", 10m, 12), [], [Lidos(20, 1)]));
        Assert.Null(cj.Ativo);
        Assert.Null(cj.DiarioCentavos);
        Assert.Equal(Formato.Desconhecido, cj.Formato);
        Assert.Equal("a1", cj.Nome);
    }

    [Fact]
    public void Conjunto_que_ja_gastava_no_primeiro_dia_da_janela_nao_tem_cobertura_e_o_corte_vira_alerta()
    {
        var cj = Um(Dados(Gastou("a1", 5m, 31), [Foto("a1")], [Lidos(35, 1)]));
        Assert.False(cj.CoberturaCompleta);
        var d = Decidir(cj);
        Assert.Equal(Acao.ALERTAR, d.Acao);
        Assert.Contains(d.Avisos, a => a.Ref == "cobertura");
    }

    [Fact]
    public void Conjunto_que_nasceu_antes_da_primeira_leitura_da_carga_nao_tem_cobertura()
    {
        /* a carga só leu a partir de 10 dias atrás (marca de 15 a 11 dias atrás falhou): o conjunto gastava antes */
        var cj = Um(Dados(Gastou("a1", 10m, 12), [Foto("a1")], [Lidos(15, 11, "erro"), Lidos(10, 1)]));
        Assert.False(cj.CoberturaCompleta);
    }

    [Fact]
    public void O_vencedor_duplica_com_mais_15_reais()
    {
        var d = Decidir(Um(Ganha()));
        Assert.Equal(Acao.DUPLICAR, d.Acao);
        Assert.Equal(50_00, d.CopiaDiarioCentavos);
    }

    [Fact]
    public void Dois_anuncios_entregando_no_ultimo_dia_seguram_a_escala()
    {
        var cj = Um(Ganha(extra: Gastou("a1", 5m, 1, ad: "ad2")));
        Assert.Equal(2, cj.AnunciosAtivos);
        Assert.Equal(Acao.MANTER, Decidir(cj).Acao);
    }

    [Fact]
    public void O_anuncio_antigo_que_gastou_na_semana_mas_nao_no_ultimo_dia_nao_conta()
    {
        var cj = Um(Ganha(extra: Gastou("a1", 5m, 1, ad: "ad2", ultimo: 5)));
        Assert.Equal(1, cj.AnunciosAtivos);
        Assert.Equal(Acao.DUPLICAR, Decidir(cj).Acao);
    }

    /* ---- E10: CBO é verba null, não 0 ------------------------------------------------------------- */

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void Conjunto_em_CBO_tem_verba_nula_e_a_copia_sai_sem_verba_propria(long? orcamento)
    {
        var cj = Um(Ganha(foto: Foto("a1", orcamento: orcamento)));
        Assert.Null(cj.DiarioCentavos);
        var d = Decidir(cj);
        Assert.Equal(Acao.DUPLICAR, d.Acao);
        Assert.True(d.TemCopia);
        Assert.Null(d.CopiaDiarioCentavos);
    }

    /* ---- E01: a memória do motor ------------------------------------------------------------------ */

    static RecomendacaoNaMemoria Dup(string estado, string? copiaStatus = "PAUSED", double diasAtras = 4) =>
        new("r1", "a1", "DUPLICAR", estado, Agora.AddDays(-diasAtras - 0.1), Agora.AddDays(-diasAtras), false, "cp1", copiaStatus);

    [Fact]
    public void Depois_de_um_DUPLICAR_aplicado_a_copia_viva_segura_a_segunda_copia()
    {
        var cj = Um(Ganha(registro: [Dup("aplicada")]));
        Assert.Equal(1, cj.CopiasVivas);
        Assert.Equal(1, cj.Memoria!.CopiasVivas);
        Assert.Equal(4, cj.Memoria.DiasDesdeUltimaAcao);
        Assert.Equal(Acao.MANTER, Decidir(cj).Acao);
    }

    [Fact]
    public void O_DUPLICAR_incerto_conta_como_copia_viva()
        => Assert.Equal(Acao.MANTER, Decidir(Um(Ganha(registro: [Dup("incerto", copiaStatus: null)]))).Acao);

    [Fact]
    public void A_copia_arquivada_ou_o_DUPLICAR_ainda_pendente_nao_contam()
    {
        Assert.Equal(Acao.DUPLICAR, Decidir(Um(Ganha(registro: [Dup("aplicada", copiaStatus: "ARCHIVED")]))).Acao);
        Assert.Equal(Acao.DUPLICAR, Decidir(Um(Ganha(registro: [Dup("pendente")]))).Acao);
    }

    static RecomendacaoNaMemoria AlertaPorPendente(double diasAtras) => new("r9", "a1", "ALERTAR", "pendente", Agora.AddDays(-diasAtras), null, true);

    [Fact]
    public void O_pendente_segura_a_pausa_pelo_que_o_lead_mais_antigo_esperou()
    {
        /* o alerta é de 5 dias atrás, mas os pendentes chegaram ontem: o relógio do D5 é 1 dia */
        var cj = Um(Queima(leads: [Lead("p1", "a1", 1, "Consultor"), Lead("p2", "a1", 1.2, "Consultor")], registro: [AlertaPorPendente(5)]));
        Assert.Equal(1, cj.Memoria!.DiasSegurandoPorPendente);
        var d = Decidir(cj);
        Assert.Equal(Acao.ALERTAR, d.Acao);
        Assert.True(d.SegurouPorPendentes);
    }

    [Fact]
    public void Passado_o_ciclo_com_o_pendente_parado_a_pausa_sai_D5()
    {
        var cj = Um(Queima(leads: [Lead("p1", "a1", 4, "Consultor")], registro: [AlertaPorPendente(5)]));
        Assert.Equal(4, cj.Memoria!.DiasSegurandoPorPendente);
        var d = Decidir(cj);
        Assert.Equal(Acao.PAUSAR, d.Acao);
        Assert.Contains(d.Avisos, a => a.Texto.Contains("D5"));
    }

    [Fact]
    public void A_memoria_lembra_so_o_alerta_depois_da_ultima_acao_executada()
    {
        var m = Memoria.Lembrar(
        [
            new("r1", "a1", "ALERTAR", "pendente", Agora.AddDays(-9), null, true),
            new("r2", "a1", "PAUSAR", "aplicada", Agora.AddDays(-7), Agora.AddDays(-6), false),
            new("r3", "a1", "ALERTAR", "pendente", Agora.AddDays(-2), null, true),
            new("r4", "a1", "ALERTAR", "pendente", Agora.AddDays(-1), null, false),
        ]);
        Assert.Equal(Agora.AddDays(-6), m["a1"].UltimaAcaoEmUtc);
        Assert.Equal(Agora.AddDays(-2), m["a1"].AlertaPorPendenteDesdeUtc);
        Assert.Equal(new MemoriaDoConjunto(6, 2, 0), Memoria.ParaArvore(m["a1"], Agora));
        Assert.Equal(new MemoriaDoConjunto(null, null, 0), Memoria.ParaArvore(null, Agora));
    }

    [Fact]
    public void A_entrada_da_arvore_usa_a_memoria_que_a_carga_calculou()
    {
        var cj = Um(Ganha(registro: [Dup("aplicada")]));
        var e = Rodada.EntradaDe(cj, "x", cj.Formato, Hoje);
        Assert.Equal(cj.Memoria, e.Memoria);
        Assert.Equal(12, e.Avaliados);
        Assert.Equal(12, e.MqlB);
    }

    /* ---- E04: o campo que não veio é pendente na contagem do motor --------------------------------- */

    [Fact]
    public void Lead_sem_o_campo_da_regua_e_pendente_e_segura_a_pausa()
    {
        var cj = Um(Queima(leads: [Lead("x1", "a1", 1, cargo: null)]));
        Assert.Equal(Situacao.Pendente, Assert.Single(cj.Leads).Situacao);
        var d = Decidir(cj);
        Assert.Equal(Acao.ALERTAR, d.Acao);
        Assert.True(d.SegurouPorPendentes);
    }

    /* ---- E02: gasto e leads na MESMA janela de 30 dias, sem teto de linhas ------------------------- */

    [Fact]
    public void Gasto_e_leads_saem_da_mesma_janela_de_30_dias_e_sem_teto_de_linhas()
    {
        var insights = Gastou("a1", 100m, 10, ultimo: 31).Concat(Gastou("a1", 1m, 10));
        var leads = Enumerable.Range(0, 500).Select(k => Lead($"n{k}", "a1", 1 + k % 20, "Diretor"))
            .Append(Lead("velho", "a1", 35, "Diretor"));
        var cj = Um(Dados(insights, [Foto("a1")], [Lidos(40, 1)], leads));
        Assert.Equal(10_00, cj.GastoVidaCentavos);
        Assert.Equal(10, cj.DiasEntrega);
        Assert.Equal(500, cj.Leads.Count);
        Assert.DoesNotContain(cj.Leads, l => l.Id == "velho");
    }

    /* ---- E03: a campanha matriculada --------------------------------------------------------------- */

    [Fact]
    public void Com_campanha_matriculada_o_motor_e_os_KPIs_so_veem_ela()
    {
        var insights = Gastou("a1", 10m, 12, camp: "c1").Concat(Gastou("b1", 10m, 12, ad: "adb", camp: "c2"));
        var fotos = new[] { Foto("a1", camp: "c1"), Foto("b1", camp: "c2") };
        var leads = new[] { Lead("la", "a1", 1, "Diretor"), Lead("lb", "b1", 1, "Diretor", ad: "adb") };
        var so = Carga.Ler(Dados(insights, fotos, [Lidos(20, 1)], leads, campanha: "c1"), P);
        Assert.Equal(["a1"], so.Conjuntos.Select(c => c.AdsetId).ToArray());
        Assert.Equal(60_00, so.Kpis.GastoCentavos); /* 01/11 a 06/11, só a c1 */
        Assert.Equal(1, so.Kpis.Leads);
        Assert.Equal(["la"], so.Central.Select(l => l.Id).ToArray());
        Assert.Equal(6, so.Kpis.DiasLidos); /* a cobertura continua sendo da conta */

        var conta = Carga.Ler(Dados(insights, fotos, [Lidos(20, 1)], leads), P);
        Assert.Equal(2, conta.Conjuntos.Count);
        Assert.Equal(120_00, conta.Kpis.GastoCentavos);
        Assert.Equal(2, conta.Kpis.Leads);
    }

    /* ---- E27: insight sem conjunto não vira conjunto ----------------------------------------------- */

    [Fact]
    public void Insight_sem_conjunto_entra_so_no_gasto_do_mes()
    {
        var insights = Gastou("a1", 10m, 12).Append(new LinhaDeInsight("adX", null, "c1", Hoje.AddDays(-1), 50m, 0));
        var l = Carga.Ler(Dados(insights, [Foto("a1")], [Lidos(20, 1)]), P);
        Assert.Equal(["a1"], l.Conjuntos.Select(c => c.AdsetId).ToArray());
        Assert.Equal(110_00, l.Kpis.GastoCentavos);
    }

    /* ---- E11: o alerta de verba sem MQL ------------------------------------------------------------ */

    [Fact]
    public void Sem_regua_nao_ha_alerta_de_verba_sem_MQL()
    {
        var l = Carga.Ler(Dados(Gastou("a1", 100m, 6), [Foto("a1")], [Lidos(20, 1)], semRegua: true, budget: 1000_00), P);
        Assert.Null(l.AlertaBudget);
        Assert.Equal(0, l.Kpis.GastoSemMqlCentavos);
    }

    [Fact]
    public void O_gasto_sem_MQL_e_por_anuncio_e_nao_some_dentro_de_um_conjunto_com_MQL()
    {
        var insights = Gastou("a1", 10m, 6).Concat(Gastou("a1", 50m, 6, ad: "ad2"));
        var l = Carga.Ler(Dados(insights, [Foto("a1")], [Lidos(20, 1)], [Lead("m1", "a1", 1, "Diretor", ad: "ad1")], budget: 1000_00), P);
        Assert.Equal(300_00, l.Kpis.GastoSemMqlCentavos);
        Assert.NotNull(l.AlertaBudget);
    }

    /* ---- E12: o dia e o mês são os da conta (BRT), não os do host ---------------------------------- */

    [Fact]
    public void Na_virada_do_mes_o_lead_das_22h_de_Brasilia_fica_no_mes_da_conta()
    {
        var agora = new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc); /* 31/10, 23:00 em Brasília */
        var insights = new[] { new LinhaDeInsight("ad1", "a1", "c1", new DateOnly(2026, 10, 30), 10m, 1) };
        var leads = new[]
        {
            new LinhaDeLead("l1", "a1", "ad1", new DateTime(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc), [new Resposta("cargo", "Diretor")]),
            new LinhaDeLead("l0", "a1", "ad1", new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc), [new Resposta("cargo", "Diretor")]),
        };
        var l = Carga.Ler(Dados(insights, [Foto("a1")], [], leads, agora: agora), P);
        Assert.Equal("2026-10", l.Kpis.Mes);
        Assert.Equal(1, l.Kpis.Leads);
        Assert.Equal(30, l.Kpis.DiasDoMes);
        var lead = l.Central.First(x => x.Id == "l1");
        Assert.Equal(new DateTime(2026, 10, 31, 22, 0, 0), lead.CriadoEm);
    }

    [Fact]
    public void Custo_por_MQL_do_mes_arredonda_no_meio_para_cima()
    {
        /* R$ 0,25 em 2 MQL = 12,5 centavos: o V1 (Math.round) dá 13 */
        var insights = new[] { new LinhaDeInsight("ad1", "a1", "c1", Hoje.AddDays(-1), 0.25m, 2) };
        var l = Carga.Ler(Dados(insights, [Foto("a1")], [Lidos(5, 1)], [Lead("m1", "a1", 1, "Diretor"), Lead("m2", "a1", 1, "Diretor")]), P);
        Assert.Equal(13, l.Kpis.CustoPorMqlCentavos);
    }
}

public class RodadaDoMotorTests
{
    static readonly ParametrosMotor P = ParametrosMotor.PadraoDoVault;
    static EntradaConjunto E(long gasto, int a = 0, int b = 0, int c = 0, int avaliados = 0, int pendentes = 0, int dias = 12, bool cobertura = true) =>
        new("x", "adset", "MOFU 1", Formato.Imagem, 1, dias, gasto, avaliados + pendentes, avaliados, pendentes, a, b, c, Leitura.Medida, cobertura, null);
    static readonly EntradaAoVivo Vivo = new(true, 39_00);

    /* ---- E05: a cadência ------------------------------------------------------------------------- */

    [Theory]
    [InlineData(Acao.PAUSAR, 1, null, true)]
    [InlineData(Acao.ALERTAR, 2, null, true)]
    [InlineData(Acao.DUPLICAR, 6, null, false)]
    [InlineData(Acao.DUPLICAR, 7, null, true)]
    [InlineData(Acao.DUPLICAR, 8, null, false)]
    [InlineData(Acao.TROCAR_CRIATIVO, 9, null, false)]
    [InlineData(Acao.TROCAR_CRIATIVO, 10, null, true)]
    [InlineData(Acao.DUPLICAR, 8, 2, false)]
    [InlineData(Acao.DUPLICAR, 8, 3, true)]
    [InlineData(Acao.DUPLICAR, 10, 1, false)]
    public void A_cadencia_le_escala_e_troca_no_setimo_dia_e_depois_a_cada_ciclo(Acao acao, int dias, int? desdeUltimaAcao, bool cabe)
        => Assert.Equal(cabe, Rodada.CabeNaCadencia(acao, dias, P, desdeUltimaAcao is null ? null : new MemoriaDoConjunto(desdeUltimaAcao, null, 0)));

    [Fact]
    public void So_as_acoes_que_pedem_gente_viram_recomendacao()
    {
        Assert.All(new[] { Acao.PAUSAR, Acao.ALERTAR, Acao.DUPLICAR, Acao.TROCAR_CRIATIVO }, a => Assert.True(Rodada.ViraRecomendacao(a)));
        Assert.All(new[] { Acao.MANTER, Acao.AGUARDAR, Acao.SEM_BASE, Acao.SEM_HISTORICO }, a => Assert.False(Rodada.ViraRecomendacao(a)));
    }

    /* ---- E06: o dedup é (conjunto, ação) pendente ------------------------------------------------- */

    [Fact]
    public void O_dedup_e_pela_mesma_acao_no_mesmo_conjunto()
    {
        var pendentes = new HashSet<string> { Rodada.ChaveDeDedup("a1", "PAUSAR") };
        Assert.True(Rodada.Repetida(pendentes, "a1", Acao.PAUSAR));
        Assert.False(Rodada.Repetida(pendentes, "a1", Acao.ALERTAR));
        Assert.False(Rodada.Repetida(pendentes, "a2", Acao.PAUSAR));
    }

    /* ---- E17: as bordas do gatilho de escala e dos cortes ----------------------------------------- */

    [Theory]
    [InlineData(100_00, 9, 9, 12, Acao.MANTER)]       /* amostra 9 < 10 */
    [InlineData(100_00, 10, 10, 12, Acao.DUPLICAR)]   /* amostra 10 */
    [InlineData(100_00, 59, 100, 12, Acao.MANTER)]    /* taxa 59% */
    [InlineData(100_00, 60, 100, 12, Acao.DUPLICAR)]  /* taxa 60% */
    [InlineData(600_00, 10, 10, 12, Acao.DUPLICAR)]   /* custo = teto */
    [InlineData(600_01, 10, 10, 12, Acao.MANTER)]     /* custo 1 centavo acima do teto */
    [InlineData(100_00, 10, 10, 6, Acao.AGUARDAR)]    /* 6 dias de entrega */
    [InlineData(100_00, 10, 10, 7, Acao.DUPLICAR)]    /* 7 dias */
    [InlineData(59_99, 0, 0, 12, Acao.MANTER)]        /* sem MQL, abaixo de 1,0× */
    [InlineData(60_00, 0, 0, 12, Acao.ALERTAR)]       /* sem MQL, 1,0× */
    [InlineData(89_99, 0, 0, 12, Acao.ALERTAR)]       /* sem MQL, abaixo de 1,5× */
    [InlineData(90_00, 0, 0, 12, Acao.PAUSAR)]        /* sem MQL, 1,5× */
    public void As_bordas_da_escala_e_do_corte(long gasto, int mqlA, int avaliados, int dias, Acao esperada)
        => Assert.Equal(esperada, Arvore.Decidir(E(gasto, a: mqlA, avaliados: avaliados, dias: dias), Vivo, 60_00, true, P).Acao);

    /* ---- E25: o arredondamento no meio é para cima, como o V1 -------------------------------------- */

    [Fact]
    public void O_arredondamento_no_meio_e_para_cima()
    {
        Assert.Equal("13%", Dinheiro.Pct(0.125));
        Assert.Equal("63%", Dinheiro.Pct(0.625));
        /* R$ 0,25 sobre 2 MQL A = 12,5 centavos: 13, não 12 */
        Assert.Equal(13, Arvore.Decidir(E(25, a: 2, avaliados: 2), Vivo, 60_00, true, P).Numeros.CustoCentavos);
    }

    /* ---- E09: o texto da trilha leva as chaves e os avisos ----------------------------------------- */

    [Fact]
    public void O_texto_da_trilha_leva_a_regra_e_os_avisos_da_arvore()
    {
        var d = Arvore.Decidir(E(200_00, cobertura: false), Vivo, 60_00, true, P);
        Assert.Equal(Acao.ALERTAR, d.Acao);
        var texto = Rodada.TextoDaTrilha(d, "MOFU 1");
        Assert.StartsWith("\"MOFU 1\": ", texto);
        Assert.Contains("Regra: corte_sem_mql.", texto);
        Assert.Contains("a carga não cobre o conjunto desde a primeira entrega", texto);
        Assert.StartsWith("este conjunto: ", Rodada.TextoDaTrilha(d, null));
    }

    [Fact]
    public void Sem_regra_disparada_o_texto_diz_a_acao()
    {
        var d = Arvore.Decidir(E(10_00, dias: 12), Vivo, 60_00, true, P);
        Assert.Equal(Acao.MANTER, d.Acao);
        Assert.Equal("\"x\": nenhuma regra disparou — manter.", Rodada.TextoDaTrilha(d, "x"));
    }

    /* ---- E18: o contrato de `numeros`, de ida e volta --------------------------------------------- */

    static string Linha(string acao, string numerosJson, string? decisao, string chaves = "[]") =>
        $$"""[{"id":"r1","criado_em":"2026-11-07T13:00:00+00:00","nivel":"conjunto","meta_id":"a1","acao":"{{acao}}","numeros":{{numerosJson}},"chaves":{{chaves}},"decisao":{{(decisao is null ? "null" : $"\"{decisao}\"")}},"em":{{(decisao is null ? "null" : "\"2026-11-07T14:00:00+00:00\"")}},"texto":"t"}]""";

    [Fact]
    public void Os_numeros_gravados_voltam_com_a_verba_da_copia_que_o_aceite_le()
    {
        var e = E(468_00, a: 6, b: 4, c: 2, avaliados: 14);
        var d = Arvore.Decidir(e, Vivo, 60_00, true, P);
        Assert.Equal(Acao.DUPLICAR, d.Acao);
        var json = Rodada.NumerosJson(Rodada.Numeros(d, e, "MOFU 1", 60_00));
        using (var doc = JsonDocument.Parse(json))
        {
            /* o Executor lê exatamente esta chave (Executor.cs, `copia_diario_centavos`): renomear tem de quebrar aqui */
            Assert.Equal(50_00, doc.RootElement.GetProperty("copia_diario_centavos").GetInt64());
            Assert.Equal(60_00, doc.RootElement.GetProperty("teto_centavos").GetInt64());
            Assert.Equal(14, doc.RootElement.GetProperty("avaliados").GetInt32());
            Assert.Equal(12, doc.RootElement.GetProperty("dias_entrega").GetInt32());
            Assert.False(doc.RootElement.GetProperty("segurou_por_pendentes").GetBoolean());
        }
        var rec = Assert.Single(Rodada.ParseRecs(JsonDocument.Parse(Linha("DUPLICAR", json, "aplicada", "[\"gatilho_escala\",\"como_escalar\"]")).RootElement, "x"));
        Assert.Equal(EstadoDaRecomendacao.Aplicada, rec.Estado);
        Assert.Equal("MOFU 1", rec.Nome);
        Assert.True(rec.Decisao.TemCopia);
        Assert.Equal(50_00, rec.Decisao.CopiaDiarioCentavos);
        Assert.Equal(d.Numeros.CustoCentavos, rec.Decisao.Numeros.CustoCentavos);
        Assert.Equal(["gatilho_escala", "como_escalar"], rec.Decisao.Chaves);
    }

    [Fact]
    public void O_alerta_que_segurou_pela_pendencia_volta_marcado()
    {
        var e = E(200_00, pendentes: 2);
        var d = Arvore.Decidir(e, Vivo, 60_00, true, P);
        Assert.True(d.SegurouPorPendentes);
        var rec = Assert.Single(Rodada.ParseRecs(JsonDocument.Parse(Linha("ALERTAR", Rodada.NumerosJson(Rodada.Numeros(d, e, "x", 60_00)), null)).RootElement, "x"));
        Assert.Equal(EstadoDaRecomendacao.Pendente, rec.Estado);
        Assert.True(rec.Decisao.SegurouPorPendentes);
        Assert.False(rec.Decisao.TemCopia);
    }

    [Theory]
    [InlineData(null, EstadoDaRecomendacao.Pendente)]
    [InlineData("aceita", EstadoDaRecomendacao.Aceita)]
    [InlineData("negada", EstadoDaRecomendacao.Negada)]
    [InlineData("aplicada", EstadoDaRecomendacao.Aplicada)]
    [InlineData("falhou", EstadoDaRecomendacao.Falhou)]
    [InlineData("incerto", EstadoDaRecomendacao.Incerto)]
    [InlineData("estornada", EstadoDaRecomendacao.Desconhecido)]
    public void O_estado_e_a_ultima_decisao_e_a_desconhecida_nao_vira_pendente(string? decisao, EstadoDaRecomendacao esperado)
        => Assert.Equal(esperado, Assert.Single(Rodada.ParseRecs(JsonDocument.Parse(Linha("PAUSAR", "{}", decisao)).RootElement, "x")).Estado);

    /* ---- E24: ação pelo nome, nunca pelo número --------------------------------------------------- */

    [Fact]
    public void Acao_desconhecida_ou_numerica_nao_vira_PAUSAR_nem_oferece_aceite()
    {
        Assert.Null(Rodada.AcaoDoNome("0"));
        Assert.Null(Rodada.AcaoDoNome("pausar"));
        Assert.Null(Rodada.AcaoDoNome("REVISAR_OFERTA"));
        Assert.Equal(Acao.PAUSAR, Rodada.AcaoDoNome("PAUSAR"));
        var rec = Assert.Single(Rodada.ParseRecs(JsonDocument.Parse(Linha("0", "{}", null)).RootElement, "x"));
        Assert.Equal(EstadoDaRecomendacao.Desconhecido, rec.Estado);
        Assert.NotEqual(Acao.PAUSAR, rec.Acao);
    }

    /* ---- E16: os conjuntos lidos na rodada voltam para a tela ------------------------------------- */

    [Fact]
    public void Os_avaliados_da_rodada_vao_e_voltam_com_a_trilha()
    {
        var d = Arvore.Decidir(E(468_00, a: 6, b: 4, c: 2, avaliados: 14), Vivo, 60_00, true, P);
        var resposta = JsonSerializer.SerializeToElement(new { avaliados = new[] { Rodada.AvaliadoNaResposta("a1", "MOFU 1", d) } }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var (id, nome, lida) = Assert.Single(Rodada.AvaliadosDaResposta(resposta));
        Assert.Equal("a1", id);
        Assert.Equal("MOFU 1", nome);
        Assert.Equal(d.Acao, lida.Acao);
        Assert.Equal(d.Chaves, lida.Chaves);
        Assert.Equal(d.Trilha, lida.Trilha);
        Assert.Equal(d.CopiaDiarioCentavos, lida.CopiaDiarioCentavos);
        Assert.Equal(d.Numeros.CustoCentavos, lida.Numeros.CustoCentavos);
    }

    /* ---- E28: valor abaixo de 1 centavo não é teto nem budget ------------------------------------- */

    [Fact]
    public void Teto_e_budget_abaixo_de_um_centavo_sao_recusados()
    {
        Assert.Null(Dinheiro.TetoEmCentavos(0.004m));
        Assert.Null(Dinheiro.TetoEmCentavos(0m));
        Assert.Null(Dinheiro.TetoEmCentavos(null));
        Assert.Equal(1, Dinheiro.TetoEmCentavos(0.005m));
        Assert.Equal(60_00, Dinheiro.TetoEmCentavos(60m));
        Assert.False(Dinheiro.BudgetDoTexto("0,004").Ok);
        Assert.True(Dinheiro.BudgetDoTexto("0,01").Ok);
        Assert.Equal("—", Dinheiro.ReaisCurto((long?)null));
    }
}

public class ReguaDoV1Tests
{
    static Dictionary<string, List<string>> L(params (string, string)[] r) => Regua.RespostasDoLead(r);
    static ReguaV2 R() => new()
    {
        Perguntas =
        [
            new() { Nome = "cargo", Forte = ["Diretor"], Media = ["Analista"], Elim = ["Estudante"] },
            new() { Nome = "frota", Chaves = ["quantos_veiculos"], Forte = ["Mais de 50"], Media = ["5 a 10"], Elim = ["Até 4"] },
        ],
    };

    /* ---- E13: a normalização da régua é a do V1 ---------------------------------------------------- */

    [Theory]
    [InlineData("  Dono_ou_Diretor__ ", "dono_ou_diretor")]
    [InlineData("__dono_ou_diretor", "dono_ou_diretor")]
    [InlineData("_ x _", "x")]
    [InlineData("a___b", "a_b")]
    [InlineData("\tGestão \n", "gestão")]
    [InlineData("cargo atual", "cargo atual")]
    [InlineData("﻿sim", "sim")]
    public void A_normalizacao_da_regua_e_a_do_V1(string entrada, string esperada) => Assert.Equal(esperada, Regua.NormalizarResposta(entrada));

    [Fact]
    public void O_acento_e_o_sublinhado_do_meio_ficam()
    {
        Assert.NotEqual(Regua.NormalizarResposta("gestão"), Regua.NormalizarResposta("gestao"));
        Assert.NotEqual(Regua.NormalizarResposta("cargo_atual"), Regua.NormalizarResposta("cargo atual"));
        var r = new ReguaV2 { Perguntas = [new() { Nome = "area", Forte = ["gestão"], Media = [], Elim = [] }] };
        Assert.Equal(Situacao.Mql, Regua.AvaliarLead(L(("area", "Gestão\t")), r).Situacao);
        Assert.Equal(Situacao.Pendente, Regua.AvaliarLead(L(("area", "gestao")), r).Situacao);
    }

    [Fact]
    public void O_detector_de_lead_de_vaga_continua_sem_acento()
        => Assert.Equal(Situacao.NaoMql, Regua.AvaliarLead(L(("cargo", "Diretor"), ("frota", "Mais de 50"), ("Pretensão_Salarial", "5 mil")), R()).Situacao);

    /* ---- E14: o texto da opção casa como o código ------------------------------------------------- */

    [Fact]
    public void O_texto_da_opcao_entra_como_mais_um_valor_do_campo()
    {
        var respostas = Regua.RespostasDoLead([new Resposta("cargo", "cargo_1", "Diretor"), new Resposta("frota", "frota_3", "Mais de 50")]);
        Assert.Equal(["cargo_1", "Diretor"], respostas["cargo"]);
        Assert.Equal(Tier.A, Regua.AvaliarLead(respostas, R()).Tier);
        /* texto igual ao código (normalizado) não repete; código em branco não ganha o texto */
        Assert.Single(Regua.RespostasDoLead([new Resposta("cargo", "Diretor", "diretor ")])["cargo"]);
        Assert.Single(Regua.RespostasDoLead([new Resposta("cargo", "", "Diretor")])["cargo"]);
    }

    [Fact]
    public void A_eliminatoria_pelo_texto_da_opcao_ainda_ganha()
        => Assert.Equal(Situacao.NaoMql, Regua.AvaliarLead(Regua.RespostasDoLead([new Resposta("cargo", "Diretor", "Estudante"), new Resposta("frota", "Mais de 50")]), R()).Situacao);

    /* ---- E04 e E19: em branco é não MQL; o campo que não veio é pendente -------------------------- */

    [Fact]
    public void Resposta_em_branco_e_nao_MQL_e_conta_como_avaliado()
    {
        var lead = L(("cargo", "Diretor"), ("frota", " "));
        var r = Regua.AvaliarLead(lead, R());
        Assert.Equal(Situacao.NaoMql, r.Situacao);
        Assert.Equal(["frota"], r.Faltantes);
        Assert.False(Regua.ContaComoPendente(lead, r, R()));
        var c = Regua.Contar([lead], R());
        Assert.Equal(1, c.Avaliados); Assert.Equal(0, c.Pendentes); Assert.Equal(0, c.Mql);
    }

    [Fact]
    public void O_campo_que_nem_veio_no_lead_e_pendente_na_contagem()
    {
        var lead = L(("cargo", "Diretor"));
        var r = Regua.AvaliarLead(lead, R());
        Assert.Equal(Situacao.NaoMql, r.Situacao);
        Assert.Equal(["frota"], Regua.CamposQueNaoVieram(lead, R()));
        Assert.True(Regua.ContaComoPendente(lead, r, R()));
        Assert.Equal(Situacao.Pendente, Regua.SituacaoNaContagem(lead, r, R()));
        var c = Regua.Contar([lead], R());
        Assert.Equal(0, c.Avaliados); Assert.Equal(1, c.Pendentes);
    }

    [Fact]
    public void O_apelido_que_veio_conta_como_o_campo_e_a_eliminatoria_derruba_mesmo_com_campo_faltando()
    {
        Assert.Empty(Regua.CamposQueNaoVieram(L(("cargo", "Diretor"), ("quantos_veiculos", "")), R()));
        var elim = L(("cargo", "Estudante"));
        var r = Regua.AvaliarLead(elim, R());
        Assert.False(Regua.ContaComoPendente(elim, r, R()));
        Assert.Equal(1, Regua.Contar([elim], R()).Avaliados);
    }

    [Fact]
    public void Sem_faltante_derruba_o_lead_que_pulou_pergunta_nao_e_derrubado_por_ela()
    {
        var lead = L(("cargo", "Diretor"), ("frota", ""));
        Assert.Equal(Situacao.NaoMql, Regua.AvaliarLead(lead, R()).Situacao);
        Assert.Equal(Situacao.Mql, Regua.AvaliarLead(lead, R(), faltanteDerruba: false).Situacao);
    }

    /* ---- E29 e E32: o rascunho da régua ----------------------------------------------------------- */

    [Fact]
    public void O_rascunho_valido_nao_tem_problema_e_a_linha_vazia_e_ignorada()
        => Assert.Empty(Regua.ProblemasDoRascunho([.. R().Perguntas, new PerguntaDaRegua()]));

    [Fact]
    public void O_rascunho_recusa_nome_vazio_resposta_em_duas_listas_e_campo_repetido()
    {
        var problemas = Regua.ProblemasDoRascunho(
        [
            new() { Nome = "", Forte = ["x"] },
            new() { Nome = "cargo", Forte = ["Dono"], Media = ["dono_"], Elim = [] },
            new() { Nome = "Cargo", Forte = ["a"] },
            new() { Nome = "frota", Forte = [] },
        ]);
        Assert.Contains(problemas, p => p.Contains("pergunta 1") && p.Contains("nome do campo"));
        Assert.Contains(problemas, p => p.Contains("duas listas"));
        Assert.Contains(problemas, p => p.Contains("\"Cargo\" aparece duas vezes"));
        Assert.Contains(problemas, p => p.Contains("\"frota\" não tem nenhuma resposta"));
    }

    [Fact]
    public void O_apelido_de_um_campo_nao_pode_ser_de_outra_pergunta()
    {
        var problemas = Regua.ProblemasDoRascunho(
        [
            new() { Nome = "cargo", Forte = ["Dono"] },
            new() { Nome = "funcao", Chaves = ["Cargo", "funcao"], Forte = ["Chefe"] },
        ]);
        Assert.Single(problemas);
        Assert.Contains("\"Cargo\" aparece duas vezes na régua (como nome ou outro nome", problemas[0]);
    }
}

public class FaixaENichoTests
{
    static readonly ParametrosMotor P = ParametrosMotor.PadraoDoVault;

    /* ---- E15: a faixa exige só as etapas que o Planejador monta ----------------------------------- */

    [Theory]
    [InlineData(3000, false, false, new[] { Etapa.MOFU })]
    [InlineData(4000, false, false, new[] { Etapa.MOFU })]                         /* f2 sem lista: BOFU reservado */
    [InlineData(4000, true, false, new[] { Etapa.MOFU, Etapa.BOFU })]
    [InlineData(10000, true, true, new[] { Etapa.MOFU, Etapa.BOFU })]              /* f3: TOFU de R$ 16,66/dia reservado */
    [InlineData(12000, true, true, new[] { Etapa.TOFU, Etapa.MOFU, Etapa.BOFU })]  /* f3: TOFU de R$ 20/dia */
    [InlineData(12000, false, false, new[] { Etapa.TOFU, Etapa.MOFU })]
    [InlineData(600, false, false, new[] { Etapa.MOFU })]
    public void A_faixa_pede_so_as_etapas_que_a_estrutura_monta(int reais, bool lista, bool site, Etapa[] esperadas)
        => Assert.Equal(esperadas, Faixa.EtapasDaFaixa(Dinheiro.CentavosDe(reais), lista, site, P));

    /* ---- E26: a sugestão de nicho casa no início da palavra --------------------------------------- */

    [Theory]
    [InlineData("Consultoria de recursos humanos", "rh-gestao-pessoas")]
    [InlineData("Curso de inglês online", "educacao")]
    [InlineData("Clínica odontológica", "saude")]
    [InlineData("Loja de e-commerce de roupas", "varejo-distribuicao")]
    [InlineData("Empresa de RH", "rh-gestao-pessoas")]
    public void A_sugestao_de_nicho_casa_no_inicio_da_palavra(string texto, string slug) => Assert.Equal(slug, Nichos.Sugerir(texto));

    [Fact]
    public void Pedaco_de_palavra_nao_casa()
    {
        Assert.NotEqual("construcao-engenharia", Nichos.Sugerir("Design de sobrancelhas"));
        Assert.NotEqual("educacao", Nichos.Sugerir("Gestão de recursos"));
    }
}
