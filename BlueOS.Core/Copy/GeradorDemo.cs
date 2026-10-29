using System.Text.RegularExpressions;
using BlueOS.Core.Modelos;

namespace BlueOS.Core.Copy;

/* ============================================================================
   O CRIVO DA COPY (lib/copy/crivo.ts, em código): confere a peça contra a régua
   V8 e as regras FECHADAS do vault. Reprovada regera UMA vez; se reprova de
   novo, a peça existe com crivo_ok = false e a publicação não a sobe.
   ============================================================================ */
public static class Crivo
{
    static readonly Regex Travessao = new(@"\s[—–-]\s|—", RegexOptions.Compiled);
    static readonly Regex Negacao = new(@"^(n[aã]o|nunca|pare de|chega de|sem |jamais)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Reuniao = new(@"reuni[aã]o|agend|call|bate-papo|conversa com", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Os problemas da peça, em texto. Vazio = passou.</summary>
    public static List<string> Conferir(PecaDeCopy p, string? nomeDaMarca)
    {
        var problemas = new List<string>();
        var tudo = $"{p.Headline}\n{p.Sub}\n{p.Cta}";

        /* R10: travessão é marca de texto de IA. */
        if (Travessao.IsMatch(tudo)) problemas.Add("R10: travessão na peça");

        /* nunca_negacao_headline (vault, fechada por decisão). */
        if (Negacao.IsMatch(p.Headline.Trim()) || p.FormasHeadline.Contains("negacao"))
            problemas.Add("nunca_negacao_headline: a headline abre com negação");

        if (p.Etapa == Etapa.MOFU)
        {
            /* nunca_reuniao_cta_mofu (vault, fechada, pareado). */
            if (Reuniao.IsMatch(p.Cta) || p.TipoCta == "agendar_solicitar")
                problemas.Add("nunca_reuniao_cta_mofu: pedido de reunião no CTA do MOFU");
            /* nunca_prova_sub_mofu (vault, fechada por decisão, Igor 11/10). */
            if (p.PapelSub == "prova") problemas.Add("nunca_prova_sub_mofu: prova na SUB do MOFU");
        }

        /* R1: TOFU não cita produto, solução ou marca. */
        if (p.Etapa == Etapa.TOFU && !string.IsNullOrWhiteSpace(nomeDaMarca)
            && tudo.Contains(nomeDaMarca, StringComparison.OrdinalIgnoreCase))
            problemas.Add("R1: o TOFU cita a marca");

        /* R3: CTA de estático é texto de botão, até 4 palavras. */
        if (p.Tipo == TipoPeca.Estatico && p.Cta.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 4)
            problemas.Add("R3: CTA de estático com mais de 4 palavras");

        if (string.IsNullOrWhiteSpace(p.Headline) || string.IsNullOrWhiteSpace(p.Sub) || string.IsNullOrWhiteSpace(p.Cta))
            problemas.Add("peça incompleta: abertura, fecho e CTA são obrigatórios");

        return problemas;
    }
}

/* ============================================================================
   O AGENTE DE COPY DE DEMONSTRAÇÃO: escreve as peças a partir do nicho, do ICP
   e do produto, com a ESTRUTURA DECLARADA, e passa cada uma pelo crivo.
   ----------------------------------------------------------------------------
   No V1 quem escreve é o modelo (Anthropic), pelo gravador. Aqui o objetivo é o
   MVP andar de ponta a ponta sem chave de API: as regras são as mesmas
   (R1 mecanismo por etapa, R2 formato, R3 papéis, R5 um rótulo de ICP, R7
   variedade dentro da estrutura, R8 uma variável por peça, R10 sem travessão).
   ============================================================================ */
public static class GeradorDemo
{
    public sealed record Pedido(Etapa Etapa, TipoPeca Tipo, int NPecas);

