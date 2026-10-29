using BlueOS.Core.Modelos;

namespace BlueOS.Core.Motor;

/// <summary>A única função de faixa do motor (AD-MT-12). Porta de planejador/faixa.ts.</summary>
public static class Faixa
{
    /// <summary>
    /// A faixa pelo budget MENSAL, em centavos. Os dois limites de cima são EXCLUSIVOS:
    /// R$ 3.000 exato é f1 (só MOFU); R$ 7.500 exato é f2 (78/22), como no estudo (Igor, 12/10).
    /// </summary>
    public static FaixaId DoBudget(long mensalCentavos, Faixas f)
    {
        if (mensalCentavos < f.MinimoCentavos) return FaixaId.AbaixoMinimo;
        if (mensalCentavos <= f.F2AcimaDeCentavos) return FaixaId.F1Mofu;
        if (mensalCentavos <= f.F3DesdeCentavos) return FaixaId.F2MofuBofu;
        return FaixaId.F3Completo;
    }

    /// <summary>"faixa R$ 1.500 a 3.000": os limites vêm dos parâmetros, nunca de constante.</summary>
    public static string Rotulo(FaixaId faixa, Faixas f)
    {
        static string Sem(long c) => Dinheiro.ReaisRedondos(c).Replace("R$ ", "");
        return faixa switch
        {
            FaixaId.AbaixoMinimo => $"abaixo de {Dinheiro.ReaisRedondos(f.MinimoCentavos)} · estrutura mínima",
            FaixaId.F1Mofu => $"faixa {Dinheiro.ReaisRedondos(f.MinimoCentavos)} a {Sem(f.F2AcimaDeCentavos)}",
            FaixaId.F2MofuBofu => $"faixa {Dinheiro.ReaisRedondos(f.F2AcimaDeCentavos)} a {Sem(f.F3DesdeCentavos)}",
            _ => $"faixa acima de {Dinheiro.ReaisRedondos(f.F3DesdeCentavos)}",
        };
    }

    /// <summary>O que a estrutura vira nessa faixa, em uma linha (setup/cadastro.ts, faixaDoBudgetParaTela).</summary>
    public static string EstruturaEmUmaLinha(FaixaId faixa) => faixa switch
    {
        FaixaId.AbaixoMinimo => "estrutura mínima: só MOFU, conjuntos de pelo menos R$ 20 por dia (fora da faixa medida)",
        FaixaId.F1Mofu => "MOFU 100%, de 2 a 4 conjuntos",
        FaixaId.F2MofuBofu => "MOFU 78% e BOFU 22% (o BOFU só com lista do cliente)",
        _ => "TOFU 5%, MOFU 75% e BOFU 20% (o TOFU só com fatia de R$ 20/dia, o que pede R$ 12.000+)",
    };

    /// <summary>As etapas do funil que a faixa pede. É o que o Copy gera e o que a campanha monta.</summary>
    public static IReadOnlyList<Etapa> EtapasDaFaixa(FaixaId faixa) => faixa switch
    {
        FaixaId.AbaixoMinimo or FaixaId.F1Mofu => [Etapa.MOFU],
        FaixaId.F2MofuBofu => [Etapa.MOFU, Etapa.BOFU],
        _ => [Etapa.TOFU, Etapa.MOFU, Etapa.BOFU],
    };
}
