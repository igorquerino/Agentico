using System.Globalization;
using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;

namespace BlueOS.Api.Dominio;

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

    async Task<JsonElement> Get(string caminho, string token)
    {
        var http = fabrica.CreateClient("meta");
        var sep = caminho.Contains('?') ? "&" : "?";
        var r = await http.GetAsync($"{Graph}/{caminho}{sep}access_token={Uri.EscapeDataString(token)}");
        var texto = await r.Content.ReadAsStringAsync();
        JsonElement j;
        try { j = JsonDocument.Parse(texto).RootElement.Clone(); } catch { throw new InvalidOperationException($"a Meta respondeu algo que não é JSON ({(int)r.StatusCode})."); }
        if (j.TryGetProperty("error", out var e))
        {
            var msg = e.TryGetProperty("message", out var m) ? m.GetString() : texto;
            var code = e.TryGetProperty("code", out var cd) ? cd.GetRawText() : "?";
            throw new InvalidOperationException($"a Meta recusou (código {code}): {msg}");
        }
        return j;
    }

    public async Task<List<FormularioDaMeta>> FormulariosAsync(Cliente c)
    {
        if (string.IsNullOrWhiteSpace(c.Conta.PageId)) throw new InvalidOperationException("o cliente não tem página do Facebook no cadastro (trafego_contas.page_id): sem ela não há formulário para listar.");
        var candidatos = await TokensCandidatosAsync(c);
        if (candidatos.Count == 0) throw new InvalidOperationException($"sem token Meta gravado para nenhum squad: grave o token do squad {c.Squad} na etapa Assets.");
        /* o token de PÁGINA: só vem se a pessoa do token tem tarefa na página. Tenta o squad do cliente primeiro, depois os outros. */
        string? tokenDaPagina = null; var tentativas = new List<string>();
        foreach (var (origem, token) in candidatos)
        {
            try
            {
                var pagina = await Get($"{c.Conta.PageId}?fields=id,name,access_token", token);
                tokenDaPagina = pagina.TryGetProperty("access_token", out var pt) ? pt.GetString() : null;
                if (!string.IsNullOrEmpty(tokenDaPagina)) break;
                tentativas.Add($"{origem}: a Meta não devolveu token de página");
            }
            catch (InvalidOperationException e) { tentativas.Add($"{origem}: {e.Message}"); }
        }
        if (string.IsNullOrEmpty(tokenDaPagina))
            throw new InvalidOperationException($"nenhum token gravado administra a página {c.Conta.PageId} ({string.Join("; ", tentativas)}). Peça acesso à página no Business Manager do cliente.");
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
