using System.Globalization;
using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Cs;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Panorama;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O PANORAMA NA API: o mapa estelar do squad numa leitura só.
   ----------------------------------------------------------------------------
   1. a carteira do escopo (Carteira.ListarAsync) com o token do squad conferido;
   2. UMA consulta com o que o mapa lê além do fluxo, por cliente: KPIs do mês
      (insights e leads, com a régua do cliente), conjuntos ativos e verba,
      automação pausada, a última análise do CS, a última mensagem do grupo e
      a última ação do agente;
   3. as regras puras do Core (Diagnostico, Montador) decidem estrela e anel;
   4. o visto, os pontos, o "a caminho" e o último alerta de cada cliente vivem
      em tabelas próprias (trafego_panorama_*), criadas na primeira leitura.
   Pontuar: um alerta que sumiu entre duas leituras acendeu a estrela; os pontos
   vão para quem tinha ação "a caminho" naquele cliente; sem ação humana,
   ninguém pontua (só a saúde do squad sobe). Nada aqui escreve na Meta.
   ============================================================================ */
public sealed class PanoramaApi(Db db, Carteira carteira, Meta meta, ILogger<PanoramaApi> log)
{
    static volatile bool _tabelas;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record VistoPedido(string ClienteSlug, string Assinatura, bool Visto);
    public sealed record ACaminhoPedido(string ClienteSlug, string Acao);

    async Task GarantirTabelasAsync()
    {
        if (_tabelas) return;
        await db.ExecutarLoteAsync(
        [
            """
            create table if not exists public.trafego_panorama_visto (
              cliente_slug text not null, visto_por uuid not null, assinatura text not null, visto_em timestamptz not null default now(),
              primary key (cliente_slug, visto_por))
            """,
            """
            create table if not exists public.trafego_panorama_alerta (
              cliente_slug text primary key, assinatura text not null, gravidade int not null, titulo text, desde timestamptz, atualizado_em timestamptz not null default now())
            """,
            """
            create table if not exists public.trafego_panorama_a_caminho (
              id bigserial primary key, cliente_slug text not null, por uuid not null, acao text not null, pontos int not null, em timestamptz not null default now())
            """,
            """
            create table if not exists public.trafego_panorama_acendeu (
              id bigserial primary key, cliente_slug text not null, cliente_nome text not null, gravidade int not null, pontos int not null, por uuid, em timestamptz not null default now())
            """,
            "create index if not exists trafego_panorama_acendeu_em on public.trafego_panorama_acendeu (em desc)",
            "create index if not exists trafego_panorama_a_caminho_slug on public.trafego_panorama_a_caminho (cliente_slug, em desc)",
        ]);
        _tabelas = true;
    }

    public async Task<PanoramaDoSquad> LerAsync(EscopoDaSessao esc, string userId, string? squad)
    {
        await GarantirTabelasAsync();
        var agora = DateTime.Now;
        var p = ParametrosMotor.PadraoDoVault;
        var lista = await carteira.ListarAsync(esc);
        try { await meta.MarcarTokensAsync(lista); } catch (Exception e) { log.LogWarning("tokens dos squads não conferidos: {Motivo}", e.Message); }
        await PreencherSinaisAsync(lista, agora, p);

        var uid = Guid.Parse(userId);
        var vistos = await db.ListaAsync(Sql.Fmt($"select cliente_slug, assinatura, visto_em from public.trafego_panorama_visto where visto_por = {uid} and visto_em >= {agora.Date}"),
            r => new Visto(r.S("cliente_slug")!, r.S("assinatura")!, r.Dt("visto_em") ?? agora, userId));
        var sinaisDeVida = SinaisDeVida(lista, agora);
        var vazio = new Placar("", 0, 0, 0, 0, 0, [], [], [], 0, 0, 0);
        var pan = Montador.Montar(lista, squad, p, agora, vistos, sinaisDeVida, vazio);
        await PontuarAsync(pan, agora);
        var placar = await PlacarAsync(uid, pan, agora);
        return pan with { Placar = placar };
    }

