using BlueOS.Core.Fluxo;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

namespace BlueOS.Core.Panorama;

/* ============================================================================
   O PANORAMA: o que a tela do mapa estelar mostra, montado em memória.
   ----------------------------------------------------------------------------
   Uma estrela por cliente do squad; o alerta mais grave pinta a estrela; o
   resumo do squad soma os KPIs já lidos; os sinais de vida dizem quando cada
   fonte foi lida; o placar vem da fonte (pontos e rankings são dado NOVO).
   ============================================================================ */

public sealed record Estrela(
    Cliente Cliente,
    Estagio Estagio,
    IReadOnlyList<Alerta> Alertas,
    Alerta? Principal,
    Visto? Visto,
    Posicao Posicao,
    int DecisoesPendentes)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool Saudavel => Principal is null;
    [System.Text.Json.Serialization.JsonIgnore] public bool EstaVista => Visto is not null;
    [System.Text.Json.Serialization.JsonIgnore] public string Slug => Cliente.Slug;
    [System.Text.Json.Serialization.JsonIgnore] public string Nome => Cliente.Nome;
}

/// <summary>Marcar como visto: silencia o alerta até amanhã (00:00 em Brasília) ou até a assinatura do estado mudar.</summary>
public sealed record Visto(string ClienteSlug, string Assinatura, DateTime Em, string Por)
{
    public bool ValeAgora(DateTime agora, string assinaturaAtual) => assinaturaAtual == Assinatura && agora.Date == Em.Date;
}

public sealed record ResumoDoSquad(
    string Squad, int Clientes, int EmAutomacao, int PrecisamDeVoce, int EmDia,
    long GastoCentavos, long BudgetCentavos, int Mql, int MetaMql, long? CustoMedioPorMqlCentavos, long? TetoMedioCentavos,
    int DiaDoMes, int DiasDoMes)
{
    /// <summary>Saúde do squad = saudáveis ÷ total, em %.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int SaudePct => Clientes == 0 ? 100 : (int)Math.Round(EmDia * 100.0 / Clientes);
}

/// <summary>"Meta (gasto, leads) · lida há 4 min". Ruim quando a leitura passou do prazo ou a fonte recusou.</summary>
public sealed record SinalDeVida(string Fonte, string Texto, bool Ruim);

public sealed record LinhaDoRanking(string Nome, int Valor, bool EhVoce, string? Extra = null);
public sealed record Acendeu(DateTime Quando, string Cliente, int Pontos, string Por);

/// <summary>Pontos e rankings (dado novo; a demonstração semeia).</summary>
public sealed record Placar(
    string Gestor, int Pontos, int PosicaoNoSquad, int GestoresNoSquad, int PosicaoGeral, int GestoresNoTotal,
    IReadOnlyList<LinhaDoRanking> Gestores, IReadOnlyList<LinhaDoRanking> Squads, IReadOnlyList<Acendeu> AcendeuHoje,
    int DiasSemVermelho, int RecordeSemVermelho, int SaudeOntemPct, IReadOnlyList<Acendeu>? ACaminho = null)
{
    /// <summary>Pontos "a caminho": o gestor agiu no Start, mas a estrela só acende (e os pontos só valem) quando o dado confirmar.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<Acendeu> ACaminhoOuVazio => ACaminho ?? [];
}

public sealed record PanoramaDoSquad(
    DateTime Agora, string Squad, IReadOnlyList<string> Squads, ResumoDoSquad Resumo,
    IReadOnlyList<Estrela> Estrelas, IReadOnlyList<SinalDeVida> SinaisDeVida, Placar Placar)
{
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<Estrela> PrecisamDeVoce => Estrelas.Where(e => !e.Saudavel).OrderBy(e => e.Principal!.Gravidade).ThenBy(e => e.Nome);
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<Estrela> Saudaveis => Estrelas.Where(e => e.Saudavel).OrderBy(e => e.Nome);
    /// <summary>O que interrompe: gravidade 1 ainda não vista.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<Estrela> Interrompem => Estrelas.Where(e => e.Principal is { } a && Gravidades.Interrompe(a.Gravidade) && !e.EstaVista);
}

public static class Montador
{
    /// <summary>A linha da etapa FEITA na trilha dobrada: "conta conectada · token aceito", "2 lotes · 9 peças aprovadas"…</summary>
    public static string ResumoDaEtapa(Cliente c, Estagio e) => e switch
    {
        Estagio.Chegada => c.Desde.ToString("dd/MM"),
        Estagio.Assets => string.Join(" · ", new[] { c.TemLogo ? "logo" : null, c.ContaConectada ? "conta conectada" : null, c.Conta.TokenValido == true ? "token aceito" : null }.Where(x => x is not null)),
        Estagio.Ficha => string.Join(" · ", new[] { c.NichoSlug is null ? null : Setup.Nichos.Rotulo(c.NichoSlug).ToLowerInvariant(), c.BudgetMensal is > 0 ? Dinheiro.ReaisRedondos(Dinheiro.CentavosDe(c.BudgetMensal.Value)) : null, c.Regua is { Perguntas.Count: > 0 } r ? $"régua {r.Perguntas.Count} pergunta(s)" : null, c.TetoMql is > 0 ? "teto " + Dinheiro.ReaisRedondos(Dinheiro.CentavosDe(c.TetoMql.Value)) : null }.Where(x => x is not null)),
        Estagio.Copy => $"{c.Lotes.Count} lote(s) · {c.Pecas.Count(p => p.Aprovada)} peça(s) aprovada(s)",
        Estagio.Design => $"{c.Artes.Count} arte(s) · pilotos {string.Join(", ", c.Pilotos.Keys.OrderBy(k => k))}",
        Estagio.Campanha => c.Publicacao is { } p ? $"publicada {p.Em:dd/MM} · {p.Conjuntos.Count} conjunto(s)" : "",
        _ => "",
    };

