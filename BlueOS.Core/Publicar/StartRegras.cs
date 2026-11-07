using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueOS.Core.Api;
using BlueOS.Core.Copy;
using BlueOS.Core.Meta;
using BlueOS.Core.Modelos;

namespace BlueOS.Core.Publicar;

/* ============================================================================
   AS REGRAS DO PUBLICAR DO START, puras: porta das peças sem banco e sem Meta
   de lib/trafego/publicar/start.ts, pedido.ts, peca.ts e post-da-pagina.ts.
   ----------------------------------------------------------------------------
   O que sobe (1 campanha ABO OUTCOME_LEADS, 1 formulário da régua, N conjuntos,
   N anúncios, tudo PAUSADO) e por que é idempotente (o hash da estrutura) estão
   descritos em BlueOS.Api/Dominio/Publicador.cs, que é quem fala com o banco
   e com a Graph. Aqui mora o que o teste cobra campo a campo.
   ============================================================================ */
public sealed record InteresseDaMeta(string Id, string Nome);

/// <summary>Os nomes que o gestor trocou no lápis do passo 2. Ausente, nulo ou igual ao recomendado = o recomendado.</summary>
public sealed record NomesEditados(string? Campanha, IReadOnlyList<string?>? Conjuntos, IReadOnlyList<string?>? Anuncios);
public sealed record NomesFinais(string Campanha, IReadOnlyList<string> Conjuntos, IReadOnlyList<string> Anuncios);

/// <summary>A estrutura DECLARADA da peça (copy por nicho, AD-4). Nula quando a peça não declarou (legado ou editada à mão).</summary>
public sealed record Declaracao(string AnguloHeadline, IReadOnlyList<string> FormaHeadline, string PapelSub, string TipoCta);

/// <summary>A linha de `trafego_anuncio_peca`, menos o `ad_id`, que só a Meta dá.</summary>
public sealed record LinhaDaPeca(string? PecaId, string? DesignerPecaId, Etapa? EtapaConjunto, string Headline, string Corpo, string Cta, string TextoHash, string? CrivoJson, string? VaultSha);

/// <summary>O que sobe num anúncio: o nome com a etiqueta, o texto e o botão da Meta.</summary>
public sealed record PublicacaoPreparada(string Nome, TextoDoAnuncio Texto, string? MetaCta, LinhaDaPeca Linha);

public sealed record ConjuntoCriadoNoStart(string Nome, string Etapa, string Publico, string? AdsetId);
public sealed record AnuncioCriadoNoStart(string Nome, string AdsetId, string? AdId, bool Congelado, string? PostId);

public sealed class RegistroDoStart
{
    public string Id { get; set; } = "";
    public string ClienteSlug { get; set; } = "";
    public string EstruturaHash { get; set; } = "";
    /// <summary>criando | parcial | ok | erro | sem_criativo</summary>
    public string Estado { get; set; } = "criando";
    public string? CampanhaId { get; set; }
    public string? FormularioId { get; set; }
    public string? ImagemHash { get; set; }
    public List<ConjuntoCriadoNoStart> Conjuntos { get; set; } = new();
    public List<AnuncioCriadoNoStart> Anuncios { get; set; } = new();
    public List<string> Avisos { get; set; } = new();
    public string? Erro { get; set; }
    /// <summary>O carimbo como o banco o escreveu (texto): é contra ele que a retomada disputa a linha.</summary>
    public string AtualizadoEm { get; set; } = "";
}

public sealed record AnuncioNaMao(string Nome, string Conjunto, string? AdsetId, string Corpo, string Headline, string Cta, string? ImagemHash);
/// <summary>A cola do gestor quando o criativo fica com ele: texto, botão, UTM e a arte de cada anúncio.</summary>
public sealed record CriativoNaMao(string Motivo, string UrlTags, string? ImagemHash, string? FormularioId, string Link, IReadOnlyList<AnuncioNaMao> Anuncios);

