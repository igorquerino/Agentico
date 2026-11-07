using BlueOS.Core.Modelos;

namespace BlueOS.Core.Panorama;

/* ============================================================================
   OS SINAIS DE VIDA: o carimbo de cada fonte e o "ao vivo" da barra do topo.
   ----------------------------------------------------------------------------
   Uma fonte sem dado não finge leitura: o carimbo diz "ainda não vigiado"
   (EXPERIENCE.md, State Patterns, "Sem dado de uma fonte"), e o "ao vivo"
   procura a Meta PELO NOME da fonte; sem leitura da Meta, fala da carteira.
   Quando a leitura do Panorama falha, "sem leitura há X" (nunca vermelho).
   ============================================================================ */
public static class Vida
{
    public const string NaoVigiado = "ainda não vigiado";
    public const string Meta = "Meta";
    public const string GruposDoCs = "grupos do CS";
    public const string Publicacoes = "publicações";

    /// <summary>A fonte pelo nome ("Meta" acha "Meta (gasto, leads)"), nunca pela posição na lista.</summary>
    public static SinalDeVida? DaFonte(IEnumerable<SinalDeVida> sinais, string fonte) => sinais.FirstOrDefault(s => EhDa(s, fonte));

    static bool EhDa(SinalDeVida s, string fonte) =>
        string.Equals(s.Fonte, fonte, StringComparison.OrdinalIgnoreCase) || s.Fonte.StartsWith(fonte + " ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Confere os carimbos contra o que os clientes do squad trouxeram: a Meta sem nenhum KPI e o CS sem nenhum sinal,
    /// quando a fonte não acusou problema, viram "ainda não vigiado"; sem carimbo de publicações, entra um não vigiado.
    /// </summary>
    public static IReadOnlyList<SinalDeVida> Conferir(IReadOnlyList<SinalDeVida> sinais, IReadOnlyCollection<Cliente> clientes)
    {
        if (sinais.Count == 0) return sinais;
        var temMeta = clientes.Any(c => c.Sinais.Kpis is not null);
        var temCs = clientes.Any(c => c.Sinais.NotaCs is not null || c.Sinais.RiscoChurn is not null || c.Sinais.DiasSemMensagem is not null);
        var saida = sinais.Select(s =>
            !s.Ruim && s.Vigiado && ((EhDa(s, Meta) && !temMeta) || (EhDa(s, GruposDoCs) && !temCs))
                ? s with { Texto = NaoVigiado, Vigiado = false }
                : s).ToList();
        if (!saida.Any(s => EhDa(s, Publicacoes))) saida.Add(new(Publicacoes, NaoVigiado, false, Vigiado: false));
        return saida;
    }

    /// <summary>O texto do "ao vivo" da barra: a leitura da Meta quando ela é vigiada; senão, a hora em que a carteira foi lida.</summary>
    public static string AoVivo(IReadOnlyList<SinalDeVida> sinais, DateTime? carteiraLidaEm)
    {
        if (DaFonte(sinais, Meta) is { Vigiado: true } meta)
            return "ao vivo · " + (meta.Texto.StartsWith("lida", StringComparison.OrdinalIgnoreCase) ? $"Meta {meta.Texto}" : $"Meta: {meta.Texto}");
        return "ao vivo · carteira lida" + (carteiraLidaEm is { } l ? $" {l:HH:mm}" : "");
    }

    /// <summary>"sem leitura há 12 min": a leitura falhou e o último estado continua na tela.</summary>
    public static string SemLeitura(DateTime? ultimaLeitura, DateTime agora)
    {
        if (ultimaLeitura is not { } u) return "sem leitura";
        var dt = agora - u;
        if (dt.TotalMinutes < 1) return "sem leitura há menos de 1 min";
        if (dt.TotalMinutes < 60) return $"sem leitura há {(int)dt.TotalMinutes} min";
        if (dt.TotalHours < 48) return $"sem leitura há {(int)dt.TotalHours} h";
        return $"sem leitura há {(int)dt.TotalDays} d";
    }
}
