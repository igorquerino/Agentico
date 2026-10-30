using System.Collections.Concurrent;
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
   perguntando ao GoTrue (/auth/v1/user) e guarda o resultado por 5 minutos.
   Ninguém troca senha; a API nunca vê a service role.
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
        var userId = j.GetProperty("user").GetProperty("id").GetString()!;
        return new SessaoSupabase(token, userId, DateTime.UtcNow.AddSeconds(expira));
    }

    /// <summary>O user id de um JWT, conferido no GoTrue. null = token inválido ou vencido.</summary>
    public async Task<string?> UserIdDoTokenAsync(string token)
    {
        if (_url == "") return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_url}/auth/v1/user");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var r = await _http.SendAsync(req);
        if (!r.IsSuccessStatusCode) return null;
        var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return j.TryGetProperty("id", out var id) ? id.GetString() : null;
    }
}

/// <summary>As sessões conferidas, por hash do token: conferir no GoTrue a cada chamada custaria um round-trip a mais.</summary>
public sealed class Sessoes
{
    readonly ConcurrentDictionary<string, (string UserId, DateTime ValeAte)> _mapa = new();
    static string Chave(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public string? UserIdDe(string token) => _mapa.TryGetValue(Chave(token), out var s) && s.ValeAte > DateTime.UtcNow ? s.UserId : null;
    public void Guardar(string token, string userId, DateTime valeAte) => _mapa[Chave(token)] = (userId, valeAte < DateTime.UtcNow.AddMinutes(5) ? valeAte : DateTime.UtcNow.AddMinutes(5));
}
