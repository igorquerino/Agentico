using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Executor;
using BlueOS.Core.Meta;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O EXECUTOR: a porta única por onde o motor escreve na Meta, e as travas de
   dinheiro na frente dela. Porta de lib/trafego/motor/executor.ts (Tráfego
   3.4) e da rota do aceite (app/api/trafego/motor/decisao/route.ts).
   ----------------------------------------------------------------------------
   A ORDEM DO ACEITE É ESTA, e importa:
     0. a árvore ainda diz a mesma coisa? (a recomendação é uma FOTO);
        a chave geral está ligada? a automação do cliente está ativa?
        a RESERVA é aberta no banco (uma escrita por recomendação);
     1. a decisão é GRAVADA;
     2. só então, com o aceite relido do banco, a escrita acontece:
        isolamento do alvo, saúde do token, leitura ao vivo, as travas de
        dinheiro, `aplicarNaMeta`, a verba da cópia, o log, o desfecho, o
        fechamento da reserva, o sino e a trilha.
   Gravar primeiro é de propósito: se a Meta falhar, ficou registrado que o
   gestor aceitou e que a aplicação falhou.

   O que NÃO está aqui: o vault (`travas_automatico`, `copia_nasce`). Esta API
   usa os limites duros do código e a cópia nasce PAUSED, que é o lado
   conservador; o vault só apertaria.
   ============================================================================ */
public sealed record RecomendacaoAAplicar(Guid Id, string ClienteSlug, string Acao, string MetaId, JsonElement Numeros);

public sealed record ResultadoDaAplicacao(
    bool Ok, string Desfecho, TipoNaMeta TipoNaMeta, ResultadoMeta R, string? CopiaId, VerbaDaCopia? VerbaDaCopia, AtivacaoDaCopia? Ativacao,
    (string Codigo, string Motivo)? Barrada, EstadoAoVivo? EstadoAnterior, IReadOnlyList<string> Avisos, ClasseDoErro? Classe, int AguardarMs);

public sealed class Executor(Db db, MetaEscrita meta, MetaGraph graph, Registro registro, Automacao automacao, Carteira carteira, ILogger<Executor> log)
{
    static readonly string[] VerbosDeVerba = ["mudou_orcamento", "duplicou_conjunto", "editou_conjunto"];

    /* ---- a folga do mês (D2) ---------------------------------------------------- */

    public async Task<Folga> FolgaDoClienteAsync(string slug, DateTime agora)
    {
        var hoje = Fuso.DiaDaConta(agora);
        var inicioDoMes = new DateOnly(hoje.Year, hoje.Month, 1);
        var seteDiasAtras = hoje.AddDays(-7);
        var desde = seteDiasAtras < inicioDoMes ? seteDiasAtras : inicioDoMes;
        var budget = (await db.ListaAsync(Sql.Fmt($"select ce.budget_ads_mensal from public.cliente_estrategia ce join public.clientes c on c.cliente_id = ce.cliente_id where c.cliente_slug = {slug}"), r => r.Dec("budget_ads_mensal"))).FirstOrDefault();
        var insights = await db.ListaAsync(Sql.Fmt($"select adset_id, dia, sum(gasto) as gasto from public.trafego_insight_dia where cliente_slug = {slug} and dia >= {desde} group by adset_id, dia"),
            r => (Adset: r.S("adset_id"), Dia: r.Data("dia") ?? hoje, Gasto: r.Dec("gasto") ?? 0m));
        var fotos = await db.ListaAsync(Sql.Fmt($"select adset_id, status, orcamento_diario_centavos from public.trafego_adset_estado where cliente_slug = {slug}"),
            r => (Adset: r.S("adset_id") ?? "", Status: r.S("status"), Verba: r.L("orcamento_diario_centavos")));
        var gasto = insights.Where(i => i.Dia >= inicioDoMes).Sum(i => i.Gasto);
        var ativos = fotos.Where(f => f.Status == "ACTIVE").ToList();
        var proprias = ativos.Sum(f => Math.Max(0, f.Verba));
        /* O conjunto ativo sem verba própria (CBO) também gasta: a diária é a média do que ele gastou nos dias em que
           gastou, conjunto por conjunto. */
        var semVerba = ativos.Where(f => f.Verba <= 0).Select(f => f.Adset).ToHashSet();
        var porConjunto = new Dictionary<string, (decimal Gasto, HashSet<DateOnly> Dias)>();
        foreach (var i in insights)
        {
            if (i.Adset is null || !semVerba.Contains(i.Adset) || i.Dia < seteDiasAtras || i.Dia >= hoje) continue;
            (decimal Gasto, HashSet<DateOnly> Dias) c = porConjunto.TryGetValue(i.Adset, out var x) ? x : (0m, new HashSet<DateOnly>());
            c.Gasto += i.Gasto; if (i.Gasto > 0) c.Dias.Add(i.Dia);
            porConjunto[i.Adset] = c;
        }
        var estimadas = (long)Math.Round(porConjunto.Values.Sum(c => (double)c.Gasto * 100 / Math.Max(1, c.Dias.Count)));
        return Travas.MontarFolga(budget is > 0 ? Dinheiro.CentavosDe(budget.Value) : null, (long)Math.Round(gasto * 100), proprias, estimadas, Travas.DiasRestantesNoMes(hoje));
    }

