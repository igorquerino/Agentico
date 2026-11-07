using BlueOS.Core.Executor;
using BlueOS.Core.Modelos;

namespace BlueOS.Core.Motor;

/* ============================================================================
   A CARGA DIÁRIA COMO O MOTOR E O ONGOING A LEEM: função pura.
   ----------------------------------------------------------------------------
   Porta de lib/trafego/motor/conjuntos.ts (conjuntosDaCargaDiaria, lerMarcas,
   anunciosNoUltimoDia), memoria.ts (a memória por conjunto), ongoing.ts (os
   KPIs do mês e a central) e avisos.ts (gastoSemMqlNoMes). A API só lê as
   linhas do banco (Automacao) e entrega aqui; nada aqui toca banco ou Meta.

   O QUE NÃO FOI LIDO SAI COMO "NÃO SEI", NUNCA COMO O VALOR QUE DEIXA AGIR:
   · a LEITURA vem da marca do dia (trafego_carga_dia): dia de entrega que
     nenhuma rodada da carga leu inteiro deixa o conjunto Incompleta (ou
     SemAcesso, com recusa de permissão), e a árvore AGUARDA;
   · a FOTO (trafego_adset_estado) vale 36 h: vencida, status e verba viram
     null e a leitura vira Vencida;
   · o FORMATO que não é imagem/video/misto é Desconhecido (SEM_BASE);
   · a VERBA ≤ 0 ou ausente é null (CBO ou não lida), nunca 0;
   · a COBERTURA só é completa quando a carga viu o conjunto NASCER;
   · os ANÚNCIOS ativos são os que gastaram no último dia de entrega (7 d);
   · gasto e leads saem da MESMA janela de 30 dias, sem teto de linhas;
   · com campanha matriculada (trafego_contas.motor_campanha_id), só ela.
   ============================================================================ */

public sealed record LinhaDeInsight(string? AdId, string? AdsetId, string? CampaignId, DateOnly Dia, decimal Gasto, long Leads);
public sealed record LinhaDeLead(string LeadId, string? AdsetId, string? AdId, DateTime? CriadoEmUtc, IReadOnlyList<Resposta> Respostas);
public sealed record FotoDoConjunto(string AdsetId, string? CampaignId, string? Nome, string? Status, long? OrcamentoDiarioCentavos, string? Formato, DateTime? VistoEmUtc);
/// <summary>Uma linha de `trafego_carga_dia`: o dia em que a carga RODOU, o estado e, do detalhe, os dias de dado que ela
/// releu (`dias`), os que leu com sucesso (`dias_ok`) e as recusas de permissão.</summary>
public sealed record MarcaDaCarga(DateOnly Dia, string? Estado, IReadOnlyList<DateOnly> Dias, IReadOnlyList<DateOnly> DiasOk, decimal RecusasDePermissao);

public sealed record DadosDaCarga(
    DateTime AgoraUtc,
    string? CampanhaId,
    ReguaV2? Regua,
    long? BudgetCentavos,
    long? TetoCentavos,
    IReadOnlyList<LinhaDeInsight> Insights,
    IReadOnlyList<LinhaDeLead> Leads,
    IReadOnlyList<FotoDoConjunto> Fotos,
    IReadOnlyList<MarcaDaCarga> Marcas,
    IReadOnlyList<RecomendacaoNaMemoria> Registro);

public sealed record LeituraDaCarga(List<ConjuntoEmVeiculacao> Conjuntos, KpisDoMes Kpis, List<LeadDaCentral> Central, Aviso? AlertaBudget);

public static class Carga
{
    public const int DiasDaJanela = 30;
    public const int DiasRecentes = 7;
    public const int MaxLeadsNaCentral = 150;
    public static readonly TimeSpan ValidadeDaFoto = TimeSpan.FromHours(36);

    /// <summary>O primeiro dia que as consultas precisam ler: o menor entre o início da janela e o início do mês.</summary>
    public static DateOnly DesdeDaLeitura(DateOnly hoje)
    {
        var desde = hoje.AddDays(-DiasDaJanela);
        var inicioDoMes = new DateOnly(hoje.Year, hoje.Month, 1);
        return desde < inicioDoMes ? desde : inicioDoMes;
    }

    /// <summary>A meia-noite do dia NO FUSO DA CONTA, como instante UTC (inicioDoDia do V1).</summary>
    public static DateTime InicioDoDiaUtc(DateOnly dia) => TimeZoneInfo.ConvertTimeToUtc(dia.ToDateTime(TimeOnly.MinValue), Fuso.DaConta);

