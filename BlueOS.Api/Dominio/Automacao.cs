using System.Globalization;
using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Executor;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O ONGOING E A ENTRADA DO MOTOR: os números do mês, os conjuntos com o estado
   da carga e a memória do motor, o que o agente fez e a central de leads.
   ----------------------------------------------------------------------------
   UMA consulta (um round-trip em qualquer transporte) devolve as linhas cruas:
   a campanha matriculada (trafego_contas.motor_campanha_id), as fotos dos
   conjuntos (trafego_adset_estado, com formato e visto_em), os insights da
   janela por anúncio, a marca de cada rodada da carga (trafego_carga_dia), os
   leads da janela SEM teto de linhas, o registro do motor para a memória
   (recomendação + última decisão + aplicação + foto da cópia), o log e as
   recomendações. Quem decide o que cada linha quer dizer é Core/Motor/Carga
   (conjuntos.ts, memoria.ts, ongoing.ts e avisos.ts do V1), que é puro.
   A rodada do motor (MotorRodada) e a reconferência do aceite (Executor) leem
   os conjuntos DAQUI: a mesma entrada para as duas.
   ============================================================================ */
public sealed record OngoingComConjuntos(Ongoing Ongoing, List<ConjuntoEmVeiculacao> Conjuntos);

public sealed class Automacao(Db db)
{
    /// <summary>As recomendações com o estado (a ÚLTIMA decisão), como subconsulta. `slugExpr` é o literal ou a coluna do
    /// slug. SEM teto de linhas: a pendente mais antiga não pode sumir da tela nem do dedup.</summary>
    public static string RecsSql(string slugExpr) => $"""
        select r.id, r.criado_em, r.nivel, r.meta_id, r.acao, r.numeros, r.chaves, r.vault_sha, r.contagem_mql, r.texto, r.texto_fonte,
               d.decisao, d.motivo, d.em, d.resultado_meta, coalesce(u.user_nome, d.por::text) as por, e.nome as conjunto_nome
        from public.trafego_motor_recomendacao r
        left join lateral (select * from public.trafego_motor_decisao x where x.recomendacao_id = r.id order by x.em desc, x.id desc limit 1) d on true
        left join public.users u on u.user_id = d.por
        left join public.trafego_adset_estado e on e.adset_id = r.meta_id
        where r.cliente_slug = {slugExpr} order by r.criado_em desc, r.id
        """;

