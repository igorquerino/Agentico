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

    /* O nicho sugerido pelo texto da ficha (setor do ICP, segmento, descrição do produto, nome): o Start
       automático propõe e o gestor confirma. Palavras do mais específico ao mais genérico; nada casou = "outro". */
    static readonly (string Slug, string[] Marcas)[] Pistas =
    [
        ("imobiliario", ["imobili", "imovel", "imoveis", "corretor", "loteamento", "incorporadora"]),
        ("saude", ["saude", "clinic", "medic", "odont", "dentist", "hospital", "fisioter", "psicolog", "nutri"]),
        ("educacao", ["escola", "curso", "educa", "faculdade", "ensino", "treinamento", "mentoria"]),
        ("financeiro-credito-seguros", ["credito", "seguro", "seguradora", "banco", "emprest", "consorcio", "fintech", "financiamento"]),
        ("contabil-financeiro", ["contab", "fiscal", "tribut", "bpo financeiro"]),
        ("juridico", ["advoc", "juridic", "direito", "escritorio de advocacia"]),
        ("construcao-engenharia", ["constru", "engenhar", "obra", "arquitet", "reforma"]),
        ("automotivo", ["automot", "veicul", "carro", "oficina", "concession", "locadora de carro", "locacao de veic"]),
        ("beleza-estetica", ["belez", "estetic", "salao", "cosmet", "barbearia"]),
        ("alimentacao-food", ["aliment", "restaurant", "food", "delivery", "comida", "lanchonete", "padaria"]),
        ("industria-logistica", ["industria", "logistic", "transport", "frota", "fabric", "armazem"]),
        ("marketing-agencias", ["marketing", "agencia", "trafego", "midia", "publicidade", "branding"]),
        ("varejo-distribuicao", ["varejo", "loja", "distribui", "atacado", "e-commerce", "ecommerce", "franquia"]),
        ("rh-gestao-pessoas", ["recrut", "gestao de pessoas", "recursos humanos", "departamento pessoal", " rh "]),
        ("tecnologia-infra-dev", ["software", "saas", "tecnolog", "aplicativo", "app ", "sistema", "plataforma", "startup", "dados", "monitoramento"]),
        ("servicos-profissionais", ["consultor", "servico", "assessor", "terceiriz"]),
    ];

    /// <summary>A marca casa no INÍCIO de uma palavra ("curso" não casa "recursos humanos", "obra" não casa "sobrancelha");
    /// a marca que termina em espaço (" rh ", "app ") casa também o fim da palavra.</summary>
    public static string Sugerir(string? texto)
    {
        var t = " " + Palavras(texto) + " ";
        if (t.Trim().Length == 0) return "outro";
        foreach (var (slug, marcas) in Pistas)
            if (marcas.Any(m => Palavras(m) is { Length: > 0 } pm && t.Contains(" " + pm + (m.EndsWith(' ') ? " " : "")))) return slug;
        return "outro";
    }

    /// <summary>Sem acento e sem caixa, com tudo que não é letra nem número virando um espaço só.</summary>
    static string Palavras(string? s)
    {
        var n = Motor.Regua.Normalizar(s);
        var sb = new System.Text.StringBuilder(n.Length);
        foreach (var ch in n) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

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