    /* ---- o que o mapa lê além do fluxo, numa consulta ------------------------------ */
    async Task PreencherSinaisAsync(List<Cliente> lista, DateTime agora, ParametrosMotor p)
    {
        if (lista.Count == 0) return;
        var slugs = lista.Select(c => c.Slug).ToArray();
        var inicioDoMes = new DateOnly(agora.Year, agora.Month, 1);
        var sql = Sql.Fmt($"""
            with s as (select unnest({slugs}) as slug)
            select s.slug,
                   (select json_build_object('gasto', coalesce(sum(i.gasto), 0), 'leads', coalesce(sum(i.leads), 0), 'dias', count(distinct i.dia), 'ultimo', max(i.dia))
                      from public.trafego_insight_dia i where i.cliente_slug = s.slug and i.dia >= {inicioDoMes})::text as mes,
                   (select json_agg(json_build_object('adset_id', g.adset_id, 'gasto', g.gasto))
                      from (select adset_id, sum(gasto) as gasto from public.trafego_insight_dia where cliente_slug = s.slug and dia >= {inicioDoMes} group by adset_id) g)::text as por_adset,
                   (select json_agg(json_build_object('adset_id', l.adset_id, 'respostas', l.respostas))
                      from public.trafego_lead l where l.cliente_slug = s.slug and l.criado_em >= {inicioDoMes.ToDateTime(TimeOnly.MinValue)})::text as leads,
                   (select json_build_object('ativos', count(*) filter (where e.status = 'ACTIVE'), 'verba', coalesce(sum(e.orcamento_diario_centavos) filter (where e.status = 'ACTIVE'), 0))
                      from public.trafego_adset_estado e where e.cliente_slug = s.slug)::text as conjuntos,
                   tc.motor_automacao, tc.motor_automacao_motivo,
                   (select json_build_object('valor', a.valor_gerado, 'risco', a.risco_churn, 'label', a.risco_churn_label, 'sub', a.sub_scores, 'tend', a.tendencia, 'sinais', a.sinais_churn)
                      from public.cs_analise a join public.clientes c on c.cliente_slug = s.slug where a.cliente_id = c.cliente_id order by a.gerada_em desc limit 1)::text as cs,
                   (select max(m.enviada_em) from public.clientes c join public.wa_grupo g on g.cliente_id = c.cliente_id and g.vinculo in ('auto','manual')
                      join public.wa_mensagem m on m.grupo_id = g.grupo_id where c.cliente_slug = s.slug) as ultima_msg,
                   (select max(x.ocorrido_em) from public.trafego_log_acao x where x.cliente_slug = s.slug and x.origem = 'sistema') as agente_em
            from s left join public.trafego_contas tc on tc.cliente_slug = s.slug
            """);
        var porSlug = lista.ToDictionary(c => c.Slug);
        var linhas = await db.ListaAsync(sql);
        foreach (var r in linhas)
        {
            if (!porSlug.TryGetValue(r.S("slug") ?? "", out var c)) continue;
            var s = c.Sinais;
            s.LidoEm = agora;
            s.AutomacaoEstado = r.S("motor_automacao");
            s.AutomacaoMotivo = r.S("motor_automacao_motivo");
            s.AgenteRodouEm = r.Dt("agente_em");
            var frio = CsRegras.GrupoFrio(r.Dt("ultima_msg"), DateTime.UtcNow);
            s.DiasSemMensagem = r.Dt("ultima_msg") is null ? null : frio.Dias;

            if (Obj(r.Json("conjuntos")) is { } cj)
            {
                s.ConjuntosAtivos = (int)Num(cj, "ativos");
                s.VerbaDiariaCentavos = Num(cj, "verba");
            }
            if (Obj(r.Json("cs")) is { } cs)
            {
                var (nota, faixa) = CsRegras.NotaDaAnalise(Int(cs, "valor"), Int(cs, "risco"), Str(cs, "label"), Raw(cs, "sub"));
                s.NotaCs = nota; s.FaixaCs = faixa; s.RiscoChurn = Str(cs, "label");
                if (cs.TryGetProperty("tend", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("d7", out var d7) && d7.ValueKind == JsonValueKind.Number) s.TendenciaD7 = (int)Math.Round(d7.GetDouble());
                if (cs.TryGetProperty("sinais", out var sn) && sn.ValueKind == JsonValueKind.Array)
                    s.SinaisDeChurn = sn.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : Str(x, "texto") ?? Str(x, "sinal")).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Take(4).ToList();
            }

            /* KPIs do mês com a régua do cliente (a mesma conta do ongoing, sem os 60 dias de histórico) */
            if (Obj(r.Json("mes")) is { } mes)
            {
                var gasto = Num(mes, "gasto");
                var leadsTotal = (int)Num(mes, "leads");
                var dias = (int)Num(mes, "dias");
                s.MetaLidaEm = DateTime.TryParse(Str(mes, "ultimo"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ult) ? ult.AddHours(7.5) : null;
                var leads = new List<(string? Adset, Dictionary<string, List<string>> Respostas)>();
                foreach (var l in Arr(r.Json("leads")))
                    leads.Add((Str(l, "adset_id"), Regua.RespostasDoLead(Respostas(l.TryGetProperty("respostas", out var rs) ? rs : default).Select(x => (x.Pergunta, x.Valor)))));
                var contagem = c.Regua is null ? new ContagemPelaRegua(0, 0, 0, 0, 0, 0) : Regua.Contar(leads.Select(l => (IReadOnlyDictionary<string, List<string>>)l.Respostas), c.Regua);
                var comMql = new HashSet<string>();
                if (c.Regua is not null)
                    foreach (var l in leads) if (l.Adset is not null && Regua.AvaliarLead(l.Respostas, c.Regua).Situacao == Situacao.Mql) comMql.Add(l.Adset);
                long gastoSemMql = 0;
                foreach (var g in Arr(r.Json("por_adset"))) if (!comMql.Contains(Str(g, "adset_id") ?? "")) gastoSemMql += Num(g, "gasto");
                long? teto = c.TetoMql is > 0 ? Dinheiro.CentavosDe(c.TetoMql.Value) : null;
                long? budget = c.BudgetMensal is > 0 ? Dinheiro.CentavosDe(c.BudgetMensal.Value) : null;
                var nLeads = Math.Max(leadsTotal, leads.Count);
                s.Kpis = new KpisDoMes($"{agora:yyyy-MM}", gasto, budget, DateTime.DaysInMonth(agora.Year, agora.Month), dias,
                    nLeads, contagem.Avaliados, contagem.Pendentes, Math.Max(0, contagem.Avaliados - contagem.Mql),
                    contagem.Mql, contagem.MqlA, contagem.MqlB, contagem.MqlC,
                    budget is not null && teto is not null && teto > 0 ? (int)(budget / teto) : null,
                    contagem.Mql > 0 ? gasto / contagem.Mql : null, teto, gastoSemMql);
            }
        }
    }

    static List<SinalDeVida> SinaisDeVida(List<Cliente> lista, DateTime agora)
    {
        var meta = lista.Select(c => c.Sinais.MetaLidaEm).Where(d => d is not null).Max();
        var agente = lista.Select(c => c.Sinais.AgenteRodouEm).Where(d => d is not null).Max();
        var token = lista.Where(c => c.Conta.TokenConferidoEm is not null).ToList();
        var vencido = lista.Any(c => c.Conta.TokenValido == false);
        var tokenEm = token.Select(c => c.Conta.TokenConferidoEm).Max();
        var pendentes = lista.Count(c => c.Publicacao is { Estado: "criando" or "parcial" });
        return
        [
            new("Meta (gasto, leads)", meta is null ? "sem carga este mês" : $"carga de {meta:dd/MM}", meta is null || (agora - meta.Value).TotalHours > 36),
            new("token do squad", tokenEm is null ? "não conferido" : $"conferido {tokenEm:HH:mm}{(vencido ? " · vencido" : "")}", vencido),
            new("agente de tráfego", agente is null ? "não rodou" : "rodou " + Diagnostico.Relativo(agente, agora), agente is not null && (agora - agente.Value).TotalHours > 26),
            new("grupos do CS", lista.Any(c => c.Sinais.DiasSemMensagem is not null) ? "lidos com a carteira" : "sem grupos", false),
            new("publicações", pendentes == 0 ? "nenhuma pendente" : $"{pendentes} em andamento", false),
        ];
    }

    /* ---- pontos: o alerta que sumiu acendeu a estrela ------------------------------- */
    async Task PontuarAsync(PanoramaDoSquad pan, DateTime agora)
    {
        var slugs = pan.Estrelas.Select(e => e.Slug).ToArray();
        if (slugs.Length == 0) return;
        var antes = await db.ListaAsync(Sql.Fmt($"select cliente_slug, assinatura, gravidade from public.trafego_panorama_alerta where cliente_slug = any({slugs})"),
            r => (Slug: r.S("cliente_slug")!, Assinatura: r.S("assinatura")!, Gravidade: (int)r.L("gravidade")));
        var porSlug = antes.ToDictionary(a => a.Slug);
        var sqls = new List<string>();
        foreach (var e in pan.Estrelas)
        {
            if (e.Principal is { } a)
            {
                if (!porSlug.TryGetValue(e.Slug, out var x) || x.Assinatura != a.Assinatura)
                    sqls.Add(Sql.Fmt($"""
                        insert into public.trafego_panorama_alerta (cliente_slug, assinatura, gravidade, titulo, desde, atualizado_em)
                        values ({e.Slug}, {a.Assinatura}, {(int)a.Gravidade}, {a.Titulo}, {a.Desde}, now())
                        on conflict (cliente_slug) do update set assinatura = excluded.assinatura, gravidade = excluded.gravidade, titulo = excluded.titulo, desde = excluded.desde, atualizado_em = now()
                        """));
            }
            else if (porSlug.TryGetValue(e.Slug, out var x))
            {
                var pts = Gravidades.Pontos((Gravidade)x.Gravidade);
                sqls.Add(Sql.Fmt($"""
                    insert into public.trafego_panorama_acendeu (cliente_slug, cliente_nome, gravidade, pontos, por, em)
                    select {e.Slug}, {e.Nome}, {x.Gravidade}, {pts}, (select por from public.trafego_panorama_a_caminho where cliente_slug = {e.Slug} order by em desc limit 1), now()
                    """));
                sqls.Add(Sql.Fmt($"delete from public.trafego_panorama_a_caminho where cliente_slug = {e.Slug}"));
                sqls.Add(Sql.Fmt($"delete from public.trafego_panorama_alerta where cliente_slug = {e.Slug}"));
            }
        }
        if (sqls.Count > 0) await db.ExecutarLoteAsync(sqls);
    }

    async Task<Placar> PlacarAsync(Guid uid, PanoramaDoSquad pan, DateTime agora)
    {
        var inicioDoMes = new DateTime(agora.Year, agora.Month, 1);
        var gestores = await db.ListaAsync(Sql.Fmt($"""
            select a.por::text as por, coalesce(u.user_nome, 'sem nome') as nome, sum(a.pontos) as pontos
            from public.trafego_panorama_acendeu a left join public.users u on u.user_id = a.por
            where a.por is not null and a.em >= {inicioDoMes} group by a.por, u.user_nome order by pontos desc limit 50
            """), r => (Por: r.S("por")!, Nome: r.S("nome")!, Pontos: (int)r.L("pontos")));
        var eu = await db.UmAsync(Sql.Fmt($"select user_nome from public.users where user_id = {uid}"), r => r.S("user_nome") ?? "você") ?? "você";
        var meus = gestores.FirstOrDefault(g => g.Por == uid.ToString()).Pontos;
        var posicao = gestores.FindIndex(g => g.Por == uid.ToString()) + 1;
        var ranking = gestores.Take(4).Select(g => new LinhaDoRanking(PrimeiroNome(g.Nome), g.Pontos, g.Por == uid.ToString())).ToList();
        if (posicao > 4) ranking.Add(new(PrimeiroNome(eu), meus, true));
        var acendeu = await db.ListaAsync(Sql.Fmt($"""
            select a.cliente_nome, a.pontos, a.em, coalesce(u.user_nome, 'ninguém') as por from public.trafego_panorama_acendeu a left join public.users u on u.user_id = a.por
            where a.em >= {agora.Date.AddDays(-1)} order by a.em desc limit 6
            """), r => new Acendeu(r.Dt("em") ?? agora, r.S("cliente_nome")!, (int)r.L("pontos"), PrimeiroNome(r.S("por") ?? "ninguém")));
        var aCaminho = await db.ListaAsync(Sql.Fmt($"""
            select distinct on (c.cliente_slug) c.cliente_slug, c.acao, c.pontos, c.em, coalesce(k.cliente_nome, c.cliente_slug) as nome
            from public.trafego_panorama_a_caminho c left join public.clientes k on k.cliente_slug = c.cliente_slug
            where c.por = {uid} and c.em >= {agora.AddDays(-7)} order by c.cliente_slug, c.em desc
            """), r => new Acendeu(r.Dt("em") ?? agora, r.S("nome")!, (int)r.L("pontos"), r.S("acao") ?? ""));
        var semVermelho = pan.Estrelas.Any(e => e.Principal?.Gravidade == Gravidade.AutomacaoParou) ? 0
            : (int)Math.Floor((agora - (await db.UmAsync(Sql.Fmt($"select max(em) as em from public.trafego_panorama_acendeu where gravidade = 1"), r => (object?)r.Dt("em")) as DateTime? ?? agora.Date)).TotalDays);
        var squads = new List<LinhaDoRanking> { new(pan.Squad, pan.Resumo.SaudePct, true) };
        return new Placar(PrimeiroNome(eu), meus, posicao == 0 ? gestores.Count + 1 : posicao, Math.Max(gestores.Count, 1), posicao == 0 ? gestores.Count + 1 : posicao, Math.Max(gestores.Count, 1),
            ranking, squads, acendeu.OrderByDescending(a => a.Quando).ToList(), semVermelho, semVermelho, 0, aCaminho.OrderByDescending(a => a.Quando).ToList());
    }

    public async Task MarcarVistoAsync(string userId, VistoPedido p)
    {
        await GarantirTabelasAsync();
        var uid = Guid.Parse(userId);
        var sqls = new List<string> { Sql.Fmt($"delete from public.trafego_panorama_visto where cliente_slug = {p.ClienteSlug} and visto_por = {uid}") };
        if (p.Visto) sqls.Add(Sql.Fmt($"insert into public.trafego_panorama_visto (cliente_slug, visto_por, assinatura, visto_em) values ({p.ClienteSlug}, {uid}, {p.Assinatura}, now())"));
        await db.ExecutarLoteAsync(sqls);
    }

    /// <summary>O gestor agiu no Start: os pontos da gravidade atual ficam "a caminho" até a estrela acender.</summary>
    public async Task RegistrarACaminhoAsync(string userId, ACaminhoPedido p)
    {
        await GarantirTabelasAsync();
        var uid = Guid.Parse(userId);
        var grav = await db.UmAsync(Sql.Fmt($"select gravidade from public.trafego_panorama_alerta where cliente_slug = {p.ClienteSlug}"), r => (object)(int)r.L("gravidade"));
        var pontos = grav is int g && g >= 1 && g <= 6 ? Gravidades.Pontos((Gravidade)g) : 10;
        await db.ExecutarAsync(Sql.Fmt($"insert into public.trafego_panorama_a_caminho (cliente_slug, por, acao, pontos, em) values ({p.ClienteSlug}, {uid}, {p.Acao}, {pontos}, now())"));
    }

    /* ---- json ------------------------------------------------------------------------ */
    static string PrimeiroNome(string nome) => nome.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? nome;
    static JsonElement? Obj(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { var e = JsonDocument.Parse(json).RootElement.Clone(); return e.ValueKind == JsonValueKind.Object ? e : null; } catch { return null; } }
    static IEnumerable<JsonElement> Arr(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement.Clone(); } catch { yield break; }
        if (root.ValueKind != JsonValueKind.Array) yield break;
        foreach (var x in root.EnumerateArray()) yield return x;
    }
    static string? Str(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.GetRawText() : null;
    static string? Raw(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetRawText() : null;
    static long Num(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) ? v switch
    {
        { ValueKind: JsonValueKind.Number } => v.TryGetInt64(out var i) ? i : (long)Math.Round(v.GetDouble()),
        { ValueKind: JsonValueKind.String } => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)Math.Round(d) : 0,
        _ => 0,
    } : 0;
    static int? Int(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : null;
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