    /// <summary>Quantas escritas que mexem em verba esta CONTA teve na última hora: as aplicações do motor (em voo,
    /// aplicada, incerto; menos a desta recomendação) mais as linhas manuais do log de ação.</summary>
    public async Task<int> MudancasDeVerbaNaUltimaHoraAsync(string slug, Guid? exceto)
    {
        var l = await db.ListaAsync(Sql.Fmt($"""
            select
              (select count(*) from public.trafego_motor_aplicacao a join public.trafego_motor_recomendacao r on r.id = a.recomendacao_id
                 where r.cliente_slug = {slug} and r.acao = 'DUPLICAR' and a.comecou_em >= now() - interval '1 hour'
                   and a.estado in ('em_voo', 'aplicada', 'incerto') and ({exceto} is null or a.recomendacao_id <> {exceto})) as do_motor,
              (select count(*) from public.trafego_log_acao g where g.cliente_slug = {slug} and g.ocorrido_em >= now() - interval '1 hour'
                 and g.acao = any({VerbosDeVerba}) and coalesce(g.detalhe->>'motor', '') <> 'true') as manuais
            """), r => (int)(r.L("do_motor") + r.L("manuais")));
        return l.FirstOrDefault();
    }

    /* ---- a ativação da cópia (D1) ---------------------------------------------- */

    async Task<AtivacaoDaCopia> AtivarCopiaAsync(Credenciais cred, string copiaId, string originalId)
    {
        var saida = new AtivacaoDaCopia();
        List<(string Id, string Status, string Origem)> daCopia; HashSet<string> ativosNoOriginal;
        try
        {
            var tc = graph.GetAsync($"{copiaId}/ads?fields=id,status,source_ad_id&limit=200", cred.Token, 15);
            var to = graph.GetAsync($"{originalId}/ads?fields=id,status&limit=200", cred.Token, 15);
            await Task.WhenAll(tc, to);
            daCopia = Linhas(tc.Result).Select(a => (MetaGraph.S(a, "id") ?? "", MetaGraph.S(a, "status") ?? "", MetaGraph.S(a, "source_ad_id") ?? "")).Where(a => a.Item1 != "").ToList();
            ativosNoOriginal = Linhas(to.Result).Where(a => MetaGraph.S(a, "status") == "ACTIVE").Select(a => MetaGraph.S(a, "id") ?? "").ToHashSet();
        }
        catch (Exception e) { saida.Erro = $"não consegui ler os anúncios da cópia e do original ({MetaGraph.Sanear(e.Message)})"; return saida; }

        /* SÓ OS QUE ESTAVAM ATIVOS NO ORIGINAL: o motor nunca reativa o que alguém pausou. */
        var aAtivar = daCopia.Where(a => a.Status != "ACTIVE" && a.Origem != "" && ativosNoOriginal.Contains(a.Origem)).ToList();
        saida.MantidosPausados = daCopia.Count(a => a.Status != "ACTIVE") - aAtivar.Count;
        if (aAtivar.Count == 0 && !daCopia.Any(a => a.Status == "ACTIVE")) { saida.Erro = "nenhum anúncio da cópia veio de um anúncio ativo do original"; return saida; }

        var noConjunto = await meta.AplicarNaMetaAsync(cred, "ativar_copia", copiaId, new Dictionary<string, object?>());
        saida.Escritas++;
        saida.Conjunto = noConjunto.Ok;
        if (!noConjunto.Ok) { saida.Erro = noConjunto.Erro ?? "a Meta recusou ativar o conjunto"; return saida; }
        foreach (var a in aAtivar)
        {
            var r = await meta.AplicarNaMetaAsync(cred, "ativar_copia", a.Id, new Dictionary<string, object?>());
            saida.Escritas++;
            if (r.Ok) saida.Ativados++; else saida.Falharam++;
        }
        if (saida.Falharam > 0) saida.Erro = $"o conjunto foi ativado, mas {saida.Falharam} anúncio(s) da cópia não";
        return saida;
    }

