using System.Collections.Concurrent;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace BlueOS.Api.Infra;

/* ============================================================================
   O MODELO DE TEXTO (Anthropic), por uma porta só: a porta de lib/copy/
   anthropic.ts e lib/ia/anthropic.ts do V1.
   ----------------------------------------------------------------------------
   · a resposta é JSON no ESQUEMA pedido (saída estruturada), nunca texto solto;
   · duas cadeias de modelo, "criativo" (gerar) e "ajuste" (retoque); sobrecarga
     (529/503) e limite (429) passam ao modelo seguinte; outro erro sobe;
   · o `stop_reason` é lido pelo valor (Raw): uma recusa vira erro com a
     categoria, e o JSON cortado no teto de tokens (max_tokens) vira erro claro,
     os dois com a resposta paga junto, para o `ia_uso` registrar o gasto;
   · um cliente por chave, reutilizado; SEM retentativa do SDK e com o tempo de
     espera abaixo dos 5 minutos do desktop: o servidor não continua gerando (e
     pagando) depois que o cliente desistiu; quem chama passa o RequestAborted;
   · a chave vem de Segredos (ANTHROPIC_API_KEY, inclusive do .env do V1).
   Os modelos: Opus 5.5 para criar e Sonnet 5.5 para ajustar (o V1 usa a
   geração anterior, claude-opus-5 e claude-sonnet-5, que ficam de reserva).
   ============================================================================ */
/// <summary>Uma resposta do modelo. `TokensIn` é a entrada "de auditoria" (cache incluído); `CacheLidos` e `CacheGravados`
/// são as parcelas do cache, que custam 10% e 125% da entrada (lib/ia/precos.ts).</summary>
public sealed record RespostaDaIa(string Texto, string Modelo, int TokensIn, int TokensOut, int LatenciaMs, int CacheLidos = 0, int CacheGravados = 0);

/// <summary>Uma falha do modelo. `Resposta` vem preenchida quando a chamada foi feita e paga (recusa, JSON cortado): o gasto
/// entra no `ia_uso` como erro.</summary>
public sealed class ErroDaIa(string mensagem, RespostaDaIa? resposta = null, Exception? causa = null) : InvalidOperationException(mensagem, causa)
{
    public RespostaDaIa? Resposta { get; } = resposta;
}

public sealed class Ia(IConfiguration cfg, Segredos segredos, ILogger<Ia> log)
{
    public const string Chave = "ANTHROPIC_API_KEY";
    /// <summary>Abaixo dos 5 minutos que o desktop espera (ApiNetApi._lento): o servidor desiste antes do cliente.</summary>
    public static readonly TimeSpan TempoDeEspera = TimeSpan.FromSeconds(270);
    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly ConcurrentDictionary<string, AnthropicClient> Clientes = new();

    public string[] Cadeia(string nivel) => nivel == "ajuste"
        ? (cfg["Ia:Ajuste"] ?? "claude-sonnet-5-5,claude-sonnet-5").Split(',').Select(s => s.Trim()).Where(s => s != "").ToArray()
        : (cfg["Ia:Criativo"] ?? "claude-opus-5-5,claude-opus-5").Split(',').Select(s => s.Trim()).Where(s => s != "").ToArray();

    public bool Configurada => segredos.Tem(Chave);
    public object Estado() => new { anthropic = segredos.Origem(Chave), criativo = Cadeia("criativo"), ajuste = Cadeia("ajuste") };

    AnthropicClient Cliente()
    {
        var chave = segredos.Ler(Chave) ?? throw new InvalidOperationException("sem ANTHROPIC_API_KEY: informe em Segredos:ANTHROPIC_API_KEY, no ambiente, ou aponte Segredos:Arquivos para o .env.local do V1.");
        /* um cliente por chave (a conexão HTTP é reaproveitada); a chave nova, se trocar, ganha o dela */
        return Clientes.GetOrAdd(chave, k => new AnthropicClient { ApiKey = k, Timeout = TempoDeEspera, MaxRetries = 0 });
    }

