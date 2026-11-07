using BlueOS.Core.Panorama;

namespace BlueOS.Shared.Estado;

/// <summary>
/// O estado que o chrome (barra do topo, sino, faixa) e a página do Panorama compartilham: o Panorama lido, a busca
/// digitada, a estrela em foco. O MainLayout é dono da leitura (ao abrir, a cada mudança da fonte e a cada minuto);
/// a página só desenha. Vai por CascadingValue, sem mexer na injeção dos hosts.
/// </summary>
public sealed class Painel
{
    public PanoramaDoSquad? Panorama { get; private set; }
    public string? Erro { get; private set; }
    public bool Lendo { get; private set; }
    public DateTime? LidoEm { get; private set; }
    public string Busca { get; set; } = "";
    /// <summary>O slug da estrela aberta (popover) — null = nenhuma.</summary>
    public string? Foco { get; set; }
    /// <summary>O erro da conexão automática (o layout tenta antes de qualquer tela): fica visível até a pessoa fechar.</summary>
    public string? AvisoDaConexao { get; private set; }
    public event Action? Mudou;
    /// <summary>Alguém mudou o que a leitura depende (o squad escolhido no Start): o layout relê na hora, sem esperar o minuto.</summary>
    public event Action? Reler;

    public void Avisar() => Mudou?.Invoke();

    public void PedirLeitura() => Reler?.Invoke();

    public void AvisarConexao(string aviso) { AvisoDaConexao = aviso; Avisar(); }

    public void DispensarAvisoDaConexao() { AvisoDaConexao = null; Avisar(); }

    public void ComecouALer() { Lendo = true; Avisar(); }

    public void Leu(PanoramaDoSquad p)
    {
        Panorama = p; Erro = null; Lendo = false; LidoEm = DateTime.Now;
        if (Foco is not null && !p.Estrelas.Any(e => e.Slug == Foco)) Foco = null;
        Avisar();
    }

    public void Falhou(string erro) { Erro = erro; Lendo = false; Avisar(); }

    public void Limpar() { Panorama = null; Foco = null; Busca = ""; Avisar(); }

    /// <summary>As estrelas que a busca acende: nome ou slug contendo o texto (sem acento, sem caixa).</summary>
    public IReadOnlyList<Estrela> Achadas()
    {
        if (Panorama is null || string.IsNullOrWhiteSpace(Busca)) return [];
        var q = Normalizar(Busca);
        return Panorama.Estrelas.Where(e => Normalizar(e.Nome).Contains(q) || e.Slug.Contains(q))
            .OrderBy(e => Normalizar(e.Nome).StartsWith(q) ? 0 : 1).ThenBy(e => e.Nome).ToList();
    }

    public static string Normalizar(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in d) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString().Trim();
    }
}
