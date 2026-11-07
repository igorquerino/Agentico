using BlueOS.Core.Modelos;

namespace BlueOS.Core.Copy;

/// <summary>O vocabulário de estruturas de copy (lib/copy/estruturas.ts): um nome só para todo mundo.</summary>
public static class Estruturas
{
    public static readonly IReadOnlyList<string> AngulosHeadline =
        ["dor", "desejo_beneficio", "pergunta", "comando", "oferta", "prova_social", "afirmacao_generica", "curiosidade", "urgencia", "outro"];
    public static readonly IReadOnlyList<string> FormasHeadline = ["negacao", "promessa_com_numero", "vocativo"];
    public static readonly IReadOnlyList<string> PapeisSub =
        ["explica_como_funciona", "detalha_o_ganho", "contexto_da_dor", "instrucao", "oferta", "prova", "reforca_o_publico", "fragmento"];
    public static readonly IReadOnlyList<string> TiposCta =
        ["saiba_mais", "ver_conhecer", "testar", "falar", "agendar_solicitar", "comecar", "inscrever", "baixar", "comprar", "outro"];

    public static string Legivel(string estrutura) => estrutura switch
    {
        "dor" => "dor", "desejo_beneficio" => "desejo / benefício", "pergunta" => "pergunta", "comando" => "comando",
        "oferta" => "oferta", "prova_social" => "prova social", "afirmacao_generica" => "afirmação genérica",
        "curiosidade" => "curiosidade", "urgencia" => "urgência",
        "explica_como_funciona" => "explica como funciona", "detalha_o_ganho" => "detalha o ganho", "contexto_da_dor" => "contexto da dor",
        "instrucao" => "instrução", "prova" => "prova", "reforca_o_publico" => "reforça o público", "fragmento" => "fragmento",
        "saiba_mais" => "saiba mais", "ver_conhecer" => "ver / conhecer", "testar" => "testar", "falar" => "falar",
        "agendar_solicitar" => "agendar / solicitar", "comecar" => "começar", "inscrever" => "inscrever", "baixar" => "baixar", "comprar" => "comprar",
        "negacao" => "negação", "promessa_com_numero" => "promessa com número", "vocativo" => "vocativo",
        _ => estrutura.Replace('_', ' '),
    };

    /// <summary>A ÚNICA tradução do tipo do CTA para o enum da Meta (call_to_action_type), a META_CTA_DE do V1
    /// (estruturas.ts): só valores da lista que a Marketing API aceita para anúncio de lead.</summary>
    public static string CtaParaMeta(string tipoCta) => tipoCta switch
    {
        "saiba_mais" or "ver_conhecer" => "LEARN_MORE",
        "testar" => "SIGN_UP",
        "falar" => "CONTACT_US",
        "agendar_solicitar" => "GET_QUOTE",
        "comecar" => "SIGN_UP",
        "inscrever" => "SIGN_UP",
        "baixar" => "DOWNLOAD",
        "comprar" => "SHOP_NOW",
        _ => "LEARN_MORE",
    };

    /// <summary>Peça de estático é MOFU/BOFU; TOFU exige storytelling (vídeo ou carrossel), régua V8 R2.</summary>
    public static TipoPeca TipoDaEtapa(Etapa e) => e == Etapa.TOFU ? TipoPeca.Video : TipoPeca.Estatico;
}

/// <summary>A orientação de nicho que o vault entrega ao Copy: a ORDEM das estruturas por etapa, sem número (AD-2, AD-5).</summary>
public sealed record OrientacaoDeEtapa(Etapa Etapa, IReadOnlyList<string> Headline, IReadOnlyList<string> Sub, IReadOnlyList<string> Cta, IReadOnlyList<string> Evitar);