    /// <summary>O formato da foto: só imagem, video e misto valem; o resto (null, gravado antes da 0147) é Desconhecido.</summary>
    public static Formato FormatoDe(string? f) => f switch
    {
        "imagem" => Formato.Imagem,
        "video" => Formato.Video,
        "misto" => Formato.Misto,
        _ => Formato.Desconhecido,
    };

    /// <summary>A etapa pelo nome do conjunto, como o Start nomeia ("0 · …" TOFU, "5.1 · …" BOFU, "MOFU" no nome).</summary>
    public static Etapa EtapaDoNome(string? nome)
    {
        var n = (nome ?? "").ToUpperInvariant();
        if (n.Contains("TOFU") || n.StartsWith("0 ")) return Etapa.TOFU;
        if (n.Contains("BOFU") || n.StartsWith("5.")) return Etapa.BOFU;
        return Etapa.MOFU;
    }

    /// <summary>
    /// O que a marca diz de cada dia de dado, e o primeiro dia que a carga leu (lerMarcas). Um dia está lido se ALGUMA
    /// rodada que o cobriu saiu `ok` (ou o tem em `dias_ok`); sem nenhuma, SemAcesso se houve recusa de permissão e
    /// Incompleta nos outros casos. O começo da carga é o primeiro dia que uma rodada leu com sucesso.
    /// </summary>
    public static (Dictionary<DateOnly, Leitura> LeituraDoDia, DateOnly? InicioDaCarga) LerMarcas(IEnumerable<MarcaDaCarga> marcas)
    {
        var doDia = new Dictionary<DateOnly, Leitura>();
        DateOnly? inicio = null;
        foreach (var m in marcas)
        {
            var ok = m.DiasOk.ToHashSet();
            foreach (var dia in m.Dias)
            {
                var lido = m.Estado == "ok" || ok.Contains(dia);
                if (lido && (inicio is null || dia < inicio)) inicio = dia;
                if (doDia.TryGetValue(dia, out var ja) && ja == Leitura.Medida) continue;
                if (lido) { doDia[dia] = Leitura.Medida; continue; }
                if (m.RecusasDePermissao > 0) doDia[dia] = Leitura.SemAcesso;
                else if (!doDia.ContainsKey(dia)) doDia[dia] = Leitura.Incompleta;
            }
        }
        return (doDia, inicio);
    }

    /// <summary>Quantos anúncios gastaram no ÚLTIMO dia em que o conjunto entregou (anunciosNoUltimoDia).</summary>
    public static int AnunciosNoUltimoDia(IReadOnlyDictionary<DateOnly, HashSet<string>> anunciosPorDia) =>
        anunciosPorDia.Count == 0 ? 0 : anunciosPorDia[anunciosPorDia.Keys.Max()].Count;

    sealed class Acumulado
    {
        public readonly SortedDictionary<DateOnly, (decimal Gasto, long Leads)> PorDia = new();
        public readonly HashSet<DateOnly> DiasComGasto = new();
        public DateOnly? PrimeiroDia;
        public readonly Dictionary<DateOnly, HashSet<string>> AnunciosPorDia = new();
    }

