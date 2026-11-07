using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueOS.Api.Infra;
using BlueOS.Core.Executor;
using BlueOS.Core.Meta;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   ESCREVER NA META, DAQUI, COM O TOKEN DO SQUAD: porta de lib/trafego/aplicar-
   meta.ts, meta-activity.ts (credenciaisDoCliente), saude-do-token.ts e
   pertence.ts.
   ----------------------------------------------------------------------------
   · a credencial é a do SQUAD do cliente (trafego_integracoes.meta_access_token,
     o Usuário do Sistema do app da Meta) e a conta é a de trafego_contas;
   · VALIDA ANTES DE ESCREVER: toda ação vai primeiro com `validate_only`;
   · ANTI-BAN: salto de verba acima de 5× não passa, e não ler a verba atual
     NÃO libera o salto (falha fechado);
   · FORA DA PRODUÇÃO só o ensaio acontece, salvo a conta de teste liberada
     ou o squad liberado no MCP (Meta:Mcp:Squads);
   · A ROTA: o cliente de um squad em Meta:Mcp:Squads escreve pelo MCP oficial
     da Meta (pausa, verba, ativação; e no Publicador campanha, conjunto e
     anúncio). O que o MCP não oferece (a cópia de conjunto, o formulário)
     segue pela Graph com o MESMO token. `Credenciais.Rota` diz qual é.
   ============================================================================ */
public sealed record Credenciais(string Token, string Act, string? Squad, string Rota = "graph")
{
    public bool PeloMcp => Rota == "mcp";
}

public sealed record ErroDaMetaInfo(string? Titulo, string? Msg, int? Codigo);

public sealed record ResultadoMeta(bool Ok, string? Id, bool Validado, string? Erro, ErroDaMetaInfo? ErroMeta, bool Alterou, bool Simulado, ClasseDoErro? Classe, int AguardarMs)
{
    /// <summary>Por onde a escrita foi (ou iria): `graph` ou `mcp`.</summary>
    public string Via { get; init; } = "graph";

    public static ResultadoMeta Falha(string erro, bool alterou, ClasseDoErro? classe = null, int aguardarMs = 0, ErroDaMetaInfo? info = null, bool simulado = false, string via = "graph") =>
        new(false, null, false, erro, info, alterou, simulado, classe, aguardarMs) { Via = via };
}

public sealed record SaudeDoToken(string Estado, bool? Valido, DateTime? VenceEm, int? DiasAteVencer, IReadOnlyList<string> Escopos, IReadOnlyList<string> Faltam, string? Motivo);

public sealed class MetaEscrita(Db db, MetaGraph graph, MetaMcp mcp, ILogger<MetaEscrita> log)
{
    public static readonly string[] EscoposDoMotor = ["ads_management", "ads_read", "leads_retrieval"];
    public const int DiasMinimosAteVencer = 7;
    static readonly ConcurrentDictionary<string, (DateTime Em, Credenciais Cred)> CredCache = new();
    static readonly ConcurrentDictionary<string, (DateTime Em, SaudeDoToken Saude)> SaudeCache = new();
    static readonly Regex IdDaMeta = new(@"^\d{8,25}$", RegexOptions.Compiled);

    /// <summary>Depois de um token novo no squad: a credencial em cache vence na hora.</summary>
    public static void EsquecerCredenciais() { CredCache.Clear(); SaudeCache.Clear(); }

    /* ---- a credencial do cliente ---------------------------------------------- */

