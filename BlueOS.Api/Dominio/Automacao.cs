using System.Globalization;
using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O ONGOING: os números do mês, os conjuntos, o que o agente fez e a central de
   leads (porta de lib/trafego/motor/ongoing.ts), com a MESMA régua do motor.
   ----------------------------------------------------------------------------
   UMA consulta devolve cinco colunas JSON (conjuntos, insights, leads, log e
   recomendações); a contagem de MQL é a régua v2 em C# (Core.Motor.Regua).
   ============================================================================ */
public sealed record OngoingComConjuntos(Ongoing Ongoing, List<ConjuntoEmVeiculacao> Conjuntos);

public sealed class Automacao(Db db)
{
    /// <summary>As recomendações com o estado (a ÚLTIMA decisão), como subconsulta. `slugExpr` é o literal ou a coluna do slug.</summary>
    public static string RecsSql(string slugExpr) => $"""
        select r.id, r.criado_em, r.nivel, r.meta_id, r.acao, r.numeros, r.chaves, r.vault_sha, r.contagem_mql, r.texto, r.texto_fonte,
               d.decisao, d.motivo, d.em, d.resultado_meta, coalesce(u.user_nome, d.por::text) as por, e.nome as conjunto_nome
        from public.trafego_motor_recomendacao r
        left join lateral (select * from public.trafego_motor_decisao x where x.recomendacao_id = r.id order by x.em desc limit 1) d on true
        left join public.users u on u.user_id = d.por
        left join public.trafego_adset_estado e on e.adset_id = r.meta_id
        where r.cliente_slug = {slugExpr} order by r.criado_em desc limit 60
        """;

