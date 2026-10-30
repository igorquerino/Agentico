using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace BlueOS.Api.Infra;

/* ============================================================================
   O BANCO, com DOIS transportes e uma trava.
   ----------------------------------------------------------------------------
   · Npgsql, pelo pooler do Supabase, quando `ConnectionStrings:BlueOS` tem a
     senha: é o alvo do plano (um join por tela, conexão persistente);
   · a Management API do Supabase (POST /v1/projects/{ref}/database/query) com
     `Supabase:AccessToken`, enquanto a senha do banco não chega: cada SQL vira
     UMA chamada HTTPS, por isso toda tela é uma consulta só, com JSON agregado.
     É o mesmo caminho que o gate de deploy do V1 usa para ler o banco.

   A TRAVA: a API só fala com o projeto cujo ref está em `Supabase:Url`, e
   recusa o ref de PRODUÇÃO (oaeqvfmvjahmlgdhpsrv) em qualquer transporte.
   Decisão do Igor em 29/10/2026: "contanto que não mexa na produção".

   Os valores entram no SQL como literais (a Management API não tem parâmetros):
   `Sql.Fmt($"... where slug = {slug}")` escapa cada argumento pelo tipo.
   ============================================================================ */
public interface ILinha
{
    string? S(string col);
    decimal? Dec(string col);
    long L(string col);
    bool? B(string col);
    DateTime? Dt(string col);
    DateOnly? Data(string col);
    List<string> Textos(string col);
    string? Json(string col);
}

public interface IBanco
{
    string Nome { get; }
    Task<List<ILinha>> ConsultarAsync(string sql);
    Task<int> ExecutarAsync(string sql);
    /// <summary>Vários statements numa transação (um round-trip quando o transporte deixa).</summary>
    Task ExecutarLoteAsync(IReadOnlyList<string> sqls);
}

public sealed class BancoNaoConfigurado(string msg) : InvalidOperationException(msg);

public readonly record struct Jsonb(string Texto);

public static class Sql
{
    /// <summary>Um valor C# como literal SQL, escapado pelo tipo. Nulo vira null.</summary>
    public static string Lit(object? v) => v switch
    {
        null => "null",
        string s => "'" + s.Replace("'", "''") + "'",
        bool b => b ? "true" : "false",
        int or long or short or byte => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double f => f.ToString("R", CultureInfo.InvariantCulture),
        DateOnly d => $"'{d:yyyy-MM-dd}'::date",
        DateTime dt => $"'{(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Local) : dt).ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss.fff'Z'}'::timestamptz",
        Guid g => $"'{g}'::uuid",
        Jsonb j => "'" + j.Texto.Replace("'", "''") + "'::jsonb",
        string[] arr => arr.Length == 0 ? "'{}'::text[]" : "array[" + string.Join(",", arr.Select(x => Lit(x))) + "]::text[]",
        Guid[] arr => arr.Length == 0 ? "'{}'::uuid[]" : "array[" + string.Join(",", arr.Select(x => $"'{x}'")) + "]::uuid[]",
        IEnumerable<string> lista => Lit(lista.ToArray()),
        _ => throw new ArgumentException($"tipo sem literal SQL: {v.GetType().Name}"),
    };

    /// <summary>Interpola os argumentos como literais. Chaves literais no SQL vão dobradas: '{{}}'.</summary>
    public static string Fmt(FormattableString f) =>
        string.Format(CultureInfo.InvariantCulture, f.Format, f.GetArguments().Select(Lit).ToArray());
}

/* ---- Npgsql ------------------------------------------------------------------- */

sealed class LinhaNpgsql(NpgsqlDataReader r) : ILinha
{
    object? V(string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetValue(i); }
    public string? S(string col) => V(col)?.ToString();
    public decimal? Dec(string col) => V(col) is { } v ? Convert.ToDecimal(v, CultureInfo.InvariantCulture) : null;
    public long L(string col) => V(col) is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
    public bool? B(string col) => V(col) is bool b ? b : null;
    public DateTime? Dt(string col) => V(col) is DateTime d ? (d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d) : null;
    public DateOnly? Data(string col) => V(col) switch { DateOnly d => d, DateTime dt => DateOnly.FromDateTime(dt), string s when DateOnly.TryParse(s, out var p) => p, _ => null };
    public List<string> Textos(string col) => V(col) is string[] arr ? arr.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : new();
    public string? Json(string col) => V(col)?.ToString();
}

