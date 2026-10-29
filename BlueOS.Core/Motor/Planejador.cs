using BlueOS.Core.Modelos;

namespace BlueOS.Core.Motor;

/* ============================================================================
   PLANEJAR A CAMPANHA: função pura, o budget entra e a estrutura sai.
   ----------------------------------------------------------------------------
   Porta fiel de lib/trafego/motor/planejador/planejar.ts (V1, 12/10/2026).

   A INVARIANTE QUE MANDA: a soma dos conjuntos e do reservado NUNCA passa do
   budget diário. Tudo é centavo inteiro e toda divisão arredonda PARA BAIXO.
   RESERVAR EM VEZ DE REDISTRIBUIR (Paulo, 08/10): BOFU sem lista e TOFU com
   fatia abaixo do mínimo ficam reservados, visíveis, com o porquê.
   ============================================================================ */
public static class Planejador
{
    const int DiasNoMes = 30;
    public const string PublicoAdvantage = "advantage";
    const string PublicoLookalike = "lookalike";
    const string PublicoSemLista = "interesses_decisor";
    const string PublicoBofu = "personalizado_lista";
    const string PublicoTofu = "interesses_amplos";
    const string PublicoTofuComSite = "interesses_amplos_e_site_ou_tag";
    const int BofuMin = 1;
    const int TofuMin = 1;
    static readonly Idade IdadeComAdvantage = new(25, 65);
    static readonly Idade IdadeSemAdvantage = new(25, 64);

    /// <summary>A fatia de uma etapa, em centavos, para baixo, com a fração em milionésimos inteiros.</summary>
    static long FatiaDe(long diarioCentavos, double fracao)
    {
        var n = diarioCentavos * (long)Math.Round(fracao * 1_000_000);
        return (n - (n % 1_000_000)) / 1_000_000;
    }

    static (Idade Idade, int Advantage) Segmentacao(string publico, ParametrosMotor p)
    {
        var adv = publico == PublicoAdvantage ? p.PublicosMofu.AdvantageAudienceAdvantage : p.PublicosMofu.AdvantageAudienceDemais;
        return (adv == 1 ? IdadeComAdvantage : IdadeSemAdvantage, adv);
    }

    static string EmFrase(IReadOnlyList<string> itens) =>
        itens.Count <= 1 ? string.Concat(itens) : $"{string.Join(", ", itens.Take(itens.Count - 1))} e {itens[^1]}";