    public async Task<OngoingComConjuntos> OngoingAsync(Cliente c)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Now);
        var inicioDoMes = new DateOnly(hoje.Year, hoje.Month, 1);
        var desde60 = hoje.AddDays(-60);
        var desdeLeads = inicioDoMes.AddDays(-30).ToDateTime(TimeOnly.MinValue);
        var desde30 = hoje.AddDays(-30).ToDateTime(TimeOnly.MinValue);
        var slugLit = Sql.Lit(c.Slug);

        var linha = await db.UmAsync(Sql.Fmt($"""
            select
              (select json_agg(json_build_object('adset_id', e.adset_id, 'nome', e.nome, 'status', e.status, 'orcamento', e.orcamento_diario_centavos) order by e.nome)
                 from public.trafego_adset_estado e where e.cliente_slug = {c.Slug})::text as conjuntos,
              (select json_agg(json_build_object('adset_id', i.adset_id, 'dia', i.dia, 'gasto', i.gasto, 'leads', i.leads))
                 from (select adset_id, dia, sum(gasto) as gasto, sum(leads) as leads from public.trafego_insight_dia
                       where cliente_slug = {c.Slug} and dia >= {desde60} group by adset_id, dia) i)::text as insights,
              (select json_agg(json_build_object('lead_id', l.lead_id, 'adset_id', l.adset_id, 'ad_id', l.ad_id, 'criado_em', l.criado_em, 'respostas', l.respostas) order by l.criado_em desc)
                 from (select * from public.trafego_lead where cliente_slug = {c.Slug} and criado_em >= {desdeLeads} order by criado_em desc limit 400) l)::text as leads,
              (select json_agg(json_build_object('acao', g.acao, 'alvo_tipo', g.alvo_tipo, 'alvo_nome', g.alvo_nome, 'alvo_id', g.alvo_id, 'ator', g.ator, 'origem', g.origem, 'ocorrido_em', g.ocorrido_em, 'detalhe', g.detalhe) order by g.ocorrido_em desc)
                 from (select * from public.trafego_log_acao where cliente_slug = {c.Slug} and ocorrido_em >= {desde30} order by ocorrido_em desc limit 40) g)::text as log,
              (select json_agg(r order by r.criado_em desc) from (
            """) + RecsSql(slugLit) + ") r)::text as recomendacoes", r => new
            {
                Conjuntos = r.Json("conjuntos"), Insights = r.Json("insights"), Leads = r.Json("leads"), Log = r.Json("log"), Recs = r.Json("recomendacoes"),
            }) ?? throw new InvalidOperationException("a consulta do ongoing não voltou");

        var conjuntos = new List<ConjuntoEmVeiculacao>();
        var porAdset = new Dictionary<string, ConjuntoEmVeiculacao>();
        foreach (var e in Arr(linha.Conjuntos))
        {
            var cj = new ConjuntoEmVeiculacao
            {
                AdsetId = Str(e, "adset_id") ?? "", Nome = Str(e, "nome") ?? Str(e, "adset_id") ?? "", Etapa = EtapaDoNome(Str(e, "nome")),
                DiarioCentavos = Num(e, "orcamento"), Ativo = Str(e, "status") is null ? null : Str(e, "status") == "ACTIVE", Formato = Formato.Desconhecido,
            };
            conjuntos.Add(cj); porAdset[cj.AdsetId] = cj;
        }
        var insights = new List<(string? Adset, DateOnly Dia, long Gasto, int Leads)>();
        foreach (var i in Arr(linha.Insights))
        {
            var dia = DateOnly.TryParse((Str(i, "dia") ?? "")[..Math.Min(10, (Str(i, "dia") ?? "").Length)], out var d) ? d : hoje;
            var gasto = (long)Math.Round(Dec(i, "gasto") * 100);
            var item = (Str(i, "adset_id"), dia, gasto, (int)Num(i, "leads"));
            insights.Add(item);
            var adset = item.Item1 ?? "sem-conjunto";
            if (!porAdset.TryGetValue(adset, out var cj))
            {
                cj = new ConjuntoEmVeiculacao { AdsetId = adset, Nome = item.Item1 ?? "sem conjunto", Etapa = Etapa.MOFU, Formato = Formato.Desconhecido };
                porAdset[adset] = cj; conjuntos.Add(cj);
            }
            cj.Dias.Add(new DiaDeInsight(dia, gasto, item.Item4));
        }
        foreach (var cj in conjuntos) cj.PrimeiraEntrega = cj.Dias.Where(d => d.GastoCentavos > 0).Select(d => (DateOnly?)d.Dia).Min();

        var leads = new List<LeadDaCentral>();
        foreach (var l in Arr(linha.Leads))
        {
            var lead = new LeadDaCentral
            {
                Id = Str(l, "lead_id") ?? "", AdsetId = Str(l, "adset_id"), AdId = Str(l, "ad_id"),
                CriadoEm = DateTime.TryParse(Str(l, "criado_em"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt.ToLocalTime() : DateTime.MinValue,
                Respostas = Respostas(l.TryGetProperty("respostas", out var rs) ? rs : default),
            };
            if (lead.AdsetId is not null && porAdset.TryGetValue(lead.AdsetId, out var cj)) { cj.Leads.Add(lead); lead.Conjunto = cj.Nome; }
            if (c.Regua is not null)
            {
                var r = Regua.AvaliarLead(Regua.RespostasDoLead(lead.Respostas.Select(x => (x.Pergunta, x.Valor))), c.Regua);
                lead.Situacao = r.Situacao; lead.Tier = r.Tier; lead.Motivo = r.Motivo; lead.NaoMapeadas = r.NaoMapeadas.ToList(); lead.Faltantes = r.Faltantes.ToList();
            }
            leads.Add(lead);
        }

        var log = new List<AcaoDoAgente>();
        foreach (var g in Arr(linha.Log))
        {
            string? via = null;
            if (g.TryGetProperty("detalhe", out var det) && det.ValueKind == JsonValueKind.Object && det.TryGetProperty("via", out var v) && v.ValueKind == JsonValueKind.String) via = v.GetString();
            var alvo = Str(g, "alvo_nome") ?? Str(g, "alvo_id");
            log.Add(new AcaoDoAgente(
                DateTime.TryParse(Str(g, "ocorrido_em"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var q) ? q.ToLocalTime() : DateTime.MinValue,
                "log", $"{(Str(g, "acao") ?? "").Replace('_', ' ')}{(Str(g, "alvo_tipo") is { } t ? $" · {t}" : "")}{(alvo is not null ? $" \"{alvo}\"" : "")}",
                Str(g, "origem") == "meta_activity" ? "lido da Meta" : via is not null ? $"pelo Blue.OS ({via})" : "pelo Blue.OS", Str(g, "ator"), null));
        }

        var recs = string.IsNullOrWhiteSpace(linha.Recs) ? new() : ParseRecs(JsonDocument.Parse(linha.Recs).RootElement, c.Slug);
        var acoes = recs.Where(r => r.Estado != EstadoDaRecomendacao.Pendente)
            .Select(r => new AcaoDoAgente(r.DecididaEm ?? r.CriadoEm, "motor", $"{Arvore.RotuloDaAcao(r.Acao).Rotulo.ToLowerInvariant()} em \"{r.Nome}\"", r.Estado.ToString().ToLowerInvariant(), r.DecididaPor, r.Motivo))
            .Concat(log).OrderByDescending(a => a.Quando).Take(40).ToList();

        /* ---- os KPIs do mês, como ongoing.ts ---------------------------------- */
        var doMes = insights.Where(i => i.Dia >= inicioDoMes).ToList();
        var gastoMes = doMes.Sum(i => i.Gasto);
        var diasLidos = doMes.Select(i => i.Dia).Distinct().Count();
        var leadsDoMes = leads.Where(l => DateOnly.FromDateTime(l.CriadoEm) >= inicioDoMes).ToList();
        var contagem = c.Regua is null ? new ContagemPelaRegua(0, 0, 0, 0, 0, 0)
            : Regua.Contar(leadsDoMes.Select(l => (IReadOnlyDictionary<string, List<string>>)Regua.RespostasDoLead(l.Respostas.Select(x => (x.Pergunta, x.Valor)))), c.Regua);
        long? teto = c.TetoMql is > 0 ? Dinheiro.CentavosDe(c.TetoMql.Value) : null;
        long? budget = c.BudgetMensal is > 0 ? Dinheiro.CentavosDe(c.BudgetMensal.Value) : null;
        var adsetsComMql = leadsDoMes.Where(l => l.Situacao == Situacao.Mql).Select(l => l.AdsetId).ToHashSet();
        var gastoSemMql = doMes.Where(i => !adsetsComMql.Contains(i.Adset)).Sum(i => i.Gasto);
        var kpis = new KpisDoMes($"{hoje.Year:0000}-{hoje.Month:00}", gastoMes, budget, Math.Max(0, hoje.Day - 1), diasLidos, leadsDoMes.Count,
            contagem.Avaliados, contagem.Pendentes, Math.Max(0, contagem.Avaliados - contagem.Mql), contagem.Mql, contagem.MqlA, contagem.MqlB, contagem.MqlC,
            budget is not null && teto is not null ? (int)(budget / teto) : null, contagem.Mql > 0 ? gastoMes / contagem.Mql : null, teto, gastoSemMql);
        var alerta = budget is not null ? Arvore.AlertaBudget(gastoSemMql, budget.Value, ParametrosMotor.PadraoDoVault) : null;
        var central = leads.Where(l => DateOnly.FromDateTime(l.CriadoEm) >= hoje.AddDays(-30)).Take(150).ToList();
        return new OngoingComConjuntos(new Ongoing(kpis, central, acoes, c.Regua is null, alerta), conjuntos);
    }

    public async Task<List<Recomendacao>> RecomendacoesAsync(string slug)
    {
        var linha = await db.UmAsync("select (select json_agg(r order by r.criado_em desc) from (" + RecsSql(Sql.Lit(slug)) + ") r)::text as recomendacoes", r => new { Recs = r.Json("recomendacoes") });
        return linha is null || string.IsNullOrWhiteSpace(linha.Recs) ? new() : ParseRecs(JsonDocument.Parse(linha.Recs).RootElement, slug);
    }

    /// <summary>O registro do motor é append-only: o estado é a ÚLTIMA decisão (lib/trafego/motor/registro.ts).</summary>
    public static List<Recomendacao> ParseRecs(JsonElement arr, string slug)
    {
        var saida = new List<Recomendacao>();
        if (arr.ValueKind != JsonValueKind.Array) return saida;
        foreach (var r in arr.EnumerateArray())
        {
            var acao = Enum.TryParse<Acao>(Str(r, "acao"), out var a) ? a : Acao.ALERTAR;
            double pond = 0, taxa = 0; long? custo = null; double? mult = null; string? nome = null;
            if (r.TryGetProperty("numeros", out var n) && n.ValueKind == JsonValueKind.Object)
            {
                if (n.TryGetProperty("ponderado", out var p) && p.ValueKind == JsonValueKind.Number) pond = p.GetDouble();
                if (n.TryGetProperty("taxa", out var t) && t.ValueKind == JsonValueKind.Number) taxa = t.GetDouble();
                /* o V1 grava custoCentavos como número livre (às vezes com fração): arredonda em vez de quebrar */
                if (n.TryGetProperty("custoCentavos", out var cc) && cc.ValueKind == JsonValueKind.Number) custo = cc.TryGetInt64(out var ci) ? ci : (long)Math.Round(cc.GetDouble());
                if (n.TryGetProperty("multiplo", out var m) && m.ValueKind == JsonValueKind.Number) mult = m.GetDouble();
                if (n.TryGetProperty("nome", out var nm) && nm.ValueKind == JsonValueKind.String) nome = nm.GetString();
            }
            var chaves = r.TryGetProperty("chaves", out var ch) && ch.ValueKind == JsonValueKind.Array ? ch.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();
            saida.Add(new Recomendacao
            {
                Id = Str(r, "id") ?? "", ClienteSlug = slug, CriadoEm = Data(r, "criado_em"), Nivel = Str(r, "nivel") ?? "conjunto",
                MetaId = Str(r, "meta_id") ?? "", Nome = nome ?? Str(r, "conjunto_nome") ?? Str(r, "meta_id") ?? "", Acao = acao,
                Decisao = new Decisao(acao, chaves, [], [], new NumerosDaDecisao(pond, taxa, custo, mult), false, null, false),
                Texto = Str(r, "texto") ?? "", TextoFonte = Str(r, "texto_fonte") ?? "trilha", VaultSha = Str(r, "vault_sha") ?? "", ContagemMql = Str(r, "contagem_mql") ?? "regua",
                Estado = Str(r, "decisao") switch { "aceita" => EstadoDaRecomendacao.Aceita, "negada" => EstadoDaRecomendacao.Negada, "aplicada" => EstadoDaRecomendacao.Aplicada, "falhou" => EstadoDaRecomendacao.Falhou, "incerto" => EstadoDaRecomendacao.Incerto, _ => EstadoDaRecomendacao.Pendente },
                DecididaPor = Str(r, "por"), DecididaEm = r.TryGetProperty("em", out var em) && em.ValueKind == JsonValueKind.String ? Data(r, "em") : null, Motivo = Str(r, "motivo"),
                ResultadoMeta = r.TryGetProperty("resultado_meta", out var rm) && rm.ValueKind != JsonValueKind.Null ? rm.GetRawText() : null,
            });
        }
        return saida;
    }

    static IEnumerable<JsonElement> Arr(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var root = JsonDocument.Parse(json).RootElement;
        return root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [];
    }
    static string? Str(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.GetRawText() : null;
    static long Num(JsonElement o, string n) => (long)Dec(o, n);
    static decimal Dec(JsonElement o, string n)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(n, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
    static DateTime Data(JsonElement o, string n) =>
        DateTime.TryParse(Str(o, n), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d.ToLocalTime() : DateTime.MinValue;

    /// <summary>A etapa pelo nome do conjunto, como o Start nomeia ("0 · …" TOFU, "5.1 · …" BOFU, "MOFU" no nome).</summary>
    static Etapa EtapaDoNome(string? nome)
    {
        var n = (nome ?? "").ToUpperInvariant();
        if (n.Contains("TOFU") || n.StartsWith("0 ")) return Etapa.TOFU;
        if (n.Contains("BOFU") || n.StartsWith("5.")) return Etapa.BOFU;
        return Etapa.MOFU;
    }

    static List<Resposta> Respostas(JsonElement root)
    {
        var saida = new List<Resposta>();
        if (root.ValueKind == JsonValueKind.String) { try { root = JsonDocument.Parse(root.GetString() ?? "[]").RootElement; } catch { return saida; } }
        if (root.ValueKind != JsonValueKind.Array) return saida;
        foreach (var x in root.EnumerateArray())
        {
            var p = Str(x, "pergunta"); var v = Str(x, "resposta");
            if (!string.IsNullOrEmpty(p)) saida.Add(new Resposta(p, v ?? ""));
        }
        return saida;
    }
}
