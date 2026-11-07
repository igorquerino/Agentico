using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueOS.Api.Infra;

/* ============================================================================
   O LOGIN E A SESSÃO: o Supabase Auth continua o mesmo (plano, "Equivalências").
   ----------------------------------------------------------------------------
   O login é a senha da pessoa no GoTrue (grant_type=password, com a anon key,
   que é pública por desenho). O que volta é o JWT dela; a API confere o JWT
   perguntando ao GoTrue (/auth/v1/user) e guarda o resultado por até 5 minutos,
   nunca além do `exp` do JWT. A conferência recusa conta banida (`banned_until`
   no futuro), como o `contaBanida` do middleware do V1: é assim que a pessoa
   desativada sai do sistema sem esperar o JWT vencer.
   Ninguém troca senha por aqui. A service role NÃO passa por este arquivo: só o
   Storage a usa (Infra/SupabaseStorage.cs), lida da Management API com o
   AccessToken (ou de Supabase:ServiceRoleKey), e ela nunca sai da API.
   ============================================================================ */
public sealed record SessaoSupabase(string Token, string UserId, DateTime ExpiraEm);

public sealed class SupabaseAuth
{
    readonly HttpClient _http;
    readonly string _url;
    readonly string _anon;

    public SupabaseAuth(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _url = (cfg["Supabase:Url"] ?? "").TrimEnd('/');
        _anon = cfg["Supabase:AnonKey"] ?? "";
        if (_anon != "") _http.DefaultRequestHeaders.Add("apikey", _anon);
    }

    public async Task<SessaoSupabase?> EntrarAsync(string email, string senha)
    {
        if (_url == "" || _anon == "") throw new InvalidOperationException("falta Supabase:Url ou Supabase:AnonKey na configuração da API.");
        var r = await _http.PostAsJsonAsync($"{_url}/auth/v1/token?grant_type=password", new { email, password = senha });
        if (!r.IsSuccessStatusCode) return null;
        var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        var token = j.GetProperty("access_token").GetString()!;
        var expira = j.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        var user = j.GetProperty("user");
        if (ContaBanida(user, DateTime.UtcNow)) return null;
        var userId = user.GetProperty("id").GetString()!;
        return new SessaoSupabase(token, userId, DateTime.UtcNow.AddSeconds(expira));
    }

    /// <summary>O user id de um JWT, conferido no GoTrue. null = token inválido, vencido ou de conta banida.</summary>
    public async Task<string?> UserIdDoTokenAsync(string token)
    {
        if (_url == "") return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_url}/auth/v1/user");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var r = await _http.SendAsync(req);
        if (!r.IsSuccessStatusCode) return null;
        var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return UserIdDoUsuario(j, DateTime.UtcNow);
    }

    /// <summary>O id do usuário que o GoTrue devolveu, ou null quando a conta está banida agora. PURO.</summary>
    public static string? UserIdDoUsuario(JsonElement user, DateTime agoraUtc)
    {
        if (user.ValueKind != JsonValueKind.Object || ContaBanida(user, agoraUtc)) return null;
        return user.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    }

    /// <summary>contaBanida do V1 (lib/os/banimento.ts): `banned_until` no futuro é banida; data que não lê também (fail-closed).</summary>
    public static bool ContaBanida(JsonElement user, DateTime agoraUtc)
    {
        if (user.ValueKind != JsonValueKind.Object || !user.TryGetProperty("banned_until", out var b)) return false;
        if (b.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        if (b.ValueKind != JsonValueKind.String) return true;
        var s = b.GetString();
        if (string.IsNullOrEmpty(s)) return false;
        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ate)) return true;
        return ate.UtcDateTime > agoraUtc;
    }
}

/// <summary>As sessões conferidas, por hash do token: conferir no GoTrue a cada chamada custaria um round-trip a mais.
/// Só a conferência no GoTrue grava aqui; o acerto do cache NÃO renova. Cada entrada vale até 5 min depois da
/// conferência e nunca além do `exp` do JWT; as vencidas saem do mapa.</summary>
public sealed class Sessoes
{
    public static readonly TimeSpan Janela = TimeSpan.FromMinutes(5);
    static readonly TimeSpan IntervaloDaLimpeza = TimeSpan.FromMinutes(1);

    readonly ConcurrentDictionary<string, (string UserId, DateTime ValeAte)> _mapa = new();
    readonly Func<DateTime> _agora;
    DateTime _ultimaLimpeza = DateTime.MinValue;

    public Sessoes() : this(() => DateTime.UtcNow) { }
    internal Sessoes(Func<DateTime> agoraUtc) => _agora = agoraUtc;

    internal int Quantas => _mapa.Count;

    static string Chave(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>O usuário da sessão já conferida e ainda dentro da validade; null = conferir no GoTrue.</summary>
    public string? UserIdDe(string token)
    {
        var chave = Chave(token);
        if (!_mapa.TryGetValue(chave, out var s)) return null;
        if (s.ValeAte > _agora()) return s.UserId;
        _mapa.TryRemove(new KeyValuePair<string, (string, DateTime)>(chave, s));
        return null;
    }

    /// <summary>Guarda a sessão que o GoTrue acabou de conferir: vale até `valeAte`, no máximo 5 min, e nunca além do `exp` do JWT.</summary>
    public void Guardar(string token, string userId, DateTime valeAte)
    {
        var agora = _agora();
        var ate = valeAte < agora + Janela ? valeAte : agora + Janela;
        if (ExpiraDoJwt(token) is { } exp && exp < ate) ate = exp;
        Limpar(agora);
        if (ate <= agora) return;
        _mapa[Chave(token)] = (userId, ate);
    }

    void Limpar(DateTime agora)
    {
        if (agora - _ultimaLimpeza < IntervaloDaLimpeza) return;
        _ultimaLimpeza = agora;
        foreach (var kv in _mapa)
            if (kv.Value.ValeAte <= agora) _mapa.TryRemove(kv);
    }

    /// <summary>O `exp` (UTC) do payload de um JWT, sem conferir a assinatura (quem confere é o GoTrue). null = não é JWT legível.</summary>
    public static DateTime? ExpiraDoJwt(string token)
    {
        var partes = (token ?? "").Split('.');
        if (partes.Length < 2) return null;
        try
        {
            var b64 = partes[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var s)
                ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;
        }
        catch { return null; }
    }
}
