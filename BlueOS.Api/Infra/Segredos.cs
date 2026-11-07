using System.Collections.Concurrent;

namespace BlueOS.Api.Infra;

/* ============================================================================
   AS CHAVES DOS PROVEDORES (Anthropic, imagem, Supabase), num lugar só.
   ----------------------------------------------------------------------------
   A ordem de busca de uma chave NOME é:
     1. a configuração: `Segredos:NOME` (appsettings.Local.json);
     2. a variável de ambiente NOME;
     3. os arquivos .env listados em `Segredos:Arquivos` (o .env.local do V1 na
        mesma máquina serve: a chave já existe lá, não precisa ser copiada).
   Nenhuma chave vai para log nem para resposta. `Tem(nome)` diz se existe.
   ============================================================================ */
public sealed class Segredos(IConfiguration cfg, ILogger<Segredos> log)
{
    static readonly ConcurrentDictionary<string, (DateTime Em, Dictionary<string, string> Valores)> ArquivosLidos = new();

    public string? Ler(string nome)
    {
        var daCfg = cfg[$"Segredos:{nome}"];
        if (!string.IsNullOrWhiteSpace(daCfg)) return daCfg.Trim();
        var doAmbiente = Environment.GetEnvironmentVariable(nome);
        if (!string.IsNullOrWhiteSpace(doAmbiente)) return doAmbiente.Trim();
        foreach (var arquivo in Arquivos())
        {
            var valores = LerArquivo(arquivo);
            if (valores.TryGetValue(nome, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    public bool Tem(string nome) => !string.IsNullOrWhiteSpace(Ler(nome));

    /// <summary>De onde a chave viria, sem o valor: para o /health dizer o que está configurado.</summary>
    public string Origem(string nome)
    {
        if (!string.IsNullOrWhiteSpace(cfg[$"Segredos:{nome}"])) return "configuração";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(nome))) return "ambiente";
        foreach (var arquivo in Arquivos())
            if (LerArquivo(arquivo).TryGetValue(nome, out var v) && !string.IsNullOrWhiteSpace(v)) return $"arquivo {Path.GetFileName(arquivo)}";
        return "ausente";
    }

    IEnumerable<string> Arquivos()
    {
        var lista = cfg.GetSection("Segredos:Arquivos").Get<string[]>() ?? [];
        var um = cfg["Segredos:Arquivo"];
        return lista.Concat(string.IsNullOrWhiteSpace(um) ? [] : [um]).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim());
    }

    Dictionary<string, string> LerArquivo(string caminho)
    {
        try
        {
            if (!File.Exists(caminho)) return new();
            var mtime = File.GetLastWriteTimeUtc(caminho);
            if (ArquivosLidos.TryGetValue(caminho, out var g) && g.Em == mtime) return g.Valores;
            var d = new Dictionary<string, string>();
            foreach (var linha in File.ReadAllLines(caminho))
            {
                var l = linha.Trim();
                if (l == "" || l.StartsWith('#')) continue;
                if (l.StartsWith("export ")) l = l[7..].Trim();
                var i = l.IndexOf('=');
                if (i <= 0) continue;
                var k = l[..i].Trim(); var v = l[(i + 1)..].Trim();
                if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))) v = v[1..^1];
                d[k] = v;
            }
            ArquivosLidos[caminho] = (mtime, d);
            return d;
        }
        catch (Exception e) { log.LogWarning("não consegui ler o arquivo de segredos {Arquivo}: {Erro}", caminho, e.Message); return new(); }
    }
}
