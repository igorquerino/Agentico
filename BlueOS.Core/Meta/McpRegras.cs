using System.Security.Cryptography;
using System.Text.Json;

namespace BlueOS.Core.Meta;

/* ============================================================================
   O MCP OFICIAL DA META (https://mcp.facebook.com/ads), sem rede: o que a API
   manda para cada ferramenta e como lê o que volta.
   ----------------------------------------------------------------------------
   POR QUE O MCP: o token do app de desenvolvedor não cria criativo nem post
   (o muro do App Mode); o token emitido pelos conectores de IA da Meta, com o
   escopo `ads_mcp_management`, escreve pelo MCP. O mesmo token continua
   servindo na Graph para o que o MCP NÃO oferece: o formulário de cadastro
   (`/{page}/leadgen_forms`), a cópia de conjunto (`/{adset}/copies`) e as
   leituras de isolamento e ao vivo.

   O MCP cria em RASCUNHO ("draft mode"): campanha, conjunto e anúncio nascem
   no rascunho do Gerenciador e só existem na conta depois do
   `ads_activate_entity` com `publish_as_active = false`, que os publica
   PAUSADOS. Quando o rascunho não está ligado para a conta, os objetos já
   nascem PAUSED e não há o que publicar.

   QUEM USA: Mcp:Squads lista os squads cujos clientes escrevem por aqui; o
   token é o da linha do squad em `trafego_integracoes`. A conta tem de estar
   no token E habilitada no MCP (`is_ads_mcp_enabled`, lançamento gradual).
   ============================================================================ */
public sealed record ContaNoMcp(string Id, string Nome, bool Habilitada, string? Status, bool Consultavel, string? MotivoDesabilitada, string? MotivoNaoConsultavel);
public sealed record PaginaNoMcp(string Id, string Nome, bool TosDeLeadAceito);

/// <summary>A resposta de uma ferramenta: `Dados` é o JSON do texto (ou o `structuredContent`), `Erro` é a frase quando `isError`.</summary>
public sealed record RespostaDoMcp(bool Ok, JsonElement Dados, string? Erro, string? Categoria, bool Repetivel, string? ConversaId)
{
    public string? S(string nome) => McpRegras.S(Dados, nome);
}

/// <summary>O que uma criação devolve: o id, o estado (PAUSED ao vivo, DRAFT no rascunho) e os erros do rascunho.</summary>
public sealed record ObjetoCriadoNoMcp(string? Id, string? Status, string? ErrosAtivos, string? LinkDoGerenciador)
{
    public bool Rascunho => string.Equals(Status, "DRAFT", StringComparison.OrdinalIgnoreCase);
}

public static class McpRegras
{
    public const string Url = "https://mcp.facebook.com/ads";
    public const string Escopo = "ads_mcp_management";
    public const string LinkDoTosDeLead = "https://www.facebook.com/legal/leadgen/tos";
    public const string Criacao = "create_and_launch_new_ads";
    public const string Atualizacao = "update_existing_campaigns";
    const string Alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>O id de conversa que o MCP exige em toda chamada: 20 caracteres, só letras e dígitos. Um por publicação ou aplicação.</summary>
    public static string NovaConversa()
    {
        Span<byte> b = stackalloc byte[20];
        RandomNumberGenerator.Fill(b);
        return string.Create(20, b.ToArray(), (dst, src) => { for (var i = 0; i < dst.Length; i++) dst[i] = Alfabeto[src[i] % Alfabeto.Length]; });
    }

    public static bool ConversaValida(string? id) => id is { Length: 20 } && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>O squad escreve pelo MCP? `Mcp:Squads` é uma lista separada por vírgula; a comparação ignora caixa e espaços.</summary>
    public static bool SquadPeloMcp(string? squadsLiberados, string? squad)
    {
        if (string.IsNullOrWhiteSpace(squadsLiberados) || string.IsNullOrWhiteSpace(squad)) return false;
        return squadsLiberados.Split(',').Select(s => s.Trim().ToUpperInvariant()).Where(s => s != "").Contains(squad.Trim().ToUpperInvariant());
    }

    /// <summary>A trava de escrita com o MCP: a variável ligada E (a conta na lista de teste OU o squad liberado no MCP).</summary>
    public static bool EscritaLiberada(bool variavelLigada, string? contasTeste, string act, string? squadsNoMcp, string? squad) =>
        variavelLigada && (GraphRegras.EscritaLiberada(true, contasTeste, act) || SquadPeloMcp(squadsNoMcp, squad));

