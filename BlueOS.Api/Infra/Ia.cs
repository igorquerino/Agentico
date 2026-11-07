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
   · duas cadeias de modelo, "criativo" (gerar) e "ajuste" (retoque), e só
     sobrecarga (529/503) passa ao modelo seguinte; outro erro sobe;
   · uma recusa (stop_reason refusal) vira erro com a categoria;
   · a chave vem de Segredos (ANTHROPIC_API_KEY, inclusive do .env do V1).
   Os modelos: Opus 5.5 para criar e Sonnet 5.5 para ajustar (o V1 usa a
   geração anterior, claude-opus-5 e claude-sonnet-5, que ficam de reserva).
   ============================================================================ */
public sealed record RespostaDaIa(string Texto, string Modelo, int TokensIn, int TokensOut, int LatenciaMs);

public sealed class Ia(IConfiguration cfg, Segredos segredos, ILogger<Ia> log)
{
    public const string Chave = "ANTHROPIC_API_KEY";
    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string[] Cadeia(string nivel) => nivel == "ajuste"
        ? (cfg["Ia:Ajuste"] ?? "claude-sonnet-5-5,claude-sonnet-5").Split(',').Select(s => s.Trim()).Where(s => s != "").ToArray()
        : (cfg["Ia:Criativo"] ?? "claude-opus-5-5,claude-opus-5").Split(',').Select(s => s.Trim()).Where(s => s != "").ToArray();

    public bool Configurada => segredos.Tem(Chave);
    public object Estado() => new { anthropic = segredos.Origem(Chave), criativo = Cadeia("criativo"), ajuste = Cadeia("ajuste") };

    AnthropicClient Cliente()
    {
        var chave = segredos.Ler(Chave) ?? throw new InvalidOperationException("sem ANTHROPIC_API_KEY: informe em Segredos:ANTHROPIC_API_KEY, no ambiente, ou aponte Segredos:Arquivos para o .env.local do V1.");
        return new AnthropicClient { ApiKey = chave, Timeout = TimeSpan.FromMinutes(6) };
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
            try
            {
                var r = await client.Messages.Create(new MessageCreateParams
                {
                    Model = modelo,
                    MaxTokens = maxTokens,
                    System = blocos,
                    OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = esquema } },
                    Messages = [new UserMessageParam { Content = user }],
                }, ct);
                var ms = (int)(DateTime.UtcNow - inicio).TotalMilliseconds;
                if (r.StopReason?.ToString() == "refusal")
                    throw new InvalidOperationException($"o modelo recusou o pedido{(r.StopDetails is { } d ? $" ({d.Category})" : "")}.");
                var texto = string.Concat(r.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
                if (string.IsNullOrWhiteSpace(texto)) throw new InvalidOperationException("o modelo devolveu uma resposta vazia.");
                var u = r.Usage;
                var tokensIn = (int)(u.InputTokens + (u.CacheReadInputTokens ?? 0) + (u.CacheCreationInputTokens ?? 0));
                return new RespostaDaIa(texto, modelo, tokensIn, (int)u.OutputTokens, ms);
            }
            catch (AnthropicApiException e) when ((int)e.StatusCode is 529 or 503)
            {
                log.LogWarning("modelo {Modelo} sobrecarregado ({Status}); tentando o seguinte da cadeia", modelo, e.StatusCode);
                ultimo = e;
            }
        }
        throw new InvalidOperationException("a Anthropic está sobrecarregada em toda a cadeia de modelos; tente de novo em instantes.", ultimo);
    }
}