    public async Task<OngoingComConjuntos> OngoingAsync(Cliente c)
    {
        var agora = DateTime.UtcNow;
        var hoje = Fuso.DiaDaConta(agora);
        var desdeDia = Carga.DesdeDaLeitura(hoje);
        var desdeUtc = Carga.InicioDoDiaUtc(desdeDia);
        var desdeLog = Carga.InicioDoDiaUtc(hoje.AddDays(-Carga.DiasDaJanela));
        var slugLit = Sql.Lit(c.Slug);

        var linha = await db.UmAsync(Sql.Fmt($"""
            select
              (select t.motor_campanha_id from public.trafego_contas t where t.cliente_slug = {c.Slug} limit 1) as campanha,
              (select json_agg(json_build_object('adset_id', e.adset_id, 'campaign_id', e.campaign_id, 'nome', e.nome, 'status', e.status,
                                                 'orcamento', e.orcamento_diario_centavos, 'formato', e.formato, 'visto_em', e.visto_em))
                 from public.trafego_adset_estado e where e.cliente_slug = {c.Slug})::text as fotos,
              (select json_agg(json_build_object('ad_id', i.ad_id, 'adset_id', i.adset_id, 'campaign_id', i.campaign_id, 'dia', i.dia, 'gasto', i.gasto, 'leads', i.leads))
                 from public.trafego_insight_dia i where i.cliente_slug = {c.Slug} and i.dia >= {desdeDia})::text as insights,
              (select json_agg(json_build_object('dia', m.dia, 'estado', m.estado, 'dias', m.detalhe->'dias', 'dias_ok', m.detalhe->'dias_ok', 'recusas', m.detalhe->'recusas_de_permissao'))
                 from public.trafego_carga_dia m where m.cliente_slug = {c.Slug})::text as marcas,
              (select json_agg(json_build_object('lead_id', l.lead_id, 'adset_id', l.adset_id, 'ad_id', l.ad_id, 'criado_em', l.criado_em, 'respostas', l.respostas))
                 from public.trafego_lead l where l.cliente_slug = {c.Slug} and l.criado_em >= {desdeUtc})::text as leads,
              (select json_agg(json_build_object('id', r.id, 'meta_id', r.meta_id, 'acao', r.acao, 'criado_em', r.criado_em,
                                                 'segurou', coalesce((r.numeros->>'segurou_por_pendentes') = 'true', false),
                                                 'estado', d.decisao, 'decidida_em', d.em, 'copia_id', a.resultado->>'copia_id', 'copia_status', ce.status))
                 from public.trafego_motor_recomendacao r
                 left join lateral (select x.decisao, x.em from public.trafego_motor_decisao x where x.recomendacao_id = r.id order by x.em desc, x.id desc limit 1) d on true
                 left join public.trafego_motor_aplicacao a on a.recomendacao_id = r.id and r.acao = 'DUPLICAR'
                 left join public.trafego_adset_estado ce on ce.adset_id = a.resultado->>'copia_id'
                 where r.cliente_slug = {c.Slug})::text as registro,
              (select json_agg(json_build_object('acao', g.acao, 'alvo_tipo', g.alvo_tipo, 'alvo_nome', g.alvo_nome, 'alvo_id', g.alvo_id, 'ator', g.ator, 'origem', g.origem, 'ocorrido_em', g.ocorrido_em, 'detalhe', g.detalhe) order by g.ocorrido_em desc)
                 from (select * from public.trafego_log_acao where cliente_slug = {c.Slug} and ocorrido_em >= {desdeLog} order by ocorrido_em desc limit 40) g)::text as log,
              (select json_agg(r order by r.criado_em desc) from (
            """) + RecsSql(slugLit) + ") r)::text as recomendacoes", r => new
            {
                Campanha = r.S("campanha"), Fotos = r.Json("fotos"), Insights = r.Json("insights"), Marcas = r.Json("marcas"), Leads = r.Json("leads"),
                Registro = r.Json("registro"), Log = r.Json("log"), Recs = r.Json("recomendacoes"),
            }) ?? throw new InvalidOperationException("a consulta do ongoing não voltou");

        var dados = new DadosDaCarga(
            agora, linha.Campanha, c.Regua,
            c.BudgetMensal is > 0 && Dinheiro.CentavosDe(c.BudgetMensal.Value) is var b && b >= 1 ? b : null,
            Dinheiro.TetoEmCentavos(c.TetoMql),
            Arr(linha.Insights).Select(i => new LinhaDeInsight(Str(i, "ad_id"), Str(i, "adset_id"), Str(i, "campaign_id"), Dia(Str(i, "dia")) ?? hoje, Dec(i, "gasto"), (long)Dec(i, "leads"))).ToList(),
            Arr(linha.Leads).Select(l => new LinhaDeLead(Str(l, "lead_id") ?? "", Str(l, "adset_id"), Str(l, "ad_id"), Utc(Str(l, "criado_em")),
                Respostas(l.TryGetProperty("respostas", out var rs) ? rs : default))).ToList(),
            Arr(linha.Fotos).Select(f => new FotoDoConjunto(Str(f, "adset_id") ?? "", Str(f, "campaign_id"), Str(f, "nome"), Str(f, "status"),
                f.TryGetProperty("orcamento", out var o) && o.ValueKind == JsonValueKind.Number ? (long)o.GetDecimal() : null, Str(f, "formato"), Utc(Str(f, "visto_em")))).ToList(),
            Arr(linha.Marcas).Select(m => new MarcaDaCarga(Dia(Str(m, "dia")) ?? hoje, Str(m, "estado"), Dias(m, "dias"), Dias(m, "dias_ok"), Dec(m, "recusas"))).ToList(),
            Arr(linha.Registro).Select(x => new RecomendacaoNaMemoria(Str(x, "id") ?? "", Str(x, "meta_id") ?? "", Str(x, "acao") ?? "", Str(x, "estado") ?? "pendente",
                Utc(Str(x, "criado_em")) ?? DateTime.MinValue, Utc(Str(x, "decidida_em")), x.TryGetProperty("segurou", out var s) && s.ValueKind == JsonValueKind.True,
                Str(x, "copia_id"), Str(x, "copia_status"))).ToList());
        var carga = Carga.Ler(dados, ParametrosMotor.PadraoDoVault);

        var log = new List<AcaoDoAgente>();
        foreach (var g in Arr(linha.Log))
        {
            string? via = null;
            if (g.TryGetProperty("detalhe", out var det) && det.ValueKind == JsonValueKind.Object && det.TryGetProperty("via", out var v) && v.ValueKind == JsonValueKind.String) via = v.GetString();
            var alvo = Str(g, "alvo_nome") ?? Str(g, "alvo_id");
            log.Add(new AcaoDoAgente(
                Utc(Str(g, "ocorrido_em")) is { } q ? Fuso.NaConta(q) : DateTime.MinValue,
                "log", $"{(Str(g, "acao") ?? "").Replace('_', ' ')}{(Str(g, "alvo_tipo") is { } t ? $" · {t}" : "")}{(alvo is not null ? $" \"{alvo}\"" : "")}",
                Str(g, "origem") == "meta_activity" ? "lido da Meta" : via is not null ? $"pelo Blue.OS ({via})" : "pelo Blue.OS", Str(g, "ator"), null));
        }

        var recs = string.IsNullOrWhiteSpace(linha.Recs) ? new() : ParseRecs(JsonDocument.Parse(linha.Recs).RootElement, c.Slug);
        var acoes = recs.Where(r => r.Estado != EstadoDaRecomendacao.Pendente)
            .Select(r => new AcaoDoAgente(r.DecididaEm ?? r.CriadoEm, "motor", $"{Arvore.RotuloDaAcao(r.Acao).Rotulo.ToLowerInvariant()} em \"{r.Nome}\"", r.Estado.ToString().ToLowerInvariant(), r.DecididaPor, r.Motivo))
            .Concat(log).OrderByDescending(a => a.Quando).Take(40).ToList();

        return new OngoingComConjuntos(new Ongoing(carga.Kpis, carga.Central, acoes, c.Regua is null, carga.AlertaBudget), carga.Conjuntos);
    }

    public async Task<List<Recomendacao>> RecomendacoesAsync(string slug)
    {
        var linha = await db.UmAsync("select (select json_agg(r order by r.criado_em desc) from (" + RecsSql(Sql.Lit(slug)) + ") r)::text as recomendacoes", r => new { Recs = r.Json("recomendacoes") });
        return linha is null || string.IsNullOrWhiteSpace(linha.Recs) ? new() : ParseRecs(JsonDocument.Parse(linha.Recs).RootElement, slug);
    }

    /// <summary>As chaves (meta_id::acao) das recomendações PENDENTES do cliente, por consulta direta e sem teto: é o
    /// dedup da rodada (registro.ts).</summary>
    public async Task<HashSet<string>> PendentesAsync(string slug) =>
        (await db.ListaAsync(Sql.Fmt($"""
            select r.meta_id, r.acao from public.trafego_motor_recomendacao r
            where r.cliente_slug = {slug} and not exists (select 1 from public.trafego_motor_decisao d where d.recomendacao_id = r.id)
            """), r => Rodada.ChaveDeDedup(r.S("meta_id") ?? "", r.S("acao") ?? ""))).ToHashSet();

    /// <summary>O registro do motor é append-only: o estado é a ÚLTIMA decisão (a regra mora em Core/Motor/Rodada).</summary>
    public static List<Recomendacao> ParseRecs(JsonElement arr, string slug) => Rodada.ParseRecs(arr, slug);

    static IEnumerable<JsonElement> Arr(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var root = JsonDocument.Parse(json).RootElement;
        return root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [];
    }
    static string? Str(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.GetRawText() : null;
    static decimal Dec(JsonElement o, string n)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(n, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
    static DateTime? Utc(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    static DateOnly? Dia(string? s) => s is { Length: >= 10 } && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    static List<DateOnly> Dias(JsonElement o, string n) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Dia(x.GetString())).OfType<DateOnly>().ToList()
            : [];

    /// <summary>As respostas gravadas ({pergunta, resposta, texto?}); `texto` é o da opção (#5004).</summary>
    static List<Resposta> Respostas(JsonElement root)
    {
        var saida = new List<Resposta>();
        if (root.ValueKind == JsonValueKind.String) { try { root = JsonDocument.Parse(root.GetString() ?? "[]").RootElement; } catch { return saida; } }
        if (root.ValueKind != JsonValueKind.Array) return saida;
        foreach (var x in root.EnumerateArray())
        {
            var p = Str(x, "pergunta"); var v = Str(x, "resposta");
            var t = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("texto", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() : null;
            if (!string.IsNullOrEmpty(p)) saida.Add(new Resposta(p, v ?? "", t));
        }
        return saida;
    }
}