    /// <summary>A conta serve para escrever pelo MCP? Falha FECHADA: fora do token ou fora do lançamento gradual, não.</summary>
    public static (bool Ok, string Motivo) ContaServe(ContaNoMcp? conta, string act)
    {
        var id = GraphRegras.SemAct(act);
        if (conta is null) return (false, $"a conta {id} não está no token do MCP da Meta: quem conectou o MCP não administra esta conta. Reconecte o MCP com o Business da conta, ou aguarde o cliente entrar no token.");
        if (!conta.Habilitada) return (false, $"a conta {id} ({conta.Nome}) ainda não está habilitada no MCP da Meta ({conta.MotivoDesabilitada ?? "lançamento gradual"}). Tente de novo em alguns dias.");
        if (!string.Equals(conta.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)) return (false, $"a conta {id} ({conta.Nome}) está {conta.Status ?? "sem status"} na Meta{(conta.MotivoNaoConsultavel is not null ? $": {conta.MotivoNaoConsultavel}" : "")}. Nada é escrito numa conta que não está ativa.");
        return (true, "");
    }

    /// <summary>A página do anúncio de cadastro aceitou os termos de lead da Meta? Sem isso o conjunto LEAD_GENERATION é recusado.</summary>
    public static (bool Ok, string Motivo) PaginaServe(IReadOnlyList<PaginaNoMcp> paginas, string pageId)
    {
        var p = paginas.FirstOrDefault(x => x.Id == pageId);
        if (p is null) return (false, $"a página {pageId} não aparece entre as páginas anunciadas por esta conta no MCP da Meta: confira o page_id do cliente no Setup.");
        if (!p.TosDeLeadAceito) return (false, $"a página \"{p.Nome}\" ({pageId}) ainda não aceitou os termos de anúncio de cadastro da Meta: aceite em {LinkDoTosDeLead} (como admin da página) e publique de novo.");
        return (true, "");
    }

    /* ---- os argumentos de cada ferramenta ---------------------------------------- */

    /// <summary>Os campos que TODA chamada leva: a conversa, o trabalho, o pedido em uma frase e se é agendado. O MCP recusa
    /// argumento que a ferramenta não declara, então `hide_ui` (sem widget) só vai nas criações e na atualização.</summary>
    public static Dictionary<string, object?> Comuns(string conversa, string trabalho, string pedido, bool agendado = false) => new()
    {
        ["client_conversation_id"] = conversa, ["advertiser_job"] = trabalho, ["advertiser_request"] = pedido, ["scheduled_task_run"] = agendado,
    };

    static Dictionary<string, object?> Com(Dictionary<string, object?> comuns, Dictionary<string, object?> proprios, bool semWidget = false)
    {
        var d = new Dictionary<string, object?>(proprios);
        foreach (var kv in comuns) d[kv.Key] = kv.Value;
        if (semWidget) d["hide_ui"] = true;
        return d;
    }

    /// <summary>`ads_create_campaign` a partir do corpo da Graph (StartRegras.CorpoDaCampanha): ABO, sem verba na campanha.
    /// `is_adset_budget_sharing_enabled` e `status` não existem no MCP (a campanha nasce PAUSED e ABO é não ter verba aqui).</summary>
    public static Dictionary<string, object?> ArgsDaCampanha(string act, IReadOnlyDictionary<string, string> corpoGraph, Dictionary<string, object?> comuns) => Com(comuns, new()
    {
        ["ad_account_id"] = GraphRegras.SemAct(act),
        ["campaign_name"] = corpoGraph.GetValueOrDefault("name") ?? "",
        ["objective"] = corpoGraph.GetValueOrDefault("objective") ?? "OUTCOME_LEADS",
        ["buying_type"] = corpoGraph.GetValueOrDefault("buying_type") ?? "AUCTION",
        ["special_ad_categories"] = corpoGraph.GetValueOrDefault("special_ad_categories") ?? "[]",
    }, semWidget: true);