public sealed class BancoNpgsql : IBanco
{
    readonly NpgsqlDataSource _fonte;
    public string Nome => "Npgsql (pooler do Supabase)";
    public BancoNpgsql(string cs)
    {
        var b = new NpgsqlDataSourceBuilder(cs);
        b.EnableDynamicJson();
        _fonte = b.Build();
    }
    public async Task<List<ILinha>> ConsultarAsync(string sql)
    {
        await using var cx = await _fonte.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, cx);
        await using var r = await cmd.ExecuteReaderAsync();
        var saida = new List<ILinha>();
        while (await r.ReadAsync()) saida.Add(new LinhaCopiada(r));
        return saida;
    }
    public async Task<int> ExecutarAsync(string sql)
    {
        await using var cx = await _fonte.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, cx);
        return await cmd.ExecuteNonQueryAsync();
    }
    public async Task ExecutarLoteAsync(IReadOnlyList<string> sqls)
    {
        await using var cx = await _fonte.OpenConnectionAsync();
        await using var tx = await cx.BeginTransactionAsync();
        foreach (var s in sqls) { await using var cmd = new NpgsqlCommand(s, cx, tx); await cmd.ExecuteNonQueryAsync(); }
        await tx.CommitAsync();
    }
    public async Task<(bool Ok, string? Erro)> ConfereAsync()
    {
        try { await ExecutarAsync("select 1"); return (true, null); } catch (Exception e) { return (false, e.Message); }
    }

    /// <summary>O reader morre com a conexão; a linha é copiada para um dicionário.</summary>
    sealed class LinhaCopiada : ILinha
    {
        readonly Dictionary<string, object?> _v = new(StringComparer.OrdinalIgnoreCase);
        public LinhaCopiada(NpgsqlDataReader r) { for (var i = 0; i < r.FieldCount; i++) _v[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i); }
        object? V(string col) => _v.GetValueOrDefault(col);
        public string? S(string col) => V(col)?.ToString();
        public decimal? Dec(string col) => V(col) is { } v ? Convert.ToDecimal(v, CultureInfo.InvariantCulture) : null;
        public long L(string col) => V(col) is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : 0;
        public bool? B(string col) => V(col) is bool b ? b : null;
        public DateTime? Dt(string col) => V(col) is DateTime d ? (d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d) : null;
        public DateOnly? Data(string col) => V(col) switch { DateOnly d => d, DateTime dt => DateOnly.FromDateTime(dt), string s when DateOnly.TryParse(s, out var p) => p, _ => null };
        public List<string> Textos(string col) => V(col) is string[] arr ? arr.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : new();
        public string? Json(string col) => V(col)?.ToString();
    }
}

/* ---- Management API ------------------------------------------------------------ */

sealed class LinhaJson(JsonElement o) : ILinha
{
    JsonElement? V(string col)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, col, StringComparison.OrdinalIgnoreCase)) return p.Value.ValueKind == JsonValueKind.Null ? null : p.Value;
        return null;
    }
    public string? S(string col) => V(col) switch { null => null, { ValueKind: JsonValueKind.String } v => v.GetString(), { } v => v.GetRawText() };
    public decimal? Dec(string col) => V(col) switch
    {
        null => null,
        { ValueKind: JsonValueKind.Number } v => v.GetDecimal(),
        { ValueKind: JsonValueKind.String } v => decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null,
        _ => null,
    };
    public long L(string col) => Dec(col) is { } d ? (long)d : 0;
    public bool? B(string col) => V(col) switch
    {
        null => null, { ValueKind: JsonValueKind.True } => true, { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } v => v.GetString() is "t" or "true", _ => null,
    };
    public DateTime? Dt(string col) => V(col) is { ValueKind: JsonValueKind.String } v && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d.ToLocalTime() : null;
    public DateOnly? Data(string col) => V(col) is { ValueKind: JsonValueKind.String } v && v.GetString() is { } s && DateOnly.TryParse(s.Length > 10 ? s[..10] : s, out var d) ? d : null;
    public List<string> Textos(string col)
    {
        var v = V(col);
        if (v is { ValueKind: JsonValueKind.Array } a) return a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText()).Where(x => x != "").ToList();
        /* text[] que veio como texto: {a,"b c"} */
        if (v is { ValueKind: JsonValueKind.String } s && s.GetString() is { } t && t.StartsWith('{') && t.EndsWith('}'))
            return t[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().Trim('"')).Where(x => x != "").ToList();
        return new();
    }
    public string? Json(string col) => V(col) switch { null => null, { ValueKind: JsonValueKind.String } v => v.GetString(), { } v => v.GetRawText() };
}

public sealed class BancoManagement : IBanco
{
    readonly HttpClient _http;
    readonly string _ref;
    public string Nome => $"Management API do Supabase (projeto {_ref})";

    public BancoManagement(HttpClient http, string projectRef, string accessToken)
    {
        _http = http; _ref = projectRef;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        _http.Timeout = TimeSpan.FromSeconds(90);
    }

