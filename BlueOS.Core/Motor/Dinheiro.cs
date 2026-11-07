using System.Globalization;
using System.Text.RegularExpressions;

namespace BlueOS.Core.Motor;

/// <summary>Dinheiro: centavos inteiros dentro do motor, reais só no texto (AD-MT-10).</summary>
public static class Dinheiro
{
    /// <summary>R$ 1.234,56 sem depender do ICU de quem roda (como o brl() do planejador).</summary>
    public static string Reais(long centavos)
    {
        var negativo = centavos < 0;
        centavos = Math.Abs(centavos);
        var inteiro = (centavos / 100).ToString(CultureInfo.InvariantCulture);
        var comMilhar = Regex.Replace(inteiro, @"\B(?=(\d{3})+(?!\d))", ".");
        var dec = (centavos % 100).ToString("00", CultureInfo.InvariantCulture);
        return $"{(negativo ? "-" : "")}R$ {comMilhar},{dec}";
    }

    /// <summary>A verba que pode não ter sido lida (CBO, foto vencida): "—" no lugar de um número inventado.</summary>
    public static string Reais(long? centavos) => centavos is { } c ? Reais(c) : "—";

    /// <summary>R$ 3.000 / R$ 25 / R$ 32,37: sem ",00" quando é inteiro (automacao-criar.tsx).</summary>
    public static string ReaisCurto(long centavos)
    {
        var t = Reais(centavos);
        return t.EndsWith(",00") ? t[..^3] : t;
    }

    /// <summary>A verba que pode não ter sido lida (CBO, foto vencida): "—" no lugar de um número inventado.</summary>
    public static string ReaisCurto(long? centavos) => centavos is { } c ? ReaisCurto(c) : "—";

    /// <summary>
    /// O teto por MQL em centavos, ou null quando não há teto que sirva: vazio, zero, negativo ou abaixo de 1 centavo
    /// (R$ 0,004 vira 0 centavo e dividiria por zero). É o guarda DEPOIS da conversão, que é a que o motor usa.
    /// </summary>
    public static long? TetoEmCentavos(decimal? reais) => reais is > 0 && CentavosDe(reais.Value) is var c && c >= 1 ? c : null;

    /// <summary>R$ 1.500, sem centavos: os limites de faixa são redondos.</summary>
    public static string ReaisRedondos(long centavos)
    {
        var inteiro = (centavos / 100).ToString(CultureInfo.InvariantCulture);
        return "R$ " + Regex.Replace(inteiro, @"\B(?=(\d{3})+(?!\d))", ".");
    }

    public static long CentavosDe(decimal reais) => (long)Math.Round(reais * 100m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// O valor em reais que a pessoa digitou: "3.000", "3000", "3.000,50" e "3000.50" valem
    /// (lib/trafego/motor/setup/budget.ts, reaisDoTexto). null = vazio ou texto que não é número.
    /// </summary>
    public static decimal? ReaisDoTexto(string? texto)
    {
        if (texto is null) return null;
        var s = Regex.Replace(texto, @"[R$\s]", "");
        if (s == "") return null;
        string normalizado;
        if (s.Contains(','))
            normalizado = s.Replace(".", "").Replace(',', '.');
        else if (Regex.IsMatch(s, @"^\d{1,3}(\.\d{3})+$"))
            normalizado = s.Replace(".", "");
        else
            normalizado = s;
        return decimal.TryParse(normalizado, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public const decimal BudgetMaximoReais = 10_000_000m;

    /// <summary>Valida o budget vindo da tela. Devolve o valor ou o motivo.</summary>
    public static (bool Ok, decimal? Valor, string? Erro) BudgetDoTexto(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return (true, null, null);
        var v = ReaisDoTexto(texto);
        if (v is null || v <= 0 || CentavosDe(v.Value) < 1) return (false, null, "o budget mensal tem de ser um valor em reais maior que zero.");
        if (v > BudgetMaximoReais) return (false, null, "o budget mensal passou de R$ 10 milhões: confira os dígitos.");
        return (true, Math.Round(v.Value, 2), null);
    }

    /// <summary>"3.000" ou "3.000,50": como o valor aparece no campo.</summary>
    public static string NoCampo(decimal? v)
    {
        if (v is null) return "";
        var c = CentavosDe(v.Value);
        return ReaisCurto(c).Replace("R$ ", "");
    }

    /// <summary>O percentual inteiro, arredondado no meio para cima como o Math.round do V1 (não o bancário do .NET).</summary>
    public static string Pct(double v) => $"{Math.Round(v * 100, MidpointRounding.AwayFromZero)}%";
    public static string Vezes(double v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',') + "×";
    public static string Fator(double v) => v.ToString(CultureInfo.InvariantCulture).Replace('.', ',') + "×";
}