    /// <summary>`ads_create_ad_set` a partir do corpo da Graph (StartRegras.CorpoDoConjunto). A verba é inteira em centavos; o
    /// targeting e o promoted_object vão como JSON em texto, como o MCP pede.</summary>
    public static Dictionary<string, object?> ArgsDoConjunto(string act, IReadOnlyDictionary<string, string> corpoGraph, Dictionary<string, object?> comuns)
    {
        var d = new Dictionary<string, object?>
        {
            ["ad_account_id"] = GraphRegras.SemAct(act),
            ["campaign_id"] = corpoGraph.GetValueOrDefault("campaign_id") ?? "",
            ["ad_set_name"] = corpoGraph.GetValueOrDefault("name") ?? "",
            ["billing_event"] = corpoGraph.GetValueOrDefault("billing_event") ?? "IMPRESSIONS",
            ["optimization_goal"] = corpoGraph.GetValueOrDefault("optimization_goal") ?? "LEAD_GENERATION",
            ["targeting"] = corpoGraph.GetValueOrDefault("targeting") ?? "{}",
        };
        if (long.TryParse(corpoGraph.GetValueOrDefault("daily_budget"), out var diaria) && diaria > 0) d["daily_budget"] = diaria;
        if (corpoGraph.TryGetValue("bid_strategy", out var bid) && bid != "") d["bid_strategy"] = bid;
        if (corpoGraph.TryGetValue("destination_type", out var dest) && dest != "") d["destination_type"] = dest;
        if (corpoGraph.TryGetValue("promoted_object", out var po) && po != "") d["promoted_object"] = po;
        return Com(comuns, d, semWidget: true);
    }

    /// <summary>`ads_create_ad` com o criativo INLINE: o MCP aceita `object_story_spec` (a arte, o texto e o formulário no botão)
    /// ou `object_story_id` (o post da página) dentro de `creative`, mais o nome e as UTMs do criativo.</summary>
    public static Dictionary<string, object?> ArgsDoAnuncio(string act, string adsetId, string nome, IReadOnlyDictionary<string, string> corpoDoCriativo, Dictionary<string, object?> comuns)
    {
        var criativo = new Dictionary<string, object?>();
        foreach (var kv in corpoDoCriativo)
        {
            if (kv.Key is "object_story_spec" or "call_to_action") criativo[kv.Key] = JsonDocument.Parse(kv.Value).RootElement.Clone();
            else criativo[kv.Key] = kv.Value;
        }
        return Com(comuns, new()
        {
            ["ad_account_id"] = GraphRegras.SemAct(act), ["ad_set_id"] = adsetId, ["ad_name"] = nome,
            ["creative"] = JsonSerializer.Serialize(criativo, GraphRegras.JsonComoJs),
        }, semWidget: true);
    }

    /// <summary>`ads_creative_upload_media` pela URL pública da arte no Storage.</summary>
    public static Dictionary<string, object?> ArgsDaImagem(string act, string url, string nome, Dictionary<string, object?> comuns) => Com(comuns, new()
    {
        ["ad_account_id"] = GraphRegras.SemAct(act), ["upload_source"] = "URL", ["media_type"] = "IMAGE", ["media_url"] = url, ["name"] = nome,
    });

    /// <summary>`ads_update_entity`: os campos com os NOMES DA GRAPH (status, daily_budget, name...), em JSON.</summary>
    public static Dictionary<string, object?> ArgsDeAtualizacao(string act, string tipoDaEntidade, string id, IReadOnlyDictionary<string, string> campos, Dictionary<string, object?> comuns)
    {
        var f = new Dictionary<string, object?>();
        foreach (var kv in campos) f[kv.Key] = kv.Key is "daily_budget" or "lifetime_budget" or "bid_amount" && long.TryParse(kv.Value, out var n) ? n : kv.Value;
        return Com(comuns, new()
        {
            ["ad_account_id"] = GraphRegras.SemAct(act), ["entity_type"] = tipoDaEntidade, ["entity_id"] = id,
            ["fields"] = JsonSerializer.Serialize(f, GraphRegras.JsonComoJs),
        }, semWidget: true);
    }

    /// <summary>`ads_activate_entity`: ao vivo, PAUSED → ACTIVE; com `objetos`, publica o rascunho (e `comoAtivo = false` os
    /// deixa como foram criados, PAUSADOS).</summary>
    public static Dictionary<string, object?> ArgsDeAtivacao(string act, string tipoDaEntidade, string id, IReadOnlyList<string>? objetos, bool comoAtivo, Dictionary<string, object?> comuns)
    {
        var d = new Dictionary<string, object?> { ["ad_account_id"] = GraphRegras.SemAct(act), ["entity_type"] = tipoDaEntidade, ["entity_id"] = id };
        if (objetos is { Count: > 0 }) { d["object_ids"] = objetos.ToList(); d["publish_as_active"] = comoAtivo; d["allow_partial_publish"] = false; d["ignore_validation_errors"] = false; }
        return Com(comuns, d);
    }

