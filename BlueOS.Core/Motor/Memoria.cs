using BlueOS.Core.Modelos;

namespace BlueOS.Core.Motor;

/* ============================================================================
   A MEMÓRIA DO MOTOR: o que ele já fez em cada conjunto (Tráfego 3.3).
   ----------------------------------------------------------------------------
   Porta de lib/trafego/motor/memoria.ts. Sai do registro dele mesmo
   (trafego_motor_recomendacao + a última decisão + trafego_motor_aplicacao +
   a foto da cópia em trafego_adset_estado), nunca de tabela nova. Por conjunto:
   · a última ação EXECUTADA (desfecho 'aplicada'): a cadência conta daqui;
   · desde quando um ALERTAR por lead pendente segura a pausa, depois da última
     ação executada (D5), limitado pelo lead pendente mais antigo;
   · quantas cópias deste original o motor fez e seguem vivas: DUPLICAR
     'aplicada' cuja cópia não está ARCHIVED/DELETED, e o 'incerto' SEMPRE
     (a cópia pode existir: contar erra para o lado de não duplicar de novo).
   A árvore é pura e recebe DIAS, não datas (ParaArvore).
   ============================================================================ */

/// <summary>Uma recomendação do registro, com o estado resolvido (a última decisão; "pendente" sem decisão). `CopiaId` e
/// `StatusDaCopia` vêm da aplicação do DUPLICAR e da foto da cópia.</summary>
public sealed record RecomendacaoNaMemoria(
    string Id, string MetaId, string Acao, string Estado, DateTime CriadoEmUtc, DateTime? DecididaEmUtc,
    bool SegurouPorPendentes, string? CopiaId = null, string? StatusDaCopia = null);

/// <summary>As três lembranças de um conjunto, em instantes UTC.</summary>
public sealed record Lembranca(DateTime? UltimaAcaoEmUtc, DateTime? AlertaPorPendenteDesdeUtc, int CopiasVivas);

public static class Memoria
{
    static readonly HashSet<string> Mortas = ["ARCHIVED", "DELETED"];

    /// <summary>Dias corridos de `desde` até `agora`, para baixo; nunca negativo (diasDesde).</summary>
    public static int? DiasDesde(DateTime? desdeUtc, DateTime agoraUtc) =>
        desdeUtc is { } d ? Math.Max(0, (int)Math.Floor((agoraUtc - d).TotalDays)) : null;

    public static MemoriaDoConjunto ParaArvore(Lembranca? l, DateTime agoraUtc) =>
        new(DiasDesde(l?.UltimaAcaoEmUtc, agoraUtc), DiasDesde(l?.AlertaPorPendenteDesdeUtc, agoraUtc), l?.CopiasVivas ?? 0);

    /// <summary>O relógio do D5 limitado pelo lead (comOPendenteMaisAntigo): o menor entre os dias desde o primeiro ALERTAR
    /// da sequência e os dias desde que o lead pendente mais antigo chegou. Sem ALERTAR, continua nulo.</summary>
    public static MemoriaDoConjunto ComOPendenteMaisAntigo(MemoriaDoConjunto m, int? diasDoPendenteMaisAntigo) =>
        m.DiasSegurandoPorPendente is null || diasDoPendenteMaisAntigo is null
            ? m
            : m with { DiasSegurandoPorPendente = Math.Min(m.DiasSegurandoPorPendente.Value, diasDoPendenteMaisAntigo.Value) };

    /// <summary>As lembranças por conjunto (meta_id), a partir do registro (lembrar).</summary>
    public static Dictionary<string, Lembranca> Lembrar(IEnumerable<RecomendacaoNaMemoria> recomendacoes)
    {
        var recs = recomendacoes.ToList();
        var m = new Dictionary<string, Lembranca>();
        Lembranca De(string id) => m.TryGetValue(id, out var l) ? l : m[id] = new Lembranca(null, null, 0);

        /* 1. a última ação executada, por conjunto */
        foreach (var r in recs)
        {
            if (r.Estado != "aplicada" || r.DecididaEmUtc is null) continue;
            var l = De(r.MetaId);
            if (l.UltimaAcaoEmUtc is null || r.DecididaEmUtc > l.UltimaAcaoEmUtc) m[r.MetaId] = l with { UltimaAcaoEmUtc = r.DecididaEmUtc };
        }
        /* 2. o pendente segurando a pausa: o primeiro ALERTAR por pendente depois da última ação executada */
        foreach (var r in recs)
        {
            if (r.Acao != "ALERTAR" || !r.SegurouPorPendentes) continue;
            var l = De(r.MetaId);
            if (l.UltimaAcaoEmUtc is { } ua && r.CriadoEmUtc <= ua) continue;
            if (l.AlertaPorPendenteDesdeUtc is null || r.CriadoEmUtc < l.AlertaPorPendenteDesdeUtc) m[r.MetaId] = l with { AlertaPorPendenteDesdeUtc = r.CriadoEmUtc };
        }
        /* 3. as cópias vivas deste original. O INCERTO CONTA: a Meta pode ter criado a cópia. */
        foreach (var r in recs)
        {
            if (r.Acao != "DUPLICAR" || r.Estado is not ("aplicada" or "incerto")) continue;
            var copiaId = r.Estado == "aplicada" ? r.CopiaId : null;
            var status = copiaId is not null ? r.StatusDaCopia : null;
            if (status is not null && Mortas.Contains(status)) continue;
            var l = De(r.MetaId);
            m[r.MetaId] = l with { CopiasVivas = l.CopiasVivas + 1 };
        }
        return m;
    }
}