    /// <summary>trafego_contas (a conta) + trafego_integracoes (o token do squad). Os outros squads do cliente no V1 só
    /// quando a conta NÃO tem squad (meta-activity.ts): com o squad conhecido e sem token, é "sem token", e nunca o
    /// token de outro squad, que a rota (MCP e liberação de escrita) não acompanharia. Nunca o OS antigo. AS FRASES DE
    /// ERRO SÃO CONTRATO: "sem conta" e "sem token".</summary>
    public async Task<(Credenciais? Cred, string? Erro)> CredenciaisDoClienteAsync(string slug)
    {
        if (CredCache.TryGetValue(slug, out var g) && DateTime.UtcNow - g.Em < TimeSpan.FromMinutes(5)) return (g.Cred, null);
        var l = await db.UmAsync(Sql.Fmt($"""
            select tc.ad_account_id, tc.squad_nome,
                   (select i.meta_access_token from public.trafego_integracoes i where upper(trim(i.squad_nome)) = upper(trim(coalesce(tc.squad_nome, ''))) and coalesce(i.meta_access_token, '') <> '' order by i.atualizado_em desc limit 1) as token,
                   (select i.meta_access_token from public.cliente_squads cs join public.squads s on s.squad_id = cs.squad_id
                      join public.trafego_integracoes i on upper(trim(i.squad_nome)) = upper(trim(s.squad_nome))
                      join public.clientes c on c.cliente_id = cs.cliente_id
                     where c.cliente_slug = tc.cliente_slug and cs.cliente_squad_ate is null and coalesce(i.meta_access_token, '') <> ''
                     order by i.atualizado_em desc limit 1) as token_dos_squads
            from public.trafego_contas tc where tc.cliente_slug = {slug}
            """), r => new { Act = r.S("ad_account_id"), Squad = r.S("squad_nome"), Token = r.S("token"), TokenDosSquads = r.S("token_dos_squads") });
        if (l is null || string.IsNullOrWhiteSpace(l.Act))
            return (null, "sem conta de anúncio: este cliente não tem ad_account_id cadastrado (nem no V1, nem no OS antigo). Vincule a conta no Setup pra ver as campanhas.");
        var token = string.IsNullOrWhiteSpace(l.Squad) ? l.Token ?? l.TokenDosSquads : l.Token;
        if (string.IsNullOrWhiteSpace(token))
            return (null, $"sem token Meta: a conta de anúncio existe, mas o squad{(l.Squad is not null ? $" {l.Squad}" : "")} não tem token gravado. Cole o token no Setup.");
        var cred = new Credenciais(token, GraphRegras.ComAct(l.Act.Trim()), l.Squad, mcp.SquadPeloMcp(l.Squad) ? "mcp" : "graph");
        CredCache[slug] = (DateTime.UtcNow, cred);
        return (cred, null);
    }

    /// <summary>A trava de escrita (AD-MT-20) com o MCP: a variável ligada E (a conta de teste OU o squad liberado no MCP).</summary>
    public bool EscritaLiberada(Credenciais cred) => McpRegras.EscritaLiberada(graph.VariavelDeEscritaLigada, graph.ContasTeste, cred.Act, mcp.Squads, cred.Squad);

    /// <summary>A conta do cliente, como o MCP a vê. Lança ErroDaMeta quando o MCP não responde.</summary>
    public async Task<(bool Ok, string Motivo, ContaNoMcp? Conta)> ContaServeNoMcpAsync(Credenciais cred, string conversa)
    {
        var conta = await mcp.ContaAsync(cred.Token, cred.Act, conversa);
        var (ok, motivo) = McpRegras.ContaServe(conta, cred.Act);
        return (ok, motivo, conta);
    }

    /* ---- a saúde do token (3.7) ----------------------------------------------- */