    public static Dictionary<string, object?> ArgsDasContas(string? cursor, Dictionary<string, object?> comuns)
    {
        var d = new Dictionary<string, object?> { ["limit"] = 100 };
        if (!string.IsNullOrEmpty(cursor)) d["cursor"] = cursor;
        return Com(comuns, d);
    }

    public static Dictionary<string, object?> ArgsDasPaginas(string act, Dictionary<string, object?> comuns) =>
        Com(comuns, new() { ["ad_account_id"] = GraphRegras.SemAct(act), ["limit"] = 100 });

    /* ---- as ações do executor no MCP ------------------------------------------------ */

    /// <summary>O tipo de entidade do MCP a partir do tipo da ação da Graph (`pause_adset` → `ad_set`).</summary>
    public static string TipoDaEntidade(string tipoGraph) =>
        tipoGraph.EndsWith("_campaign") ? "campaign" : tipoGraph.EndsWith("_ad") ? "ad" : "ad_set";

    /// <summary>A ação da porta (GraphRegras.Montar) traduzida para o MCP: `Ferramenta` e os campos, ou null quando o MCP não
    /// a oferece (duplicar e criar: ficam na Graph, com o mesmo token).</summary>
    public static (string Ferramenta, string TipoDaEntidade, Dictionary<string, string> Campos)? TraduzirAcao(string tipo, IReadOnlyDictionary<string, object?> parametros)
    {
        var entidade = TipoDaEntidade(tipo);
        switch (tipo)
        {
            case "pause_campaign": case "pause_adset": case "pause_ad":
                return ("ads_update_entity", entidade, new() { ["status"] = "PAUSED" });
            case "resume_campaign": case "resume_adset": case "resume_ad": case "ativar_copia":
                return ("ads_activate_entity", entidade, new());
            case "set_daily_budget":
            {
                var centavos = parametros.TryGetValue("daily_budget_cents", out var c) && c is not null ? c : parametros.GetValueOrDefault("daily_budget");
                return ("ads_update_entity", entidade, new() { ["daily_budget"] = GraphRegras.Texto(centavos) });
            }
            case "update_campaign": case "update_adset": case "update_ad":
                return ("ads_update_entity", entidade, parametros.ToDictionary(kv => kv.Key, kv => GraphRegras.Texto(kv.Value)));
            case "update_targeting":
                return ("ads_update_entity", entidade, new() { ["targeting"] = GraphRegras.Texto(parametros.TryGetValue("targeting", out var t) && t is not null ? t : parametros) });
            default:
                return null;
        }
    }

    /* ---- a resposta ------------------------------------------------------------------ */

    public static string? S(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v)
        ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.GetRawText() : null;

    /// <summary>O envelope JSON-RPC de `tools/call`: `result.isError` com a frase em `content[0].text`; no sucesso, o JSON do
    /// texto (ou `structuredContent`). Um `error` de JSON-RPC vira falha com a mensagem dele.</summary>
    public static RespostaDoMcp Ler(JsonElement rpc)
    {
        var vazio = JsonDocument.Parse("{}").RootElement.Clone();
        if (rpc.ValueKind != JsonValueKind.Object) return new(false, vazio, "o MCP respondeu algo que não é JSON-RPC", null, false, null);
        if (rpc.TryGetProperty("error", out var e))
            return new(false, vazio, $"o MCP recusou a chamada: {S(e, "message") ?? e.GetRawText()}", "RPC", false, null);
        if (!rpc.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Object) return new(false, vazio, "o MCP respondeu sem result", null, false, null);

        var texto = r.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
            ? string.Join("\n", c.EnumerateArray().Where(x => S(x, "type") == "text").Select(x => S(x, "text") ?? "")).Trim() : "";
        JsonElement dados = vazio;
        if (r.TryGetProperty("structuredContent", out var sc) && sc.ValueKind == JsonValueKind.Object) dados = sc.Clone();
        else if (texto.StartsWith('{')) { try { dados = JsonDocument.Parse(texto).RootElement.Clone(); } catch { } }
        var conversa = S(dados, "client_conversation_id");

        var isError = r.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
        var categoria = S(dados, "error_category");
        var repetivel = dados.TryGetProperty("is_retryable", out var ir) && ir.ValueKind == JsonValueKind.True;
        var erroNosDados = S(dados, "error_message") ?? S(dados, "error");
        if (isError || (dados.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) || (categoria is not null && erroNosDados is not null))
            return new(false, dados, (erroNosDados ?? texto) is { Length: > 0 } f ? f : "o MCP recusou sem dizer por quê", categoria, repetivel, conversa);
        return new(true, dados, null, null, false, conversa);
    }

