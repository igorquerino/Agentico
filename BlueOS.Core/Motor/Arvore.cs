using BlueOS.Core.Modelos;
using static BlueOS.Core.Motor.Dinheiro;

namespace BlueOS.Core.Motor;

/* ============================================================================
   DECIDIR O QUE FAZER COM UM CONJUNTO: árvore em código, com trilha.
   ----------------------------------------------------------------------------
   Porta fiel de lib/trafego/motor/arvore/decidir.ts (V1, 12/10/2026).
   Função pura. A primeira regra que dispara decide. Cada passo entra na trilha
   com a chave, o resultado e o texto: é o que o gestor confere quando discorda.
   As comparações são em INTEIROS (centésimos), nunca em ponto flutuante.
   ============================================================================ */
public static class Arvore
{
    static int Centesimos(double v) => (int)Math.Round(v * 100);

    static readonly Dictionary<Leitura, string> LeituraFalha = new()
    {
        [Leitura.SemAcesso] = "sem acesso aos leads (o token não lê o formulário)",
        [Leitura.Incompleta] = "a leitura dos leads ficou incompleta (falhou no meio)",
        [Leitura.Vencida] = "a carga não vê este conjunto há mais de 36 h (a foto dele venceu), e os números param ali",
    };

    public static Decisao Decidir(EntradaConjunto e, EntradaAoVivo vivo, long? tetoCentavosDoPpl, bool clienteConverte, ParametrosMotor p)
    {
        var trilha = new List<PassoDaTrilha>();
        var avisos = new List<Aviso>();
        void Passo(string chave, ResultadoDaRegra r, string texto) => trilha.Add(new PassoDaTrilha(chave, r, texto));

        var mql = e.MqlA + e.MqlB + e.MqlC;
        long ponderadoX100 = e.MqlA * Centesimos(p.Pesos.A) + e.MqlB * Centesimos(p.Pesos.B) + e.MqlC * Centesimos(p.Pesos.C);
        var ponderado = ponderadoX100 / 100.0;
        var taxa = e.Avaliados > 0 ? ponderado / e.Avaliados : 0;
        long? custoCentavos = ponderadoX100 > 0 ? (long)Math.Round((double)e.GastoCentavos * 100 / ponderadoX100) : null;
        long? teto = tetoCentavosDoPpl is > 0 ? tetoCentavosDoPpl : null;
        double? multiplo = teto is not null ? (double)e.GastoCentavos / teto.Value : null;
        var numeros = new NumerosDaDecisao(ponderado, taxa, custoCentavos, multiplo);

        Decisao Fim(Acao acao, bool temCopia = false, long? copia = null, bool segurou = false) => new(
            acao,
            trilha.Where(t => t.Resultado == ResultadoDaRegra.Disparou).Select(t => t.Chave).ToList(),
            trilha, avisos, numeros, temCopia, copia, segurou);

        /* ---- 1. VÍDEO FICA FORA (decidido em 08/10) ------------------------ */
        if (e.Formato != Formato.Imagem)
        {
            var porque = e.Formato switch
            {
                Formato.Misto => "conjunto misto (imagem e vídeo): a metodologia só cobre imagem, e o número dele mistura os dois formatos. Nenhuma ação e nenhuma sugestão de criativo.",
                Formato.Video => "vídeo: a metodologia não cobre o formato (decidido em 08/10). Nenhuma ação e nenhuma sugestão de criativo.",
                _ => "formato do criativo ainda não lido pela carga: sem saber se é vídeo, nenhuma ação até a próxima carga.",
            };
            Passo("video_fora", ResultadoDaRegra.Disparou, porque);
            return Fim(Acao.SEM_BASE);
        }
        Passo("video_fora", ResultadoDaRegra.Passou, "imagem: dentro da metodologia.");

        /* ---- 2. A LEITURA DOS LEADS: estado, não regra ---------------------- */
        if (e.Leitura != Leitura.Medida)
        {
            avisos.Add(new Aviso("leitura", $"{LeituraFalha[e.Leitura]}: sem a leitura completa, \"sem MQL\" não quer dizer nada. Não mexer até ela voltar."));
            return Fim(Acao.AGUARDAR);
        }

        /* ---- 3. SEM TETO NO PPL, O MOTOR NÃO DECIDE DINHEIRO ---------------- */
        if (teto is null)
        {
            Passo("teto_ppl", ResultadoDaRegra.Disparou,
                $"sem teto por MQL no PPL deste cliente: o corte por gasto, a escala e a troca de criativo ficam desligados. Preencha o teto no card. O alerta de {Pct(p.AlertaBudgetFracao)} do budget continua.");
            Passo("corte_sem_mql", ResultadoDaRegra.NaoSeAplica, "sem teto, o corte por gasto fica desligado: não há com o que comparar o gasto.");
            if (e.DiasEntrega < p.NaoTocarDias)
            {
                Passo("nao_tocar", ResultadoDaRegra.Disparou, $"{e.DiasEntrega} dia(s) de entrega: a primeira leitura é no {p.NaoTocarDias}º, e até lá a Meta ainda está aprendendo.");
                return Fim(Acao.AGUARDAR);
            }
            Passo("nao_tocar", ResultadoDaRegra.Passou, $"{e.DiasEntrega} dias de entrega: já pode ler.");
            return Fim(Acao.MANTER);
        }
        var t = teto.Value;
        Passo("teto_ppl", ResultadoDaRegra.Passou, $"teto de {Reais(t)} por MQL, do PPL do cliente.");

        /* ---- 4. CORTE POR GASTO, EM QUALQUER DIA ----------------------------- */
        if (mql == 0)
        {
            var mult = (double)e.GastoCentavos / t;
            var gastoX100 = e.GastoCentavos * 100;
            if (vivo.Ativo == false)
            {
                Passo("corte_sem_mql", ResultadoDaRegra.NaoSeAplica, "sem MQL, mas o conjunto não está ACTIVE na Meta: não há o que pausar.");
            }
            else if (gastoX100 >= Centesimos(p.Corte.PausarEm) * t)
            {
                if (!e.CoberturaCompleta)
                {
                    avisos.Add(new Aviso("cobertura", "a carga não cobre o conjunto desde a primeira entrega: sem MQL na janela não quer dizer sem MQL na vida dele."));
                    var tambemPendente = e.Pendentes > 0
                        ? $" E {e.Pendentes} lead(s) continuam esperando avaliação: classifique antes, porque a cobertura completa libera a pausa."
                        : "";
                    Passo("corte_sem_mql", ResultadoDaRegra.Disparou,
                        $"sem MQL na janela e {Reais(e.GastoCentavos)} gastos, {Vezes(mult)} o teto, mas a carga não cobre o conjunto desde o início: o corte não passa de alerta.{tambemPendente}");
                    return Fim(Acao.ALERTAR, segurou: e.Pendentes > 0);
                }
                if (e.Pendentes > 0)
                {
                    var segurando = e.Memoria?.DiasSegurandoPorPendente;
                    var seguraDias = p.PendenteSeguraDias ?? p.CicloDias;
                    if (segurando is not null && segurando >= seguraDias)
                    {
                        Passo("corte_sem_mql", ResultadoDaRegra.Disparou,
                            $"{Reais(e.GastoCentavos)} gastos sem nenhum MQL, {Vezes(mult)} o teto de {Reais(t)}, e {e.Pendentes} lead(s) esperando avaliação há {segurando} dia(s): o pendente segurou a pausa por {seguraDias} dia(s) e ninguém classificou. Pausa.");
                        avisos.Add(new Aviso("corte_sem_mql",
                            $"{e.Pendentes} lead(s) continuam sem classificação: a pausa saiu mesmo assim (D5). Classifique as respostas no card do cliente, e reative no Gerenciador se o conjunto converter."));
                        return Fim(Acao.PAUSAR);
                    }
                    Passo("corte_sem_mql", ResultadoDaRegra.Disparou,
                        $"sem MQL e {Reais(e.GastoCentavos)} gastos, {Vezes(mult)} o teto, mas {e.Pendentes} lead(s) esperando avaliação: o corte não passa de alerta por {seguraDias} dia(s). Classificar as respostas pendentes antes de pausar.");
                    return Fim(Acao.ALERTAR, segurou: true);
                }
                var statusNaoLido = vivo.Ativo is null ? " O status do conjunto não foi lido: pausar um conjunto já pausado não faz mal." : "";
                Passo("corte_sem_mql", ResultadoDaRegra.Disparou,
                    $"{Reais(e.GastoCentavos)} gastos sem nenhum MQL, {Vezes(mult)} o teto de {Reais(t)}. Pausa em {Fator(p.Corte.PausarEm)}. O corte é por gasto, não por dia.{statusNaoLido}");
                return Fim(Acao.PAUSAR);
            }
            else if (gastoX100 >= Centesimos(p.Corte.AlertarEm) * t)
            {
                Passo("corte_sem_mql", ResultadoDaRegra.Disparou,
                    $"sem MQL e {Reais(e.GastoCentavos)} gastos, já em {Vezes(mult)} o teto: passou do alerta ({Fator(p.Corte.AlertarEm)}), ainda abaixo da pausa ({Fator(p.Corte.PausarEm)}).");
                return Fim(Acao.ALERTAR);
            }
            else
            {
                Passo("corte_sem_mql", ResultadoDaRegra.Passou,
                    $"sem MQL, mas {Reais(e.GastoCentavos)} ainda está abaixo de {Reais((long)Math.Round(p.Corte.AlertarEm * t))} ({Fator(p.Corte.AlertarEm)} o teto).");
            }
        }
        else
        {
            Passo("corte_sem_mql", ResultadoDaRegra.NaoSeAplica, $"tem MQL ({mql}): o corte por gasto não se aplica.");
        }

        /* ---- 5. UM ANÚNCIO POR CONJUNTO (AD-MT-17) --------------------------- */
        if (e.AnunciosAtivos > 1)
        {
            Passo("um_anuncio_por_conjunto", ResultadoDaRegra.Disparou,
                $"{e.AnunciosAtivos} anúncios ativos no conjunto: o motor só escala ou troca criativo em conjunto com 1 anúncio. Manter e olhar os números por anúncio.");
            return Fim(Acao.MANTER);
        }
        Passo("um_anuncio_por_conjunto", ResultadoDaRegra.Passou, $"{e.AnunciosAtivos} anúncio(s) ativo(s) no conjunto: não passa de 1.");

        /* ---- 6. OS DIAS DE APRENDIZADO --------------------------------------- */
        if (e.DiasEntrega < p.NaoTocarDias)
        {
            Passo("nao_tocar", ResultadoDaRegra.Disparou, $"{e.DiasEntrega} dia(s) de entrega: a primeira leitura é no {p.NaoTocarDias}º, e até lá a Meta ainda está aprendendo.");
            return Fim(Acao.AGUARDAR);
        }
        Passo("nao_tocar", ResultadoDaRegra.Passou, $"{e.DiasEntrega} dias de entrega: já pode ler.");
        Passo("ciclo_leitura", ResultadoDaRegra.Info,
            $"depois do {p.NaoTocarDias}º dia de entrega, a escala e a troca de criativo são lidas a cada {p.CicloDias} dias; o corte, todo dia.");

        /* ---- 7. ESCALA ------------------------------------------------------- */
        var okTaxa = e.Avaliados > 0 && ponderadoX100 * 100 >= (long)Math.Round(p.Escala.TaxaMin * 10_000) * e.Avaliados;
        var okCusto = ponderadoX100 > 0 && e.GastoCentavos * 100 <= t * ponderadoX100;
        var okAmostra = e.Avaliados >= p.Escala.PisoAvaliados;
        if (okTaxa && okCusto && okAmostra)
        {
            Passo("gatilho_escala", ResultadoDaRegra.Disparou,
                $"taxa ponderada {Pct(taxa)} ≥ {Pct(p.Escala.TaxaMin)}, {Reais(custoCentavos!.Value)} por MQL ≤ teto de {Reais(t)}, {e.Avaliados} avaliados ≥ {p.Escala.PisoAvaliados}.");
            if (vivo.Ativo != true)
            {
                Passo("reconfirmar_ativo", ResultadoDaRegra.Disparou, vivo.Ativo is null
                    ? "passaria na escala, mas o status do conjunto não foi lido pela carga: reconfirmar que ele está ACTIVE antes de escalar."
                    : "passaria na escala, mas o conjunto não está ACTIVE na Meta: reconfirmar por que ele foi pausado antes de escalar.");
                return Fim(Acao.MANTER);
            }
            var copiasVivas = e.Memoria?.CopiasVivas ?? 0;
            if (copiasVivas >= 1)
            {
                Passo("como_escalar", ResultadoDaRegra.Disparou,
                    $"passaria na escala, mas este original já tem {copiasVivas} cópia viva feita pelo motor: uma cópia por original, nunca mais que dobrar. Manter, e ler a cópia.");
                return Fim(Acao.MANTER);
            }
            if (vivo.OrcamentoDiarioCentavos is null)
            {
                Passo("como_escalar", ResultadoDaRegra.Disparou, "duplicar, nunca editar o que roda. Conjunto em CBO: a verba é da campanha, e a cópia sai sem verba própria.");
                return Fim(Acao.DUPLICAR, temCopia: true, copia: null);
            }
            var diarioCentavos = Math.Max(p.Conjunto.MinCentavos, Math.Min(vivo.OrcamentoDiarioCentavos.Value + p.CopiaIncrementoCentavos, p.TetoMontagemCentavos));
            Passo("como_escalar", ResultadoDaRegra.Disparou,
                $"duplicar, nunca editar o que roda. A cópia nasce PAUSED com {Reais(diarioCentavos)}/dia: {Reais(vivo.OrcamentoDiarioCentavos.Value)} do original + {Reais(p.CopiaIncrementoCentavos)}, entre {Reais(p.Conjunto.MinCentavos)} e o teto de {Reais(p.TetoMontagemCentavos)}.");
            return Fim(Acao.DUPLICAR, temCopia: true, copia: diarioCentavos);
        }
        var falta = new List<string>();
        if (!okTaxa) falta.Add($"taxa {Pct(taxa)} < {Pct(p.Escala.TaxaMin)}");
        if (!okCusto) falta.Add(custoCentavos is null ? "sem custo por MQL" : $"{Reais(custoCentavos.Value)} por MQL acima do teto de {Reais(t)}");
        if (!okAmostra) falta.Add($"{e.Avaliados} avaliados < {p.Escala.PisoAvaliados}");
        Passo("gatilho_escala", ResultadoDaRegra.Passou, $"não escala: {string.Join(", ", falta)}.");

        /* ---- 8. CARO DEMAIS: TROCAR O CRIATIVO ------------------------------- */
        var limiteTroca = (long)Math.Round(p.TrocaMultiploTeto * t);
        var caro = ponderadoX100 > 0 && e.GastoCentavos * 100 * 100 > (long)Centesimos(p.TrocaMultiploTeto) * t * ponderadoX100;
        if (mql > 0 && caro)
        {
            Passo("trocar_criativo", ResultadoDaRegra.Disparou, $"{Reais(custoCentavos!.Value)} por MQL, acima de {Fator(p.TrocaMultiploTeto)} o teto ({Reais(limiteTroca)}).");
            if (!clienteConverte)
                avisos.Add(new Aviso("revisar_oferta", "PEND-014: revisar oferta, formulário e régua antes de trocar criativo"));
            return Fim(Acao.TROCAR_CRIATIVO);
        }
        Passo("trocar_criativo", ResultadoDaRegra.Passou, custoCentavos is null
            ? "sem MQL ponderado: ainda não há custo por MQL para comparar."
            : $"{Reais(custoCentavos.Value)} por MQL ≤ {Fator(p.TrocaMultiploTeto)} o teto ({Reais(limiteTroca)}).");

        /* ---- 9. NADA A FAZER ------------------------------------------------- */
        return Fim(Acao.MANTER);
    }