    public static LoteDeCopy Gerar(Cliente c, IReadOnlyList<Pedido> pedido, string? briefing, int numeroDoLote, string vaultSha)
    {
        var voc = Vocabulario.Para(c.NichoSlug);
        var marca = string.IsNullOrWhiteSpace(c.Produto.NomeCorreto) ? c.Nome : c.Produto.NomeCorreto!;
        var icp = string.IsNullOrWhiteSpace(c.Icp.CargoDecisor) ? voc.IcpSingular : c.Icp.CargoDecisor!;
        var dores = c.Icp.DoresPrincipais.Count > 0 ? c.Icp.DoresPrincipais : voc.Sintomas.ToList();
        var ganhos = c.Icp.DesejosPrincipais.Count > 0 ? c.Icp.DesejosPrincipais : voc.Ganhos.ToList();
        var promessa = c.Estrategia.PromessaPrincipal;
        var semente = (c.Slug + numeroDoLote).GetHashCode();
        var rnd = new Random(semente);

        var lote = new LoteDeCopy
        {
            Id = Guid.NewGuid().ToString("n"),
            Numero = numeroDoLote,
            Modelo = "demo-copy-v8 (sem API: regras da régua V8 e do vault em código)",
            Briefing = briefing,
            VaultSha = vaultSha,
            NichoSlug = c.NichoSlug,
            EstadoDoNicho = OrientacaoDoNicho.Estado(c.NichoSlug),
            TokensIn = 0,
            TokensOut = 0,
        };

        var numero = 0;
        foreach (var item in pedido)
        {
            var o = OrientacaoDoNicho.Para(c.NichoSlug, item.Etapa);
            for (var i = 0; i < item.NPecas; i++)
            {
                numero++;
                /* R7: a variedade é de ângulo e de dor DENTRO da estrutura: cada peça
                   é uma hipótese diferente para o mesmo ICP. */
                var angulo = o.Headline[i % o.Headline.Count];
                var papel = o.Sub[(i / o.Headline.Count + i) % o.Sub.Count];
                var tipoCta = o.Cta[i % o.Cta.Count];
                var dor = dores[(i + rnd.Next(0, 2)) % dores.Count];
                var sintoma = voc.Sintomas[i % voc.Sintomas.Count];
                var mecanismoDaDor = voc.MecanismosDaDor[i % voc.MecanismosDaDor.Count];
                var ganho = ganhos[i % ganhos.Count];
                var prova = voc.Provas[i % voc.Provas.Count];
                var termo = voc.TermosReais[(i * 2) % voc.TermosReais.Count];

                var peca = new PecaDeCopy
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Etapa = item.Etapa,
                    Tipo = item.Tipo,
                    Numero = numero,
                    AnguloHeadline = angulo,
                    PapelSub = papel,
                    TipoCta = tipoCta,
                    Icp = icp,
                    Hipotese = $"ângulo {Estruturas.Legivel(angulo)} sobre \"{Curto(dor)}\" para {icp}",
                };
                Escrever(peca, item.Etapa, angulo, papel, tipoCta, icp, dor, sintoma, mecanismoDaDor, ganho, prova, termo, marca, promessa, voc);

                var problemas = Crivo.Conferir(peca, marca);
                if (problemas.Count > 0)
                {
                    /* Reprovada regera UMA vez, só ela, com o motivo. */
                    peca.Regeneracoes.Add(string.Join("; ", problemas));
                    var angulo2 = o.Headline[(i + 1) % o.Headline.Count];
                    var papel2 = o.Sub.FirstOrDefault(s => s != "prova") ?? papel;
                    var cta2 = o.Cta.FirstOrDefault(x => x != "agendar_solicitar") ?? tipoCta;
                    peca.AnguloHeadline = angulo2; peca.PapelSub = papel2; peca.TipoCta = cta2;
                    peca.FormasHeadline.Clear();
                    Escrever(peca, item.Etapa, angulo2, papel2, cta2, icp, dor, sintoma, mecanismoDaDor, ganho, prova, termo, marca, promessa, voc);
                    problemas = Crivo.Conferir(peca, marca);
                }
                peca.CrivoOk = problemas.Count == 0;
                peca.MotivoDoCrivo = problemas.Count == 0 ? null : string.Join("; ", problemas);
                lote.Pecas.Add(peca);
            }
        }
        return lote;
    }