public static class OrientacaoDoNicho
{
    /// <summary>A orientação padrão, pelas regras do vault (nunca_reuniao_cta_mofu, nunca_prova_sub_mofu, prefira_contexto_ou_fragmento_sub).</summary>
    static readonly Dictionary<Etapa, OrientacaoDeEtapa> Padrao = new()
    {
        [Etapa.TOFU] = new(Etapa.TOFU, ["dor", "pergunta", "curiosidade"], ["contexto_da_dor", "fragmento"], ["saiba_mais", "ver_conhecer"], ["oferta", "prova_social"]),
        [Etapa.MOFU] = new(Etapa.MOFU, ["dor", "desejo_beneficio", "comando", "pergunta"], ["contexto_da_dor", "fragmento", "explica_como_funciona"], ["ver_conhecer", "saiba_mais", "testar"], ["prova", "agendar_solicitar", "negacao"]),
        [Etapa.BOFU] = new(Etapa.BOFU, ["oferta", "prova_social", "urgencia", "comando"], ["prova", "oferta", "detalha_o_ganho"], ["agendar_solicitar", "falar", "comecar"], ["afirmacao_generica"]),
    };

    /// <summary>Ajustes por nicho: o que o estudo (586 estáticos, 82 clientes) mediu por nicho. Sem nota, vale o padrão.</summary>
    static readonly Dictionary<string, Dictionary<Etapa, OrientacaoDeEtapa>> PorNicho = new()
    {
        ["imobiliario"] = new()
        {
            [Etapa.MOFU] = new(Etapa.MOFU, ["dor", "comando", "desejo_beneficio"], ["contexto_da_dor", "explica_como_funciona"], ["ver_conhecer", "testar"], ["prova", "pergunta"]),
        },
        ["financeiro-credito-seguros"] = new()
        {
            [Etapa.MOFU] = new(Etapa.MOFU, ["desejo_beneficio", "dor", "pergunta"], ["fragmento", "contexto_da_dor"], ["saiba_mais", "ver_conhecer"], ["prova", "negacao"]),
            [Etapa.BOFU] = new(Etapa.BOFU, ["oferta", "urgencia", "prova_social"], ["oferta", "prova"], ["agendar_solicitar", "falar"], ["afirmacao_generica"]),
        },
        ["saude"] = new()
        {
            [Etapa.MOFU] = new(Etapa.MOFU, ["pergunta", "dor", "desejo_beneficio"], ["contexto_da_dor", "explica_como_funciona"], ["ver_conhecer", "saiba_mais"], ["prova", "comando"]),
        },
        ["tecnologia-infra-dev"] = new()
        {
            [Etapa.MOFU] = new(Etapa.MOFU, ["comando", "dor", "curiosidade"], ["explica_como_funciona", "fragmento"], ["testar", "ver_conhecer"], ["prova", "oferta"]),
        },
        ["industria-logistica"] = new()
        {
            [Etapa.MOFU] = new(Etapa.MOFU, ["dor", "pergunta", "comando"], ["contexto_da_dor", "explica_como_funciona"], ["ver_conhecer", "saiba_mais"], ["prova", "urgencia"]),
        },
        ["juridico"] = new()
        {
            [Etapa.MOFU] = new(Etapa.MOFU, ["dor", "desejo_beneficio", "pergunta"], ["contexto_da_dor", "fragmento"], ["saiba_mais", "ver_conhecer"], ["prova", "comando"]),
        },
    };

    public static OrientacaoDeEtapa Para(string? nichoSlug, Etapa etapa)
    {
        if (nichoSlug is not null && PorNicho.TryGetValue(nichoSlug, out var n) && n.TryGetValue(etapa, out var o)) return o;
        return Padrao[etapa];
    }

    /// <summary>ok | sem_nicho | nicho_sem_orientacao: o estado que a etapa Nicho do Copy mostra.</summary>
    public static string Estado(string? nichoSlug) =>
        nichoSlug is null ? "sem_nicho" : PorNicho.ContainsKey(nichoSlug) ? "ok" : "nicho_sem_orientacao";
}
