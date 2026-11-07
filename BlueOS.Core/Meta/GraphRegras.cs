using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueOS.Core.Meta;

/* ============================================================================
   AS REGRAS DA GRAPH API DA META, sem rede: porta de lib/trafego/graph.ts,
   aplicar-meta.ts (a classe do erro, o header BUC, o corpo de cada ação, a
   trava de escrita fora da produção) e criar-meta.ts (a frase da Meta).
   ----------------------------------------------------------------------------
   Puro de propósito: o teste cobra cada regra sem tocar a Meta. Quem fala HTTP
   é BlueOS.Api/Infra/MetaGraph.cs, que só traduz a resposta para estes tipos.
   ============================================================================ */
public enum ClasseDoErro { Temporario, Permanente, Credencial, Desconhecido }

/// <summary>A recusa da Graph, com o código junto da frase: a frase é para gente; o código separa o erro
/// que passa (limite, instabilidade) do que não passa (parâmetro, permissão, objeto que não existe).</summary>
public sealed class ErroDaMeta : Exception
{
    public int? Codigo { get; }
    public int? Subcodigo { get; }
    public bool Transitorio { get; }
    public string? Titulo { get; }
    public string? MensagemAoUsuario { get; }
    public ClasseDoErro Classe => GraphRegras.ClasseDe(Codigo, Message, Transitorio);

    public ErroDaMeta(string frase, int? codigo = null, int? subcodigo = null, bool transitorio = false, string? titulo = null, string? mensagemAoUsuario = null)
        : base(frase)
    {
        Codigo = codigo; Subcodigo = subcodigo; Transitorio = transitorio; Titulo = titulo; MensagemAoUsuario = mensagemAoUsuario;
    }

    /// <summary>O bloco `error` da resposta da Graph.</summary>
    public static ErroDaMeta De(JsonElement erro)
    {
        string? S(string n) => erro.ValueKind == JsonValueKind.Object && erro.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? N(string n) => erro.ValueKind == JsonValueKind.Object && erro.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
        var transitorio = erro.ValueKind == JsonValueKind.Object && erro.TryGetProperty("is_transient", out var t) && t.ValueKind == JsonValueKind.True;
        var msg = S("message"); var userMsg = S("error_user_msg"); var titulo = S("error_user_title");
        return new ErroDaMeta(GraphRegras.FraseDaMeta(msg, userMsg, titulo), N("code"), N("error_subcode"), transitorio, titulo, userMsg ?? msg);
    }
}

public static class GraphRegras
{
    /// <summary>A versão da Graph, num lugar só (lib/trafego/graph.ts, 3.7).</summary>
    public const string Versao = "v21.0";
    public const string Graph = $"https://graph.facebook.com/{Versao}";

    /// <summary>O muro do app em modo de desenvolvimento: a Meta recusa CRIAR CRIATIVO, e só o criativo.</summary>
    public const int SubcodigoAppEmDesenvolvimento = 1885183;