    public static Plano Planejar(EntradaDoPlano e, ParametrosMotor p)
    {
        if (e.BudgetMensalCentavos < 0)
            throw new ArgumentOutOfRangeException(nameof(e), $"budgetMensalCentavos tem que ser centavo inteiro, não negativo (veio {e.BudgetMensalCentavos}).");

        var avisos = new List<string>();
        var chaves = new List<string> { "faixas_budget" };
        void Chave(string c) { if (!chaves.Contains(c)) chaves.Add(c); }
        var conjuntos = new List<ConjuntoPlanejado>();
        var reservado = new List<Reservado>();

        var mensal = e.BudgetMensalCentavos;
        var diario = mensal / DiasNoMes;
        var faixa = Faixa.DoBudget(mensal, p.Faixas);

        var avisoDaFonte = e.FonteBudget == "gasto_observado"
            ? "o budget veio do gasto observado, não da estratégia do cliente: confira o valor antes de montar."
            : null;

        if (faixa == FaixaId.AbaixoMinimo)
        {
            avisos.Add(
                $"budget abaixo do mínimo medido: {Dinheiro.Reais(mensal)} por mês fica abaixo de {Dinheiro.Reais(p.Faixas.MinimoCentavos)}, onde não há regra medida. " +
                "Estrutura mínima: só MOFU, com conjuntos de pelo menos " + Dinheiro.Reais(p.Conjunto.MinCentavos) + " por dia.");
        }

        Dictionary<Etapa, double> funil = faixa is FaixaId.F1Mofu or FaixaId.AbaixoMinimo
            ? new() { [Etapa.TOFU] = 0, [Etapa.MOFU] = 1, [Etapa.BOFU] = 0 }
            : faixa == FaixaId.F2MofuBofu
                ? new() { [Etapa.TOFU] = 0, [Etapa.MOFU] = p.Faixas.SplitF2[Etapa.MOFU], [Etapa.BOFU] = p.Faixas.SplitF2[Etapa.BOFU] }
                : new() { [Etapa.TOFU] = p.Faixas.SplitF3[Etapa.TOFU], [Etapa.MOFU] = p.Faixas.SplitF3[Etapa.MOFU], [Etapa.BOFU] = p.Faixas.SplitF3[Etapa.BOFU] };

        var restante = diario;
        long Fatia(Etapa et)
        {
            var f = Math.Min(restante, FatiaDe(diario, funil[et]));
            restante -= f;
            return f;
        }

        var bateramNoTeto = new List<string>();

        void Montar(Etapa etapa, long valor, int nMin, int nMax, Func<int, string> publicoDo)
        {
            Chave("conjuntos_por_budget");
            Chave("faixa_conjunto");
            var alvo = (int)(valor / p.Conjuntos.DivisorCentavos);
            var cabemNaTrava = (int)(valor / p.Conjunto.MinCentavos);
            var n = Math.Min(nMax, Math.Min(Math.Max(nMin, alvo), cabemNaTrava));
            if (n < Math.Max(1, nMin))
            {
                var pelaTrava = cabemNaTrava < nMin;
                reservado.Add(new Reservado(etapa, valor,
                    pelaTrava
                        ? $"a fatia do {etapa} dá {Dinheiro.Reais(valor)} por dia e não monta {nMin} conjunto(s) de pelo menos {Dinheiro.Reais(p.Conjunto.MinCentavos)}. Fica reservada."
                        : $"o {etapa} pede pelo menos {nMin} conjunto(s) e os parâmetros deixam só {nMax}. Fica reservada até a nota fechar a conta.",
                    pelaTrava ? "faixa_conjunto" : "conjuntos_por_budget"));
                return;
            }
            Chave("teto_escala");
            Chave("publicos_mofu");
            var bruto = valor / n;
            var porConjunto = Math.Min(bruto, Math.Min(p.TetoMontagemCentavos, p.Conjunto.MaxCentavos));
            if (porConjunto < bruto) bateramNoTeto.Add(etapa.ToString());
            for (var i = 0; i < n; i++)
            {
                var publico = publicoDo(i);
                var (idade, adv) = Segmentacao(publico, p);
                conjuntos.Add(new ConjuntoPlanejado(etapa, publico, porConjunto, idade, adv));
            }
        }

        /* ---- MOFU: existe em toda faixa que monta --------------------------- */
        var fatiaMofu = Fatia(Etapa.MOFU);
        if (fatiaMofu > 0)
        {
            Chave("publicos_mofu");
            var publicos = p.PublicosMofu.Ordem.Select(pub => !e.TemLista && pub == PublicoLookalike ? PublicoSemLista : pub).ToList();
            if (publicos.Count < p.Conjuntos.MofuMin)
            {
                reservado.Add(new Reservado(Etapa.MOFU, fatiaMofu,
                    $"a ordem de públicos do MOFU tem {publicos.Count} público(s), menos que o mínimo de {p.Conjuntos.MofuMin} conjuntos. Fica reservada até a nota corrigir a ordem.",
                    "publicos_mofu"));
            }
            else
            {
                var antes = conjuntos.Count;
                var mofuMin = faixa == FaixaId.AbaixoMinimo ? 1 : p.Conjuntos.MofuMin;
                Montar(Etapa.MOFU, fatiaMofu, mofuMin, Math.Min(p.Conjuntos.MofuMax, publicos.Count), i => publicos[i]);
                var posicao = conjuntos.Skip(antes).ToList().FindIndex(c => c.Publico == PublicoSemLista);
                if (posicao >= 0)
                {
                    avisos.Add(
                        $"sem lista do cliente, o {posicao + 1}º público do MOFU é \"interesses de decisor\", que não tem regra nem medição na metodologia. " +
                        "Com a lista, ele seria o lookalike.");
                }
            }
        }

        /* ---- BOFU: só com lista (decidido em 08/10) ------------------------- */
        var fatiaBofu = Fatia(Etapa.BOFU);
        if (fatiaBofu > 0)
        {
            Chave("fatia_reservada");
            if (!e.TemLista)
            {
                reservado.Add(new Reservado(Etapa.BOFU, fatiaBofu,
                    "o BOFU precisa da lista do cliente para o público personalizado. Sem ela a fatia fica reservada, não vai para o MOFU.",
                    "fatia_reservada"));
            }
            else
            {
                Montar(Etapa.BOFU, fatiaBofu, BofuMin, p.Conjuntos.BofuMax, _ => PublicoBofu);
            }
        }

        /* ---- TOFU: só com fatia de pelo menos o mínimo do dia --------------- */
        var fatiaTofu = Fatia(Etapa.TOFU);
        if (fatiaTofu > 0)
        {
            Chave("fatia_reservada");
            if (fatiaTofu < p.TofuMinimoDiaCentavos)
            {
                reservado.Add(new Reservado(Etapa.TOFU, fatiaTofu,
                    $"a fatia do TOFU dá {Dinheiro.Reais(fatiaTofu)} por dia, abaixo do mínimo de {Dinheiro.Reais(p.TofuMinimoDiaCentavos)}. Fica reservada, não vai para o MOFU.",
                    "fatia_reservada"));
            }
            else
            {
                var antes = conjuntos.Count;
                Montar(Etapa.TOFU, fatiaTofu, TofuMin, p.Conjuntos.TofuMax, _ => e.TemSiteOuTag ? PublicoTofuComSite : PublicoTofu);
                if (conjuntos.Count > antes && !e.TemSiteOuTag)
                    avisos.Add("o TOFU vai só com interesses amplos: o cliente não tem site com pixel nem tag para público de site.");
            }
        }

        var somaConjuntos = conjuntos.Sum(c => c.DiarioCentavos);
        var somaReservado = reservado.Sum(r => r.DiarioCentavos);
        var naoAlocado = diario - somaConjuntos - somaReservado;

        if (bateramNoTeto.Count > 0)
        {
            avisos.Add(
                $"budget acima do que a estrutura comporta: {EmFrase(bateramNoTeto)} {(bateramNoTeto.Count > 1 ? "bateram" : "bateu")} no teto de " +
                $"{Dinheiro.Reais(Math.Min(p.TetoMontagemCentavos, p.Conjunto.MaxCentavos))} por conjunto, e sobram {Dinheiro.Reais(naoAlocado)} por dia sem destino.");
        }
        if (avisoDaFonte is not null) avisos.Add(avisoDaFonte);

        return new Plano(faixa, diario, conjuntos, reservado, naoAlocado, avisos, chaves);
    }
}