    /// <summary>`/debug_token` com o app (Meta:AppId/AppSecret): válido, sem vencer em 7 dias, com os escopos do motor.
    /// SEM APP no ambiente não há debug_token: sai `nao_conferida` e NÃO barra.</summary>
    public async Task<SaudeDoToken> SaudeDoTokenAsync(string token)
    {
        var app = graph.App;
        if (app is null) return new("nao_conferida", null, null, null, [], [], "sem Meta:AppId/Meta:AppSecret neste ambiente: a saúde do token não foi conferida");
        var chave = $"{token.Length}:{token[^Math.Min(8, token.Length)..]}";
        if (SaudeCache.TryGetValue(chave, out var g) && DateTime.UtcNow - g.Em < TimeSpan.FromHours(1)) return g.Saude;
        try
        {
            var r = await graph.GetAsync($"debug_token?input_token={Uri.EscapeDataString(token)}", $"{app.Value.AppId}|{app.Value.AppSecret}", 15);
            if (!r.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object)
                return new("nao_conferida", null, null, null, [], [], "o debug_token falhou (sem dado)");
            var valido = d.TryGetProperty("is_valid", out var iv) && iv.ValueKind == JsonValueKind.True;
            var expires = d.TryGetProperty("expires_at", out var ex) && ex.ValueKind == JsonValueKind.Number ? ex.GetInt64() : 0;
            DateTime? venceEm = expires > 0 ? DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime : null;
            int? dias = venceEm is { } v ? (int)Math.Floor((v - DateTime.UtcNow).TotalDays) : null;
            var escopos = d.TryGetProperty("scopes", out var sc) && sc.ValueKind == JsonValueKind.Array ? sc.EnumerateArray().Select(x => x.ToString()).ToList() : new();
            var faltam = EscoposDoMotor.Where(e => !escopos.Contains(e)).ToList();
            string? motivo = !valido ? "a Meta diz que o token não é válido (revogado, senha trocada ou app desautorizado). Reconecte o squad no Setup."
                : dias is { } dd && dd < DiasMinimosAteVencer ? $"o token vence em {Math.Max(0, dd)} dia(s): troque antes que o automático pare. Token de Usuário do Sistema não vence."
                : faltam.Count > 0 ? $"o token não tem {string.Join(", ", faltam)}: sem isso o motor não escreve (ou não lê leads). Reconecte com os escopos do Setup."
                : null;
            var saude = new SaudeDoToken(motivo is null ? "serve" : "nao_serve", valido, venceEm, dias, escopos, faltam, motivo);
            SaudeCache[chave] = (DateTime.UtcNow, saude);
            return saude;
        }
        catch (Exception e)
        {
            return new("nao_conferida", null, null, null, [], [], $"o debug_token falhou ({MetaGraph.Sanear(e.Message)})");
        }
    }

    /* ---- o isolamento por cliente -------------------------------------------- */

    /// <summary>O objeto é lido na Graph com a credencial do cliente e o `account_id` dele tem de ser a conta do cliente.
    /// FALHA FECHADA: em verba de cliente, "não sei" e "não" são a mesma resposta.</summary>
    public async Task<(bool Ok, string Motivo)> AlvoPertenceAoClienteAsync(Credenciais cred, string id)
    {
        if (!IdDaMeta.IsMatch(id)) return (false, $"id \"{id}\" não tem forma de objeto da Meta");
        JsonElement d;
        try { d = await graph.GetAsync($"{id}?fields=account_id", cred.Token, 15); }
        catch (ErroDaMeta) { return (false, $"a Meta não reconhece o objeto {id} com a credencial deste cliente"); }
        catch (Exception) { return (false, "não consegui confirmar na Meta a quem o alvo pertence"); }
        var dono = GraphRegras.SemAct(MetaGraph.S(d, "account_id") ?? "");
        if (dono == "") return (false, $"o objeto {id} não declara conta — não dá pra confirmar o dono");
        if (dono != GraphRegras.SemAct(cred.Act)) return (false, $"o objeto {id} pertence a OUTRA conta de anúncio (act_{dono}), não à deste cliente");
        return (true, "");
    }

    /* ---- a leitura ao vivo ----------------------------------------------------- */

    public async Task<EstadoAoVivo> LerAoVivoAsync(Credenciais cred, string adsetId)
    {
        var lidoEm = DateTime.UtcNow;
        try
        {
            var tc = graph.GetAsync($"{adsetId}?fields=effective_status,daily_budget", cred.Token, 15);
            var ta = graph.GetAsync($"{cred.Act}?fields=account_status,disable_reason", cred.Token, 15);
            await Task.WhenAll(tc, ta);
            var c = tc.Result; var a = ta.Result;
            long? verba = long.TryParse(MetaGraph.S(c, "daily_budget"), out var v) && v > 0 ? v : null;
            int? status = int.TryParse(MetaGraph.S(a, "account_status"), out var s) ? s : null;
            int? motivo = int.TryParse(MetaGraph.S(a, "disable_reason"), out var m) ? m : null;
            return new EstadoAoVivo(lidoEm, new ConjuntoAoVivo(MetaGraph.S(c, "effective_status"), verba), new ContaAoVivo(status, motivo), null);
        }
        catch (Exception e) { return new EstadoAoVivo(lidoEm, null, null, MetaGraph.Sanear(e.Message)); }
    }

