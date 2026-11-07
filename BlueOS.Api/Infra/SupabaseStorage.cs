using System.Net.Http.Headers;
using System.Text.Json;

namespace BlueOS.Api.Infra;

/* ============================================================================
   O STORAGE DO SUPABASE, por HTTP: subir, apagar e assinar arquivos nos
   buckets do V1 (designer-files é público; brand-assets e cliente-assets não).
   ----------------------------------------------------------------------------
   A chave service_role NÃO fica em arquivo nenhum da API: ela é pedida à
   Management API (`GET /v1/projects/{ref}/api-keys?reveal=true`) com o
   AccessToken que a API já tem, e guardada só na memória do processo. Se a
   configuração trouxer `Supabase:ServiceRoleKey`, ela vale primeiro.
   ============================================================================ */
public sealed class SupabaseStorage(IHttpClientFactory fabrica, IConfiguration cfg, ILogger<SupabaseStorage> log)
{
    string Url => (cfg["Supabase:Url"] ?? "").TrimEnd('/');
    string? _service; DateTime _serviceEm;

    HttpClient Http(int segundos = 120)
    {
        var h = fabrica.CreateClient("supabase-storage");
        h.Timeout = TimeSpan.FromSeconds(segundos);
        return h;
    }

    public string UrlPublica(string bucket, string caminho) => $"{Url}/storage/v1/object/public/{bucket}/{caminho.TrimStart('/')}";

    /// <summary>A service_role: da configuração, ou da Management API (uma vez por hora).</summary>
    public async Task<string> ChaveAsync()
    {
        if (!string.IsNullOrWhiteSpace(cfg["Supabase:ServiceRoleKey"])) return cfg["Supabase:ServiceRoleKey"]!.Trim();
        if (_service is not null && DateTime.UtcNow - _serviceEm < TimeSpan.FromHours(1)) return _service;
        var acesso = cfg["Supabase:AccessToken"];
        var projeto = Db.RefDe(Url);
        if (string.IsNullOrWhiteSpace(acesso) || string.IsNullOrWhiteSpace(projeto))
            throw new InvalidOperationException("sem chave do Storage: informe Supabase:ServiceRoleKey, ou Supabase:AccessToken (Management API) para a API buscá-la.");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.supabase.com/v1/projects/{projeto}/api-keys?reveal=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acesso);
        using var r = await Http(30).SendAsync(req);
        var texto = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"a Management API não deu a chave do Storage ({(int)r.StatusCode}).");
        foreach (var k in JsonDocument.Parse(texto).RootElement.EnumerateArray())
            if (k.TryGetProperty("name", out var n) && n.GetString() == "service_role" && k.TryGetProperty("api_key", out var v) && v.ValueKind == JsonValueKind.String)
            { _service = v.GetString()!; _serviceEm = DateTime.UtcNow; return _service; }
        throw new InvalidOperationException("a Management API não listou a service_role do projeto.");
    }

    /// <summary>Sobe (ou substitui) o arquivo. Devolve o caminho dentro do bucket.</summary>
    public async Task<string> SubirAsync(string bucket, string caminho, byte[] bytes, string mime)
    {
        var chave = await ChaveAsync();
        var path = caminho.TrimStart('/');
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Url}/storage/v1/object/{bucket}/{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chave);
        req.Headers.Add("apikey", chave);
        req.Headers.Add("x-upsert", "true");
        req.Content = new ByteArrayContent(bytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        using var r = await Http().SendAsync(req);
        if (!r.IsSuccessStatusCode)
        {
            var corpo = await r.Content.ReadAsStringAsync();
            log.LogWarning("Storage recusou {Bucket}/{Path}: {Status} {Corpo}", bucket, path, (int)r.StatusCode, corpo.Length > 300 ? corpo[..300] : corpo);
            throw new InvalidOperationException($"o Storage recusou o arquivo ({(int)r.StatusCode}): {Resumo(corpo)}");
        }
        return path;
    }

    public async Task ApagarAsync(string bucket, string caminho)
    {
        var chave = await ChaveAsync();
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Url}/storage/v1/object/{bucket}/{caminho.TrimStart('/')}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chave);
        req.Headers.Add("apikey", chave);
        using var r = await Http(30).SendAsync(req);
        if (!r.IsSuccessStatusCode && (int)r.StatusCode != 404) throw new InvalidOperationException($"o Storage não apagou o arquivo ({(int)r.StatusCode}).");
    }

    public async Task<byte[]> BaixarAsync(string bucket, string caminho)
    {
        var chave = await ChaveAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Url}/storage/v1/object/{bucket}/{caminho.TrimStart('/')}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chave);
        req.Headers.Add("apikey", chave);
        using var r = await Http().SendAsync(req);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"não consegui ler {bucket}/{caminho} ({(int)r.StatusCode}).");
        return await r.Content.ReadAsByteArrayAsync();
    }

    /// <summary>URL assinada (bucket privado), válida por `segundos`.</summary>
    public async Task<string> AssinarAsync(string bucket, string caminho, int segundos = 3600)
    {
        var chave = await ChaveAsync();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Url}/storage/v1/object/sign/{bucket}/{caminho.TrimStart('/')}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chave);
        req.Headers.Add("apikey", chave);
        req.Content = new StringContent(JsonSerializer.Serialize(new { expiresIn = segundos }), System.Text.Encoding.UTF8, "application/json");
        using var r = await Http(30).SendAsync(req);
        var texto = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"o Storage não assinou {bucket}/{caminho} ({(int)r.StatusCode}).");
        var j = JsonDocument.Parse(texto).RootElement;
        var rel = j.TryGetProperty("signedURL", out var s) ? s.GetString() : j.TryGetProperty("signedUrl", out var s2) ? s2.GetString() : null;
        if (string.IsNullOrEmpty(rel)) throw new InvalidOperationException("o Storage não devolveu a URL assinada.");
        return rel.StartsWith("http") ? rel : $"{Url}/storage/v1{(rel.StartsWith('/') ? "" : "/")}{rel}";
    }

    static string Resumo(string corpo)
    {
        try { var j = JsonDocument.Parse(corpo).RootElement; return (j.TryGetProperty("message", out var m) ? m.GetString() : j.TryGetProperty("error", out var e) ? e.GetString() : null) ?? corpo; }
        catch { return corpo.Length > 200 ? corpo[..200] : corpo; }
    }
}