public sealed class PedidoDoStart
{
    public List<string> DesignerPecaIds { get; set; } = new();
    /// <summary>O piloto de cada etapa (o que o desktop sabe); a API o converte em uma arte por conjunto.</summary>
    public Dictionary<string, string>? Pilotos { get; set; }
    public TextoDoAnuncio? Texto { get; set; }
    public string? PoliticaUrl { get; set; }
    public string? Link { get; set; }
    public string? FormularioId { get; set; }
    public NomesEditados? Nomes { get; set; }
    public List<string?>? Etapas { get; set; }
    public List<List<InteresseDaMeta>?>? Interesses { get; set; }
}

public sealed class ResultadoDoStart
{
    public bool Ok { get; set; }
    public bool JaPublicada { get; set; }
    public bool Pausado { get; set; } = true;
    public string? CampanhaId { get; set; }
    public string? FormularioId { get; set; }
    public List<ConjuntoCriadoNoStart> Conjuntos { get; set; } = new();
    public List<AnuncioCriadoNoStart> Anuncios { get; set; } = new();
    public string? Link { get; set; }
    public List<string> Avisos { get; set; } = new();
    public string? EstruturaHash { get; set; }
    public bool SemCriativo { get; set; }
    public CriativoNaMao? CriativoNaMao { get; set; }
    public string? Recado { get; set; }
    /// <summary>Por onde a escrita foi (ou iria): `graph` ou `mcp` (o MCP oficial da Meta).</summary>
    public string Via { get; set; } = "graph";
    /* a recusa */
    public string? Codigo { get; set; }
    public string? Motivo { get; set; }
    public bool Simulado { get; set; }
    public RegistroDoStart? Parcial { get; set; }

    public static ResultadoDoStart Falha(string codigo, string motivo, string? hash = null, bool simulado = false, RegistroDoStart? parcial = null) =>
        new() { Ok = false, Codigo = codigo, Motivo = motivo, EstruturaHash = hash, Simulado = simulado, Parcial = parcial };
}

public static class StartRegras
{
    public const string Pais = "BR";
    /// <summary>O botão do anúncio de cadastro quando a peça não declara o CTA (vault, `receita_campanha`).</summary>
    public const string CtaPadrao = "LEARN_MORE";
    public static readonly IReadOnlyList<string> EtapasDoFunil = ["TOFU", "MOFU", "BOFU"];
    public const int NomeMax = 200;
    public const int NomeTeto = 200;
    public const int NomeBaseTeto = 129;
    /// <summary>Por quanto tempo um registro `criando` quer dizer "alguém está publicando agora".</summary>
    public static readonly TimeSpan PrazoDaPublicacao = TimeSpan.FromMinutes(6);
    public const string AvisoDoCriativoNaMao =
        "a Meta não deixa este app criar criativo (o app da squad está em modo de desenvolvimento, tarefa #5000): a campanha, o formulário e os conjuntos estão criados e pausados, e o criativo e o anúncio ficam para subir no Gerenciador.";
    public const string EscopoPostNaPagina = "pages_manage_posts";

    /* ---- AS UTMs DO ANÚNCIO: em `url_tags`, nunca na query do link ---------- */
    public const string UtmsDoAnuncio =
        "utm_source=facebook&utm_medium=paid&utm_campaign={{campaign.name}}&utm_content={{ad.name}}&utm_term={{adset.name}}&campaign_id={{campaign.id}}&adset_id={{adset.id}}&ad_id={{ad.id}}";