    /// <summary>O token tem `pages_manage_posts` concedido? Leitura que falha responde `false` (o caminho conservador).</summary>
    public async Task<bool> TemEscopoDePostAsync(string token)
    {
        try
        {
            var p = await graph.GetAsync("me/permissions", token, 15);
            return p.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
                && d.EnumerateArray().Any(x => MetaGraph.S(x, "permission") == Core.Publicar.StartRegras.EscopoPostNaPagina && MetaGraph.S(x, "status") == "granted");
        }
        catch { return false; }
    }

    /* ---- a escrita, pela única porta de baixo --------------------------------- */

    /// <summary>As verbas atuais do objeto, como a Graph as devolve (texto). `Lido = false` quando a leitura falhou.</summary>
    async Task<(bool Lido, string? Diaria, string? Vitalicia)> OrcamentoAtualAsync(string id, string token)
    {
        try
        {
            var j = await graph.GetAsync($"{id}?fields=daily_budget,lifetime_budget", token, 15);
            return (true, MetaGraph.S(j, "daily_budget"), MetaGraph.S(j, "lifetime_budget"));
        }
        catch { return (false, null, null); }
    }

    /// <summary>A Meta não respondeu ANTES do POST valendo: nada foi enviado, então é recusa limpa e temporária
    /// (a reserva reabre), e não "pode ter acontecido".</summary>
    static ResultadoMeta SemRespostaAntesDaEscrita(string onde, Exception e, string via = "graph") =>
        ResultadoMeta.Falha($"a Meta não respondeu {onde} ({MetaGraph.Sanear(e.Message)}): nada foi enviado valendo. Tente de novo em instantes.", false, ClasseDoErro.Temporario, 60_000, via: via);

