using BlueOS.Core.Modelos;

namespace BlueOS.Core.Executor;

/* ============================================================================
   AS TRAVAS DE DINHEIRO DO EXECUTOR, puras: porta de lib/trafego/motor/travas.ts
   e da parte sem banco de lib/trafego/motor/executor.ts (Tráfego 3.4).
   ----------------------------------------------------------------------------
   Todas falham FECHADO:
   · o teto da conta é o budget do mês; sem budget, nada que abra verba passa;
   · a verba da cópia sai da FOLGA do mês (D2): (budget − gasto) ÷ dias que
     faltam − soma das diárias ativas; folga abaixo de R$ 20 não duplica;
   · no máximo 4 mudanças de verba por hora por conta e 20 escritas por rodada;
   · leitura ao vivo antes de escrever: conta não elegível, conjunto que não
     está ACTIVE ou que não dá para ler não recebe duplicação.
   O vault pode APERTAR estes números, nunca afrouxar: a porta corta no teto.
   ============================================================================ */
public static class Limites
{
    /// <summary>Abaixo de R$ 20/dia o pareado piora (1,17); é também a folga mínima para uma cópia.</summary>
    public const long ConjuntoMinCentavos = 2_000;
    public const int MaxMudancasVerbaPorHora = 4;
    public const int MaxEscritasPorRodada = 20;
}

/// <summary>As únicas ações do motor que viram escrita na Meta. Lista fechada: ação que ninguém pôs aqui não escreve.</summary>
public enum TipoNaMeta { PauseAdset, DuplicateAdset }

public static class TipoNaMetaExt
{
    public static string NaGraph(this TipoNaMeta t) => t == TipoNaMeta.PauseAdset ? "pause_adset" : "duplicate_adset";
    /// <summary>O verbo do log de ação, o MESMO vocabulário da /api/trafego/acao.</summary>
    public static string VerboNoLog(this TipoNaMeta t) => t == TipoNaMeta.PauseAdset ? "pausou_conjunto" : "duplicou_conjunto";
    public static TipoNaMeta? DaAcao(Acao a) => a switch { Acao.PAUSAR => TipoNaMeta.PauseAdset, Acao.DUPLICAR => TipoNaMeta.DuplicateAdset, _ => null };
}

public sealed record ConjuntoAoVivo(string? Status, long? DiarioCentavos);
public sealed record ContaAoVivo(int? Status, int? Motivo);
/// <summary>O que a Meta dizia do conjunto e da conta no instante da escrita; vai gravado como `estado_anterior`.</summary>
public sealed record EstadoAoVivo(DateTime LidoEm, ConjuntoAoVivo? Conjunto, ContaAoVivo? Conta, string? Erro);

public sealed record Folga(
    long? BudgetMensalCentavos,
    long GastoMesCentavos,
    long DiariasAtivasCentavos,
    /// <summary>A parte estimada: conjuntos ativos sem verba própria (CBO), pela média do gasto dos últimos 7 dias.</summary>
    long? DiariasEstimadasCentavos,
    int DiasRestantes,
    long? FolgaDiariaCentavos);

public enum CodigoDaTrava { ContaNaoElegivel, ConjuntoNaoLido, ConjuntoComProblema, SemBudget, SemFolga, VerbaPorHora, EscritasPorRodada, TokenNaoServe, Isolamento }

public sealed record Trava(bool Ok, CodigoDaTrava? Codigo, string? Motivo, IReadOnlyList<string> Avisos, long? CopiaCentavos);

/// <summary>O bloco `travas_automatico` do vault, como o executor o usa: os limites só apertam; `CopiaNasce` é a D1.</summary>
public sealed record LimitesDoAutomatico(int? MaxMudancasVerbaPorHora, int? MaxEscritasPorRodada, string? CopiaNasce);

public sealed record VerbaDaCopia(long Pedida, bool Ok, string? Erro);

public sealed class AtivacaoDaCopia
{
    public bool Conjunto { get; set; }
    public int Ativados { get; set; }
    public int Falharam { get; set; }
    public int MantidosPausados { get; set; }
    public int Escritas { get; set; }
    public string? Erro { get; set; }
}

