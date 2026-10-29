namespace BlueOS.Core.Setup;

/// <summary>Os 17 slugs de nicho da base do Intel-Blue (setup/cadastro.ts). Lista fechada: nicho
/// inventado não casa com nota nenhuma do vault, e a orientação de copy sai vazia.</summary>
public static class Nichos
{
    public static readonly IReadOnlyList<string> Slugs =
    [
        "alimentacao-food",
        "automotivo",
        "beleza-estetica",
        "construcao-engenharia",
        "contabil-financeiro",
        "educacao",
        "financeiro-credito-seguros",
        "imobiliario",
        "industria-logistica",
        "juridico",
        "marketing-agencias",
        "outro",
        "rh-gestao-pessoas",
        "saude",
        "servicos-profissionais",
        "tecnologia-infra-dev",
        "varejo-distribuicao",
    ];

    public static bool Valido(string? v) => v is not null && Slugs.Contains(v);

    static readonly Dictionary<string, string> Acento = new()
    {
        ["alimentacao"] = "alimentação", ["estetica"] = "estética", ["construcao"] = "construção", ["contabil"] = "contábil",
        ["educacao"] = "educação", ["credito"] = "crédito", ["imobiliario"] = "imobiliário", ["industria"] = "indústria",
        ["logistica"] = "logística", ["juridico"] = "jurídico", ["agencias"] = "agências", ["gestao"] = "gestão",
        ["saude"] = "saúde", ["servicos"] = "serviços", ["distribuicao"] = "distribuição",
        ["rh"] = "RH",
    };

    /// <summary>O rótulo que a tela mostra, derivado do slug ("Rh / Gestão / Pessoas" vira "RH / Gestão / Pessoas").</summary>
    public static string Rotulo(string slug)
    {
        if (slug == "outro") return "Outro / não classificado";
        return string.Join(" / ", slug.Split('-').Select(p =>
        {
            var palavra = Acento.GetValueOrDefault(p, p);
            return palavra == palavra.ToUpperInvariant() ? palavra : char.ToUpperInvariant(palavra[0]) + palavra[1..];
        }));
    }
}