    static IEnumerable<JsonElement> Linhas(JsonElement d) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty("data", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    /* ---- aplicar: depois do aceite registrado ---------------------------------- */

    public async Task<ResultadoDaAplicacao> AplicarRecomendacaoAsync(RecomendacaoAAplicar rec, TipoNaMeta tipo, Guid por, string origem, string? motivo, int escritasNaRodada = 0, LimitesDoAutomatico? limites = null)
    {
        var nome = rec.Numeros.ValueKind == JsonValueKind.Object && rec.Numeros.TryGetProperty("nome", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : rec.MetaId;
        var acaoLegivel = rec.Acao == "PAUSAR" ? "pausar" : rec.Acao == "DUPLICAR" ? "duplicar" : rec.Acao.ToLowerInvariant();
        var tipoNaGraph = tipo.NaGraph();

        async Task<ResultadoDaAplicacao> Barrar(string codigo, string motivoDaTrava, EstadoAoVivo? estadoAnterior)
        {
            await registro.FecharAplicacaoAsync(rec.Id, "falhou", new { ok = false, alterou = false, erro = motivoDaTrava, porta = codigo, estado_anterior = estadoAnterior });
            if (codigo is "sem_folga" or "sem_budget")
                await registro.AvisarAsync(rec.ClienteSlug, "sem_folga", rec.MetaId, $"Sem folga no mês para duplicar \"{nome}\"", motivoDaTrava, new { codigo, origem }, rec.Id);
            else if (codigo == "conta_nao_elegivel")
                await registro.AvisarAsync(rec.ClienteSlug, "conta_restrita", "conta", "A conta de anúncios não aceita escrita", motivoDaTrava, new { codigo, origem }, rec.Id);
            else if (codigo == "token_nao_serve")
                await registro.AvisarAsync(rec.ClienteSlug, "token_vencido", "conta", "O token do squad não serve para o motor", motivoDaTrava, new { codigo, origem }, rec.Id);
            try { await registro.RegistrarDecisaoAsync(rec.Id, "falhou", por, $"{codigo}: {motivoDaTrava}", new { ok = false, erro = motivoDaTrava, alterou = false, tipo = tipoNaGraph, porta = codigo, estado_anterior = estadoAnterior }); } catch { }
            await registro.TrilhaAsync("aplicou_na_meta", por, rec.ClienteSlug, "negado", $"{codigo}: {motivoDaTrava}", tipoNaGraph, rec.MetaId, new { via = "motor", recomendacao_id = rec.Id, porta = codigo }, origem: origem);
            return new ResultadoDaAplicacao(false, "falhou", tipo, ResultadoMeta.Falha(motivoDaTrava, false), null, null, null, (codigo, motivoDaTrava), estadoAnterior, [], null, 0);
        }

        /* ---- a. a credencial e o isolamento: o token do squad alcança VÁRIAS contas ---- */
        var (cred, erroCred) = await meta.CredenciaisDoClienteAsync(rec.ClienteSlug);
        if (cred is null) return await Barrar("conta_nao_elegivel", erroCred ?? "sem credencial", null);
        var dono = await meta.AlvoPertenceAoClienteAsync(cred, rec.MetaId);
        if (!dono.Ok) return await Barrar("isolamento", dono.Motivo, null);

        /* ---- a½. a saúde do token (3.7): sem app no ambiente não barra ---------- */
        var saude = await meta.SaudeDoTokenAsync(cred.Token);
        if (saude.Estado == "nao_serve") return await Barrar("token_nao_serve", saude.Motivo ?? "o token não serve para o motor", null);

        /* ---- b. as travas de dinheiro, com a leitura ao vivo --------------------- */
        long? pedida = rec.Numeros.ValueKind == JsonValueKind.Object && rec.Numeros.TryGetProperty("copia_diario_centavos", out var cd) && cd.ValueKind == JsonValueKind.Number && cd.GetDouble() > 0 ? (long)Math.Round(cd.GetDouble()) : null;
        var vivo = await meta.LerAoVivoAsync(cred, rec.MetaId);
        var folga = tipo == TipoNaMeta.DuplicateAdset ? await FolgaDoClienteAsync(rec.ClienteSlug, DateTime.UtcNow) : Travas.MontarFolga(null, 0, 0, 0, 1);
        var mudancas = tipo == TipoNaMeta.DuplicateAdset ? await MudancasDeVerbaNaUltimaHoraAsync(rec.ClienteSlug, rec.Id) : 0;
        var trava = Travas.TravasDeDinheiro(tipo, pedida, folga, mudancas, escritasNaRodada, vivo, limites);
        if (!trava.Ok) return await Barrar(trava.Codigo!.Value.Slug(), trava.Motivo!, vivo);
        var avisos = new List<string>(trava.Avisos);

        /* ---- c. a escrita, pela única porta de baixo ------------------------------ */
        var parametros = new Dictionary<string, object?>();
        if (tipo == TipoNaMeta.DuplicateAdset && trava.CopiaCentavos is { } cc) parametros["daily_budget_cents"] = cc;
        var copiaNasce = tipo == TipoNaMeta.DuplicateAdset && limites?.CopiaNasce == "ACTIVE" ? "ACTIVE" : "PAUSED";
        ResultadoMeta r; var semResposta = false;
        try { r = await meta.AplicarNaMetaAsync(cred, tipoNaGraph, rec.MetaId, parametros); }
        catch (Exception e) { semResposta = true; r = ResultadoMeta.Falha($"a Meta não respondeu ({MetaGraph.Sanear(e.Message)}): a ação pode ter acontecido", true); }

        /* ---- d. a verba da cópia, numa segunda escrita (o /copies ignora params) --- */
        VerbaDaCopia? verbaDaCopia = null;
        var centavosDaCopia = trava.CopiaCentavos ?? 0;
        if (r.Ok && tipo == TipoNaMeta.DuplicateAdset && centavosDaCopia > 0 && r.Id is not null)
        {
            try
            {
                var up = await meta.AplicarNaMetaAsync(cred, "update_adset", r.Id, new Dictionary<string, object?> { ["daily_budget"] = centavosDaCopia.ToString() });
                verbaDaCopia = new VerbaDaCopia(centavosDaCopia, up.Ok, up.Ok ? null : up.Erro);
            }
            catch (Exception e) { verbaDaCopia = new VerbaDaCopia(centavosDaCopia, false, MetaGraph.Sanear(e.Message)); }
            if (verbaDaCopia is { Ok: false }) avisos.Add(Travas.TextoDaCopia(verbaDaCopia, copiaNasce, null));
        }

        /* ---- d½. a ativação da cópia, depois da verba (D1) ------------------------- */
        AtivacaoDaCopia? ativacao = null;
        if (r.Ok && tipo == TipoNaMeta.DuplicateAdset && copiaNasce == "ACTIVE" && r.Id is not null)
        {
            if (verbaDaCopia is { Ok: false }) avisos.Add("A cópia NÃO foi ativada porque a verba dela não entrou: ela fica pausada com a verba do original. Ajuste a verba no Gerenciador antes de ativar.");
            else if (centavosDaCopia <= 0) avisos.Add("A cópia ficou PAUSADA de propósito: este conjunto está em CBO, a verba é da campanha e a cópia a herda sem passar pela folga do mês. Ativar é no Gerenciador, conferindo a verba da campanha.");
            else { ativacao = await AtivarCopiaAsync(cred, r.Id, rec.MetaId); avisos.Add(Travas.TextoDaCopia(verbaDaCopia, copiaNasce, ativacao)); }
        }

        /* ---- e. o log de ação, só quando a Meta confirmou -------------------------- */
        if (r.Ok)
            await registro.LogAcaoAsync(rec.ClienteSlug, tipo.VerboNoLog(), "conjunto", rec.MetaId, null, por.ToString(),
                new { via = origem == "cron" ? "chat" : "tela", motor = true, automatico = origem == "cron", recomendacao_id = rec.Id, acao_do_motor = rec.Acao, copia_id = r.Id, verba_da_copia = verbaDaCopia });

        /* ---- f. o desfecho: 'aplicada', 'falhou' (recusa limpa) ou 'incerto' -------- */
        var copiaSemCerteza = !r.Ok && tipo == TipoNaMeta.DuplicateAdset && r.ErroMeta is not null && !r.Simulado;
        var desfecho = r.Ok ? "aplicada" : (semResposta || r.Alterou || copiaSemCerteza) ? "incerto" : "falhou";
        ClasseDoErro? classe = r.Ok ? null : semResposta ? ClasseDoErro.Temporario : (r.Classe ?? ClasseDoErro.Desconhecido);
        var aguardarMs = r.Ok ? r.AguardarMs : (r.AguardarMs > 0 ? r.AguardarMs : classe == ClasseDoErro.Temporario ? 60_000 : 0);
        var resultado = new
        {
            ok = r.Ok, erro = r.Ok ? null : r.Erro, alterou = r.Ok || r.Alterou, tipo = tipoNaGraph, simulado = r.Simulado, via = r.Via,
            copia_id = r.Ok ? r.Id : null, verba_da_copia = verbaDaCopia, estado_anterior = vivo, folga = tipo == TipoNaMeta.DuplicateAdset ? folga : null,
            avisos_da_porta = avisos, classe = classe?.ToString().ToLowerInvariant(), permanente = classe == ClasseDoErro.Permanente, aguardar_ms = aguardarMs,
        };
        try { await registro.RegistrarDecisaoAsync(rec.Id, desfecho, por, motivo, resultado); } catch (Exception e) { log.LogError("a decisão de desfecho não gravou: {Erro}", e.Message); }
        await registro.FecharAplicacaoAsync(rec.Id, desfecho, resultado);

        /* ---- o sino (3.6) e a trilha ------------------------------------------------- */
        var sufixo = origem == "cron" ? " (automático)" : "";
        if (r.Ok)
            await registro.AvisarAsync(rec.ClienteSlug, "acao_executada", rec.MetaId, $"{(acaoLegivel == "pausar" ? "Pausou" : "Duplicou")} \"{nome}\"{sufixo}",
                tipo == TipoNaMeta.DuplicateAdset ? Travas.TextoDaCopia(verbaDaCopia) : "O conjunto está pausado na Meta.", new { tipo = tipoNaGraph, copia_id = r.Id, origem }, rec.Id);
        else if (r.Simulado)
            avisos.Add(r.Erro ?? "fora da produção: só ensaio, nada foi alterado");
        else if (classe == ClasseDoErro.Credencial)
            await registro.AvisarAsync(rec.ClienteSlug, "token_vencido", "conta", "O token da conta não serve mais", $"A Meta recusou a credencial ao {acaoLegivel} \"{nome}\": {r.Erro}. Reconecte a conta no Setup.", new { classe = "credencial", origem }, rec.Id);
        else
            await registro.AvisarAsync(rec.ClienteSlug, desfecho == "incerto" ? "incerto" : "falha", rec.MetaId, desfecho == "incerto" ? $"Sem resposta da Meta ao {acaoLegivel} \"{nome}\"{sufixo}" : $"A Meta recusou {acaoLegivel} \"{nome}\"{sufixo}",
                desfecho == "incerto" ? $"{r.Erro}. A reconciliação do cron confere amanhã; se tiver pressa, olhe o Gerenciador." : $"{r.Erro}{(classe == ClasseDoErro.Permanente ? " Repetir não conserta: a recomendação fica fechada até a causa ser corrigida." : "")}",
                new { classe = classe?.ToString().ToLowerInvariant(), aguardar_ms = aguardarMs, origem }, rec.Id);
        await registro.TrilhaAsync("aplicou_na_meta", por, rec.ClienteSlug, r.Ok ? "ok" : "erro", r.Ok ? null : r.Erro, tipoNaGraph, rec.MetaId,
            new { via = "motor", rota = r.Via, recomendacao_id = rec.Id, tipo = tipoNaGraph, target_id = rec.MetaId, automatico = origem == "cron", simulado = r.Simulado }, origem: origem);

        return new ResultadoDaAplicacao(r.Ok, desfecho, tipo, r, r.Ok ? r.Id : null, verbaDaCopia, ativacao, null, vivo, avisos, classe, aguardarMs);
    }

    /* ---- a decisão do gestor (a rota) ------------------------------------------- */

    /// <summary>POST .../recomendacoes/{id}/decisao: devolve o status HTTP e o corpo, como a rota do V1.</summary>
    public async Task<(int Status, object Corpo)> DecidirAsync(EscopoDaSessao esc, string slug, string recomendacaoId, bool aceita, string? motivo)
    {
        var decisao = aceita ? "aceita" : "negada";
        if (!aceita && string.IsNullOrWhiteSpace(motivo)) return (400, new { error = "negar pede motivo: é o que diz ao motor onde ele errou." });
        if (!Guid.TryParse(recomendacaoId, out var recId)) return (404, new { error = "recomendação não encontrada" });
        var rec = await registro.LerAsync(recId);
        if (rec is null || rec.ClienteSlug != slug) return (404, new { error = "recomendação não encontrada" });
        if (aceita && !esc.PodeAplicarNaMeta) return (403, new { error = "aceitar recomendação do motor é de head ou acima. Negar você pode." });
        var por = Guid.Parse(esc.UserId);

        var tipoNaMeta = Enum.TryParse<Acao>(rec.Acao, out var acao) ? TipoNaMetaExt.DaAcao(acao) : null;
        var vaiEscrever = aceita && tipoNaMeta is not null;
        var ehTroca = rec.Acao == "TROCAR_CRIATIVO";

        /* ---- a troca de criativo também escreve na Meta (#5012), pela fila dela ---- */
        if (ehTroca)
        {
            var t = await db.UmAsync(Sql.Fmt($"select estado, ad_novo_id from public.trafego_motor_troca where recomendacao_id = {recId} order by atualizado_em desc limit 1"), r => new { Estado = r.S("estado"), AdNovo = r.S("ad_novo_id") });
            if (t?.Estado is "copy" or "arte" or "publicar")
                return (409, new { error = $"a troca automática está trabalhando nesta recomendação (agora: {t.Estado}). Para parar, pause a automação do cliente na aba Ongoing; decidir aqui no meio deixaria a troca pela metade.", troca = t.Estado });
            if (!aceita && !string.IsNullOrEmpty(t?.AdNovo))
                return (409, new { error = $"a troca automática já criou o anúncio novo ({t.AdNovo}) neste conjunto: negar agora não desfaz. A task do gestor diz o que ficou ativo; mexa pelo Gerenciador.", troca = t.Estado });
        }

        /* ---- negar o que já mexeu na conta não desfaz nada ------------------------- */
        if (!aceita && (tipoNaMeta is not null || ehTroca))
        {
            EstadoDaAplicacao? aplic;
            try { aplic = await registro.EstadoDaAplicacaoAsync(recId); }
            catch (Exception e) { return (503, new { error = $"não consegui conferir se esta recomendação já foi aplicada: {e.Message}. Nada foi gravado." }); }
            if (aplic is not null && aplic.Estado is not ("falhou" or "sem_escrita"))
                return (409, new
                {
                    error = aplic.Estado == "aplicada"
                        ? "esta recomendação já foi aplicada na Meta: negar agora não desfaz a ação. Para voltar atrás, mexa no conjunto pelo Gerenciador."
                        : "esta recomendação está sendo aplicada, ou a última tentativa não teve resposta da Meta: confira o conjunto no Gerenciador antes de negar.",
                    estado = aplic.Estado,
                });
        }

        if (vaiEscrever)
        {
            /* ---- 0a. a árvore ainda diz a mesma coisa? (AD-MT-07) ------------------ */
            try
            {
                var cli = await carteira.ObterAsync(esc, slug);
                if (cli is null) return (403, new { error = "cliente fora do seu escopo" });
                var ongoing = await automacao.OngoingAsync(cli);
                var cj = ongoing.Conjuntos.FirstOrDefault(x => x.AdsetId == rec.MetaId);
                if (cj is null) return (409, new { error = "não consigo reconferir este conjunto na carga de hoje: ele pode ter sido apagado na Meta. Rode o motor de novo.", obsoleta = true });
                var formato = await db.UmAsync(Sql.Fmt($"select formato from public.trafego_adset_estado where adset_id = {rec.MetaId} and cliente_slug = {slug}"), r => r.S("formato"));
                var hoje = Arvore.Decidir(EntradaDe(cj, slug, formato), new EntradaAoVivo(cj.Ativo, cj.DiarioCentavos),
                    cli.TetoMql is > 0 ? Dinheiro.CentavosDe(cli.TetoMql.Value) : null,
                    ongoing.Conjuntos.Any(x => x.Leads.Any(l => l.Situacao == Situacao.Mql)), ParametrosMotor.PadraoDoVault);
                var mesmasChaves = string.Join(",", hoje.Chaves.OrderBy(x => x, StringComparer.Ordinal)) == string.Join(",", rec.Chaves.OrderBy(x => x, StringComparer.Ordinal));
                if (hoje.Acao.ToString() != rec.Acao || !mesmasChaves)
                    return (409, new
                    {
                        error = "esta recomendação está obsoleta: os números ou as regras mudaram desde que ela foi feita. Rode o motor de novo e decida sobre a recomendação nova.",
                        obsoleta = true, acao_de_antes = rec.Acao, acao_de_agora = hoje.Acao.ToString(), chaves_de_antes = rec.Chaves, chaves_de_agora = hoje.Chaves,
                    });
            }
            catch (Exception e) when (e is not InvalidOperationException { Message: var m } || !m.Contains("escopo"))
            {
                return (503, new { error = $"não consegui reconferir a árvore antes de aplicar: {e.Message}. Nada foi alterado." });
            }

            /* ---- 0a½. a chave geral e a automação do cliente --------------------- */
            var chave = await db.UmAsync("select ligada, motivo from public.trafego_motor_chave_geral where id = 1", r => new { Ligada = r.B("ligada") ?? true, Motivo = r.S("motivo") });
            if (chave is { Ligada: false })
                return (409, new { error = $"o motor está desligado pela chave geral{(chave.Motivo is not null ? $" ({chave.Motivo})" : "")}: nada escreve na Meta. Ligue a chave na aba Automação, ou negue a recomendação.", chave_geral = chave });
            var aut = await db.UmAsync(Sql.Fmt($"select motor_automacao, motor_automacao_motivo from public.trafego_contas where cliente_slug = {slug}"), r => new { Estado = r.S("motor_automacao"), Motivo = r.S("motor_automacao_motivo") });
            if (aut?.Estado is "pausada" or "cancelada")
            {
                var porque = aut.Motivo is not null ? $" ({aut.Motivo})" : "";
                return (409, new { error = (aut.Estado == "cancelada" ? $"a automação deste cliente foi cancelada{porque}: o motor não lê nem age nele. Reative na aba Automação › Ongoing." : $"a automação deste cliente está pausada{porque}: o motor não lê nem age nele. Retome na aba Automação › Ongoing.") + " Ou negue a recomendação.", automacao = aut });
            }

            /* ---- 0b. a reserva, antes de qualquer escrita -------------------------- */
            var reserva = await registro.ReservarAplicacaoAsync(recId, tipoNaMeta!.Value.NaGraph(), por, rec.MetaId);
            if (!reserva.Ok) return (409, new { error = reserva.Motivo, estado = reserva.Estado });
        }

        /* ---- 1. grava a decisão ---------------------------------------------------- */
        try { await registro.RegistrarDecisaoAsync(recId, decisao, por, motivo, null); }
        catch (Exception e)
        {
            if (vaiEscrever) await registro.FecharAplicacaoAsync(recId, "falhou", new { ok = false, alterou = false, erro = "a decisão não foi gravada; nada foi enviado à Meta" });
            return (500, new { error = e.Message });
        }
        await registro.TrilhaAsync(aceita ? "aceitou_recomendacao_motor" : "negou_recomendacao_motor", por, slug, "ok", null, null, null,
            new { via = "motor", recomendacao_id = recId, acao = rec.Acao, meta_id = rec.MetaId, motivo });

        /* ---- 2. negada, ou ação que só registra: acabou ----------------------------- */
        if (!aceita || tipoNaMeta is null)
            return (200, new { ok = true, decisao, aplicou = false, motivo_de_nao_aplicar = !aceita ? "negada pelo gestor" : $"a ação {rec.Acao} não escreve na Meta: ela orienta o gestor." });

        /* ---- 3. sem aceite, sem escrita: relê do banco ----------------------------- */
        RecomendacaoLida aceite;
        try { aceite = await registro.ExigirAceiteAsync(recId); }
        catch (Exception e)
        {
            await registro.FecharAplicacaoAsync(recId, "falhou", new { ok = false, alterou = false, erro = e.Message });
            return (409, new { error = e.Message });
        }

        /* ---- 4. a escrita, pela única porta ----------------------------------------- */
        var feito = await AplicarRecomendacaoAsync(new RecomendacaoAAplicar(recId, aceite.ClienteSlug, aceite.Acao, aceite.MetaId, aceite.Numeros), tipoNaMeta.Value, por, "tela", motivo);
        if (feito.Barrada is { } b)
        {
            var seguranca = b.Codigo == "isolamento";
            return (seguranca ? 403 : 422, new { error = seguranca ? $"ação bloqueada por segurança: {b.Motivo}. Nada foi aplicado." : $"{b.Motivo} Nada foi aplicado.", codigo = b.Codigo, estado_anterior = feito.EstadoAnterior });
        }
        var r = feito.R;
        return (r.Ok ? 200 : r.Simulado ? 200 : 502, new
        {
            ok = r.Ok, decisao = feito.Desfecho, aplicou = r.Ok, alterou = r.Ok || r.Alterou, simulado = r.Simulado,
            erro = r.Ok ? null : feito.Desfecho == "incerto" ? $"{r.Erro}. Confira o conjunto no Gerenciador antes de qualquer coisa: esta recomendação fica travada para não repetir a ação." : r.Erro,
            tipo = tipoNaMeta.Value.NaGraph(), via = r.Via, copia_id = feito.CopiaId, verba_da_copia = feito.VerbaDaCopia, avisos = feito.Avisos, estado_anterior = feito.EstadoAnterior,
        });
    }

    /// <summary>A entrada da árvore a partir do conjunto como o Ongoing o lê (a carga de hoje).</summary>
    internal static EntradaConjunto EntradaDe(ConjuntoEmVeiculacao cj, string slug, string? formato)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Now);
        var leads = cj.Leads;
        var fmt = (formato ?? "").ToLowerInvariant() switch { "video" => Formato.Video, "misto" => Formato.Misto, _ => Formato.Imagem };
        var memoria = new MemoriaDoConjunto(
            cj.UltimaAcaoExecutada is { } ua ? hoje.DayNumber - ua.DayNumber : null,
            cj.PendenteDesde is { } pd ? hoje.DayNumber - pd.DayNumber : null,
            cj.CopiasVivas);
        return new EntradaConjunto(slug, cj.AdsetId, cj.Nome, fmt, cj.AnunciosAtivos, cj.DiasEntrega, cj.GastoVidaCentavos, leads.Count,
            leads.Count(l => l.Situacao is Situacao.Mql or Situacao.NaoMql), leads.Count(l => l.Situacao == Situacao.Pendente),
            leads.Count(l => l.Situacao == Situacao.Mql && l.Tier == Tier.A), leads.Count(l => l.Situacao == Situacao.Mql && l.Tier == Tier.B), leads.Count(l => l.Situacao == Situacao.Mql && l.Tier == Tier.C),
            cj.Leitura, cj.CoberturaCompleta, memoria);
    }
}