    async Task<JsonElement> Rodar(string sql)
    {
        var r = await _http.PostAsJsonAsync($"https://api.supabase.com/v1/projects/{_ref}/database/query", new { query = sql });
        var texto = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode)
        {
            string msg;
            try { var j = JsonDocument.Parse(texto).RootElement; msg = j.TryGetProperty("message", out var m) ? m.GetString() ?? texto : texto; } catch { msg = texto; }
            throw new InvalidOperationException($"Management API ({(int)r.StatusCode}): {msg}");
        }
        return string.IsNullOrWhiteSpace(texto) ? default : JsonDocument.Parse(texto).RootElement.Clone();
    }

    public async Task<List<ILinha>> ConsultarAsync(string sql)
    {
        var j = await Rodar(sql);
        return j.ValueKind == JsonValueKind.Array ? j.EnumerateArray().Select(x => (ILinha)new LinhaJson(x)).ToList() : new();
    }

    public async Task<int> ExecutarAsync(string sql)
    {
        /* O endpoint não devolve "linhas afetadas": o RETURNING conta por nós. */
        var j = await Rodar(sql.TrimEnd().TrimEnd(';') + " returning 1");
        return j.ValueKind == JsonValueKind.Array ? j.GetArrayLength() : 0;
    }

    public async Task ExecutarLoteAsync(IReadOnlyList<string> sqls)
    {
        if (sqls.Count == 0) return;
        await Rodar("begin;\n" + string.Join(";\n", sqls.Select(s => s.TrimEnd().TrimEnd(';'))) + ";\ncommit;");
    }

    public async Task<(bool Ok, string? Erro)> ConfereAsync()
    {
        try { await ConsultarAsync("select 1 as um"); return (true, null); } catch (Exception e) { return (false, e.Message); }
    }
}

/* ---- a fachada ------------------------------------------------------------------ */

public sealed class Db(IConfiguration cfg, IHttpClientFactory fabrica)
{
    public const string RefProducao = "oaeqvfmvjahmlgdhpsrv";
    readonly object _trava = new();
    IBanco? _banco;
    string? _assinatura;

    public string Ref
    {
        get
        {
            var url = cfg["Supabase:Url"] ?? "";
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
            return host.Split('.')[0];
        }
    }

    public string Transporte => Resolver().Banco?.Nome ?? "nenhum";

    (IBanco? Banco, string? Erro) Resolver()
    {
        if (string.Equals(Ref, RefProducao, StringComparison.OrdinalIgnoreCase))
            return (null, "esta API não fala com a PRODUÇÃO (ref oaeqvfmvjahmlgdhpsrv): aponte Supabase:Url para a homologação.");
        var cs = cfg.GetConnectionString("BlueOS") ?? "";
        var senhaOk = cs.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            && cs.Split("Password=", 2, StringSplitOptions.None)[1].Split(';')[0].Trim() is { Length: > 0 } s && s != "SENHA_DO_BANCO" && s != "[YOUR-PASSWORD]";
        var token = cfg["Supabase:AccessToken"] ?? "";
        var assinatura = senhaOk ? "npgsql:" + cs : token != "" ? "mgmt:" + Ref + ":" + token.Length : "";
        if (assinatura == "") return (null, "falta a senha em ConnectionStrings:BlueOS ou o Supabase:AccessToken (appsettings.Local.json)");
        lock (_trava)
        {
            if (_banco is not null && _assinatura == assinatura) return (_banco, null);
            try
            {
                _banco = senhaOk ? new BancoNpgsql(cs) : new BancoManagement(fabrica.CreateClient("supabase-mgmt"), Ref, token);
                _assinatura = assinatura;
                return (_banco, null);
            }
            catch (Exception e) { return (null, $"banco não configurado: {e.Message}"); }
        }
    }

    IBanco Banco => Resolver() is (not null and var b, _) ? b! : throw new BancoNaoConfigurado(Resolver().Erro ?? "banco não configurado");

    public async Task<(bool Ok, string? Erro)> ConfereAsync()
    {
        var (b, erro) = Resolver();
        if (b is null) return (false, erro);
        return b switch { BancoNpgsql n => await n.ConfereAsync(), BancoManagement m => await m.ConfereAsync(), _ => (false, "transporte desconhecido") };
    }

    public Task<List<ILinha>> ListaAsync(string sql) => Banco.ConsultarAsync(sql);
    public async Task<List<T>> ListaAsync<T>(string sql, Func<ILinha, T> ler) => (await Banco.ConsultarAsync(sql)).Select(ler).ToList();
    public async Task<T?> UmAsync<T>(string sql, Func<ILinha, T> ler) where T : class { var l = await Banco.ConsultarAsync(sql); return l.Count == 0 ? null : ler(l[0]); }
    public Task<int> ExecutarAsync(string sql) => Banco.ExecutarAsync(sql);
    public Task ExecutarLoteAsync(IReadOnlyList<string> sqls) => Banco.ExecutarLoteAsync(sqls);
}
