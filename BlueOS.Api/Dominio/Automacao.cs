using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using Npgsql;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O ONGOING: os números do mês, os conjuntos, o que o agente fez e a central de
   leads (porta de lib/trafego/motor/ongoing.ts), com a MESMA régua do motor.
   ----------------------------------------------------------------------------
   Quatro leituras numa conexão só; a contagem de MQL é a régua v2 em C#
   (Core.Motor.Regua), nunca o julgador por IA.
   ============================================================================ */
public sealed record OngoingComConjuntos(Ongoing Ongoing, List<ConjuntoEmVeiculacao> Conjuntos);

public sealed class Automacao(Db db)
{
    public async Task<OngoingComConjuntos> OngoingAsync(Cliente c)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Now);
        var inicioDoMes = new DateOnly(hoje.Year, hoje.Month, 1);
        var desde = hoje.AddDays(-60);
        await using var cx = await db.AbrirAsync();

        var conjuntos = await Db.ListaAsync(cx, """
            select e.adset_id, e.nome, e.status, e.orcamento_diario_centavos, e.campaign_id
            from public.trafego_adset_estado e where e.cliente_slug = @s order by e.nome
            """, r => new ConjuntoEmVeiculacao
            {
                AdsetId = r.S("adset_id")!, Nome = r.S("nome") ?? r.S("adset_id")!, Etapa = EtapaDoNome(r.S("nome")),
                DiarioCentavos = r.L("orcamento_diario_centavos"), Ativo = r.S("status") is null ? null : r.S("status") == "ACTIVE",
                Formato = Formato.Desconhecido,
            }, ("s", c.Slug));
        var porAdset = conjuntos.ToDictionary(x => x.AdsetId);

        var insights = await Db.ListaAsync(cx, """
            select adset_id, dia, sum(gasto) as gasto, sum(leads) as leads
            from public.trafego_insight_dia where cliente_slug = @s and dia >= @desde
            group by adset_id, dia order by adset_id, dia
            """, r => (Adset: r.S("adset_id"), Dia: r.Data("dia")!.Value, Gasto: (long)Math.Round((r.Dec("gasto") ?? 0) * 100), Leads: (int)r.L("leads")), ("s", c.Slug), ("desde", desde));
        foreach (var i in insights)
        {
            if (i.Adset is null || !porAdset.TryGetValue(i.Adset, out var cj))
            {
                cj = new ConjuntoEmVeiculacao { AdsetId = i.Adset ?? "sem-conjunto", Nome = i.Adset ?? "sem conjunto", Etapa = Etapa.MOFU, Formato = Formato.Desconhecido };
                porAdset[cj.AdsetId] = cj; conjuntos.Add(cj);
            }
            cj.Dias.Add(new DiaDeInsight(i.Dia, i.Gasto, i.Leads));
        }
        foreach (var cj in conjuntos) cj.PrimeiraEntrega = cj.Dias.Where(d => d.GastoCentavos > 0).Select(d => (DateOnly?)d.Dia).Min();

        var leads = await Db.ListaAsync(cx, """
            select lead_id, adset_id, ad_id, criado_em, respostas::text as respostas
            from public.trafego_lead where cliente_slug = @s and criado_em >= @desde
            order by criado_em desc limit 400
            """, r => new LeadDaCentral
            {
                Id = r.S("lead_id")!, AdsetId = r.S("adset_id"), AdId = r.S("ad_id"), CriadoEm = r.Dt("criado_em") ?? DateTime.MinValue,
                Respostas = Respostas(r.Json("respostas")),
            }, ("s", c.Slug), ("desde", inicioDoMes.AddDays(-30).ToDateTime(TimeOnly.MinValue)));
        foreach (var l in leads)
        {
            if (l.AdsetId is not null && porAdset.TryGetValue(l.AdsetId, out var cj)) { cj.Leads.Add(l); l.Conjunto = cj.Nome; }
            if (c.Regua is not null)
            {
                var r = Regua.AvaliarLead(Regua.RespostasDoLead(l.Respostas.Select(x => (x.Pergunta, x.Valor))), c.Regua);
                l.Situacao = r.Situacao; l.Tier = r.Tier; l.Motivo = r.Motivo; l.NaoMapeadas = r.NaoMapeadas.ToList(); l.Faltantes = r.Faltantes.ToList();
            }
        }

        var log = await Db.ListaAsync(cx, """
            select acao, alvo_tipo, alvo_nome, alvo_id, ator, origem, ocorrido_em, detalhe::text as detalhe
            from public.trafego_log_acao where cliente_slug = @s and ocorrido_em >= @desde
            order by ocorrido_em desc limit 40
            """, r =>
            {
                var via = (string?)null;
                try { var d = r.Json("detalhe"); if (d is not null && JsonDocument.Parse(d).RootElement.TryGetProperty("via", out var v)) via = v.GetString(); } catch { }
                var alvo = r.S("alvo_nome") ?? r.S("alvo_id");
                return new AcaoDoAgente(r.Dt("ocorrido_em") ?? DateTime.MinValue, "log",
                    $"{(r.S("acao") ?? "").Replace('_', ' ')}{(r.S("alvo_tipo") is { } t ? $" · {t}" : "")}{(alvo is not null ? $" \"{alvo}\"" : "")}",
                    r.S("origem") == "meta_activity" ? "lido da Meta" : via is not null ? $"pelo Blue.OS ({via})" : "pelo Blue.OS", r.S("ator"), null);
            }, ("s", c.Slug), ("desde", hoje.AddDays(-30).ToDateTime(TimeOnly.MinValue)));

        var recs = await RecomendacoesAsync(cx, c.Slug);
        var acoes = recs.Where(r => r.Estado != EstadoDaRecomendacao.Pendente)
            .Select(r => new AcaoDoAgente(r.DecididaEm ?? r.CriadoEm, "motor", $"{Arvore.RotuloDaAcao(r.Acao).Rotulo.ToLowerInvariant()} em \"{r.Nome}\"", r.Estado.ToString().ToLowerInvariant(), r.DecididaPor, r.Motivo))
            .Concat(log).OrderByDescending(a => a.Quando).Take(40).ToList();

        /* ---- os KPIs do mês, como ongoing.ts ---------------------------------- */
        var doMes = insights.Where(i => i.Dia >= inicioDoMes).ToList();
        var gasto = doMes.Sum(i => i.Gasto);
        var diasLidos = doMes.Select(i => i.Dia).Distinct().Count();
        var leadsDoMes = leads.Where(l => DateOnly.FromDateTime(l.CriadoEm) >= inicioDoMes).ToList();
        var contagem = c.Regua is null ? new ContagemPelaRegua(0, 0, 0, 0, 0, 0)
            : Regua.Contar(leadsDoMes.Select(l => (IReadOnlyDictionary<string, List<string>>)Regua.RespostasDoLead(l.Respostas.Select(x => (x.Pergunta, x.Valor)))), c.Regua);
        long? teto = c.TetoMql is > 0 ? Dinheiro.CentavosDe(c.TetoMql.Value) : null;
        long? budget = c.BudgetMensal is > 0 ? Dinheiro.CentavosDe(c.BudgetMensal.Value) : null;
        var adsetsComMql = leadsDoMes.Where(l => l.Situacao == Situacao.Mql).Select(l => l.AdsetId).ToHashSet();
        var gastoSemMql = doMes.Where(i => !adsetsComMql.Contains(i.Adset)).Sum(i => i.Gasto);
        var kpis = new KpisDoMes($"{hoje.Year:0000}-{hoje.Month:00}", gasto, budget, Math.Max(0, hoje.Day - 1), diasLidos, leadsDoMes.Count,
            contagem.Avaliados, contagem.Pendentes, Math.Max(0, contagem.Avaliados - contagem.Mql), contagem.Mql, contagem.MqlA, contagem.MqlB, contagem.MqlC,
            budget is not null && teto is not null ? (int)(budget / teto) : null, contagem.Mql > 0 ? gasto / contagem.Mql : null, teto, gastoSemMql);
        var alerta = budget is not null ? Arvore.AlertaBudget(gastoSemMql, budget.Value, ParametrosMotor.PadraoDoVault) : null;
        var central = leads.Where(l => DateOnly.FromDateTime(l.CriadoEm) >= hoje.AddDays(-30)).Take(150).ToList();
        return new OngoingComConjuntos(new Ongoing(kpis, central, acoes, c.Regua is null, alerta), conjuntos);
    }

    public async Task<List<Recomendacao>> RecomendacoesAsync(string slug)
    {
        await using var cx = await db.AbrirAsync();
        return await RecomendacoesAsync(cx, slug);
    }

    /// <summary>O registro do motor é append-only: o estado é a ÚLTIMA decisão (lib/trafego/motor/registro.ts).</summary>
    public static async Task<List<Recomendacao>> RecomendacoesAsync(NpgsqlConnection cx, string slug) =>
        await Db.ListaAsync(cx, """
            select r.id::text, r.criado_em, r.nivel, r.meta_id, r.acao, r.numeros::text as numeros, r.chaves, r.vault_sha, r.contagem_mql, r.texto, r.texto_fonte,
                   d.decisao, d.motivo, d.em, d.resultado_meta::text as resultado_meta, coalesce(u.user_nome, d.por::text) as por,
                   e.nome as conjunto_nome
            from public.trafego_motor_recomendacao r
            left join lateral (select * from public.trafego_motor_decisao x where x.recomendacao_id = r.id order by x.em desc limit 1) d on true
            left join public.users u on u.user_id = d.por
            left join public.trafego_adset_estado e on e.adset_id = r.meta_id
            where r.cliente_slug = @s order by r.criado_em desc limit 60
            """, r =>
            {
                var acao = Enum.TryParse<Acao>(r.S("acao"), out var a) ? a : Acao.ALERTAR;
                double pond = 0, taxa = 0; long? custo = null; double? mult = null; string? nome = null;
                try
                {
                    var n = JsonDocument.Parse(r.Json("numeros") ?? "{}").RootElement;
                    if (n.TryGetProperty("ponderado", out var p) && p.ValueKind == JsonValueKind.Number) pond = p.GetDouble();
                    if (n.TryGetProperty("taxa", out var t) && t.ValueKind == JsonValueKind.Number) taxa = t.GetDouble();
                    if (n.TryGetProperty("custoCentavos", out var cc) && cc.ValueKind == JsonValueKind.Number) custo = cc.GetInt64();
                    if (n.TryGetProperty("multiplo", out var m) && m.ValueKind == JsonValueKind.Number) mult = m.GetDouble();
                    if (n.TryGetProperty("nome", out var nm) && nm.ValueKind == JsonValueKind.String) nome = nm.GetString();
                }
                catch { }
                return new Recomendacao
                {
                    Id = r.S("id")!, ClienteSlug = slug, CriadoEm = r.Dt("criado_em") ?? DateTime.MinValue, Nivel = r.S("nivel") ?? "conjunto",
                    MetaId = r.S("meta_id") ?? "", Nome = nome ?? r.S("conjunto_nome") ?? r.S("meta_id") ?? "", Acao = acao,
                    Decisao = new Decisao(acao, r.Textos("chaves"), [], [], new NumerosDaDecisao(pond, taxa, custo, mult), false, null, false),
                    Texto = r.S("texto") ?? "", TextoFonte = r.S("texto_fonte") ?? "trilha", VaultSha = r.S("vault_sha") ?? "", ContagemMql = r.S("contagem_mql") ?? "regua",
                    Estado = r.S("decisao") switch { "aceita" => EstadoDaRecomendacao.Aceita, "negada" => EstadoDaRecomendacao.Negada, "aplicada" => EstadoDaRecomendacao.Aplicada, "falhou" => EstadoDaRecomendacao.Falhou, "incerto" => EstadoDaRecomendacao.Incerto, _ => EstadoDaRecomendacao.Pendente },
                    DecididaPor = r.S("por"), DecididaEm = r.Dt("em"), Motivo = r.S("motivo"), ResultadoMeta = r.Json("resultado_meta"),
                };
            }, ("s", slug));

    /// <summary>A etapa pelo nome do conjunto, como o Start nomeia ("0 · …" TOFU, "5.1 · …" BOFU, "MOFU" no nome).</summary>
    static Etapa EtapaDoNome(string? nome)
    {
        var n = (nome ?? "").ToUpperInvariant();
        if (n.Contains("TOFU") || n.StartsWith("0 ")) return Etapa.TOFU;
        if (n.Contains("BOFU") || n.StartsWith("5.")) return Etapa.BOFU;
        return Etapa.MOFU;
    }

    static List<Resposta> Respostas(string? json)
    {
        var saida = new List<Resposta>();
        if (string.IsNullOrWhiteSpace(json)) return saida;
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (root.ValueKind != JsonValueKind.Array) return saida;
            foreach (var x in root.EnumerateArray())
            {
                var p = x.TryGetProperty("pergunta", out var pp) ? pp.GetString() : null;
                var v = x.TryGetProperty("resposta", out var rr) ? rr.GetString() : null;
                if (!string.IsNullOrEmpty(p)) saida.Add(new Resposta(p, v ?? ""));
            }
        }
        catch { }
        return saida;
    }
}
