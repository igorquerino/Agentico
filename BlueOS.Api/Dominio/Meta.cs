using System.Globalization;
using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Meta;
using BlueOS.Core.Modelos;

namespace BlueOS.Api.Dominio;

/// <summary>A recusa da Graph numa leitura, com o código e o `is_transient`. Continua InvalidOperationException (a rota dos
/// formulários e o filtro "(código 10)" dependem disso); o código separa o token recusado do limite e da instabilidade.</summary>
public sealed class RecusaDaGraph(string frase, int? codigo, bool transitorio) : InvalidOperationException(frase)
{
    public int? Codigo { get; } = codigo;
    public bool Transitorio { get; } = transitorio;
    /// <summary>A Meta recusou o TOKEN (ou o acesso dele): só isso marca o token como inválido.</summary>
    public bool TokenRecusado => GraphRegras.ClasseDe(Codigo, Message, Transitorio) is ClasseDoErro.Credencial or ClasseDoErro.Permanente;
}

/* ============================================================================
   A GRAPH API DA META, só leitura por enquanto: os formulários de cadastro da
   página do cliente, com as perguntas e opções.
   ----------------------------------------------------------------------------
   O token é o do SQUAD do cliente (trafego_integracoes, como o V1 faz em
   credenciaisDoCliente), com `Meta:AccessToken` da configuração como reserva
   para o piloto. Os formulários pedem token DE PÁGINA: a API troca o token do
   usuário pelo da página (/{page_id}?fields=access_token), que só vem se a
   pessoa tiver a tarefa na página. A versão da Graph é uma só, como no V1.
   ============================================================================ */
public sealed class Meta(Db db, IHttpClientFactory fabrica, IConfiguration cfg)
{
    public const string Graph = "https://graph.facebook.com/v21.0";

    /// <summary>Os tokens que podem alcançar este cliente, na ordem de tentativa: o do squad do cliente, depois os dos outros
    /// squads (o Business Manager de uma pessoa costuma alcançar páginas de mais de um squad) e, por fim, Meta:AccessToken
    /// da configuração. Cada item é (origem, token).</summary>
    public async Task<List<(string Origem, string Token)>> TokensCandidatosAsync(Cliente c)
    {
        var squadsDoCliente = c.Squads.Concat([c.Squad]).Select(s => (s ?? "").Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        var linhas = await db.ListaAsync("select upper(trim(squad_nome)) as squad_nome, meta_access_token from public.trafego_integracoes where coalesce(meta_access_token, '') <> '' order by atualizado_em desc",
            r => (Origem: r.S("squad_nome") ?? "", Token: r.S("meta_access_token") ?? ""));
        var saida = linhas.Where(l => squadsDoCliente.Contains(l.Origem)).Concat(linhas.Where(l => !squadsDoCliente.Contains(l.Origem))).Where(l => l.Token.Length > 40).ToList();
        var cfgTok = cfg["Meta:AccessToken"];
        if (!string.IsNullOrWhiteSpace(cfgTok)) saida.Add(("configuração da API", cfgTok.Trim()));
        return saida;
    }

    /* ---- o token do squad, conferido na Meta --------------------------------------
       O fluxo só sai de Assets com o token do squad conferido. O V1 confere pela rota
       token-meta; aqui a API confere direto na Graph (GET /me) e guarda por 15 min
       por token, para a carteira (448 clientes, 9 squads) custar 9 chamadas, não 448. */
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Ok, DateTime Em)> TokensConferidos = new();

    /// <summary>O token responde ao `/me`? Só a recusa DO TOKEN (credencial, permissão) fica guardada como inválida por 15
    /// minutos; limite de taxa, instabilidade, resposta que não é JSON e timeout não são veredito sobre o token: valem o
    /// último veredito guardado, ou `false` sem guardar (a próxima chamada confere de novo).</summary>
    public async Task<bool> TokenValidoAsync(string token)
    {
        if (TokensConferidos.TryGetValue(token, out var v) && DateTime.UtcNow - v.Em < TimeSpan.FromMinutes(15)) return v.Ok;
        bool ok;
        try { var me = await Get("me?fields=id,name", token); ok = me.ValueKind == JsonValueKind.Object && me.TryGetProperty("id", out _); }
        catch (RecusaDaGraph e) when (e.TokenRecusado) { ok = false; }
        catch (Exception e) when (e is RecusaDaGraph or HttpRequestException or TaskCanceledException)
        {
            return TokensConferidos.TryGetValue(token, out var antigo) && antigo.Ok;
        }
        catch (InvalidOperationException) { ok = false; }
        TokensConferidos[token] = (ok, DateTime.UtcNow);
        return ok;
    }

    /// <summary>Marca em cada cliente se o token do squad dele está conferido na Meta (Conta.TokenValido).</summary>
    public async Task MarcarTokensAsync(IEnumerable<Cliente> clientes)
    {
        var lista = clientes.ToList();
        if (lista.Count == 0) return;
        var linhas = await db.ListaAsync("select upper(trim(squad_nome)) as squad_nome, meta_access_token, atualizado_em from public.trafego_integracoes where coalesce(meta_access_token, '') <> '' order by atualizado_em desc",
            r => (Squad: r.S("squad_nome") ?? "", Token: r.S("meta_access_token") ?? "", Em: r.Dt("atualizado_em")));
        var validade = new Dictionary<string, bool>();
        await Task.WhenAll(linhas.Select(l => l.Token).Distinct().Select(async t => { var ok = await TokenValidoAsync(t); lock (validade) validade[t] = ok; }));
        /* duas linhas do MESMO squad (caixa ou espaço diferentes) não derrubam a carteira: vale a mais recente */
        var porSquad = linhas.GroupBy(l => l.Squad).ToDictionary(g => g.Key, g => (Valido: validade.GetValueOrDefault(g.First().Token), g.First().Em));
        foreach (var c in lista)
        {
            var squads = c.Squads.Concat([c.Squad]).Select(s => (s ?? "").Trim().ToUpperInvariant()).Where(s => s.Length > 0);
            var achado = squads.Select(s => porSquad.TryGetValue(s, out var x) ? x : ((bool, DateTime?)?)null).FirstOrDefault(x => x is not null);
            if (achado is { } a) { c.Conta.TokenValido = a.Item1; c.Conta.TokenConferidoEm = a.Item2 ?? DateTime.Now; }
            else { c.Conta.TokenValido = null; }
        }
    }