public static class CodigoDaTravaExt
{
    public static string Slug(this CodigoDaTrava c) => c switch
    {
        CodigoDaTrava.ContaNaoElegivel => "conta_nao_elegivel", CodigoDaTrava.ConjuntoNaoLido => "conjunto_nao_lido",
        CodigoDaTrava.ConjuntoComProblema => "conjunto_com_problema", CodigoDaTrava.SemBudget => "sem_budget", CodigoDaTrava.SemFolga => "sem_folga",
        CodigoDaTrava.VerbaPorHora => "verba_por_hora", CodigoDaTrava.EscritasPorRodada => "escritas_por_rodada",
        CodigoDaTrava.TokenNaoServe => "token_nao_serve", _ => "isolamento",
    };
}

public static class Travas
{
    public static string Real(long c) => "R$ " + (c / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>A conta pura da folga. Os dias restantes contam o dia de hoje.</summary>
    public static long? FolgaDiaria(long? budgetMensalCentavos, long gastoMesCentavos, long diariasAtivasCentavos, int diasRestantes)
    {
        if (budgetMensalCentavos is null or <= 0) return null;
        var dias = Math.Max(1, diasRestantes);
        return (long)Math.Floor((budgetMensalCentavos.Value - gastoMesCentavos) / (double)dias) - diariasAtivasCentavos;
    }

    public static int DiasRestantesNoMes(DateOnly hoje) => DateTime.DaysInMonth(hoje.Year, hoje.Month) - hoje.Day + 1;

    public static Folga MontarFolga(long? budgetMensalCentavos, long gastoMesCentavos, long diariasProprias, long diariasEstimadas, int diasRestantes)
    {
        var ativas = diariasProprias + diariasEstimadas;
        return new Folga(budgetMensalCentavos, gastoMesCentavos, ativas, diariasEstimadas > 0 ? diariasEstimadas : null, diasRestantes,
            FolgaDiaria(budgetMensalCentavos, gastoMesCentavos, ativas, diasRestantes));
    }

    public static Trava TravasDeDinheiro(TipoNaMeta tipo, long? pedidaCentavos, Folga folga, int mudancasNaHora, int escritasNaRodada, EstadoAoVivo vivo, LimitesDoAutomatico? limites)
    {
        var avisos = new List<string>();
        var tetoEscritas = Math.Min(limites?.MaxEscritasPorRodada ?? Limites.MaxEscritasPorRodada, Limites.MaxEscritasPorRodada);
        var tetoVerba = Math.Min(limites?.MaxMudancasVerbaPorHora ?? Limites.MaxMudancasVerbaPorHora, Limites.MaxMudancasVerbaPorHora);
        Trava Nao(CodigoDaTrava c, string m) => new(false, c, m, avisos, null);

        if (escritasNaRodada >= tetoEscritas)
            return Nao(CodigoDaTrava.EscritasPorRodada, $"esta conta já teve {escritasNaRodada} escritas nesta rodada (o teto é {tetoEscritas}): fica para a próxima.");
        if (vivo.Conta is { Status: not null } conta && conta.Status != 1)
            return Nao(CodigoDaTrava.ContaNaoElegivel, $"a conta de anúncios não está ativa na Meta agora (account_status {conta.Status}{(conta.Motivo is > 0 ? $", motivo {conta.Motivo}" : "")}): nada se escreve nela.");
        if (tipo == TipoNaMeta.PauseAdset)
        {
            if (vivo.Erro is not null) avisos.Add($"a leitura ao vivo falhou ({vivo.Erro}); pausar um conjunto já pausado não faz mal, e a pausa segue.");
            return new Trava(true, null, null, avisos, null);
        }

        /* duplicar: abre verba, então tudo abaixo vale */
        if (vivo.Conjunto is null || string.IsNullOrEmpty(vivo.Conjunto.Status))
            return Nao(CodigoDaTrava.ConjuntoNaoLido, $"não consegui ler o conjunto na Meta agora{(vivo.Erro is not null ? $" ({vivo.Erro})" : "")}: verba não se abre no escuro.");
        if (vivo.Conjunto.Status != "ACTIVE")
            return Nao(vivo.Conjunto.Status == "WITH_ISSUES" ? CodigoDaTrava.ConjuntoComProblema : CodigoDaTrava.ConjuntoNaoLido,
                $"o conjunto está {vivo.Conjunto.Status} na Meta agora, não ACTIVE: não se duplica o que não está rodando.");
        if (mudancasNaHora >= tetoVerba)
            return Nao(CodigoDaTrava.VerbaPorHora, $"esta conta já teve {mudancasNaHora} mudanças de verba na última hora (o teto é {tetoVerba}): a Meta bloqueia conta que mexe em orçamento em rajada. Fica para depois.");
        if (folga.FolgaDiariaCentavos is null)
            return Nao(CodigoDaTrava.SemBudget, "o cliente não tem budget mensal de ads na estratégia: sem o teto do mês, o motor não abre verba.");
        if (folga.FolgaDiariaCentavos < Limites.ConjuntoMinCentavos)
            return Nao(CodigoDaTrava.SemFolga,
                $"sem folga no mês para a cópia: budget {Real(folga.BudgetMensalCentavos ?? 0)}, já gastos {Real(folga.GastoMesCentavos)}, {Real(folga.DiariasAtivasCentavos)}/dia nos conjuntos ativos"
                + (folga.DiariasEstimadasCentavos is { } est ? $" ({Real(est)} estimados pelo gasto dos que não têm verba própria)" : "")
                + $", {folga.DiasRestantes} dia(s) para o fim do mês, folga de {Real(Math.Max(0, folga.FolgaDiariaCentavos.Value))}/dia (menos que o mínimo de {Real(Limites.ConjuntoMinCentavos)}). Não duplica (D2).");
        var pedida = pedidaCentavos ?? vivo.Conjunto.DiarioCentavos;
        if (pedida is null) return new Trava(true, null, null, avisos, null);
        var copia = Math.Min(pedida.Value, folga.FolgaDiariaCentavos.Value);
        if (copia < pedida) avisos.Add($"a verba da cópia desceu de {Real(pedida.Value)} para {Real(copia)}/dia: é a folga que o mês tem (D2).");
        return new Trava(true, null, null, avisos, copia);
    }

    /// <summary>O texto do aviso de DUPLICAR: em que estado a cópia está AGORA na conta, e o que fazer a seguir.
    /// Nenhuma frase promete o que não aconteceu.</summary>
    public static string TextoDaCopia(VerbaDaCopia? verba, string nasce = "PAUSED", AtivacaoDaCopia? ativacao = null)
    {
        var comVerba = verba is { Ok: true } ? $" com {Real(verba.Pedida)}/dia" : "";
        if (verba is { Ok: false })
            return $"A cópia nasceu pausada, MAS a verba de {Real(verba.Pedida)}/dia não foi aplicada ({verba.Erro ?? "a Meta recusou"}): ela ficou com a verba do conjunto original, acima da folga do mês. Ajuste a verba no Gerenciador ANTES de ativar.";
        if (nasce == "PAUSED")
            return verba is not null ? $"A cópia nasceu pausada{comVerba}. Ativar é no Gerenciador." : "A cópia nasceu pausada. Ativar é no Gerenciador.";
        if (ativacao is null) return $"A cópia nasceu pausada{comVerba} e a ativação não foi tentada. Ative no Gerenciador.";
        if (!ativacao.Conjunto) return $"A cópia nasceu pausada{comVerba}, mas ATIVAR falhou ({ativacao.Erro ?? "a Meta recusou"}). Ela está pausada com a verba certa: ativar é um clique no Gerenciador.";
        if (ativacao.Falharam > 0) return $"A cópia foi ativada{comVerba}, MAS {ativacao.Falharam} anúncio(s) dela continuam pausados: conjunto ativo com anúncio pausado não entrega. Ative os anúncios no Gerenciador.";
        if (ativacao.Erro is not null) return $"A cópia foi ativada{comVerba}, mas {ativacao.Erro}. Confira os anúncios dela no Gerenciador.";
        return $"A cópia está ATIVA{comVerba}{(ativacao.Ativados > 0 ? $" (e {ativacao.Ativados} anúncio(s) dela)" : "")}, dentro da folga do mês."
             + (ativacao.MantidosPausados > 0 ? $" {ativacao.MantidosPausados} anúncio(s) seguem pausados, como estavam no original." : "");
    }
}

/// <summary>O dia da conta é o dia em Brasília (lib/trafego/fuso.ts): é por ele que a Meta conta gasto e leads.</summary>
public static class Fuso
{
    public static readonly TimeZoneInfo DaConta = Achar();

    static TimeZoneInfo Achar()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Local;
    }

    public static DateTime NaConta(DateTime instante)
    {
        var utc = instante.Kind switch
        {
            DateTimeKind.Utc => instante,
            DateTimeKind.Local => instante.ToUniversalTime(),
            _ => DateTime.SpecifyKind(instante, DateTimeKind.Local).ToUniversalTime(),
        };
        return TimeZoneInfo.ConvertTimeFromUtc(utc, DaConta);
    }

    public static DateOnly DiaDaConta(DateTime instante) => DateOnly.FromDateTime(NaConta(instante));
}