    /// <summary>O erro do SDK que passa ao modelo seguinte da cadeia: sobrecarga (529, 503) e limite (429).</summary>
    public static bool PassaAoSeguinte(int status) => status is 429 or 503 or 529;

    /// <summary>O que o `stop_reason` diz da resposta: null quando ela vale; a frase do erro quando não vale.</summary>
    public static string? ProblemaDoFim(string? stopReason, int maxTokens, string? categoria = null) => stopReason switch
    {
        "refusal" => $"o modelo recusou o pedido{(string.IsNullOrWhiteSpace(categoria) ? "" : $" ({categoria})")}.",
        "max_tokens" => $"a resposta do modelo passou do teto de {maxTokens} tokens e veio cortada: o JSON não fecha. Peça menos peças por lote.",
        _ => null,
    };

    static string? CategoriaDaRecusa(Message r)
    {
        try { return r.StopDetails?.Category?.Raw(); } catch { return null; }
    }

    /// <summary>Pede JSON no esquema. `system` são os blocos na ordem (o primeiro leva cache de prompt).</summary>
    public async Task<RespostaDaIa> PedirJsonAsync(IReadOnlyList<string> system, string user, Dictionary<string, object> schema, int maxTokens, string nivel = "criativo", CancellationToken ct = default)
    {
        var client = Cliente();
        var blocos = new List<TextBlockParam>();
        for (var i = 0; i < system.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(system[i])) continue;
            blocos.Add(i == 0 ? new TextBlockParam { Text = system[i], CacheControl = new CacheControlEphemeral() } : new TextBlockParam { Text = system[i] });
        }
        var schemaJson = JsonSerializer.SerializeToElement(schema, Json);
        var esquema = new Dictionary<string, JsonElement>();
        foreach (var p in schemaJson.EnumerateObject()) esquema[p.Name] = p.Value.Clone();

        Exception? ultimo = null;
        foreach (var modelo in Cadeia(nivel))
        {
            var inicio = DateTime.UtcNow;
            Message r;
            try
            {
                r = await client.Messages.Create(new MessageCreateParams
                {
                    Model = modelo,
                    MaxTokens = maxTokens,
                    System = blocos,
                    OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = esquema } },
                    Messages = [new UserMessageParam { Content = user }],
                }, ct);
            }
            catch (AnthropicApiException e) when (PassaAoSeguinte((int)e.StatusCode))
            {
                log.LogWarning("modelo {Modelo} indisponível ({Status}); tentando o seguinte da cadeia", modelo, e.StatusCode);
                ultimo = e;
                continue;
            }
            var ms = (int)(DateTime.UtcNow - inicio).TotalMilliseconds;
            var u = r.Usage;
            var lidos = (int)(u.CacheReadInputTokens ?? 0); var gravados = (int)(u.CacheCreationInputTokens ?? 0);
            var texto = string.Concat(r.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            var resposta = new RespostaDaIa(texto, modelo, (int)u.InputTokens + lidos + gravados, (int)u.OutputTokens, ms, lidos, gravados);
            /* pelo VALOR do enum: o ToString() do SDK devolve o JSON com aspas ("\"refusal\"") e a comparação nunca batia */
            string? fim;
            try { fim = r.StopReason?.Raw(); } catch (AnthropicInvalidDataException) { fim = null; }
            if (ProblemaDoFim(fim, maxTokens, CategoriaDaRecusa(r)) is { } problema) throw new ErroDaIa(problema, resposta);
            if (string.IsNullOrWhiteSpace(texto)) throw new ErroDaIa("o modelo devolveu uma resposta vazia.", resposta);
            return resposta;
        }
        throw new ErroDaIa("a Anthropic está sobrecarregada (ou no limite) em toda a cadeia de modelos; tente de novo em instantes.", null, ultimo);
    }
}

/* ============================================================================
   O USO DE IA (`ia_uso`), como o registrarUso do V1 (lib/designer/custo.ts e
   lib/ia/precos.ts): cada chamada, ok ou erro, com o ambiente DERIVADO do
   banco em uso (homologacao | producao | desconhecido, nunca o ref cru), o
   squad, os 4 últimos caracteres da chave (chave_ref) e o custo pela tabela
   única, com o cache cobrado pelos multiplicadores oficiais (leitura 10%,
   escrita 125% da entrada). Nunca lança: contabilidade não derruba o trabalho.
   ============================================================================ */