    public async Task<ResultadoMeta> AplicarNaMetaAsync(Credenciais cred, string tipo, string? alvo, IReadOnlyDictionary<string, object?> parametros)
    {
        /* ---- ANTI-BAN: salto de verba acima de 5× não passa, em qualquer campo de orçamento (GraphRegras.SaltoDeVerba) ---- */
        if (GraphRegras.OrcamentoPedido(parametros) is not null && !string.IsNullOrEmpty(alvo))
        {
            var (lido, diaria, vitalicia) = await OrcamentoAtualAsync(alvo, cred.Token);
            var (veredito, frase) = GraphRegras.SaltoDeVerba(parametros, lido, diaria, vitalicia);
            switch (veredito)
            {
                case GraphRegras.VereditoDaVerba.SemLeitura: return ResultadoMeta.Falha(frase!, false, ClasseDoErro.Temporario, 60_000);
                case GraphRegras.VereditoDaVerba.SemVerbaDoMesmoTipo: return ResultadoMeta.Falha(frase!, false, ClasseDoErro.Permanente);
                case GraphRegras.VereditoDaVerba.SaltoGrande: return ResultadoMeta.Falha(frase!, false);
            }
        }

        var montado = GraphRegras.Montar(tipo, alvo, parametros, cred.Act);
        if (montado is null) return ResultadoMeta.Falha($"ação \"{tipo}\" não é executada por aqui.", false);
        var (caminho, corpo) = montado.Value;

        /* ---- 1. ensaio: `validate_only` em /copies não é suportado pela Meta ---- */
        var ensaiavel = !caminho.EndsWith("/copies");
        if (ensaiavel)
        {
            try { await graph.PostAsync(caminho, corpo, cred.Token, validarSo: true, segundos: 30); }
            catch (ErroDaMeta e) { return Falha(e, false, 0); }
            catch (Exception e) { return SemRespostaAntesDaEscrita("ao ensaio", e); }
        }

        /* ---- 1b. fora da produção, para aqui (AD-MT-20) ------------------------- */
        var noMcp = cred.PeloMcp ? McpRegras.TraduzirAcao(tipo, parametros) : null;
        var via = noMcp is not null ? "mcp" : "graph";
        if (!EscritaLiberada(cred))
            return ResultadoMeta.Falha(ensaiavel
                ? "fora da produção: só ensaio, nada foi alterado"
                : "fora da produção: só ensaio, nada foi alterado (a Meta não ensaia cópia, então nem a chamada foi feita)", false, simulado: true, via: via);

        /* ---- 2a. valendo, pelo MCP oficial da Meta ----------------------------------- */
        if (noMcp is { } m)
        {
            var conversa = McpRegras.NovaConversa();
            /* a conta no MCP é LEITURA: falhar nela (rede, lista de contas) é antes de qualquer escrita */
            bool serve; string motivo;
            try { (serve, motivo, _) = await ContaServeNoMcpAsync(cred, conversa); }
            catch (ErroDaMeta e) { return Falha(e, false, 0) with { Via = "mcp" }; }
            catch (Exception e) { return SemRespostaAntesDaEscrita("ao conferir a conta no MCP", e, "mcp"); }
            if (!serve) return ResultadoMeta.Falha(motivo, false, ClasseDoErro.Permanente, via: "mcp");
            try
            {
                var comuns = McpRegras.Comuns(conversa, McpRegras.Atualizacao, $"{tipo} no objeto {alvo} da conta {GraphRegras.SemAct(cred.Act)}, a pedido do gestor");
                var args = m.Ferramenta == "ads_activate_entity"
                    ? McpRegras.ArgsDeAtivacao(cred.Act, m.TipoDaEntidade, alvo ?? "", null, false, comuns)
                    : McpRegras.ArgsDeAtualizacao(cred.Act, m.TipoDaEntidade, alvo ?? "", m.Campos, comuns);
                var r = await mcp.ChamarAsync(m.Ferramenta, args, cred.Token, 60);
                if (!r.Ok)
                {
                    var classe = McpRegras.ClasseDoErroDoMcp(r.Categoria, r.Repetivel, r.Erro);
                    return ResultadoMeta.Falha(McpRegras.FraseDoMcp(r.Erro), false, classe, classe == ClasseDoErro.Temporario ? 60_000 : 0, new ErroDaMetaInfo(r.Categoria, r.Erro, null), via: "mcp");
                }
                return new ResultadoMeta(true, r.S("entity_id") ?? alvo, true, null, null, true, false, null, 0) { Via = "mcp" };
            }
            catch (ErroDaMeta e) { return Falha(e, false, 0) with { Via = "mcp" }; }
        }

        /* ---- 2b. valendo, pela Graph (a cópia, e os squads fora do MCP) -------------- */
        RespostaGraph feito;
        try { feito = await graph.PostAsync(caminho, corpo, cred.Token, segundos: 30); }
        catch (ErroDaMeta e) { return Falha(e, ensaiavel, 0); }
        var id = MetaGraph.S(feito.Json, "copied_adset_id") ?? MetaGraph.S(feito.Json, "copied_campaign_id") ?? MetaGraph.S(feito.Json, "id");
        return new ResultadoMeta(true, id, true, null, null, true, false, null, feito.AguardarMs);
    }

    static ResultadoMeta Falha(ErroDaMeta e, bool alterou, int aguardarMs)
    {
        var info = new ErroDaMetaInfo(e.Titulo, e.MensagemAoUsuario, e.Codigo);
        var classe = e.Classe;
        var espera = aguardarMs > 0 || classe == ClasseDoErro.Temporario ? Math.Max(aguardarMs, 60_000) : 0;
        return ResultadoMeta.Falha(GraphRegras.FraseDoErro(e.Titulo, e.MensagemAoUsuario), alterou, classe, espera, info);
    }
}