    /// <summary>O alerta de verba queimada no mês (arvore/alerta-budget.ts). Não depende do teto.</summary>
    public static Aviso? AlertaBudget(long gastoSemMqlNoMesCentavos, long budgetMensalCentavos, ParametrosMotor p)
    {
        if (budgetMensalCentavos <= 0) return null;
        var fracao = (double)gastoSemMqlNoMesCentavos / budgetMensalCentavos;
        if (fracao < p.AlertaBudgetFracao) return null;
        return new Aviso("alerta_budget",
            $"{Pct(fracao)} do budget do mês ({Reais(gastoSemMqlNoMesCentavos)} de {Reais(budgetMensalCentavos)}) foi para anúncio que não deu nenhum MQL. O alerta acende em {Pct(p.AlertaBudgetFracao)}.");
    }

    /// <summary>A frase de cada ação, no lugar do nome em caixa alta (secao-automacao.tsx).</summary>
    public static (string Rotulo, string Frase) RotuloDaAcao(Acao a) => a switch
    {
        Acao.PAUSAR => ("Pausar o conjunto", "queimou verba sem MQL"),
        Acao.ALERTAR => ("Olhar este conjunto", "chegou no teto sem MQL"),
        Acao.DUPLICAR => ("Duplicar com mais verba", "está ganhando"),
        Acao.TROCAR_CRIATIVO => ("Trocar o criativo", "caro: o custo por MQL passou do teto"),
        Acao.AGUARDAR => ("Aguardar", "ainda aprendendo ou sem leitura"),
        Acao.MANTER => ("Manter", "dentro das regras"),
        Acao.SEM_BASE => ("Sem base", "fora da metodologia (vídeo ou misto)"),
        _ => ("Sem histórico", "sem entrega na janela"),
    };

    /// <summary>Escreve na Meta? PAUSAR vira pause_adset; DUPLICAR vira duplicate_adset (cópia PAUSED). O resto só registra.</summary>
    public static bool Escreve(Acao a) => a is Acao.PAUSAR or Acao.DUPLICAR;
}