    async Task<JsonElement> Get(string caminho, string token)
    {
        var http = fabrica.CreateClient("meta");
        var sep = caminho.Contains('?') ? "&" : "?";
        var r = await http.GetAsync($"{Graph}/{caminho}{sep}access_token={Uri.EscapeDataString(token)}");
        var texto = await r.Content.ReadAsStringAsync();
        JsonElement j;
        try { j = JsonDocument.Parse(texto).RootElement.Clone(); } catch { throw new RecusaDaGraph($"a Meta respondeu algo que não é JSON ({(int)r.StatusCode}).", null, true); }
        if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("error", out var e))
        {
            var msg = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() : texto;
            var code = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var cd) ? cd.GetRawText() : "?";
            var transitorio = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("is_transient", out var t) && t.ValueKind == JsonValueKind.True;
            throw new RecusaDaGraph($"a Meta recusou (código {code}): {msg}", int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null, transitorio);
        }
        return j;
    }

    public async Task<List<FormularioDaMeta>> FormulariosAsync(Cliente c)
    {
        if (string.IsNullOrWhiteSpace(c.Conta.PageId)) throw new InvalidOperationException("o cliente não tem página do Facebook no cadastro (trafego_contas.page_id): sem ela não há formulário para listar.");
        var candidatos = await TokensCandidatosAsync(c);
        if (candidatos.Count == 0) throw new InvalidOperationException($"sem token Meta gravado para nenhum squad: grave o token do squad {c.Squad} na etapa Assets.");
        /* o token de PÁGINA: só vem se a pessoa do token tem tarefa na página. Tenta o squad do cliente primeiro, depois os outros. */
        string? tokenDaPagina = null; var semTarefa = new List<string>(); var outros = new List<string>();
        foreach (var (origem, token) in candidatos)
        {
            try
            {
                var pagina = await Get($"{c.Conta.PageId}?fields=id,name,access_token", token);
                tokenDaPagina = pagina.TryGetProperty("access_token", out var pt) ? pt.GetString() : null;
                if (!string.IsNullOrEmpty(tokenDaPagina)) break;
                semTarefa.Add(origem);
            }
            /* código 10 (ou 200) é "a pessoa deste token não tem tarefa na página": não é erro de token, é acesso que falta */
            catch (InvalidOperationException e) when (e.Message.Contains("(código 10)") || e.Message.Contains("(código 200)")) { semTarefa.Add(origem); }
            catch (InvalidOperationException e) { outros.Add($"{origem}: {e.Message}"); }
        }
        if (string.IsNullOrEmpty(tokenDaPagina))
        {
            var quem = semTarefa.Count > 0 ? $" Nenhuma das pessoas dos tokens gravados ({string.Join(", ", semTarefa)}) tem tarefa nessa página." : "";
            var resto = outros.Count > 0 ? $" Outros tokens falharam por outro motivo: {string.Join("; ", outros.Take(2))}." : "";
            throw new InvalidOperationException(
                $"a página {c.Conta.PageId} não libera token de página para nenhum token gravado.{quem}{resto} " +
                $"Peça ao cliente para dar acesso à página (tarefa de Anunciante ou Administrador) à pessoa do token do squad {c.Squad} no Business Manager, ou cadastre no Setup uma página que ela já administre, e busque de novo.");
        }
        var forms = await Get($"{c.Conta.PageId}/leadgen_forms?fields=id,name,status,leads_count,created_time,questions{{key,label,type,options{{key,value}}}}&limit=100", tokenDaPagina);
        var saida = new List<FormularioDaMeta>();
        if (!forms.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return saida;
        foreach (var f in data.EnumerateArray())
        {
            var perguntas = new List<PerguntaDoFormulario>();
            if (f.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array)
                foreach (var q in qs.EnumerateArray())
                {
                    var opcoes = new List<OpcaoDoFormulario>();
                    if (q.TryGetProperty("options", out var os) && os.ValueKind == JsonValueKind.Array)
                        foreach (var o in os.EnumerateArray())
                            opcoes.Add(new OpcaoDoFormulario(S(o, "key") ?? S(o, "value") ?? "", S(o, "value") ?? S(o, "key") ?? ""));
                    perguntas.Add(new PerguntaDoFormulario(S(q, "key") ?? "", S(q, "label") ?? S(q, "key") ?? "", S(q, "type") ?? "CUSTOM", opcoes));
                }
            saida.Add(new FormularioDaMeta(S(f, "id") ?? "", S(f, "name") ?? "", S(f, "status") ?? "", (f.TryGetProperty("leads_count", out var lc) && lc.ValueKind == JsonValueKind.Number && lc.TryGetInt32(out var li) ? li : 0),
                DateTime.TryParse(S(f, "created_time"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt.ToLocalTime() : null, perguntas));
        }
        /* ativos primeiro, depois os que mais receberam leads */
        return saida.OrderBy(f => f.Status == "ACTIVE" ? 0 : 1).ThenByDescending(f => f.Leads).ToList();
    }

    static string? S(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