    public static LeituraDaCarga Ler(DadosDaCarga d, ParametrosMotor p)
    {
        var hoje = Fuso.DiaDaConta(d.AgoraUtc);
        var desde = hoje.AddDays(-DiasDaJanela);
        var desdeUtc = InicioDoDiaUtc(desde);
        var inicioDoMes = new DateOnly(hoje.Year, hoje.Month, 1);
        var inicioDoMesUtc = InicioDoDiaUtc(inicioDoMes);
        var recenteDesde = hoje.AddDays(-DiasRecentes);
        var campanha = string.IsNullOrEmpty(d.CampanhaId) ? null : d.CampanhaId;

        /* ---- os leads, avaliados uma vez (a MESMA situação na central, no KPI e na árvore) ---- */
        var instante = new Dictionary<LeadDaCentral, DateTime?>(ReferenceEqualityComparer.Instance);
        var respostasDe = new Dictionary<LeadDaCentral, IReadOnlyDictionary<string, List<string>>>(ReferenceEqualityComparer.Instance);
        var fotos = new Dictionary<string, FotoDoConjunto>();
        foreach (var f in d.Fotos) fotos[f.AdsetId] = f;
        var leads = new List<LeadDaCentral>();
        foreach (var l in d.Leads.OrderByDescending(x => x.CriadoEmUtc).ThenBy(x => x.LeadId, StringComparer.Ordinal))
        {
            var lead = new LeadDaCentral
            {
                Id = l.LeadId, AdsetId = l.AdsetId, AdId = l.AdId, Respostas = l.Respostas.ToList(),
                CriadoEm = l.CriadoEmUtc is { } u ? Fuso.NaConta(u) : DateTime.MinValue,
                Conjunto = l.AdsetId is not null && fotos.TryGetValue(l.AdsetId, out var fl) ? fl.Nome : null,
            };
            var porCampo = Regua.RespostasDoLead(lead.Respostas);
            respostasDe[lead] = porCampo;
            if (d.Regua is not null)
            {
                var r = Regua.AvaliarLead(porCampo, d.Regua);
                lead.Situacao = Regua.SituacaoNaContagem(porCampo, r, d.Regua);
                lead.Tier = r.Tier; lead.Motivo = r.Motivo; lead.NaoMapeadas = r.NaoMapeadas.ToList(); lead.Faltantes = r.Faltantes.ToList();
            }
            instante[lead] = l.CriadoEmUtc;
            leads.Add(lead);
        }

        /* ---- os conjuntos do motor: só a janela, e só a campanha matriculada ---- */
        var porConjunto = new Dictionary<string, Acumulado>();
        foreach (var i in d.Insights)
        {
            if (i.Dia < desde || i.AdsetId is null) continue;
            if (campanha is not null && i.CampaignId != campanha) continue;
            if (!porConjunto.TryGetValue(i.AdsetId, out var a)) porConjunto[i.AdsetId] = a = new Acumulado();
            var doDia = a.PorDia.TryGetValue(i.Dia, out var x) ? x : (Gasto: 0m, Leads: 0L);
            a.PorDia[i.Dia] = (doDia.Gasto + i.Gasto, doDia.Leads + i.Leads);
            if (i.Gasto > 0)
            {
                if (a.PrimeiroDia is null || i.Dia < a.PrimeiroDia) a.PrimeiroDia = i.Dia;
                if (!string.IsNullOrEmpty(i.AdId) && i.Dia >= recenteDesde)
                {
                    if (!a.AnunciosPorDia.TryGetValue(i.Dia, out var ads)) a.AnunciosPorDia[i.Dia] = ads = new();
                    ads.Add(i.AdId);
                }
            }
        }
        foreach (var a in porConjunto.Values)
            foreach (var (dia, v) in a.PorDia)
                if (v.Gasto > 0) a.DiasComGasto.Add(dia);
        /* a tela mostra também o conjunto que a carga fotografou e não entregou na janela (da campanha, se há) */
        foreach (var f in d.Fotos)
            if (!porConjunto.ContainsKey(f.AdsetId) && (campanha is null || f.CampaignId == campanha)) porConjunto[f.AdsetId] = new Acumulado();

        var (leituraDoDia, inicioDaCarga) = LerMarcas(d.Marcas);
        var memoria = Memoria.Lembrar(d.Registro);
        var leadsPorConjunto = leads.Where(l => l.AdsetId is not null && instante[l] >= desdeUtc).GroupBy(l => l.AdsetId!).ToDictionary(g => g.Key, g => g.ToList());

        var conjuntos = new List<ConjuntoEmVeiculacao>();
        foreach (var (id, a) in porConjunto)
        {
            fotos.TryGetValue(id, out var foto);
            var valida = foto?.VistoEmUtc is { } visto && d.AgoraUtc - visto <= ValidadeDaFoto;
            var vencida = foto is not null && !valida;

            var leitura = Leitura.Medida;
            foreach (var dia in a.DiasComGasto)
            {
                var lida = leituraDoDia.TryGetValue(dia, out var ld) ? ld : Leitura.Incompleta;
                if (lida == Leitura.SemAcesso) { leitura = Leitura.SemAcesso; break; }
                if (lida == Leitura.Incompleta) leitura = Leitura.Incompleta;
            }
            if (leitura != Leitura.SemAcesso && vencida) leitura = Leitura.Vencida;

            var nome = foto?.Nome ?? id;
            var cj = new ConjuntoEmVeiculacao
            {
                AdsetId = id, Nome = nome, Etapa = EtapaDoNome(foto?.Nome),
                DiarioCentavos = valida && foto!.OrcamentoDiarioCentavos is > 0 ? foto.OrcamentoDiarioCentavos : null,
                Ativo = valida && foto!.Status is { } st ? st == "ACTIVE" : null,
                Formato = FormatoDe(foto?.Formato),
                AnunciosAtivos = AnunciosNoUltimoDia(a.AnunciosPorDia),
                PrimeiraEntrega = a.PrimeiroDia,
                Dias = a.PorDia.Select(x => new DiaDeInsight(x.Key, Dinheiro.CentavosDe(x.Value.Gasto), (int)x.Value.Leads)).ToList(),
                Leads = leadsPorConjunto.GetValueOrDefault(id) ?? new(),
                CoberturaCompleta = a.PrimeiroDia is { } pd && pd > desde && inicioDaCarga is { } ic && pd > ic,
                Leitura = leitura,
            };
            foreach (var l in cj.Leads) l.Conjunto ??= nome;

            /* a memória (3.3), com o relógio do D5 limitado pelo pendente mais antigo */
            memoria.TryGetValue(id, out var lembranca);
            var m = Memoria.ParaArvore(lembranca, d.AgoraUtc);
            var maisAntigo = cj.Leads.Where(l => l.Situacao == Situacao.Pendente && instante[l] is not null).Select(l => instante[l]).Min();
            cj.Memoria = Memoria.ComOPendenteMaisAntigo(m, Memoria.DiasDesde(maisAntigo, d.AgoraUtc));
            cj.CopiasVivas = lembranca?.CopiasVivas ?? 0;
            cj.UltimaAcaoExecutada = lembranca?.UltimaAcaoEmUtc is { } ua ? Fuso.DiaDaConta(ua) : null;
            cj.PendenteDesde = lembranca?.AlertaPorPendenteDesdeUtc is { } pdu ? Fuso.DiaDaConta(pdu) : null;
            conjuntos.Add(cj);
        }
        conjuntos = conjuntos.OrderBy(c => c.Nome, StringComparer.Ordinal).ToList();

        /* ---- os KPIs do mês (ongoing.ts): só a campanha do agente, quando há ---- */
        var doMes = d.Insights.Where(i => i.Dia >= inicioDoMes).ToList();
        var gastoMes = Dinheiro.CentavosDe(doMes.Where(i => campanha is null || i.CampaignId == campanha).Sum(i => i.Gasto));
        var diasLidos = doMes.Select(i => i.Dia).Distinct().Count();
        HashSet<string>? daCampanha = null;
        if (campanha is not null)
        {
            daCampanha = d.Fotos.Where(f => f.CampaignId == campanha).Select(f => f.AdsetId).ToHashSet();
            foreach (var i in d.Insights) if (i.CampaignId == campanha && i.AdsetId is not null) daCampanha.Add(i.AdsetId);
        }
        var doRecorte = daCampanha is null ? leads : leads.Where(l => l.AdsetId is not null && daCampanha.Contains(l.AdsetId)).ToList();
        var leadsDoMes = doRecorte.Where(l => instante[l] >= inicioDoMesUtc).ToList();
        var contagem = d.Regua is null ? new ContagemPelaRegua(0, 0, 0, 0, 0, 0) : Regua.Contar(leadsDoMes.Select(l => respostasDe[l]), d.Regua);

        /* o gasto do mês em anúncio sem MQL (gastoSemMqlNoMes): por ANÚNCIO, na conta; sem régua não há "sem MQL" */
        long semMql = 0;
        Aviso? alerta = null;
        if (d.Regua is not null)
        {
            var leadsPorAd = leads.Where(l => l.AdId is not null && instante[l] >= inicioDoMesUtc).GroupBy(l => l.AdId!).ToDictionary(g => g.Key, g => g.ToList());
            var semMqlReais = 0m;
            foreach (var g in doMes.Where(i => i.AdId is not null).GroupBy(i => i.AdId!))
            {
                var mqlDoAd = leadsPorAd.TryGetValue(g.Key, out var ls) ? Regua.Contar(ls.Select(l => respostasDe[l]), d.Regua).Mql : 0;
                if (mqlDoAd == 0) semMqlReais += g.Sum(i => i.Gasto);
            }
            semMql = Dinheiro.CentavosDe(semMqlReais);
            if (d.BudgetCentavos is { } b) alerta = Arvore.AlertaBudget(semMql, b, p);
        }

        var kpis = new KpisDoMes($"{hoje.Year:0000}-{hoje.Month:00}", gastoMes, d.BudgetCentavos, Math.Max(0, hoje.Day - 1), diasLidos, leadsDoMes.Count,
            contagem.Avaliados, contagem.Pendentes, Math.Max(0, contagem.Avaliados - contagem.Mql), contagem.Mql, contagem.MqlA, contagem.MqlB, contagem.MqlC,
            d.BudgetCentavos is { } bc && d.TetoCentavos is > 0 ? (int)(bc / d.TetoCentavos.Value) : null,
            contagem.Mql > 0 ? (long)Math.Round((double)gastoMes / contagem.Mql, MidpointRounding.AwayFromZero) : null,
            d.TetoCentavos, semMql);

        var central = doRecorte.Where(l => instante[l] >= desdeUtc).Take(MaxLeadsNaCentral).ToList();
        return new LeituraDaCarga(conjuntos, kpis, central, alerta);
    }
}