    static readonly string[] DaMeta = ["facebook.com", "fb.com", "fb.me", "messenger.com", "m.me"];
    static readonly Regex Http = new("^https?://", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Dominio = new("^https?://([^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Json(object v) => JsonSerializer.Serialize(v, GraphRegras.JsonComoJs);

    public static List<InteresseDaMeta> InteressesLimpos(IEnumerable<InteresseDaMeta?>? lista) =>
        (lista ?? []).Where(x => x is not null).Select(x => new InteresseDaMeta((x!.Id ?? "").Trim(), (x.Nome ?? "").Trim()))
            .Where(x => x.Id != "" && x.Nome != "").ToList();

    /* ---- o formulário da régua ---------------------------------------------- */

    /// <summary>Nome, e-mail e telefone são os campos de contato padrão da Meta; cada pergunta da régua vira múltipla
    /// escolha com o NOME DE CAMPO como `key` (é por ele que a carga e a régua se encontram).</summary>
    public static List<Dictionary<string, object>> PerguntasDoFormulario(ReguaV2 regua)
    {
        var saida = new List<Dictionary<string, object>>
        {
            new() { ["type"] = "FULL_NAME" }, new() { ["type"] = "EMAIL" }, new() { ["type"] = "PHONE" },
        };
        foreach (var p in regua.Perguntas)
        {
            var opcoes = OpcoesDaPergunta(p);
            saida.Add(new Dictionary<string, object>
            {
                ["type"] = "CUSTOM", ["key"] = p.Nome,
                ["label"] = RotuloDaPergunta(p),
                ["options"] = opcoes.Select(v => new Dictionary<string, string> { ["key"] = v, ["value"] = v }).ToList(),
            });
        }
        return saida;
    }

    static List<string> OpcoesDaPergunta(PerguntaDaRegua p) => p.Forte.Concat(p.Media).Concat(p.Elim).Select(v => v.Trim()).Where(v => v != "").Distinct().ToList();
    static string RotuloDaPergunta(PerguntaDaRegua p) => string.IsNullOrWhiteSpace(p.Rotulo) ? p.Nome : p.Rotulo.Trim();

    /// <summary>O que impede criar o formulário da régua, ANTES de qualquer escrita: pergunta sem nenhuma opção. Ela vira
    /// múltipla escolha vazia, que a Meta recusa; recusar aqui dá a frase certa e nada é tentado na Meta.</summary>
    public static (string Codigo, string Motivo)? FaltaNaRegua(ReguaV2 regua)
    {
        foreach (var p in regua.Perguntas)
            if (OpcoesDaPergunta(p).Count == 0)
                return ("regua_sem_opcoes", $"a pergunta \"{RotuloDaPergunta(p)}\" da régua não tem nenhuma resposta (forte, média ou eliminatória): o formulário de cadastro não sobe com pergunta sem opções. Complete a régua no card do cliente, ou escolha um formulário existente da página. Nada foi criado.");
        return null;
    }

    public static string NomeDoFormulario(DateOnly quando) => $"[\U0001F30A Blue] [FORM-L] {quando.Day:00}/{quando.Month:00} | régua v2";

    /* ---- os corpos que a Meta recebe ----------------------------------------- */

    /// <summary>O corpo do conjunto. A verba, a idade e o Advantage+ vêm do plano; o resto é a receita (AD-MT-23).
    /// Só a PÁGINA no `promoted_object` (hotfix #4999): o formulário vai no botão do criativo.</summary>
    public static Dictionary<string, string> CorpoDoConjunto(ConjuntoProposto c, string nome, string campanhaId, string pageId, IReadOnlyList<InteresseDaMeta> interesses)
    {
        var targeting = new Dictionary<string, object>
        {
            ["geo_locations"] = new Dictionary<string, object> { ["countries"] = new[] { Pais } },
            ["age_min"] = c.Idade.Min, ["age_max"] = c.Idade.Max,
            ["targeting_automation"] = new Dictionary<string, object> { ["advantage_audience"] = c.AdvantageAudience },
        };
        if (interesses.Count > 0)
            targeting["flexible_spec"] = new[] { new Dictionary<string, object> { ["interests"] = interesses.Select(i => new Dictionary<string, string> { ["id"] = i.Id, ["name"] = i.Nome }).ToList() } };
        return new Dictionary<string, string>
        {
            ["name"] = nome, ["campaign_id"] = campanhaId, ["status"] = "PAUSED",
            ["optimization_goal"] = "LEAD_GENERATION", ["billing_event"] = "IMPRESSIONS", ["bid_strategy"] = "LOWEST_COST_WITHOUT_CAP",
            ["daily_budget"] = c.DiarioCentavos.ToString(), ["destination_type"] = "ON_AD",
            ["promoted_object"] = Json(new Dictionary<string, string> { ["page_id"] = pageId }),
            ["targeting"] = Json(targeting),
        };
    }

    public static Dictionary<string, string> CorpoDaCampanha(string nome) => new()
    {
        ["name"] = nome, ["objective"] = "OUTCOME_LEADS", ["status"] = "PAUSED",
        ["special_ad_categories"] = "[]",
        /* ABO: a verba fica em cada conjunto (criar-meta.ts, 21/09). */
        ["is_adset_budget_sharing_enabled"] = "false",
    };

    /// <summary>O criativo do anúncio de cadastro: a arte, o texto aprovado e o formulário no botão.</summary>
    public static Dictionary<string, string> CorpoDoCriativo(string nome, PublicacaoPreparada pub, string pageId, string imagemHash, string formularioId, string link) => new()
    {
        ["name"] = $"{nome} — criativo",
        ["url_tags"] = UtmsDoAnuncio,
        ["object_story_spec"] = Json(new Dictionary<string, object>
        {
            ["page_id"] = pageId,
            ["link_data"] = new Dictionary<string, object>
            {
                ["message"] = pub.Texto.Corpo, ["name"] = pub.Texto.Headline, ["image_hash"] = imagemHash, ["link"] = link,
                ["call_to_action"] = new Dictionary<string, object> { ["type"] = pub.MetaCta ?? CtaPadrao, ["value"] = new Dictionary<string, string> { ["lead_gen_form_id"] = formularioId } },
            },
        }),
    };

    /// <summary>O ensaio do criativo: o mínimo (página e link), só para a Meta dizer se esta conta sobe criativo.</summary>
    public static Dictionary<string, string> CorpoDoEnsaioDoCriativo(string nomeDaCampanha, string pageId, string link) => new()
    {
        ["name"] = $"{nomeDaCampanha} — ensaio do criativo",
        ["object_story_spec"] = Json(new Dictionary<string, object> { ["page_id"] = pageId, ["link_data"] = new Dictionary<string, string> { ["message"] = "ensaio", ["name"] = "ensaio", ["link"] = link } }),
    };

    /* ---- o post da página (#5067) --------------------------------------------- */

    /// <summary>A legenda do post: post de foto não tem título, então o título da copy vai na primeira linha.</summary>
    public static string LegendaDoPost(string headline, string corpo) =>
        string.Join("\n\n", new[] { headline.Trim(), corpo.Trim() }.Where(x => x != ""));

    /// <summary>`/photos` devolve `post_id` quando publica; sem publicar, só o `id` da foto, e o post é "pagina_foto".</summary>
    public static string IdDoPostDaFoto(string pageId, string? id, string? postId) =>
        !string.IsNullOrEmpty(postId) ? postId : !string.IsNullOrEmpty(id) ? $"{pageId}_{id}" : "";

    /// <summary>O criativo que aponta para o post da página, COM O FORMULÁRIO NO BOTÃO.</summary>
    public static Dictionary<string, string> CorpoDoCriativoDoPost(string nome, string postId, string cta, string formularioId)
    {
        var n = $"{nome} — criativo";
        return new()
        {
            ["name"] = n.Length > 200 ? n[..200] : n, ["object_story_id"] = postId, ["url_tags"] = UtmsDoAnuncio,
            ["call_to_action"] = Json(new Dictionary<string, object> { ["type"] = cta, ["value"] = new Dictionary<string, string> { ["lead_gen_form_id"] = formularioId } }),
        };
    }

    /* ---- o hash e o link ------------------------------------------------------- */

    /// <summary>O hash do que vai subir. NADA que mude com o relógio entra aqui (o nome da campanha leva o dia); o nome
    /// do conjunto FICA, porque é a chave da retomada. O formato é o mesmo JSON do V1, para a mesma estrutura dar o
    /// mesmo hash nos dois lados.</summary>
    public static string HashDaEstrutura(string vaultSha, string? designerPecaId, string textoHash, string? formularioId,
        IReadOnlyList<(string Nome, string Etapa, string Publico, long DiarioCentavos, string? Interesses)> conjuntos)
    {
        var lista = conjuntos.Select(c => string.IsNullOrEmpty(c.Interesses)
            ? new object[] { c.Nome, c.Etapa, c.Publico, c.DiarioCentavos }
            : new object[] { c.Nome, c.Etapa, c.Publico, c.DiarioCentavos, c.Interesses }).ToList();
        var json = Json(new object?[] { vaultSha, designerPecaId, textoHash, formularioId, lista });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    /// <summary>Os conjuntos como entram no hash: nome, etapa, público, diária e os ids dos interesses juntados por "+"
    /// (null sem interesse). O nome do conjunto entra porque é a chave da retomada.</summary>
    public static List<(string Nome, string Etapa, string Publico, long DiarioCentavos, string? Interesses)> ConjuntosParaHash(
        IReadOnlyList<ConjuntoProposto> conjuntos, IReadOnlyList<IReadOnlyList<InteresseDaMeta>> interesses) =>
        conjuntos.Select((cj, i) => (cj.Nome, cj.Etapa.ToString(), cj.Publico, cj.DiarioCentavos,
            (string?)(i < interesses.Count && string.Join("+", interesses[i].Select(x => x.Id)) is { Length: > 0 } s ? s : null))).ToList();

    /// <summary>O hash do PEDIDO como o Publicar o compõe, num lugar só: as artes na ordem, separadas por vírgula (null sem
    /// arte); o hash do texto do PRIMEIRO anúncio ("sem-criativo" sem anúncio); o formulário escolhido; e os conjuntos. É a
    /// chave da retomada: mudar a composição faz a linha 'ok' sumir e a mesma estrutura subir de novo.</summary>
    public static string HashDoPedido(string vaultSha, IReadOnlyList<string> artes, string? textoHashDoPrimeiro, string? formularioId,
        IReadOnlyList<(string Nome, string Etapa, string Publico, long DiarioCentavos, string? Interesses)> conjuntos) =>
        HashDaEstrutura(vaultSha, artes.Count > 0 ? string.Join(",", artes) : null, textoHashDoPrimeiro ?? "sem-criativo", formularioId, conjuntos);

    /* ---- a trava da publicação em voo ---------------------------------------- */

    public enum OutraPublicacao { Ignora, EmCurso, PelaMetade }

    static DateTime? Carimbo(string texto) =>
        DateTime.TryParse(texto, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var em) ? em : null;

    /// <summary>O registro está sendo trabalhado agora: 'criando' com o último batimento (`atualizado_em`) dentro do prazo.</summary>
    public static bool EmCurso(RegistroDoStart r, DateTime agoraUtc) =>
        r.Estado == "criando" && Carimbo(r.AtualizadoEm) is { } em && agoraUtc - em < PrazoDaPublicacao;

    /// <summary>Uma publicação de OUTRA estrutura do mesmo cliente barra a nova? 'criando' recente: alguém publica agora.
    /// 'parcial', ou 'criando' parado (o app fechou no meio) com campanha: ficou pela metade, e publicar outra estrutura
    /// criaria a segunda campanha ao lado dela. O resto não barra.</summary>
    public static OutraPublicacao SituacaoDeOutra(RegistroDoStart r, string hash, DateTime agoraUtc)
    {
        if (r.EstruturaHash == hash) return OutraPublicacao.Ignora;
        if (EmCurso(r, agoraUtc)) return OutraPublicacao.EmCurso;
        if (r.Estado is "parcial" or "criando" && r.CampanhaId is not null) return OutraPublicacao.PelaMetade;
        return OutraPublicacao.Ignora;
    }

    public static string LinkDoGerenciador(string act, string campanhaId) =>
        $"https://adsmanager.facebook.com/adsmanager/manage/campaigns?act={GraphRegras.SemAct(act)}&selected_campaign_ids={campanhaId}";

    /* ---- o pedido (pedido.ts) ------------------------------------------------- */

    static bool Url(string? v) => v is not null && Http.IsMatch(v.Trim());

    /// <summary>O que falta no pedido, na parte que não depende do banco. A ordem das recusas é régua (fora daqui),
    /// política, link.</summary>
    public static (string Codigo, string Motivo)? FaltaNoPedido(string? link, string? politicaUrl, string? formularioId)
    {
        var temFormulario = !string.IsNullOrWhiteSpace(formularioId);
        if (!temFormulario && !Url(politicaUrl))
            return ("sem_politica", "informe a URL da política de privacidade do cliente: a Meta exige no formulário de cadastro.");
        var digitado = (link ?? "").Trim();
        if (digitado != "" && !Url(link))
            return ("sem_link", "o endereço do anúncio precisa começar com http:// ou https://.");
        if (!temFormulario && !EhLinkExterno(link) && !EhLinkExterno(politicaUrl))
            return ("sem_link", "preencha o endereço do anúncio: a Meta exige que o anúncio de cadastro leve a um site externo, e a página do Facebook do cliente não serve. Nada foi criado.");
        return null;
    }

    public static string LinkDaPagina(string pageId) => $"https://www.facebook.com/{pageId}";

    /// <summary>`true` quando a URL é externa de verdade, e não a própria página do Facebook (nem fb.me, m.me…).</summary>
    public static bool EhLinkExterno(string? v)
    {
        var u = (v ?? "").Trim();
        if (!Http.IsMatch(u)) return false;
        var dominio = (Dominio.Match(u).Groups[1].Value ?? "").ToLowerInvariant();
        var semPorta = Regex.Replace(dominio, @":\d+$", "");
        var raiz = semPorta.StartsWith("www.") ? semPorta[4..] : semPorta;
        if (DaMeta.Contains(raiz)) return false;
        return !DaMeta.Any(d => raiz.EndsWith("." + d));
    }

    /// <summary>O destino do anúncio: o link digitado, senão a política, senão a página.</summary>
    public static string DestinoDoAnuncio(string? link, string? politicaUrl, string pageId) =>
        Url(link) ? link!.Trim() : Url(politicaUrl) ? politicaUrl!.Trim() : LinkDaPagina(pageId);

    /* ---- os nomes (tarefa #5001) ---------------------------------------------- */

    public static NomesFinais NomesFinais(string campanha, IReadOnlyList<(string Nome, string Anuncio)> recomendados, NomesEditados? editados)
    {
        static string Escolhe(string rec, string? ed) => ed is not null ? ed.Trim() : rec;
        return new NomesFinais(
            Escolhe(campanha, editados?.Campanha),
            recomendados.Select((c, i) => Escolhe(c.Nome, editados?.Conjuntos is { } l && i < l.Count ? l[i] : null)).ToList(),
            recomendados.Select((c, i) => Escolhe(c.Anuncio, editados?.Anuncios is { } l && i < l.Count ? l[i] : null)).ToList());
    }

    /// <summary>O que impede publicar com estes nomes, ou null. Conjunto com nome repetido não passa: a retomada acha o
    /// conjunto já criado pelo nome.</summary>
    public static (string Codigo, string Motivo)? FaltaNosNomes(NomesFinais n)
    {
        var todos = new List<(string DeQuem, string Nome)> { ("da campanha", n.Campanha) };
        todos.AddRange(n.Conjuntos.Select((v, i) => ($"do conjunto {i + 1}", v)));
        todos.AddRange(n.Anuncios.Select((v, i) => ($"do anúncio {i + 1}", v)));
        if (todos.FirstOrDefault(t => string.IsNullOrWhiteSpace(t.Nome)) is { DeQuem: not null } vazio)
            return ("nome_invalido", $"o nome {vazio.DeQuem} está vazio: escreva um nome ou volte ao recomendado.");
        if (todos.FirstOrDefault(t => t.Nome.Length > NomeMax) is { DeQuem: not null } longo)
            return ("nome_invalido", $"o nome {longo.DeQuem} passa de {NomeMax} caracteres.");
        var vistos = new Dictionary<string, int>();
        for (var i = 0; i < n.Conjuntos.Count; i++)
        {
            var chave = n.Conjuntos[i].Trim().ToLowerInvariant();
            if (vistos.TryGetValue(chave, out var j)) return ("nome_invalido", $"os conjuntos {j + 1} e {i + 1} estão com o mesmo nome: cada conjunto precisa de um nome só dele.");
            vistos[chave] = i;
        }
        return null;
    }

    /* ---- a peça (peca.ts) ----------------------------------------------------- */

    /// <summary>A declaração só vale inteira e dentro do vocabulário (lib/copy/etiqueta.ts).</summary>
    public static Declaracao? DeclaracaoDe(string? angulo, IEnumerable<string>? formas, string? papel, string? tipoCta)
    {
        if (angulo is null || papel is null || tipoCta is null) return null;
        if (!Estruturas.AngulosHeadline.Contains(angulo) || !Estruturas.PapeisSub.Contains(papel) || !Estruturas.TiposCta.Contains(tipoCta)) return null;
        var f = (formas ?? []).Distinct().ToList();
        if (f.Any(x => !Estruturas.FormasHeadline.Contains(x))) return null;
        return new Declaracao(angulo, f, papel, tipoCta);
    }

    /// <summary>` · H:<angulo>[+forma] S:<papel> C:<tipo>`: a etiqueta legível no Gerenciador.</summary>
    public static string SufixoDaEtiqueta(Declaracao d) =>
        $" · H:{string.Join("+", new[] { d.AnguloHeadline }.Concat(d.FormaHeadline))} S:{d.PapelSub} C:{d.TipoCta}";

    /// <summary>O nome com a etiqueta, nunca acima de 200. Sem declaração, o nome base.</summary>
    public static string NomeComEtiqueta(string baseNome, Declaracao? d)
    {
        var limpo = Regex.Replace(baseNome, @"\s+", " ").Trim();
        if (d is null) return limpo.Length > NomeTeto ? limpo[..(NomeTeto - 1)].TrimEnd() + "…" : limpo;
        var sufixo = SufixoDaEtiqueta(d);
        var cabe = Math.Min(NomeBaseTeto, NomeTeto - sufixo.Length);
        var corpo = limpo.Length > cabe ? limpo[..(cabe - 1)].TrimEnd() + "…" : limpo;
        return corpo + sufixo;
    }

    /// <summary>sha256 dos três textos: acha a mesma peça publicada em dois anúncios. O mesmo JSON do V1.</summary>
    public static string HashDoTexto(TextoDoAnuncio t) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(new[] { t.Headline.Trim(), t.Corpo.Trim(), t.Cta.Trim() })))).ToLowerInvariant();

