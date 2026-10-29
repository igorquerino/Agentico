using System.Text.Json;

namespace BlueOS.Core.Api;

/// <summary>A configuração guardada na máquina: qual fonte e o endereço do V1. Nunca a senha.</summary>
public sealed class ConfiguracaoDaFonte
{
    public string Modo { get; set; } = "demo";
    public string BaseUrlV1 { get; set; } = "http://localhost:3080";
    public string? UltimoLogin { get; set; }
}

/// <summary>
/// O que a tela injeta: a fonte atual (demo ou V1) e a troca em tempo de execução.
/// É um singleton; a tela escuta <see cref="Mudou"/> e relê.
/// </summary>
public sealed class FonteDeDados
{
    readonly string _arquivo;
    public ConfiguracaoDaFonte Config { get; private set; }
    public IBlueApi Atual { get; private set; }
    public event Action? Mudou;

    public FonteDeDados(string? pastaDeConfig = null)
    {
        var pasta = pastaDeConfig ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueOS");
        Directory.CreateDirectory(pasta);
        _arquivo = Path.Combine(pasta, "fonte.json");
        Config = Ler();
        Atual = new DemoApi();
        Atual.Mudou += () => Mudou?.Invoke();
    }

    ConfiguracaoDaFonte Ler()
    {
        try
        {
            if (File.Exists(_arquivo))
                return JsonSerializer.Deserialize<ConfiguracaoDaFonte>(File.ReadAllText(_arquivo)) ?? new();
        }
        catch { }
        return new();
    }

    void Gravar()
    {
        try { File.WriteAllText(_arquivo, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    public void UsarDemo()
    {
        (Atual as IDisposable)?.Dispose();
        Atual = new DemoApi();
        Atual.Mudou += () => Mudou?.Invoke();
        Config.Modo = "demo";
        Gravar();
        Mudou?.Invoke();
    }

    /// <summary>Conecta ao V1 pela sessão da pessoa. Falhou o login, a fonte não muda.</summary>
    public async Task<string> UsarV1Async(string baseUrl, string login, string senha)
    {
        var api = new V1Api(baseUrl);
        var nome = await api.LoginAsync(login, senha);
        (Atual as IDisposable)?.Dispose();
        Atual = api;
        Atual.Mudou += () => Mudou?.Invoke();
        Config.Modo = "v1";
        Config.BaseUrlV1 = baseUrl;
        Config.UltimoLogin = login;
        Gravar();
        Mudou?.Invoke();
        return nome;
    }
}