    /// <summary>O objeto criado: o id (campo por ferramenta), o status e os erros do rascunho.</summary>
    public static ObjetoCriadoNoMcp ObjetoCriado(RespostaDoMcp r, string campoDoId) =>
        new(r.S(campoDoId), r.S("status"), r.S("active_errors") is { } ae && ae is not ("[]" or "null" or "") ? ae : null, r.S("ads_manager_url"));

    /// <summary>O hash da imagem subida pela URL: o MCP devolve `image_hash` (ou `hash`) no topo ou dentro do objeto da imagem.</summary>
    public static string? HashDaImagem(JsonElement dados)
    {
        if (dados.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in new[] { "image_hash", "hash" }) if (S(dados, n) is { Length: > 0 } h) return h;
        foreach (var p in dados.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Object && HashDaImagem(p.Value) is { } h) return h;
            if (p.Value.ValueKind == JsonValueKind.Array) foreach (var x in p.Value.EnumerateArray()) if (x.ValueKind == JsonValueKind.Object && HashDaImagem(x) is { } h2) return h2;
        }
        return null;
    }

    /// <summary>A classe do erro do MCP, pela categoria que ele declara; sem categoria, pela frase (as mesmas da Graph).</summary>
    public static ClasseDoErro ClasseDoErroDoMcp(string? categoria, bool repetivel, string? frase)
    {
        var cat = (categoria ?? "").ToUpperInvariant();
        if (cat.Contains("AUTH") || cat.Contains("PERMISSION") || cat.Contains("TOKEN")) return ClasseDoErro.Credencial;
        if (repetivel || cat.Contains("TRANSIENT") || cat.Contains("RATE") || cat.Contains("THROTTL")) return ClasseDoErro.Temporario;
        if (cat.Contains("VALIDATION") || cat.Contains("NOT_FOUND") || cat.Contains("INVALID")) return ClasseDoErro.Permanente;
        if (EhLancamentoGradual(frase)) return ClasseDoErro.Permanente;
        return GraphRegras.ClasseDe(null, frase);
    }

    public static bool EhLancamentoGradual(string? frase) => frase is not null && frase.Contains("gradually", StringComparison.OrdinalIgnoreCase) && frase.Contains("roll", StringComparison.OrdinalIgnoreCase);

    public static List<ContaNoMcp> Contas(JsonElement dados)
    {
        var saida = new List<ContaNoMcp>();
        if (dados.ValueKind != JsonValueKind.Object || !dados.TryGetProperty("ad_accounts", out var arr) || arr.ValueKind != JsonValueKind.Array) return saida;
        foreach (var c in arr.EnumerateArray())
        {
            var id = S(c, "ad_account_id"); if (string.IsNullOrEmpty(id)) continue;
            saida.Add(new ContaNoMcp(id, S(c, "ad_account_name") ?? id,
                c.TryGetProperty("is_ads_mcp_enabled", out var en) && en.ValueKind == JsonValueKind.True, S(c, "account_status"),
                c.TryGetProperty("is_queryable", out var q) && q.ValueKind == JsonValueKind.True, S(c, "is_ads_mcp_disabled_reason"), S(c, "not_queryable_reason")));
        }
        return saida;
    }

    public static string? ProximoCursor(JsonElement dados) => S(dados, "next_cursor") is { Length: > 0 } c ? c : null;

    public static List<PaginaNoMcp> Paginas(JsonElement dados)
    {
        var saida = new List<PaginaNoMcp>();
        if (dados.ValueKind != JsonValueKind.Object || !dados.TryGetProperty("pages", out var arr) || arr.ValueKind != JsonValueKind.Array) return saida;
        foreach (var p in arr.EnumerateArray())
        {
            var id = S(p, "page_id"); if (string.IsNullOrEmpty(id)) continue;
            saida.Add(new PaginaNoMcp(id, S(p, "page_name") ?? id, p.TryGetProperty("leadgen_tos_accepted", out var t) && t.ValueKind == JsonValueKind.True));
        }
        return saida;
    }

    /// <summary>A frase para o gestor: curta, sem o "AI client" do MCP, com a dica do lançamento gradual.</summary>
    public static string FraseDoMcp(string? erro)
    {
        var f = (erro ?? "").Trim();
        if (f == "") return "o MCP da Meta recusou";
        if (EhLancamentoGradual(f)) return "o MCP da Meta ainda não está liberado para esta conta (lançamento gradual): tente em alguns dias";
        return f.Length > 400 ? f[..400] + "…" : f;
    }
}