    public static Etapa? EtapaDoFunil(string? funil) => (funil ?? "").ToUpperInvariant() switch
    {
        "TOFU" => Etapa.TOFU, "MOFU" => Etapa.MOFU, "BOFU" => Etapa.BOFU, _ => null,
    };

    /* ---- a cola do gestor ------------------------------------------------------ */

    public static CriativoNaMao CriativoNaMao0(RegistroDoStart r, IReadOnlyList<PublicacaoPreparada> publicacoes, IReadOnlyList<(string Nome, string Etapa, string Publico, long DiarioCentavos, string? Interesses)> conjuntos, string link, IReadOnlyList<string?>? hashes)
    {
        var anuncios = conjuntos.Select((c, i) =>
        {
            var pub = i < publicacoes.Count ? publicacoes[i] : null;
            return new AnuncioNaMao(pub?.Nome ?? c.Nome, c.Nome, r.Conjuntos.FirstOrDefault(x => x.Nome == c.Nome)?.AdsetId,
                pub?.Texto.Corpo ?? "", pub?.Texto.Headline ?? "", pub?.MetaCta ?? CtaPadrao,
                hashes is not null && i < hashes.Count ? hashes[i] ?? r.ImagemHash : r.ImagemHash);
        }).ToList();
        return new CriativoNaMao(AvisoDoCriativoNaMao, UtmsDoAnuncio, r.ImagemHash, r.FormularioId, link, anuncios);
    }
}