    static string Curto(string s) => s.Length > 48 ? s[..48] + "…" : s;
    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static void Escrever(PecaDeCopy p, Etapa etapa, string angulo, string papel, string tipoCta, string icp,
        string dor, string sintoma, string mecanismoDaDor, string ganho, string prova, string termo, string marca, string? promessa, VocabularioDoNicho voc)
    {
        var video = p.Tipo == TipoPeca.Video;
        /* ---- a abertura (HEAD / HOOK): carrega C1 (identificação, via B) ------ */
        p.Headline = angulo switch
        {
            "dor" => Cap(sintoma),
            "pergunta" => etapa == Etapa.TOFU
                ? $"Quantas vezes por mês {voc.IcpPlural} vivem isto: {sintoma}?"
                : $"Quantos {termo}s a sua operação perde quando {dor.ToLowerInvariant()}?",
            "comando" => $"Resolva {termo} antes que {Curto(dor).ToLowerInvariant()} vire rotina",
            "desejo_beneficio" => Cap(ganho),
            "curiosidade" => $"O número de {termo} que {voc.IcpPlural} não olham, e que explica o mês fraco",
            "prova_social" => Cap(prova),
            "oferta" => $"{marca}: implantação em 15 dias, com {ganho.Split(',')[0]}",
            "urgencia" => $"Últimas 5 vagas de implantação de {marca} neste mês",
            "afirmacao_generica" => $"{Cap(termo)} com dono, prazo e número, todo dia",
            _ => Cap(sintoma),
        };
        if (video) p.Headline = $"{Cap(icp)}, {char.ToLowerInvariant(p.Headline[0])}{p.Headline[1..]}";

        /* ---- o fecho (SUB / BODY): carrega C3 no objeto da etapa (R1) --------- */
        var mecanismoDaSolucao = etapa switch
        {
            Etapa.TOFU => Cap(mecanismoDaDor) + ".",
            Etapa.MOFU => $"{marca} {ganho}.",
            _ => $"{Cap(prova)}. {marca}: {promessa ?? ganho}.",
        };
        p.Sub = papel switch
        {
            "explica_como_funciona" => etapa == Etapa.TOFU ? Cap(mecanismoDaDor) + "." : $"Como funciona: {marca} {ganho}.",
            "detalha_o_ganho" => etapa == Etapa.TOFU ? Cap(mecanismoDaDor) + "." : $"{Cap(ganho)}, com {marca}.",
            "contexto_da_dor" => Cap(mecanismoDaDor) + (etapa == Etapa.TOFU ? "." : $" {marca} muda isso."),
            "instrucao" => etapa == Etapa.TOFU ? $"Veja o sinal antes do mês fechar: {mecanismoDaDor}." : $"Comece pelo {termo}: {marca} {ganho}.",
            "oferta" => $"{marca} com implantação em 15 dias e {ganho}.",
            "prova" => mecanismoDaSolucao,
            "reforca_o_publico" => $"Para {voc.IcpPlural} que vivem isto todo mês. " + (etapa == Etapa.TOFU ? "" : $"{marca} {ganho}."),
            "fragmento" => etapa == Etapa.TOFU ? $"{Cap(mecanismoDaDor)}." : $"{Cap(ganho)}. {marca}.",
            _ => mecanismoDaSolucao,
        };
        if (video) p.Sub = $"{p.Sub} Mostre na tela: {termo}, antes e depois.";

        /* ---- o CTA: estático é texto de botão (até 4 palavras); vídeo é falado -- */
        p.Cta = tipoCta switch
        {
            "saiba_mais" => video ? "Toque em saiba mais e veja o mecanismo" : "Saiba mais",
            "ver_conhecer" => video ? "Clique aqui embaixo e veja como funciona" : "Ver como funciona",
            "testar" => video ? "Teste grátis clicando aqui embaixo" : "Testar grátis",
            "falar" => video ? "Fale com um especialista pelo botão" : "Falar com especialista",
            "agendar_solicitar" => video ? "Solicite a proposta clicando abaixo" : "Solicitar proposta",
            "comecar" => video ? "Comece agora pelo botão" : "Começar agora",
            "inscrever" => video ? "Inscreva-se pelo botão abaixo" : "Inscrever-se",
            "baixar" => video ? "Baixe o guia clicando aqui" : "Baixar o guia",
            "comprar" => video ? "Contrate agora pelo botão" : "Contratar agora",
            _ => "Saiba mais",
        };
        p.FormasHeadline.Clear();
        if (Regex.IsMatch(p.Headline, @"\d")) p.FormasHeadline.Add("promessa_com_numero");
        if (video) p.FormasHeadline.Add("vocativo");
    }

    /// <summary>Ajuste por instrução (o /ajustar do V1): troca a parte que a instrução cita.</summary>
    public static void Ajustar(PecaDeCopy p, string instrucao, string? marca)
    {
        var i = instrucao.ToLowerInvariant();
        if (i.Contains("cta")) p.Cta = p.Tipo == TipoPeca.Video ? "Veja como funciona clicando aqui" : "Ver como funciona";
        else if (i.Contains("sub") || i.Contains("fecho") || i.Contains("body")) p.Sub = p.Sub.Replace(". ", ", sem planilha. ");
        else if (i.Contains("curt")) { p.Headline = string.Join(' ', p.Headline.Split(' ').Take(7)); }
        else p.Headline = p.Headline.EndsWith('?') ? p.Headline : p.Headline + ", todo mês";
        p.Versao++;
        var problemas = Crivo.Conferir(p, marca);
        p.CrivoOk = problemas.Count == 0;
        p.MotivoDoCrivo = problemas.Count == 0 ? null : string.Join("; ", problemas);
    }
}
