using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueOS.Api.Infra;
using BlueOS.Core.Cs;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O AGENTE DE CS NA API — a carteira, o dossiê, o alerta de churn e o que o
   time registra sobre o cliente. Porte de app/api/cs/* do V1, sem telas.
   ----------------------------------------------------------------------------
   A tela do gestor nasce desta base, então o que manda aqui é o tempo de
   resposta: a carteira inteira sai em DUAS consultas (squads + uma só com
   clientes, última análise, contratos e última mensagem de WhatsApp), onde o
   V1 fazia dezenas de idas ao PostgREST em lotes de 100/300. As regras (nota,
   faixa, alcance, dinheiro por squad, grupo frio, decisões) vivem em
   BlueOS.Core.Cs.CsRegras, puras e testadas; aqui é só SQL e montagem.

   ESCOPO: a lista, o dossiê e o alerta usam o vínculo EFETIVO (vigente; sem
   vigente, o encerrado responde — a mesma régua de `Escopo.FiltroDoAlcance`).
   As escritas usam vínculo por QUALQUER squad, e respondem 404 (nunca 403)
   quando o cliente está fora do alcance: 403 confirmaria que ele existe.
   ============================================================================ */
public sealed class Cs(Db db, Escopo escopo, Ia ia, ILogger<Cs> log)
{
    static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);
    public sealed record Erro(int Http, string Mensagem);
    const string NaoEncontrado = "nao encontrado";

    static int? I(ILinha r, string c) => r.Dec(c) is { } d ? (int)d : null;
    static JsonElement? J(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); } catch (JsonException) { return null; }
    }
    static bool Uuid(string? v) => Guid.TryParse((v ?? "").Trim(), out _);

    /* O squad PRINCIPAL vigente do cliente e os vínculos efetivos, como fragmentos de SQL. */
    static string PrincipalDe(string clienteExpr) =>
        $"(select cs.squad_id from public.cliente_squads cs where cs.cliente_id = {clienteExpr} and cs.cliente_squad_tipo = 'principal' and cs.cliente_squad_ate is null order by cs.cliente_squad_desde desc limit 1)";
    static string VinculosEfetivos(string clienteExpr) =>
        $"(select cs.squad_id from public.cliente_squads cs where cs.cliente_id = {clienteExpr} and (cs.cliente_squad_ate is null or not exists (select 1 from public.cliente_squads o where o.cliente_id = cs.cliente_id and o.cliente_squad_ate is null)))";
    /* lib/os/interno.ts: cliente cujos vínculos efetivos são TODOS de squad interno (holding) não entra em carteira nem alerta, nem para o escopo global. */
    static string ForaDosInternos(string clienteExpr) =>
        $" and not (exists (select 1 from public.cliente_squads x where x.cliente_id = {clienteExpr}) and not exists (select 1 from {VinculosEfetivos(clienteExpr)} v join public.squads s on s.squad_id = v.squad_id where not coalesce(s.squad_interno, false)))";

    /* ── /me: quem é a pessoa, o que ela alcança e o que pode fazer no CS ─────── */
    public async Task<object?> MeAsync(string userId)
    {
        var uid = Guid.Parse(userId);
        var perfil = await db.UmAsync(Sql.Fmt($"""
            select u.user_nome, u.user_username, u.user_email,
                   (select json_agg(json_build_object('role_nome', r.role_nome, 'role_slug', r.role_slug, 'escopo', r.role_escopo, 'squad', s.squad_nome, 'departamento', d.departamento_nome))
                      from public.membros m join public.roles r on r.role_id = m.role_id
                      left join public.squads s on s.squad_id = m.squad_id left join public.departamentos d on d.departamento_id = m.departamento_id
                     where m.user_id = u.user_id and m.membro_ate is null)::text as papeis
            from public.users u where u.user_id = {uid}
            """), r => new { Nome = r.S("user_nome"), Username = r.S("user_username"), Email = r.S("user_email"), Papeis = J(r.S("papeis")) });
        if (perfil is null) return null;
        var esc = await escopo.DoUsuarioAsync(userId);
        var squads = await db.ListaAsync("select s.squad_id::text as squad_id, s.squad_nome, coalesce(s.squad_interno, false) as interno, t.tribo_nome from public.squads s left join public.tribos t on t.tribo_id = s.tribo_id",
            r => new { Id = r.S("squad_id")!, Nome = r.S("squad_nome") ?? "", Interno = r.B("interno") ?? false, Tribo = r.S("tribo_nome") });
        var nomeDe = squads.ToDictionary(s => s.Id, s => s.Nome, StringComparer.OrdinalIgnoreCase);
        var escopoSquads = esc.SquadIds?.Select(id => nomeDe.GetValueOrDefault(id)).Where(n => n is not null).OrderBy(n => n, StringComparer.Create(new CultureInfo("pt-BR"), true)).ToList();
        return new
        {
            user_id = userId, nome = perfil.Nome ?? perfil.Email, username = perfil.Username ?? "", email = perfil.Email,
            papeis = perfil.Papeis ?? JsonSerializer.SerializeToElement(Array.Empty<object>()),
            escopo_squads = escopoSquads,
            escopo_global = esc.Global,
            tribo_por_squad = squads.ToDictionary(s => s.Nome, s => s.Tribo),
            cx_squad = squads.FirstOrDefault(s => s.Interno)?.Nome,
            pode_administrar = CsRegras.PodeAdministrar(esc.Papeis),
            pode_setup_cs = CsRegras.PodeAbrirSetupCs(esc.Papeis),
            pode_sem_performance = CsRegras.PodeMudarSemPerformance(esc.Papeis),
            pode_criar_cliente = CsRegras.PodeCriarCliente(esc.Papeis),
            pode_aplicar_na_meta = esc.PodeAplicarNaMeta,
            tier = CsRegras.Tier(esc.Papeis),
        };
    }

    /* ── A CARTEIRA (GET /api/cs/carteira) ─────────────────────────────────────
       Os cinco status da aba Clientes (churn e pausado mostram a última análise
       antes da virada), fora do squad interno, no alcance da sessão, com recorte
       opcional por squad (id ou nome). A última análise é a do squad principal
       (vw_cs_analise_recente) ou, com recorte, a daquele squad. */
    public async Task<object> CarteiraAsync(EscopoDaSessao esc, string? squadPedido, string? grupo)
    {
        if (!esc.Global && (esc.SquadIds is null || esc.SquadIds.Count == 0)) return new { clientes = Array.Empty<object>(), grupo_frio_indisponivel = false };
        var squads = await SquadsOperacionaisAsync();
        var pedido = CsRegras.ResolverSquadPedido(squadPedido, squads.Select(s => (s.Id, s.Nome)));
        var foco = CsRegras.PontoDeVista(esc.SquadIds, pedido);
        var recorte = CsRegras.RecorteDeSquad(esc.SquadIds, pedido);
        var nomeSquad = squads.ToDictionary(s => s.Id, s => s.Nome, StringComparer.OrdinalIgnoreCase);

        var recente = recorte is null
            ? "select r.cliente_id, r.gerada_em, null::uuid as squad_id from public.vw_cs_analise_recente r where r.cliente_id = v.cliente_id"
            : Sql.Fmt($"select r.cliente_id, r.gerada_em, r.squad_id from public.vw_cs_analise_recente_squad r where r.cliente_id = v.cliente_id and r.squad_id = {Guid.Parse(recorte)}");
        var filtroRecorte = recorte is null ? "" : Sql.Fmt($" and exists (select 1 from {new Raw(VinculosEfetivos("c.cliente_id"))} v where v.squad_id = {Guid.Parse(recorte)})");
        var sql = Sql.Fmt($"""
            with vis as (
              select c.cliente_id, c.cliente_nome, c.cliente_slug, c.cliente_status_atual::text as status, c.cliente_desde, c.cliente_criado_em,
                     {new Raw(PrincipalDe("c.cliente_id"))} as principal
              from public.clientes c
              where c.cliente_arquivado_em is null and c.cliente_status_atual::text = any({CsRegras.StatusDaCarteira})
            """) + ForaDosInternos("c.cliente_id") + Escopo.FiltroDoAlcance(esc) + filtroRecorte + $"""

            )
            select v.cliente_id::text as cliente_id, v.cliente_nome, v.cliente_slug, v.status, v.cliente_desde, v.cliente_criado_em, v.principal::text as principal,
                   a.gerada_em, a.valor_gerado, a.valor_tipo, a.valor_sumario, a.risco_churn, a.risco_churn_label, a.tendencia::text as tendencia,
                   a.etapa_atual, a.tempo_blue_dias, a.upsell_timing, a.upsell_slug, a.sub_scores::text as sub_scores,
                   (select json_agg(json_build_object('squad_id', ct.squad_id, 'modalidade', ct.modalidade, 'mrr', ct.mrr, 'valor_total', ct.valor_total))
                      from public.cliente_contrato ct where ct.cliente_id = v.cliente_id)::text as contratos,
                   (select max(t.ultima) from (select (select m.enviada_em from public.wa_mensagem m where m.grupo_id = g.grupo_id order by m.enviada_em desc limit 1) as ultima
                                                 from public.wa_grupo g where g.cliente_id = v.cliente_id and g.vinculo in ('auto','manual')) t) as ultima_msg
            from vis v
            left join lateral (
              select a.* from ({recente}) r join public.cs_analise a on a.cliente_id = r.cliente_id and a.gerada_em = r.gerada_em and (r.squad_id is null or a.squad_id = r.squad_id)
              order by a.gerada_em desc limit 1
            ) a on true
            order by v.cliente_nome
            """;
        var agora = DateTime.UtcNow;
        var soFrios = grupo == "frio";
        var linhas = await db.ListaAsync(sql, r =>
        {
            var temAnalise = r.Dt("gerada_em") is not null;
            var (nota, faixa) = temAnalise ? CsRegras.NotaDaAnalise(I(r, "valor_gerado"), I(r, "risco_churn"), r.S("risco_churn_label"), r.S("sub_scores")) : (null, "sem_analise");
            var contratos = LerContratos(r.S("contratos"));
            var leitura = CsRegras.LeituraDoSquad(contratos, foco, r.S("principal"));
            var tcv = leitura.Contrato?.Modalidade == "tcv";
            var tend = J(r.S("tendencia"));
            var frio = CsRegras.GrupoFrio(r.Dt("ultima_msg"), agora);
            return new
            {
                id = r.S("cliente_id"), nome = r.S("cliente_nome"), slug = r.S("cliente_slug"), status = r.S("status"),
                mrr = leitura.Situacao == "proprio" && !tcv ? leitura.Contrato!.Mrr : null,
                modalidade = leitura.Situacao == "proprio" ? leitura.Contrato!.Modalidade : null,
                valor_contrato = leitura.Situacao == "proprio" && tcv ? leitura.Contrato!.ValorTotal : null,
                mrr_incluso_em = leitura.Situacao == "incluso" && leitura.NoSquadId is not null ? nomeSquad.GetValueOrDefault(leitura.NoSquadId) : null,
                tempo_blue_dias = I(r, "tempo_blue_dias"),
                desde = r.Data("cliente_desde") ?? (r.Dt("cliente_criado_em") is { } cr ? DateOnly.FromDateTime(cr) : null),
                nota, faixa, faixa_apelido = CsRegras.ApelidoDaFaixa(faixa),
                valor_gerado = I(r, "valor_gerado"), valor_tipo = r.S("valor_tipo"), sumario = r.S("valor_sumario"),
                risco_churn_label = r.S("risco_churn_label"),
                tendencia_d7 = tend is { ValueKind: JsonValueKind.Object } t && t.TryGetProperty("d7", out var d7) && d7.ValueKind == JsonValueKind.Number ? d7.GetDouble() : (double?)null,
                etapa = r.S("etapa_atual"), upsell_timing = r.S("upsell_timing"), upsell_slug = r.S("upsell_slug"),
                gerada_em = r.Dt("gerada_em"),
                grupo_frio = frio.Frio, grupo_frio_dias = frio.Dias,
            };
        });
        var clientes = soFrios ? linhas.Where(c => c.grupo_frio).ToList() : linhas;
        return new
        {
            clientes,
            total = linhas.Count,
            grupo_frio_indisponivel = false,
            squad = recorte is null ? null : new { squad_id = recorte, nome = nomeSquad.GetValueOrDefault(recorte) },
            foco = foco is null ? null : new { squad_id = foco, nome = nomeSquad.GetValueOrDefault(foco) },
            filtro_grupo = soFrios ? "frio" : null,
            leitura_do_filtro = soFrios ? CsRegras.LeituraDoFiltroFrio : null,
        };
    }

    static List<CsRegras.Contrato> LerContratos(string? json)
    {
        var lista = new List<CsRegras.Contrato>();
        if (J(json) is not { ValueKind: JsonValueKind.Array } arr) return lista;
        foreach (var c in arr.EnumerateArray())
            lista.Add(new(
                c.TryGetProperty("squad_id", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                c.TryGetProperty("modalidade", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                c.TryGetProperty("mrr", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null,
                c.TryGetProperty("valor_total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDecimal() : null));
        return lista;
    }

    sealed record SquadLinha(string Id, string Nome);
    Task<List<SquadLinha>> SquadsOperacionaisAsync() => db.ListaAsync(
        "select s.squad_id::text as squad_id, s.squad_nome from public.squads s where not coalesce(s.squad_interno, false) and s.squad_arquivado_em is null order by s.squad_nome",
        r => new SquadLinha(r.S("squad_id")!, r.S("squad_nome") ?? ""));

    /* ── O DOSSIÊ (GET /api/cs/carteira?cliente=&squad=) ───────────────────────
       A análise COMPLETA mais recente do cliente no squad escolhido (pedido, se
       ele atende o cliente e está no alcance; senão o principal), as 30 últimas
       rodadas desse squad como histórico de nota e as abas de squad com o
       estado do botão Sem Performance. Duas consultas. */
    sealed record VinculoLinha(string SquadId, string Nome, bool Principal, bool Vigente, bool SemPerf, DateTime? SemPerfEm, string? SemPerfPor, string? SemPerfPorNome, bool TemAnalise);

    public async Task<(object? Corpo, Erro? Erro)> DossieAsync(EscopoDaSessao esc, string clienteId, string? squadPedido)
    {
        if (!Uuid(clienteId)) return (null, new(400, "informe cliente (uuid)"));
        var cid = Guid.Parse(clienteId);
        var vinculos = await db.ListaAsync(Sql.Fmt($"""
            select cs.squad_id::text as squad_id, s.squad_nome, cs.cliente_squad_tipo::text as tipo, cs.cliente_squad_ate,
                   coalesce(cs.cs_sem_performance, false) as sem_perf, cs.cs_sem_performance_em, cs.cs_sem_performance_por::text as por, u.user_nome as por_nome,
                   exists (select 1 from public.cs_analise a where a.cliente_id = cs.cliente_id and a.squad_id = cs.squad_id) as tem_analise
            from public.cliente_squads cs join public.squads s on s.squad_id = cs.squad_id
            left join public.users u on u.user_id = cs.cs_sem_performance_por
            where cs.cliente_id = {cid}
            order by (cs.cliente_squad_tipo = 'principal') desc, cs.cliente_squad_desde desc
            """), r => new VinculoLinha(r.S("squad_id")!, r.S("squad_nome") ?? "?", r.S("tipo") == "principal", r.Data("cliente_squad_ate") is null,
                r.B("sem_perf") ?? false, r.Dt("cs_sem_performance_em"), r.S("por"), r.S("por_nome"), r.B("tem_analise") ?? false));
        var vigentes = vinculos.Where(v => v.Vigente).ToList();
        var efetivos = (vigentes.Count > 0 ? vigentes : vinculos).Select(v => v.SquadId).ToList();
        if (!CsRegras.ClienteNoAlcance(esc.SquadIds, efetivos)) return (null, new(403, "cliente fora do seu alcance"));

        bool Alcanca(string s) => esc.Global || (esc.SquadIds?.Contains(s, StringComparer.OrdinalIgnoreCase) ?? false);
        var pedido = CsRegras.ResolverSquadPedido(squadPedido, vinculos.Select(v => (v.SquadId, v.Nome)));
        var principal = vigentes.FirstOrDefault(v => v.Principal) ?? vigentes.FirstOrDefault();
        var squadDaAnalise = pedido is not null && vigentes.Any(v => v.SquadId.Equals(pedido, StringComparison.OrdinalIgnoreCase)) && Alcanca(pedido)
            ? vigentes.First(v => v.SquadId.Equals(pedido, StringComparison.OrdinalIgnoreCase))
            : principal;

        object Botao(VinculoLinha v)
        {
            var fixo = CsRegras.SquadSemPerformanceFixo(v.Nome);
            return new
            {
                sem_performance = fixo || v.SemPerf, sem_performance_fixo = fixo,
                sem_performance_em = fixo ? null : v.SemPerfEm, sem_performance_por = fixo ? null : v.SemPerfPor, sem_performance_por_nome = fixo ? null : v.SemPerfPorNome,
            };
        }
        var abas = vigentes.Where(v => Alcanca(v.SquadId)).OrderByDescending(v => v.Principal)
            .Select(v => new { squad_id = v.SquadId, nome = v.Nome, principal = v.Principal, tem_analise = v.TemAnalise, botao = Botao(v) }).ToList();
        var squadInfo = squadDaAnalise is null ? null : new { squad_id = squadDaAnalise.SquadId, nome = squadDaAnalise.Nome, botao = Botao(squadDaAnalise) };

        var filtroSquad = squadDaAnalise is null ? " and a.squad_id is null" : Sql.Fmt($" and a.squad_id = {Guid.Parse(squadDaAnalise.SquadId)}");
        var analises = await db.ListaAsync(Sql.Fmt($"""
            select a.analise_id::text as analise_id, a.cliente_id::text as cliente_id, a.squad_id::text as squad_id, a.gerada_em, a.modelo, a.fonte,
                   a.valor_gerado, a.valor_tipo, a.valor_sumario, a.valor_evidencias::text as valor_evidencias, a.risco_churn, a.risco_churn_label,
                   a.sub_scores::text as sub_scores, a.tendencia::text as tendencia, a.sinais_churn::text as sinais_churn,
                   a.padroes_positivos::text as padroes_positivos, a.padroes_negativos::text as padroes_negativos,
                   a.proximos_passos::text as proximos_passos, a.proximos_passos_decisao::text as proximos_passos_decisao,
                   a.diagnostico::text as diagnostico, a.alinhamento_comercial::text as alinhamento_comercial, a.alinhamento_status_decisao::text as alinhamento_status_decisao,
                   a.upsells::text as upsells, a.upsell_slug, a.upsell_justificativa, a.upsell_timing, a.etapa_atual, a.tempo_blue_dias, a.contexto_resumo,
                   a.tokens_in, a.tokens_out, a.custo_usd, a.criado_em
            from public.cs_analise a where a.cliente_id = {cid}
            """) + filtroSquad + " order by a.gerada_em desc limit 30", r => r);
        if (analises.Count == 0)
            return (new { error = squadDaAnalise is not null ? $"o {squadDaAnalise.Nome} ainda nao tem analise deste cliente" : "cliente sem analise", squad = squadInfo, squads = abas }, new(404, "sem analise"));

        var a = analises[0];
        var (nota, faixa) = CsRegras.NotaDaAnalise(I(a, "valor_gerado"), I(a, "risco_churn"), a.S("risco_churn_label"), a.S("sub_scores"));
        var historico = analises.AsEnumerable().Reverse()
            .Select(h => new { gerada_em = h.Dt("gerada_em"), nota = CsRegras.NotaDaAnalise(I(h, "valor_gerado"), I(h, "risco_churn"), h.S("risco_churn_label"), h.S("sub_scores")).Nota }).ToList();
        var analise = new Dictionary<string, object?>
        {
            ["analise_id"] = a.S("analise_id"), ["cliente_id"] = a.S("cliente_id"), ["squad_id"] = a.S("squad_id"), ["gerada_em"] = a.Dt("gerada_em"),
            ["modelo"] = a.S("modelo"), ["fonte"] = a.S("fonte"), ["valor_gerado"] = I(a, "valor_gerado"), ["valor_tipo"] = a.S("valor_tipo"), ["valor_sumario"] = a.S("valor_sumario"),
            ["valor_evidencias"] = J(a.S("valor_evidencias")), ["risco_churn"] = I(a, "risco_churn"), ["risco_churn_label"] = a.S("risco_churn_label"),
            ["sub_scores"] = CsRegras.LerSubScores(a.S("sub_scores")), ["tendencia"] = J(a.S("tendencia")), ["sinais_churn"] = J(a.S("sinais_churn")),
            ["padroes_positivos"] = J(a.S("padroes_positivos")), ["padroes_negativos"] = J(a.S("padroes_negativos")),
            ["proximos_passos"] = J(a.S("proximos_passos")), ["proximos_passos_decisao"] = J(a.S("proximos_passos_decisao")),
            ["diagnostico"] = J(a.S("diagnostico")), ["alinhamento_comercial"] = J(a.S("alinhamento_comercial")), ["alinhamento_status_decisao"] = J(a.S("alinhamento_status_decisao")),
            ["upsells"] = J(a.S("upsells")), ["upsell_slug"] = a.S("upsell_slug"), ["upsell_justificativa"] = a.S("upsell_justificativa"), ["upsell_timing"] = a.S("upsell_timing"),
            ["etapa_atual"] = a.S("etapa_atual"), ["tempo_blue_dias"] = I(a, "tempo_blue_dias"), ["contexto_resumo"] = a.S("contexto_resumo"),
            ["tokens_in"] = I(a, "tokens_in"), ["tokens_out"] = I(a, "tokens_out"), ["custo_usd"] = a.Dec("custo_usd"), ["criado_em"] = a.Dt("criado_em"),
            ["nota"] = nota, ["faixa"] = faixa, ["faixa_apelido"] = CsRegras.ApelidoDaFaixa(faixa),
        };
        return (new { analise, historico, squad = squadInfo, squads = abas }, null);
    }

    /* ── O ALERTA DE CHURN (GET /api/cs/risco?todos=1) ─────────────────────────
       Clientes ativos/onboarding/aviso prévio do escopo cuja última análise do
       principal marcou risco alto ou crítico, com nota (sem base não alerta). A
       resposta diz quantos são; `alertas` traz os 8 piores, ou todos. */
    public async Task<object> RiscoAsync(EscopoDaSessao esc, bool todos)
    {
        if (!esc.Global && (esc.SquadIds is null || esc.SquadIds.Count == 0)) return new { alertas = Array.Empty<object>(), total_em_risco = 0, mostrando = 0 };
        var sql = Sql.Fmt($"""
            select c.cliente_id::text as cliente_id, c.cliente_nome, a.gerada_em, a.valor_gerado, a.valor_sumario, a.risco_churn, a.risco_churn_label, a.sub_scores::text as sub_scores
            from public.clientes c
            join public.vw_cs_analise_recente r on r.cliente_id = c.cliente_id
            join lateral (select a.* from public.cs_analise a where a.cliente_id = r.cliente_id and a.gerada_em = r.gerada_em order by a.gerada_em desc limit 1) a on true
            where c.cliente_arquivado_em is null and c.cliente_status_atual::text = any({CsRegras.StatusDoRisco})
              and a.risco_churn_label in ('alto','critico')
            """) + ForaDosInternos("c.cliente_id") + Escopo.FiltroDoAlcance(esc);
        var elegiveis = new List<CsRegras.Alerta>();
        foreach (var r in await db.ListaAsync(sql))
        {
            var (nota, faixa) = CsRegras.NotaDaAnalise(I(r, "valor_gerado"), I(r, "risco_churn"), r.S("risco_churn_label"), r.S("sub_scores"));
            if (nota is null) continue;
            var sumario = r.S("valor_sumario") ?? "";
            elegiveis.Add(new(r.S("cliente_id")!, r.S("cliente_nome") ?? "?", r.S("risco_churn_label"), nota.Value, faixa, CsRegras.ApelidoDaFaixa(faixa), r.Dt("gerada_em"), sumario[..Math.Min(sumario.Length, 180)]));
        }
        var ordenados = CsRegras.OrdenarAlertas(elegiveis);
        var mostrando = todos ? ordenados : ordenados.Take(CsRegras.AlertasNaRespostaPadrao).ToList();
        return new
        {
            alertas = mostrando.Select(a => new { cliente_id = a.ClienteId, nome = a.Nome, risco = a.Risco, nota = a.Nota, faixa = a.Faixa, faixa_apelido = a.FaixaApelido, gerada_em = a.GeradaEm, sumario = a.Sumario }),
            total_em_risco = ordenados.Count, mostrando = mostrando.Count,
            corte_declarado = ordenados.Count > mostrando.Count ? CsRegras.CorteDeclarado(mostrando.Count, ordenados.Count) : null,
        };
    }

    /* ── ESCOPO DAS ESCRITAS: o cliente existe e a sessão o alcança por QUALQUER vínculo? 404 nos dois nãos. */
    async Task<Erro?> ClienteNoAlcanceAsync(EscopoDaSessao esc, string? clienteId)
    {
        if (!Uuid(clienteId)) return new(400, "informe cliente_id");
        var l = await db.UmAsync(Sql.Fmt($"select (select array_agg(cs.squad_id::text) from public.cliente_squads cs where cs.cliente_id = c.cliente_id) as squads from public.clientes c where c.cliente_id = {Guid.Parse(clienteId!)}"),
            r => new { Squads = r.Textos("squads") });
        if (l is null || !CsRegras.ClienteNoAlcance(esc.SquadIds, l.Squads)) return new(404, NaoEncontrado);
        return null;
    }

    static string NomeDe(Guid userId) => Sql.Fmt($"(select u.user_nome from public.users u where u.user_id = {userId})");

    /* ── ANOTAÇÃO DO TIME (cs_anotacao): lista as visíveis, grava, arquiva (nunca apaga). */
    const string CamposAnotacao = "a.anotacao_id::text as anotacao_id, a.cliente_id::text as cliente_id, a.texto, a.autor_user_id::text as autor_user_id, a.autor_nome, a.criado_em";
    static object LerAnotacao(ILinha r) => new { anotacao_id = r.S("anotacao_id"), cliente_id = r.S("cliente_id"), texto = r.S("texto"), autor_user_id = r.S("autor_user_id"), autor_nome = r.S("autor_nome"), criado_em = r.Dt("criado_em") };

    public async Task<(object? Corpo, Erro? Erro)> AnotacoesAsync(EscopoDaSessao esc, string? clienteId)
    {
        if (await ClienteNoAlcanceAsync(esc, clienteId) is { } e) return (null, e);
        var lista = await db.ListaAsync(Sql.Fmt($"select {new Raw(CamposAnotacao)} from public.cs_anotacao a where a.cliente_id = {Guid.Parse(clienteId!)} and a.arquivada_em is null order by a.criado_em desc limit 200"), LerAnotacao);
        return (new { anotacoes = lista }, null);
    }

    public async Task<(object? Corpo, Erro? Erro)> AnotarAsync(EscopoDaSessao esc, string? clienteId, string? texto)
    {
        if (await ClienteNoAlcanceAsync(esc, clienteId) is { } e) return (null, e);
        var (t, recusa) = CsRegras.ValidarTexto(texto);
        if (recusa is not null) return (null, new(recusa.Http, recusa.Erro));
        var uid = Guid.Parse(esc.UserId);
        var nova = await db.ListaAsync(Sql.Fmt($"""
            with a as (insert into public.cs_anotacao (cliente_id, texto, autor_user_id, autor_nome) values ({Guid.Parse(clienteId!)}, {t}, {uid}, {new Raw(NomeDe(uid))}) returning *)
            select {new Raw(CamposAnotacao)} from a
            """), LerAnotacao);
        return (new { ok = true, anotacao = nova.FirstOrDefault() }, null);
    }

    public async Task<(object? Corpo, Erro? Erro)> ArquivarAnotacaoAsync(EscopoDaSessao esc, string? anotacaoId)
    {
        if (!Uuid(anotacaoId)) return (null, new(400, "informe anotacao_id"));
        var alvo = await db.UmAsync(Sql.Fmt($"select a.cliente_id::text as cliente_id, a.arquivada_em from public.cs_anotacao a where a.anotacao_id = {Guid.Parse(anotacaoId!)}"),
            r => new { ClienteId = r.S("cliente_id"), ArquivadaEm = r.Dt("arquivada_em") });
        if (alvo is null) return (null, new(404, NaoEncontrado));
        if (await ClienteNoAlcanceAsync(esc, alvo.ClienteId) is { } e) return (null, e);
        if (alvo.ArquivadaEm is not null) return (new { ok = true, ja_arquivada = true }, null);
        await db.ExecutarAsync(Sql.Fmt($"update public.cs_anotacao set arquivada_em = now(), arquivada_por = {Guid.Parse(esc.UserId)} where anotacao_id = {Guid.Parse(anotacaoId!)}"));
        return (new { ok = true, anotacao_id = anotacaoId }, null);
    }

    /* ── UPSELL VENDIDO (cs_upsell_vendido): só registra; não mexe em MRR, contrato nem nota. */
    const string CamposVenda = "v.venda_id::text as venda_id, v.cliente_id::text as cliente_id, v.upsell_id::text as upsell_id, v.upsell_slug, v.upsell_nome, v.valor, v.vendido_em, v.observacao, v.registrado_por::text as registrado_por, v.registrado_por_nome, v.criado_em";
    static object LerVenda(ILinha r) => new
    {
        venda_id = r.S("venda_id"), cliente_id = r.S("cliente_id"), upsell_id = r.S("upsell_id"), upsell_slug = r.S("upsell_slug"), upsell_nome = r.S("upsell_nome"),
        valor = r.Dec("valor"), vendido_em = r.Data("vendido_em"), observacao = r.S("observacao"), registrado_por = r.S("registrado_por"), registrado_por_nome = r.S("registrado_por_nome"), criado_em = r.Dt("criado_em"),
    };

    public async Task<(object? Corpo, Erro? Erro)> UpsellsVendidosAsync(EscopoDaSessao esc, string? clienteId)
    {
        if (await ClienteNoAlcanceAsync(esc, clienteId) is { } e) return (null, e);
        var vendidos = await db.ListaAsync(Sql.Fmt($"select {new Raw(CamposVenda)} from public.cs_upsell_vendido v where v.cliente_id = {Guid.Parse(clienteId!)} and v.arquivado_em is null order by v.vendido_em desc limit 200"), LerVenda);
        var catalogo = await db.ListaAsync("select upsell_id::text as upsell_id, slug, nome, ticket from public.cs_upsell_catalogo where ativo order by ordem",
            r => new { upsell_id = r.S("upsell_id"), slug = r.S("slug"), nome = r.S("nome"), ticket = r.S("ticket") });
        return (new { vendidos, catalogo }, null);
    }

    public async Task<(object? Corpo, Erro? Erro)> RegistrarUpsellAsync(EscopoDaSessao esc, string? clienteId, string? slug, string? nome, JsonElement? valor, string? vendidoEm, string? observacao)
    {
        if (!Uuid(clienteId)) return (null, new(400, "informe cliente_id"));
        var (p, recusa) = CsRegras.ValidarPedidoUpsell(slug, nome, valor, vendidoEm, observacao);
        if (recusa is not null) return (null, new(recusa.Http, recusa.Erro));
        if (await ClienteNoAlcanceAsync(esc, clienteId) is { } e) return (null, e);
        Guid? upsellId = null; var nomeFinal = p!.Nome;
        if (p.Slug is not null)
        {
            var doCatalogo = await db.UmAsync(Sql.Fmt($"select upsell_id::text as upsell_id, nome from public.cs_upsell_catalogo where slug = {p.Slug} limit 1"), r => new { Id = r.S("upsell_id"), Nome = r.S("nome") });
            if (doCatalogo?.Id is not null) { upsellId = Guid.Parse(doCatalogo.Id); nomeFinal = string.IsNullOrWhiteSpace(doCatalogo.Nome) ? nomeFinal : doCatalogo.Nome; }
        }
        var uid = Guid.Parse(esc.UserId);
        var colData = p.VendidoEm is null ? "" : ", vendido_em";
        var valData = p.VendidoEm is null ? "" : ", " + Sql.Lit(p.VendidoEm.Value);
        var nova = await db.ListaAsync(Sql.Fmt($"""
            with v as (insert into public.cs_upsell_vendido (cliente_id, upsell_id, upsell_slug, upsell_nome, valor, observacao, registrado_por, registrado_por_nome{new Raw(colData)})
                       values ({Guid.Parse(clienteId!)}, {upsellId}, {p.Slug}, {nomeFinal}, {p.Valor}, {p.Observacao}, {uid}, {new Raw(NomeDe(uid))}{new Raw(valData)}) returning *)
            select {new Raw(CamposVenda)} from v
            """), LerVenda);
        return (new { ok = true, venda = nova.FirstOrDefault() }, null);
    }

    public async Task<(object? Corpo, Erro? Erro)> ArquivarUpsellAsync(EscopoDaSessao esc, string? vendaId)
    {
        if (!Uuid(vendaId)) return (null, new(400, "informe venda_id"));
        var alvo = await db.UmAsync(Sql.Fmt($"select v.cliente_id::text as cliente_id, v.arquivado_em from public.cs_upsell_vendido v where v.venda_id = {Guid.Parse(vendaId!)}"),
            r => new { ClienteId = r.S("cliente_id"), ArquivadoEm = r.Dt("arquivado_em") });
        if (alvo is null) return (null, new(404, NaoEncontrado));
        if (await ClienteNoAlcanceAsync(esc, alvo.ClienteId) is { } e) return (null, e);
        if (alvo.ArquivadoEm is not null) return (new { ok = true, ja_arquivada = true }, null);
        await db.ExecutarAsync(Sql.Fmt($"update public.cs_upsell_vendido set arquivado_em = now(), arquivado_por = {Guid.Parse(esc.UserId)} where venda_id = {Guid.Parse(vendaId!)}"));
        return (new { ok = true, venda_id = vendaId }, null);
    }

    /* ── DECISÃO HUMANA sobre um passo (fechado | ignorado, justificativa obrigatória)
       ou sobre uma promessa do alinhamento comercial (status da lista fechada,
       justificativa opcional). Grava AO LADO do que o agente escreveu. */
    public Task<(object? Corpo, Erro? Erro)> DecidirPassoAsync(EscopoDaSessao esc, string? analiseId, int? indice, string? status, string? justificativa) =>
        DecidirAsync(esc, analiseId, indice, status, justificativa, CsRegras.StatusPasso, true, "proximos_passos", "proximos_passos_decisao");
    public Task<(object? Corpo, Erro? Erro)> DecidirPromessaAsync(EscopoDaSessao esc, string? analiseId, int? indice, string? status, string? justificativa) =>
        DecidirAsync(esc, analiseId, indice, status, justificativa, CsRegras.StatusPromessa, false, "alinhamento_comercial->'status_de_cada'", "alinhamento_status_decisao");

    async Task<(object? Corpo, Erro? Erro)> DecidirAsync(EscopoDaSessao esc, string? analiseId, int? indice, string? status, string? justificativa, string[] aceitos, bool exige, string colunaLista, string colunaDecisao)
    {
        var (pedido, recusa) = CsRegras.ValidarDecisao(analiseId, indice, status, justificativa, aceitos, exige);
        if (recusa is not null) return (null, new(recusa.Http, recusa.Erro));
        if (!Uuid(analiseId)) return (null, new(404, "analise nao encontrada"));
        var aid = Guid.Parse(analiseId!);
        var a = await db.UmAsync(Sql.Fmt($"""
            select a.cliente_id::text as cliente_id, (a.{new Raw(colunaLista)})::text as lista, a.{new Raw(colunaDecisao)}::text as decisoes,
                   (select array_agg(cs.squad_id::text) from public.cliente_squads cs where cs.cliente_id = a.cliente_id) as squads
            from public.cs_analise a where a.analise_id = {aid}
            """), r => new { Lista = r.S("lista"), Decisoes = r.S("decisoes"), Squads = r.Textos("squads") });
        if (a is null || !CsRegras.ClienteNoAlcance(esc.SquadIds, a.Squads)) return (null, new(404, "analise nao encontrada"));
        if (CsRegras.ValidarIndice(pedido!.Indice, CsRegras.TamanhoDaLista(a.Lista)) is { } fora) return (null, new(fora.Http, fora.Erro));
        var uid = Guid.Parse(esc.UserId);
        var porNome = await db.UmAsync(Sql.Fmt($"select u.user_nome from public.users u where u.user_id = {uid}"), r => new { Nome = r.S("user_nome") });
        var decisao = new { status = pedido.Status, justificativa = pedido.Justificativa, por = esc.UserId, por_nome = porNome?.Nome, em = DateTime.UtcNow.ToString("o") };
        var mapa = CsRegras.AplicarDecisao(a.Decisoes, pedido.Indice, decisao);
        await db.ExecutarAsync(Sql.Fmt($"update public.cs_analise set {new Raw(colunaDecisao)} = {new Jsonb(mapa)} where analise_id = {aid}"));
        return (new { ok = true, indice = pedido.Indice, decisao }, null);
    }

    /* ── SEM PERFORMANCE: coordenador e acima, cliente e squad no alcance, vínculo vigente; ABYSSAL/DIVERS são fixos (409). */
    public async Task<(object? Corpo, Erro? Erro)> SemPerformanceAsync(EscopoDaSessao esc, string? clienteId, string? squadId, bool? ligado)
    {
        if (!Uuid(clienteId)) return (null, new(400, "informe cliente_id"));
        if (!Uuid(squadId)) return (null, new(400, "informe squad_id: o botão vale para o cliente num squad"));
        if (ligado is null) return (null, new(400, "informe ligado (true ou false)"));
        if (!CsRegras.PodeMudarSemPerformance(esc.Papeis)) return (null, new(403, $"Sem Performance: {CsRegras.MotivoSoCoordenador}"));
        if (!esc.Global && !(esc.SquadIds?.Contains(squadId!, StringComparer.OrdinalIgnoreCase) ?? false)) return (null, new(403, "squad fora do seu alcance"));
        var cid = Guid.Parse(clienteId!); var sid = Guid.Parse(squadId!);
        var vigentes = await db.ListaAsync(Sql.Fmt($"select cs.squad_id::text as squad_id, s.squad_nome from public.cliente_squads cs join public.squads s on s.squad_id = cs.squad_id where cs.cliente_id = {cid} and cs.cliente_squad_ate is null"),
            r => new { Id = r.S("squad_id")!, Nome = r.S("squad_nome") });
        if (!CsRegras.ClienteNoAlcance(esc.SquadIds, vigentes.Select(v => v.Id))) return (null, new(403, "cliente fora do seu alcance"));
        var doSquad = vigentes.FirstOrDefault(v => v.Id.Equals(squadId, StringComparison.OrdinalIgnoreCase));
        if (doSquad is null) return (null, new(403, "este squad nao atende este cliente"));
        if (CsRegras.SquadSemPerformanceFixo(doSquad.Nome)) return (null, new(409, $"Sem Performance: {CsRegras.AvisoSquadFixo}"));
        var uid = Guid.Parse(esc.UserId);
        var em = DateTime.UtcNow;
        var gravados = await db.ListaAsync(Sql.Fmt($"""
            update public.cliente_squads set cs_sem_performance = {ligado.Value}, cs_sem_performance_em = {em}, cs_sem_performance_por = {uid}
            where cliente_id = {cid} and squad_id = {sid} and cliente_squad_ate is null returning squad_id::text as squad_id
            """));
        if (gravados.Count == 0) return (null, new(409, "este squad nao atende este cliente"));
        var porNome = await db.UmAsync(Sql.Fmt($"select u.user_nome from public.users u where u.user_id = {uid}"), r => new { Nome = r.S("user_nome") });
        return (new { ok = true, cliente_id = clienteId, squad_id = squadId, sem_performance = ligado.Value, em, por = esc.UserId, por_nome = porNome?.Nome }, null);
    }

    /* ── OS MEMBROS que podem receber a task do cliente: quem está nos squads vigentes dele. */
    public async Task<(object? Corpo, Erro? Erro)> MembrosAsync(EscopoDaSessao esc, string? clienteId)
    {
        if (await ClienteNoAlcanceAsync(esc, clienteId) is { } e) return (null, e);
        var lista = await db.ListaAsync(Sql.Fmt($"""
            select distinct u.user_id::text as user_id, u.user_nome, s.squad_nome
            from public.cliente_squads cs join public.membros m on m.squad_id = cs.squad_id and m.membro_ate is null
            join public.users u on u.user_id = m.user_id and u.user_ativo join public.squads s on s.squad_id = cs.squad_id
            where cs.cliente_id = {Guid.Parse(clienteId!)} and cs.cliente_squad_ate is null order by u.user_nome
            """), r => new { user_id = r.S("user_id"), nome = r.S("user_nome"), squad = r.S("squad_nome") });
        return (new { membros = lista }, null);
    }

    /* ── PRÓXIMO PASSO → TASK (tasks, origem 'cs'): nasce de clique humano, nunca em
       massa; dedup pelo texto aberto do mesmo cliente; responsável pelo primeiro
       nome sugerido casado contra os membros dos squads do cliente, senão quem clicou. */
    public async Task<(object? Corpo, Erro? Erro)> PassoParaTaskAsync(EscopoDaSessao esc, PassoPedido p, CancellationToken ct)
    {
        var texto = (p.Texto ?? "").Trim();
        texto = texto[..Math.Min(texto.Length, 500)];
        if (!Uuid(p.ClienteId) || texto == "") return (null, new(400, "informe cliente_id e texto"));
        if (await ClienteNoAlcanceAsync(esc, p.ClienteId) is { } e) return (null, e with { Mensagem = e.Http == 404 ? "cliente nao encontrado" : e.Mensagem });
        var cid = Guid.Parse(p.ClienteId!);
        var uid = Guid.Parse(esc.UserId);
        var jaExiste = await db.UmAsync(Sql.Fmt($"select t.task_id::text as task_id from public.tasks t where t.cliente_id = {cid} and t.origem = 'cs' and t.status = 'aberta' and left(coalesce(t.detalhe, ''), {texto.Length}) = {texto} limit 1"), r => new { Id = r.S("task_id") });
        if (jaExiste is not null) return (new { ok = true, ja_existia = true, task_id = jaExiste.Id }, null);

        var titulo = await ResumirTituloAsync(texto, ct);
        Guid? responsavelId = null; string? responsavelNome = null;
        if (Uuid(p.ResponsavelUserId))
        {
            var u = await db.UmAsync(Sql.Fmt($"select u.user_id::text as id, u.user_nome from public.users u where u.user_id = {Guid.Parse(p.ResponsavelUserId!)}"), r => new { Id = r.S("id"), Nome = r.S("user_nome") });
            if (u?.Id is not null) { responsavelId = Guid.Parse(u.Id); responsavelNome = u.Nome; }
        }
        if (responsavelId is null && !string.IsNullOrWhiteSpace(p.Responsavel))
        {
            var alvo = CsRegras.PrimeiroNome(p.Responsavel);
            if (alvo != "")
            {
                var membros = await db.ListaAsync(Sql.Fmt($"""
                    select distinct u.user_id::text as id, u.user_nome from public.cliente_squads cs
                    join public.membros m on m.squad_id = cs.squad_id and m.membro_ate is null join public.users u on u.user_id = m.user_id
                    where cs.cliente_id = {cid} and cs.cliente_squad_ate is null
                    """), r => new { Id = r.S("id")!, Nome = r.S("user_nome") ?? "" });
                var m = membros.FirstOrDefault(x => CsRegras.PrimeiroNome(x.Nome) == alvo);
                if (m is not null) { responsavelId = Guid.Parse(m.Id); responsavelNome = m.Nome; }
            }
        }
        responsavelId ??= uid;
        var prazo = CsRegras.PrazoDoPasso(p.Prazo, DateTime.UtcNow);
        var motivo = (p.Motivo ?? "").Trim();
        var detalhe = texto + (motivo == "" ? "" : $"\nMotivo: {motivo}");
        detalhe = detalhe[..Math.Min(detalhe.Length, 4000)];
        var prioridade = p.Prioridade is "alta" or "media" or "baixa" ? p.Prioridade : "media";
        Guid? analiseId = Uuid(p.AnaliseId) ? Guid.Parse(p.AnaliseId!) : null;
        var nova = await db.ListaAsync(Sql.Fmt($"""
            insert into public.tasks (cliente_id, analise_id, titulo, detalhe, origem, responsavel_user_id, prioridade, prazo, tipo_passo, criado_por)
            values ({cid}, {analiseId}, {titulo}, {detalhe}, 'cs', {responsavelId}, {prioridade}, {prazo}, {p.TipoPasso}, {uid}) returning task_id::text as task_id
            """));
        return (new { ok = true, task_id = nova.FirstOrDefault()?.S("task_id"), titulo, responsavel = responsavelNome ?? "voce", prazo }, null);
    }

    /* O título curto pela IA (cadeia de ajuste, 20 s); sem chave ou com erro, o corte na palavra. */
    async Task<string> ResumirTituloAsync(string texto, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var schema = new Dictionary<string, object>
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new Dictionary<string, object> { ["titulo"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "O título da task, como pedido." } },
                ["required"] = new[] { "titulo" },
            };
            var r = await ia.PedirJsonAsync(["Você resume ações de atendimento ao cliente em títulos curtos de task."],
                $"Resuma esta ação em um título de task com NO MÁXIMO 8 palavras, imperativo, sem ponto final, sem aspas:\n\n{texto[..Math.Min(texto.Length, 600)]}",
                schema, 120, "ajuste", cts.Token);
            using var doc = JsonDocument.Parse(r.Texto);
            var bruto = doc.RootElement.TryGetProperty("titulo", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return CsRegras.TituloDaResposta(bruto) ?? CsRegras.CorteDoTitulo(texto);
        }
        catch (Exception e)
        {
            log.LogWarning("título do passo pela IA falhou, cortando na palavra: {Motivo}", e.Message);
            return CsRegras.CorteDoTitulo(texto);
        }
    }
}

public sealed record PassoPedido([property: JsonPropertyName("cliente_id")] string? ClienteId, [property: JsonPropertyName("analise_id")] string? AnaliseId, string? Texto, string? Motivo, string? Responsavel,
    [property: JsonPropertyName("responsavel_user_id")] string? ResponsavelUserId, string? Prioridade, string? Prazo, [property: JsonPropertyName("tipo_passo")] string? TipoPasso);