    public static readonly JsonSerializerOptions JsonComoJs = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static readonly HashSet<int> Permanentes = [100, 10, 200, 294, 368, 803, 1487000, 1487390, 2635];
    /* 80000-80014: os limites por caso de uso (BUC) da Meta, cada família de endpoint com o seu código. */
    static readonly HashSet<int> Temporarios = [1, 2, 4, 17, 32, 341, 613, 80000, 80001, 80002, 80003, 80004, 80005, 80006, 80008, 80009, 80014];
    static readonly HashSet<int> Credencial = [190, 102, 463, 467];
    static readonly Regex TemporarioPelaFrase = new("rate limit|too many calls|temporarily unavailable|try again later", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AppEmDesenvolvimentoPelaFrase = new("modo de desenvolvimento|development mode", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NaoChave = new("[^a-z0-9à-ú]+", RegexOptions.Compiled);

    /// <summary>A frase que a Meta escreveu PARA O USUÁRIO vem primeiro; o título só entra quando acrescenta algo.</summary>
    public static string FraseDaMeta(string? message, string? mensagemAoUsuario, string? titulo)
    {
        var msg = mensagemAoUsuario ?? message ?? "";
        var tit = titulo ?? "";
        static string Chave(string t) { var c = NaoChave.Replace(t.ToLowerInvariant(), " ").Trim(); return c.Length > 40 ? c[..40] : c; }
        var chaveTit = Chave(tit);
        var repete = tit != "" && Chave(msg).StartsWith(chaveTit.Length > 30 ? chaveTit[..30] : chaveTit, StringComparison.Ordinal);
        var partes = new[] { repete ? "" : tit, msg }.Where(x => !string.IsNullOrEmpty(x));
        var frase = string.Join(" — ", partes);
        return (frase == "" ? "a Meta recusou" : frase) + DicaDaMeta(msg);
    }

    /// <summary>O que a mensagem da Meta não diz: onde se resolve.</summary>
    public static string DicaDaMeta(string msg)
    {
        var m = msg.ToLowerInvariant();
        if (m.Contains("modo de desenvolvimento") || m.Contains("development mode"))
            return " Isso é do app da Meta que gerou o token desta conta, não da campanha: em developers.facebook.com, "
                 + "no app, mude o modo de \"Desenvolvimento\" para \"Ativo\" (App Mode → Live). Depois é só criar o anúncio de novo.";
        return "";
    }

    /// <summary>A frase que a tela mostra quando a Meta recusou uma ação.</summary>
    public static string FraseDoErro(string? titulo, string? msg)
    {
        var frase = string.Join(" — ", new[] { titulo, msg }.Where(x => !string.IsNullOrEmpty(x)));
        return frase == "" ? "a Meta recusou a ação, sem detalhe." : frase;
    }

    /// <summary>A classe do erro pelo código da Graph (Tráfego 3.5). Desconhecido reabre a reserva: esconder
    /// erro novo atrás de "permanente" seria pior. `is_transient` da Meta vale como Temporário, salvo credencial.</summary>
    public static ClasseDoErro ClasseDe(int? codigo, string? msg, bool transitorio = false)
    {
        if (codigo is { } cr && Credencial.Contains(cr)) return ClasseDoErro.Credencial;
        if (transitorio) return ClasseDoErro.Temporario;
        if (codigo is { } c)
        {
            if (Temporarios.Contains(c)) return ClasseDoErro.Temporario;
            if (Permanentes.Contains(c)) return ClasseDoErro.Permanente;
            if (c is >= 200 and <= 299) return ClasseDoErro.Permanente;
        }
        if (TemporarioPelaFrase.IsMatch(msg ?? "")) return ClasseDoErro.Temporario;
        return ClasseDoErro.Desconhecido;
    }

    /// <summary>O header `x-business-use-case-usage`: quanto a Meta quer que se espere, em ms. Acima de 100% em
    /// qualquer medida a próxima chamada falha, então parar antes é o que o header existe para pedir.</summary>
    public static int EsperaPeloBUCMs(string? cabecalho)
    {
        if (string.IsNullOrWhiteSpace(cabecalho)) return 0;
        JsonElement uso;
        try { uso = JsonDocument.Parse(cabecalho).RootElement; } catch { return 0; }
        if (uso.ValueKind != JsonValueKind.Object) return 0;
        long ms = 0;
        foreach (var conta in uso.EnumerateObject())
        {
            if (conta.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var u in conta.Value.EnumerateArray())
            {
                static double N(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                var regain = N(u, "estimated_time_to_regain_access");
                if (regain > 0) ms = Math.Max(ms, (long)(regain * 60_000));
                var pct = Math.Max(N(u, "call_count"), Math.Max(N(u, "total_cputime"), N(u, "total_time")));
                if (pct >= 100) ms = Math.Max(ms, 5 * 60_000);
            }
        }
        return (int)Math.Min(ms, int.MaxValue);
    }

    /// <summary>AD-MT-20: fora da produção não se escreve na Meta. A liberação exige DUAS coisas: a variável ligada
    /// E a conta na lista de contas de teste. Uma só seria o acidente de sempre.</summary>
    public static bool EscritaLiberada(bool variavelLigada, string? contasTeste, string act)
    {
        if (!variavelLigada) return false;
        var lista = (contasTeste ?? "").Split(',').Select(s => SemAct(s.Trim())).Where(s => s.Length > 0).ToList();
        if (lista.Count == 0) return false;
        return lista.Contains(SemAct(act));
    }

    public static string SemAct(string act) => act.StartsWith("act_", StringComparison.OrdinalIgnoreCase) ? act[4..] : act;
    public static string ComAct(string act) => act.StartsWith("act_", StringComparison.OrdinalIgnoreCase) ? act : $"act_{act}";

    /// <summary>`true` quando a recusa é o muro do App Mode (subcode 1885183), e não erro do nosso corpo.</summary>
    public static bool EhAppEmDesenvolvimento(Exception? e)
    {
        if (e is not ErroDaMeta g) return false;
        if (g.Subcodigo == SubcodigoAppEmDesenvolvimento) return true;
        if (g.Subcodigo is not null) return false;
        return g.Codigo == 100 && AppEmDesenvolvimentoPelaFrase.IsMatch(g.Message);
    }

    /// <summary>Valor da Graph: texto sempre. Objeto e lista viram JSON, que é como `targeting` viaja.</summary>
    public static string Texto(object? v) => v switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        int or long or short => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        JsonElement j => j.ValueKind == JsonValueKind.String ? j.GetString() ?? "" : j.GetRawText(),
        _ => JsonSerializer.Serialize(v, JsonComoJs),
    };

    /// <summary>O orçamento que ESTA ação quer gravar, em centavos, ou null quando não mexe em verba. Por CAMPO, e
    /// não por tipo: ação nova que mexa em verba e não passe por aqui volta a furar o anti-ban.</summary>
    public static long? OrcamentoPedido(IReadOnlyDictionary<string, object?> parametros) => OrcamentoPedidoComTipo(parametros)?.Centavos;

    /// <summary>O orçamento pedido e se ele é DIÁRIO (`daily_budget*`) ou vitalício (`lifetime_budget*`).</summary>
    public static (long Centavos, bool Diario)? OrcamentoPedidoComTipo(IReadOnlyDictionary<string, object?> parametros)
    {
        foreach (var chave in new[] { "daily_budget_cents", "daily_budget", "lifetime_budget_cents", "lifetime_budget" })
        {
            if (!parametros.TryGetValue(chave, out var v) || v is null) continue;
            if (double.TryParse(Texto(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n > 0) return ((long)Math.Round(n), chave.StartsWith("daily_", StringComparison.Ordinal));
        }
        return null;
    }

    public enum VereditoDaVerba { Passa, SemLeitura, SemVerbaDoMesmoTipo, SaltoGrande }

    /// <summary>ANTI-BAN (aplicar-meta.ts): o salto de verba acima de 5× não passa. A comparação é da verba do MESMO tipo
    /// (diária com diária, vitalícia com vitalícia): a Graph devolve "0" na diária de quem tem verba vitalícia, e não
    /// devolve nenhuma em conjunto de campanha CBO. Falha FECHADA: sem ler a verba atual não passa (Temporário, a leitura
    /// pode voltar); lida e sem verba do tipo pedido também não (o objeto não tem de onde subir: repetir não conserta).</summary>
    public static (VereditoDaVerba Veredito, string? Frase) SaltoDeVerba(IReadOnlyDictionary<string, object?> parametros, bool lido, string? diariaAtual, string? vitaliciaAtual)
    {
        if (OrcamentoPedidoComTipo(parametros) is not { } pedido) return (VereditoDaVerba.Passa, null);
        if (!lido)
            return (VereditoDaVerba.SemLeitura, "não consegui ler a verba atual do conjunto na Meta: mudar verba sem saber de onde é risco de bloqueio da conta. Nada foi alterado; tente de novo em instantes.");
        var texto = pedido.Diario ? diariaAtual : vitaliciaAtual;
        long? atual = long.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) && a > 0 ? a : null;
        if (atual is null)
            return (VereditoDaVerba.SemVerbaDoMesmoTipo, pedido.Diario
                ? "o conjunto não tem verba diária própria na Meta (está numa campanha CBO, ou tem verba vitalícia): não há de onde subir a diária, nem a dele nem a da cópia. Nada foi alterado; rode o motor de novo para a recomendação sair sem verba própria."
                : "o objeto não tem verba vitalícia na Meta: não há de onde subir a verba vitalícia. Nada foi alterado.");
        if (pedido.Centavos > atual.Value * 5)
        {
            var pct = ((double)pedido.Centavos / atual.Value * 100 - 100).ToString("0", CultureInfo.InvariantCulture);
            return (VereditoDaVerba.SaltoGrande, $"aumento de verba grande demais num passo só (+{pct}%) — risco de bloqueio da conta. Suba em incrementos de até 5x.");
        }
        return (VereditoDaVerba.Passa, null);
    }

    /// <summary>A resposta da Graph em JSON. O bloco `error` vira ErroDaMeta; e um status fora de 2xx SEM o bloco (corpo
    /// vazio, JSON sem erro) também, porque tratar 5xx vazio como sucesso dava pausa 'aplicada' com o conjunto gastando.</summary>
    public static JsonElement LerResposta(string? texto, int status)
    {
        JsonElement j;
        int? Codigo() => status >= 500 || status == 429 ? 2 : null;
        try { j = JsonDocument.Parse(string.IsNullOrWhiteSpace(texto) ? "{}" : texto).RootElement.Clone(); }
        catch
        {
            var frase = $"a Meta respondeu algo que não é JSON ({status}).";
            throw new ErroDaMeta(frase, Codigo(), mensagemAoUsuario: frase);
        }
        if (j.ValueKind == JsonValueKind.Object && j.TryGetProperty("error", out var e)) throw ErroDaMeta.De(e);
        if (status is < 200 or > 299)
        {
            var frase = $"a Meta respondeu {status} sem dizer o erro: a ação não foi confirmada.";
            throw new ErroDaMeta(frase, Codigo(), mensagemAoUsuario: frase);
        }
        return j;
    }

    /// <summary>O caminho (relativo à Graph) e o corpo de cada ação. `null` = esta porta não executa.</summary>
    public static (string Caminho, Dictionary<string, string> Corpo)? Montar(string tipo, string? alvo, IReadOnlyDictionary<string, object?> parametros, string act)
    {
        var id = alvo ?? "";
        var conta = ComAct(act);
        static Dictionary<string, string> Status(string s) => new() { ["status"] = s };
        Dictionary<string, string> ComoTexto() => parametros.ToDictionary(kv => kv.Key, kv => Texto(kv.Value));
        switch (tipo)
        {
            case "pause_campaign": case "pause_adset": case "pause_ad":
                return (id, Status("PAUSED"));
            case "resume_campaign": case "resume_adset": case "resume_ad":
                return (id, Status("ACTIVE"));
            case "set_daily_budget":
            {
                var centavos = parametros.TryGetValue("daily_budget_cents", out var c) && c is not null ? c : parametros.GetValueOrDefault("daily_budget");
                return (id, new Dictionary<string, string> { ["daily_budget"] = Texto(centavos) });
            }
            case "update_campaign": case "update_adset": case "update_ad":
                return (id, ComoTexto());
            case "duplicate_campaign": case "duplicate_adset":
                /* A cópia NASCE PAUSADA, sempre: duplicar já é abrir leilão novo. */
                return ($"{id}/copies", new Dictionary<string, string> { ["deep_copy"] = "true", ["status_option"] = "PAUSED" });
            case "ativar_copia": case "ativar_copia_ad":
                /* O passo final de uma CRIAÇÃO do próprio motor (D1, Igor 11/10). Não é resume_adset: o motor nunca
                   reativa o que alguém pausou. `ativar_copia` é o conjunto da cópia; `ativar_copia_ad`, cada anúncio
                   dela (na Graph é o mesmo POST; no MCP o tipo da entidade muda). */
                return (id, Status("ACTIVE"));
            case "create_campaign":
                return ($"{conta}/campaigns", ComoTexto());
            case "create_adset":
            {
                var corpo = ComoTexto(); corpo["status"] = "PAUSED"; return ($"{conta}/adsets", corpo);
            }
            case "create_ad":
            {
                var corpo = ComoTexto(); corpo["status"] = "PAUSED"; return ($"{conta}/ads", corpo);
            }
            case "update_targeting":
                return (id, new Dictionary<string, string> { ["targeting"] = Texto(parametros.TryGetValue("targeting", out var t) && t is not null ? t : parametros) });
            default:
                return null;
        }
    }
}
