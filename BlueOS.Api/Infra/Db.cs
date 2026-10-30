using Npgsql;

namespace BlueOS.Api.Infra;

/* ============================================================================
   O BANCO: um NpgsqlDataSource (pool) para a API inteira.
   ----------------------------------------------------------------------------
   A connection string vem de `ConnectionStrings:BlueOS` (appsettings.Local.json,
   que fica fora do repositório, ou a variável BLUEOS_ConnectionStrings__BlueOS).
   Formato do pooler do Supabase (a conexão direta é só IPv6, README do V1):
     Host=aws-0-sa-east-1.pooler.supabase.com;Port=6543;Database=postgres;
     Username=postgres.<ref>;Password=...;SSL Mode=Require;No Reset On Close=true
   A leitura é preguiçosa e acompanha o arquivo: salvar a connection string no
   appsettings.Local.json com a API no ar já basta, sem reiniciar.
   ============================================================================ */
public sealed class Db(IConfiguration cfg)
{
    readonly object _trava = new();
    NpgsqlDataSource? _fonte;
    string? _csEmUso;

    (NpgsqlDataSource? Fonte, string? Erro) Resolver()
    {
        var cs = cfg.GetConnectionString("BlueOS");
        if (string.IsNullOrWhiteSpace(cs)) return (null, "falta ConnectionStrings:BlueOS (appsettings.Local.json)");
        lock (_trava)
        {
            if (_fonte is not null && _csEmUso == cs) return (_fonte, null);
            try
            {
                var b = new NpgsqlDataSourceBuilder(cs);
                b.EnableDynamicJson();
                var nova = b.Build();
                var antiga = _fonte;
                _fonte = nova; _csEmUso = cs;
                antiga?.Dispose();
                return (nova, null);
            }
            catch (Exception e) { return (null, $"connection string inválida: {e.Message}"); }
        }
    }

    NpgsqlDataSource Fonte
    {
        get
        {
            var (f, erro) = Resolver();
            return f ?? throw new BancoNaoConfigurado(erro ?? "banco não configurado");
        }
    }

    public async Task<(bool Ok, string? Erro)> ConfereAsync()
    {
        var (f, erro) = Resolver();
        if (f is null) return (false, erro);
        try
        {
            await using var cmd = f.CreateCommand("select 1");
            await cmd.ExecuteScalarAsync();
            return (true, null);
        }
        catch (Exception e) { return (false, e.Message); }
    }

    static NpgsqlCommand Cmd(NpgsqlConnection cx, string sql, (string Nome, object? Valor)[] ps)
    {
        var cmd = new NpgsqlCommand(sql, cx);
        foreach (var (nome, valor) in ps) cmd.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        return cmd;
    }

    public async Task<NpgsqlConnection> AbrirAsync() => await Fonte.OpenConnectionAsync();

    public async Task<T?> UmAsync<T>(string sql, Func<NpgsqlDataReader, T> ler, params (string, object?)[] ps) where T : class
    {
        await using var cx = await AbrirAsync();
        await using var cmd = Cmd(cx, sql, ps);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? ler(r) : null;
    }

    public async Task<List<T>> ListaAsync<T>(string sql, Func<NpgsqlDataReader, T> ler, params (string, object?)[] ps)
    {
        await using var cx = await AbrirAsync();
        return await ListaAsync(cx, sql, ler, ps);
    }

    public static async Task<List<T>> ListaAsync<T>(NpgsqlConnection cx, string sql, Func<NpgsqlDataReader, T> ler, params (string, object?)[] ps)
    {
        await using var cmd = Cmd(cx, sql, ps);
        await using var r = await cmd.ExecuteReaderAsync();
        var saida = new List<T>();
        while (await r.ReadAsync()) saida.Add(ler(r));
        return saida;
    }

    public async Task<int> ExecutarAsync(string sql, params (string, object?)[] ps)
    {
        await using var cx = await AbrirAsync();
        await using var cmd = Cmd(cx, sql, ps);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<int> ExecutarAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, string sql, params (string, object?)[] ps)
    {
        await using var cmd = Cmd(cx, sql, ps);
        cmd.Transaction = tx;
        return await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class BancoNaoConfigurado(string msg) : InvalidOperationException(msg);

/// <summary>Leitura tolerante: coluna nula vira null, nunca exceção.</summary>
public static class Leitor
{
    public static string? S(this NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetValue(i)?.ToString(); }
    public static decimal? Dec(this NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : Convert.ToDecimal(r.GetValue(i)); }
    public static long L(this NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i)); }
    public static bool? B(this NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetBoolean(i); }
    public static DateTime? Dt(this NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetFieldValue<DateTime>(i); }
    public static DateOnly? Data(this NpgsqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return null;
        var v = r.GetValue(i);
        return v switch { DateOnly d => d, DateTime dt => DateOnly.FromDateTime(dt), _ => DateOnly.TryParse(v.ToString(), out var p) ? p : null };
    }
    public static List<string> Textos(this NpgsqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return new();
        var v = r.GetValue(i);
        return v is string[] arr ? arr.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : new();
    }
    public static string? Json(this NpgsqlDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetValue(i)?.ToString(); }
}