    /// <summary>Monta o Panorama de um squad a partir da carteira já lida. `vistos` são os vistos vigentes de quem olha.</summary>
    public static PanoramaDoSquad Montar(
        IReadOnlyList<Cliente> carteira, string? squad, ParametrosMotor p, DateTime agora,
        IReadOnlyList<Visto> vistos, IReadOnlyList<SinalDeVida> sinaisDeVida, Placar placar)
    {
        var squads = carteira.SelectMany(c => c.Squads.Count > 0 ? c.Squads : [c.Squad]).Select(s => s.ToUpperInvariant()).Distinct().OrderBy(s => s).ToList();
        var escolhido = squad is not null && squads.Contains(squad.ToUpperInvariant()) ? squad.ToUpperInvariant() : squads.FirstOrDefault() ?? (squad ?? "");
        var doSquad = carteira.Where(c => escolhido == "" || c.EstaNoSquad(escolhido)).ToList();

        var brutas = doSquad.Select(c =>
        {
            var alertas = Diagnostico.DoCliente(c, p, agora);
            var principal = alertas.FirstOrDefault();
            Visto? visto = null;
            if (principal is not null)
            {
                visto = vistos.FirstOrDefault(v => v.ClienteSlug == c.Slug && v.ValeAgora(agora, principal.Assinatura));
            }
            var pend = c.Recomendacoes.Count(r => r.Estado == EstadoDaRecomendacao.Pendente);
            return (c, estagio: Estagios.DoCliente(c, p), alertas, principal, visto, pend);
        }).ToList();

        /* posições: por anel, as mais graves primeiro a partir do topo; os saudáveis na borda em ordem alfabética */
        var estrelas = new List<Estrela>();
        for (var anel = 1; anel <= 3; anel++)
        {
            var doAnel = brutas.Where(b => b.principal is not null && Gravidades.Anel(b.principal.Gravidade) == anel)
                .OrderBy(b => b.principal!.Gravidade).ThenBy(b => b.visto is not null).ThenBy(b => b.c.Nome).ToList();
            var dist = Orbita.Distribuir(doAnel.Count, Orbita.Capacidade(anel, Orbita.RaiosDoMock[anel]));
            foreach (var (i, ang, volta) in dist)
            {
                var b = doAnel[i];
                estrelas.Add(new(b.c, b.estagio, b.alertas, b.principal, b.visto, new(anel, ang, volta), b.pend));
            }
        }
        var saudaveis = brutas.Where(b => b.principal is null).OrderBy(b => b.c.Nome, StringComparer.Create(new System.Globalization.CultureInfo("pt-BR"), true)).ToList();
        for (var i = 0; i < saudaveis.Count; i++)
        {
            var b = saudaveis[i];
            estrelas.Add(new(b.c, b.estagio, b.alertas, null, null, new(Orbita.AnelDosSaudaveis, Orbita.AnguloDaBorda(i, saudaveis.Count)), b.pend));
        }

        var kpis = doSquad.Select(c => c.Sinais.Kpis).Where(k => k is not null).ToList();
        var tetos = doSquad.Where(c => c.TetoMql is > 0).Select(c => Dinheiro.CentavosDe(c.TetoMql!.Value)).ToList();
        var gasto = kpis.Sum(k => k!.GastoCentavos);
        var mql = kpis.Sum(k => k!.Mql);
        var resumo = new ResumoDoSquad(escolhido, doSquad.Count,
            estrelas.Count(e => Estagios.CampanhaPublicada(e.Cliente)),
            estrelas.Count(e => !e.Saudavel), estrelas.Count(e => e.Saudavel),
            gasto, doSquad.Where(c => c.BudgetMensal is > 0).Sum(c => Dinheiro.CentavosDe(c.BudgetMensal!.Value)),
            mql, kpis.Sum(k => k!.MetaMql ?? 0),
            mql > 0 ? gasto / mql : null, tetos.Count > 0 ? (long)tetos.Average() : null,
            agora.Day, DateTime.DaysInMonth(agora.Year, agora.Month));

        return new PanoramaDoSquad(agora, escolhido, squads, resumo, estrelas, sinaisDeVida, placar);
    }

    /// <summary>"lida há 4 min", "lido há 1 h", "ontem 07:30".</summary>
    public static string HaQuanto(DateTime? q, DateTime agora, bool feminino = true)
    {
        if (q is not { } d) return "nunca";
        var dt = agora - d;
        var p = feminino ? "lida" : "lido";
        if (dt.TotalMinutes < 1) return $"{p} agora";
        if (dt.TotalMinutes < 60) return $"{p} há {(int)dt.TotalMinutes} min";
        if (dt.TotalHours < 24 && d.Date == agora.Date) return $"{p} há {(int)dt.TotalHours} h";
        return $"{p} {Diagnostico.Relativo(d, agora)}";
    }
}