public static class UsoDaIa
{
    /// <summary>Os projetos Supabase do V1: é o que distingue os dois ambientes (custo.ts, ambienteAtual).</summary>
    public const string RefHomologacao = "yurhgwqilbgtkvoymyab";

    public static string Ambiente(string? refDoProjeto) =>
        string.Equals(refDoProjeto, RefHomologacao, StringComparison.OrdinalIgnoreCase) ? "homologacao"
        : string.Equals(refDoProjeto, Db.RefProducao, StringComparison.OrdinalIgnoreCase) ? "producao" : "desconhecido";

    /// <summary>custo.ts refDaChave: os 4 últimos caracteres da OPENAI_API_KEY, só para a telemetria.</summary>
    public static string? ChaveRef(string? chave) => chave is { Length: >= 4 } ? chave[^4..] : null;

    /// <summary>US$ por milhão de tokens (entrada, saída), por família do modelo. Fora da tabela: null (custa 0 e fica visível
    /// no registro, como no V1; nunca um preço inventado).</summary>
    public static (decimal Entrada, decimal Saida)? Preco(string? modelo) => (modelo ?? "").ToLowerInvariant() switch
    {
        var m when m.Contains("fable-5") || m.Contains("mythos-5") => (10m, 50m),
        var m when m.StartsWith("claude-opus-5-5") => (4m, 20m),
        var m when m.Contains("opus-5") || m.Contains("opus-4-6") || m.Contains("opus-4-7") || m.Contains("opus-4-8") => (5m, 25m),
        var m when m.Contains("opus") => (15m, 75m),
        var m when m.Contains("sonnet-5") => (2m, 10m),
        var m when m.Contains("sonnet") => (3m, 15m),
        var m when m.StartsWith("claude-haiku-5") => (0.1m, 0.5m),
        var m when m.Contains("haiku-4-5") => (1m, 5m),
        var m when m.Contains("haiku") => (0.8m, 4m),
        _ => null,
    };

    /// <summary>O custo de UMA chamada (precos.ts custoAnthropicUsd). `tokensIn` é a entrada de auditoria (cache incluído).</summary>
    public static decimal CustoAnthropicUsd(string? modelo, int tokensIn, int tokensOut, int cacheLidos = 0, int cacheGravados = 0)
    {
        if (Preco(modelo) is not { } p) return 0m;
        var semCache = Math.Max(0, tokensIn - cacheLidos - cacheGravados);
        var entrada = semCache * p.Entrada + cacheLidos * p.Entrada * 0.1m + cacheGravados * p.Entrada * 1.25m;
        return Math.Round((entrada + tokensOut * p.Saida) / 1_000_000m, 6);
    }

    public static async Task GravarAsync(Db db, Segredos segredos, ILogger log, Guid por, string? clienteId, string? squadId, string agente, string operacao, string? modelo, string status, string? erro,
        int latenciaMs, int tokensIn, int tokensOut, int imagens, decimal custo, string? provedor)
    {
        try
        {
            Guid? cliente = Guid.TryParse(clienteId, out var cg) ? cg : null;
            Guid? squad = Guid.TryParse(squadId, out var sg) ? sg : null;
            await db.ExecutarAsync(Sql.Fmt($"insert into public.ia_uso (user_id, squad_id, cliente_id, agente, operacao, modelo, status, erro, latencia_ms, tokens_in, tokens_out, imagens, custo_usd, ambiente, chave_ref, provedor) values ({por}, {squad}, {cliente}, {agente}, {operacao}, {modelo}, {status}, {(erro is { Length: > 500 } ? erro[..500] : erro)}, {latenciaMs}, {tokensIn}, {tokensOut}, {imagens}, {custo}, {Ambiente(db.Ref)}, {ChaveRef(segredos.Ler("OPENAI_API_KEY"))}, {provedor})"));
        }
        catch (Exception e) { log.LogWarning("ia_uso não gravou: {Erro}", e.Message); }
    }
}
