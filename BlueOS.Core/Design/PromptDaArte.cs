using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueOS.Core.Modelos;

namespace BlueOS.Core.Design;

/* ============================================================================
   O PROMPT DA ARTE, sem rede: a porta de lib/designer/prompt.ts, diagramacao-
   centralizada.ts, contexto-visual.ts, tema-da-peca.ts e variacao-lote.ts.
   ----------------------------------------------------------------------------
   O que sai daqui é o pedido ao modelo de imagem no modo da casa: composição
   CENTRALIZADA, faixa calma no topo (a logo real entra depois), SEM TEXTO (o
   texto é camada: Diagramacao + Compositor), com o elemento principal do
   rodízio de cenas e a MENSAGEM da peça (o tema lido da copy por um modelo de
   texto). O texto das regras é o do V1, parágrafo a parágrafo: cada um existe
   por causa de um defeito real que a equipe reportou.
   ============================================================================ */
public sealed record MarcaDaArte(IReadOnlyList<string> Cores, string? FontePrimaria, string? CorCta, IReadOnlyList<string> CoresFundo, string? Notas);
public sealed record TemaDaPeca(string Tema, string Objeto, string Cena);
public sealed record CopyDaArte(int Numero, string Funil, string Head, string Sub, string Cta);

public static class PromptDaArte
{
    public static readonly string[] Cenas = ["laptop", "icp", "conceito-3d", "smartphone", "objeto-simbolo", "mockup-premium", "comparativo"];
    static readonly Dictionary<string, int> FunilOffset = new() { ["TOFU"] = 0, ["MOFU"] = 3, ["BOFU"] = 5 };
    public const int FaixaLogoFeed = 150, FaixaLogoStory = 230;
    public const string MarcaFaixaTopo = "FAIXA DE RESPIRO NO TOPO";
    public const string ModeloDoTema = "claude-sonnet-5-5";

    /* ---- o rodízio de cenas (variacao-lote.ts) ---------------------------------- */

    public static int IndiceDeRodizio(int numero, string funil) => Math.Max(0, numero - 1) + FunilOffset.GetValueOrDefault(funil.ToUpperInvariant(), 0);

    /// <summary>A cena da peça: ancorada em funil + número (a mesma cena gerada junto ou sozinha) e deslocada pelo número do lote.</summary>
    public static string CenaDaPeca(int numero, string funil, int loteNumero, string? template)
    {
        if (!string.IsNullOrWhiteSpace(template) && Cenas.Contains(template)) return template;
        var n = Cenas.Length;
        return Cenas[(((IndiceDeRodizio(numero, funil) + loteNumero) % n) + n) % n];
    }

    /* ---- o contexto do cliente (contexto-visual.ts) ------------------------------ */

    const int TetoLista = 5, TetoItem = 180, TetoApoio = 4;
    static string Limpar(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = Regex.Replace(s, @"\s+", " ").Trim();
        if (t.Length <= TetoItem) return t;
        var corte = t[..TetoItem]; var espaco = corte.LastIndexOf(' ');
        return Regex.Replace(espaco > TetoItem * 0.6 ? corte[..espaco] : corte, @"[\s,;:.—-]+$", "") + "…";
    }
    static List<string> Lista(IEnumerable<string>? v, int teto = TetoLista) => (v ?? []).Select(Limpar).Where(x => x != "").Take(teto).ToList();
    static IEnumerable<string> Itens(string? texto) => string.IsNullOrWhiteSpace(texto) ? [] : texto.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('-', '•', ' ')).Where(x => x != "");

    public static string ContextoVisual(Cliente c)
    {
        var linhas = new List<string>();
        var p = c.Produto; var i = c.Icp;
        var nomeProduto = Limpar(p.NomeCorreto) is { Length: > 0 } np ? np : Limpar(c.Nome);
        var descricao = Limpar(p.Descricao);
        if (descricao != "") linhas.Add($"- O que vende: {(nomeProduto != "" ? $"{nomeProduto} — " : "")}{descricao}");
        else if (Limpar(p.NomeCorreto) != "") linhas.Add($"- O que vende: {Limpar(p.NomeCorreto)}");
        if (Limpar(p.ModeloNegocio) is { Length: > 0 } m) linhas.Add($"- Modelo de negócio: {m}");
        var dif = Lista(Itens(p.Diferenciais)); if (dif.Count > 0) linhas.Add($"- Diferenciais: {string.Join("; ", dif)}");
        var fun = Lista(Itens(p.FuncionalidadesChave)); if (fun.Count > 0) linhas.Add($"- O que o produto faz (funcionalidades): {string.Join("; ", fun)}");
        var quem = string.Join(" — ", new[] { Limpar(i.CargoDecisor), Limpar(i.Setor), Limpar(i.Segmento) }.Where(x => x != ""));
        if (quem != "") linhas.Add($"- Pra quem (quem decide e em que negócio): {quem}");
        var dores = Lista(i.DoresPrincipais); if (dores.Count > 0) linhas.Add($"- Dores do público: {string.Join("; ", dores)}");
        var desejos = Lista(i.DesejosPrincipais); if (desejos.Count > 0) linhas.Add($"- O que o público quer: {string.Join("; ", desejos)}");
        if (linhas.Count == 0) return "";
        return $"O CLIENTE — é pra ELE que esta peça é feita (use pra escolher o ambiente, a cena, os objetos e as pessoas; ⛔ nada disto vira texto desenhado na arte):\n{string.Join("\n", linhas)}\nA imagem tem de ser reconhecível como deste negócio: o ambiente, os objetos e as pessoas são os do mundo dele, não os de uma empresa qualquer.{ElementosDeApoio(c)}";
    }

    public static string ElementosDeApoio(Cliente c)
    {
        var funcoes = Lista(Itens(c.Produto.FuncionalidadesChave), TetoApoio);
        var diferenciais = Lista(Itens(c.Produto.Diferenciais), TetoApoio);
        var desejos = Lista(c.Icp.DesejosPrincipais, TetoApoio);
        var fonte = funcoes.Count > 0 ? funcoes : diferenciais.Count > 0 ? diferenciais : desejos;
        if (fonte.Count == 0) return "";
        var oQue = funcoes.Count > 0 ? "faz" : diferenciais.Count > 0 ? "entrega de diferente" : "resolve pro público";
        return $"\n\nELEMENTOS DE APOIO — os cards, ícones, badges e métricas que flutuam na cena (⛔ nada disto vira texto desenhado na arte):\n- Cada um mostra UMA coisa que o produto {oQue}, desta lista: {string.Join("; ", fonte)}.\n- Diga isso por DESENHO: o pictograma da etapa (documento, calendário, rota, ficha, cesta, boleto, exame — o que for do mundo deste produto), com blocos, barras e linhas no lugar de palavras. Sem letra legível, sem número inventado.\n- No máximo {TetoApoio}, em vidro fosco, com profundidade: eles PROVAM o produto, não enfeitam a peça, e nunca competem com o texto.\n- ⛔ PROIBIDO o ícone de banco de imagem que serve pra qualquer empresa (cadeado, escudo, cofre, engrenagem, nuvem, cifrão solto, gráfico de pizza aleatório, check sem contexto, aperto de mão). Teste: se trocar a logo por outra empresa e a cena continuar fazendo sentido, está genérico — refaça a partir da lista acima.";
    }

    public static string DescreverIcp(Cliente c)
    {
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Icp.Tipo)) partes.Add(c.Icp.Tipo);
        if (!string.IsNullOrWhiteSpace(c.Icp.Segmento)) partes.Add(c.Icp.Segmento!.Trim());
        var dores = c.Icp.DoresPrincipais.Where(d => !string.IsNullOrWhiteSpace(d)).Take(2).ToList();
        if (dores.Count > 0) partes.Add($"enfrenta: {string.Join("; ", dores)}");
        return partes.Count > 0 ? string.Join(" — ", partes) : "profissional brasileiro 35-50 anos, postura executiva, vestimenta business casual";
    }

    /* ---- o tema da peça (tema-da-peca.ts): a instrução, o pedido e as travas ------ */

    public const string InstrucaoDoTema = """
        Você é diretor de arte de uma agência de performance. Recebe o CONTEXTO de um cliente (o anunciante) e a copy de UM anúncio estático dele, e devolve a TRADUÇÃO VISUAL desse anúncio, que vai orientar um fotógrafo (um modelo de imagem).
        O anúncio é do CLIENTE. A foto conta O QUE ELE OFERECE e a situação da headline — nunca o ramo do público. Um software de dados que vende pra transportadoras NÃO anuncia com caminhão e combustível (isso é anúncio de transportadora); anuncia com o mundo de quem decide com dados: a mesa de quem gerencia, o fechamento do mês, a resposta que não chega. O ramo do público entra só como detalhe de contexto (um caminhão desfocado pela janela), nunca como assunto da foto.
        Como pensar:
        1. Pelo CONTEXTO, responda em uma frase: o que o cliente VENDE e em que LUGAR físico a oferta dele é usada ou faz falta. Registre nos campos "oferta" e "lugar" ANTES dos outros: a foto acontece nesse lugar, e a cena e o objeto saem dele.
           - Oferta usada no chão da operação (cozinha, obra, loja, clínica, oficina): a foto é no chão da operação.
           - Software de gestão, dados, finanças, vendas, RH: a foto é onde se decide — a mesa de quem gerencia, o escritório da empresa do público, a sala de reunião.
        2. Ache a SITUAÇÃO CONCRETA da HEADLINE: o que está acontecendo, com quem, onde, quando. É ela que diz o assunto; a subheadline só desempata.
        3. O objeto é o rastro FÍSICO dessa situação, nesse lugar, no estado que conta a dor (lotado, largado, vazio, pela metade, em cima da hora, quebrado, atrasado) ou a promessa (resolvido, em ordem, pronto, no lugar). Ele EXISTE de verdade, é comum nesse lugar e cabe numa foto sem efeito especial: nada de gadget inventado nem de combinação que não existe no mundo real — quem vê percebe o render na hora.
        4. Headline sobre algo digital ou burocrático (grupo de mensagem, aplicativo, sistema, registro, imposto, contrato)? NÃO fotografe o papel nem a tela: mostre o que aquilo CAUSA — o posto vazio, a equipe incompleta, o improviso, a espera, o fechamento que atrasa.
        5. Teste final, obrigatório, antes de responder:
           a) Quem vê a foto SEM texto diria que o anunciante é do ramo do CLIENTE (do que ele vende), e não do ramo do público. Se a foto parece anúncio do público, refaça.
           b) Troque a headline por outra do mesmo cliente. Se o objeto ainda servisse, ele está genérico: escolha outro.
           c) Quem lê a headline e vê a foto entende a ligação em um segundo, sem legenda.
        Regras:
        - Nada de objeto genérico de "negócios" ou de "segurança" (cofre, escudo, cadeado, ampulheta, foguete, gráfico, crachá, pasta de documentos, aperto de mão) — é o que sai quando não se sabe do que a peça fala.
        - Nada de celular, notebook, tablet, monitor ou tela: este estilo não usa device.
        - Nada de papel escrito ou impresso como objeto (caderno, planilha, cartaz, formulário, recibo, prancheta, contrato): o modelo de imagem transforma isso em letra desenhada.
        - O objeto não carrega escrita NENHUMA: nada rabiscado, anotado, numerado ou com nome escrito — vira letra falsa desenhada, a cara de IA que o anúncio não pode ter.
        - Se vier a lista de OBJETOS QUE OUTRAS PEÇAS DESTE LOTE JÁ USAM, o seu é DIFERENTE de todos — nem o mesmo, nem uma variação dele. O lote vai pro ar junto, pra teste A/B.
        - Escreva em português, com as SUAS palavras. NÃO repita frases da copy: o modelo de imagem desenha qualquer texto que lê, e a copy entra depois, como camada.
        - Nada de texto, letra, número, logo, marca ou interface legível nas descrições, nem nome de aplicativo, sistema ou marca: diga "grupo de mensagens", não o nome do app.
        - Se vier um PEDIDO DO GESTOR, ele manda mais que a sua escolha: a tradução visual obedece a ele.
        Responda SÓ com o JSON pedido.
        """;

    public static Dictionary<string, object> SchemaDoTema() => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["oferta"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "O que o CLIENTE (o anunciante) vende, em uma linha com as suas palavras. Até 120 caracteres." },
            ["lugar"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "O LUGAR físico onde essa oferta é usada ou faz falta. É onde a foto acontece. Até 120 caracteres." },
            ["tema"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Do que ESTA headline fala, em UMA frase curta com as suas palavras (até 160 caracteres). Não copie a headline." },
            ["objeto"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "O objeto que é a PROVA desta headline: uma coisa física COMUM e que EXISTE, no lugar onde a oferta do cliente é usada ou faz falta, no estado que conta a dor ou a promessa. Sem texto, número ou logo; nada genérico de negócios; nada inventado. Até 140 caracteres." },
            ["cena"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "O AMBIENTE REAL onde a oferta do cliente é usada ou faz falta, no momento de que a headline fala: que lugar, que superfície, que luz, quem aparece ao fundo. Sem telas com texto legível, placas ou papéis escritos. Até 200 caracteres." },
        },
        ["required"] = new[] { "oferta", "lugar", "tema", "objeto", "cena" }, ["additionalProperties"] = false,
    };

    public static string PedidoDoTema(string cliente, string? contexto, CopyDaArte copy, string? ajuste, IReadOnlyList<string>? jaUsados, IReadOnlyList<string>? descartados)
    {
        var p = new List<string> { $"CLIENTE: {cliente}" };
        if (!string.IsNullOrWhiteSpace(contexto)) p.Add($"\nCONTEXTO DO CLIENTE:\n{contexto!.Trim()}");
        p.Add("\nCOPY DESTE ANÚNCIO:"); p.Add($"HEADLINE: {copy.Head.Trim()}");
        if (!string.IsNullOrWhiteSpace(copy.Sub)) p.Add($"SUBHEADLINE: {copy.Sub.Trim()}");
        if (!string.IsNullOrWhiteSpace(copy.Cta)) p.Add($"CTA: {copy.Cta.Trim()}");
        if (!string.IsNullOrWhiteSpace(ajuste)) p.Add($"\nPEDIDO DO GESTOR PRA ESTA PEÇA: {ajuste!.Trim()}");
        if (jaUsados is { Count: > 0 }) p.Add($"\nOBJETOS QUE OUTRAS PEÇAS DESTE LOTE JÁ USAM (o desta peça é diferente de todos):\n{string.Join("\n", jaUsados.Select(o => $"- {o}"))}");
        if (descartados is { Count: > 0 }) p.Add($"\nJÁ DESCARTADOS (escolha OUTRO objeto, diferente destes; se a headline é sobre algo digital ou burocrático, siga o passo 4):\n{string.Join("\n", descartados)}");
        return string.Join("\n", p);
    }

    static string LimparCampo(string? s, int teto)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = Regex.Replace(Regex.Replace(s, "[\"“”«»‘’]", ""), @"\s+", " ").Trim();
        return t.Length > teto ? t[..teto] : t;
    }

    public static TemaDaPeca? LerTema(string json)
    {
        try
        {
            var r = JsonDocument.Parse(json).RootElement;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var tema = LimparCampo(S("tema"), 180); var objeto = LimparCampo(S("objeto"), 160); var cena = LimparCampo(S("cena"), 220);
            return tema != "" && objeto != "" && cena != "" ? new TemaDaPeca(tema, objeto, cena) : null;
        }
        catch { return null; }
    }

    static readonly (Regex Re, string Motivo, bool SoInicio)[] Proibidos =
    [
        (new(@"\b(cofres?|escudos?|cadeados?|ampulhetas?|foguetes?|graficos?|crachas?|aperto de mao|pastas? de documentos?)\b"), "objeto genérico", true),
        (new(@"\b(celular|celulares|smartphones?|notebooks?|laptops?|computador|computadores|tablets?|monitor|monitores|telas?|maquininhas?)\b"), "device", true),
        (new(@"\b(cadernos?|planilhas?|cartaz|cartazes|formularios?|recibos?|pranchetas?|contratos?|papel|papeis|comandas?|fichas?|documentos?|boletos?|cardapios?|carteira de trabalho|extratos?|notas? fisca(?:l|is))\b"), "papel escrito", true),
        (new(@"\b(rabiscad[oa]s?|anotad[oa]s?|escrit[oa]s?|numerad[oa]s?|letras?|numeros?)\b"), "escrita", false),
    ];
    static readonly Regex Falsos = new(@"\b(?:papel (?:aluminio|toalha|filme|manteiga)|tela de (?:protecao|arame|galinheiro)|fichas? de (?:cassino|poker))\b");
    static readonly Regex Negacao = new(@"\b(?:sem|nada|nenhum|nenhuma)\s(?:(?:um|uma|nenhum|nenhuma|qualquer|o|a|os|as|seu|sua|seus|suas)\s)*\w+(?:\s\w+)?");

    public static List<string> Normalizar(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return Regex.Replace(sb.ToString(), "[^a-z0-9\\s]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>Por que o objeto não serve ("device: celular"), ou null se serve.</summary>
    public static string? ObjetoProibido(string objeto, string? contexto)
    {
        var palavras = Negacao.Replace(Falsos.Replace(string.Join(" ", Normalizar(objeto)), " "), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var inicio = string.Join(" ", palavras.Take(10)); var todo = string.Join(" ", palavras);
        var doCliente = contexto is null ? "" : $" {string.Join(" ", Normalizar(contexto))} ";
        foreach (var (re, motivo, soInicio) in Proibidos)
        {
            var m = re.Match(soInicio ? inicio : todo);
            if (!m.Success) continue;
            if (motivo == "objeto genérico" && Regex.IsMatch(doCliente, $@"\b{Regex.Escape(m.Groups[1].Value.TrimEnd('s'))}s?\b")) continue;
            return $"{motivo}: {m.Groups[1].Value}";
        }
        return null;
    }

    /// <summary>O tema repete uma frase da copy (6 palavras seguidas; a copy curta entra inteira)?</summary>
    public static bool CitaCopy(TemaDaPeca tema, IEnumerable<string> copy)
    {
        const int Janela = 6, Minimo = 3;
        var trechos = new HashSet<string>(); var tamanhos = new HashSet<int>();
        foreach (var texto in copy)
        {
            var p = Normalizar(texto); var n = Math.Min(Janela, p.Count);
            if (n < Minimo) continue;
            tamanhos.Add(n);
            for (var i = 0; i + n <= p.Count; i++) trechos.Add(string.Join(" ", p.Skip(i).Take(n)));
        }
        if (trechos.Count == 0) return false;
        foreach (var campo in new[] { tema.Tema, tema.Objeto, tema.Cena })
        {
            var p = Normalizar(campo);
            foreach (var n in tamanhos) for (var i = 0; i + n <= p.Count; i++) if (trechos.Contains(string.Join(" ", p.Skip(i).Take(n)))) return true;
        }
        return false;
    }

    public static string BlocoTemaDaPeca(TemaDaPeca t, string nivel)
    {
        const string aviso = "(⛔ NÃO escreva nada disto na arte: nem título, nem placa, nem tela, nem rótulo)";
        if (nivel == "assunto") return $"A MENSAGEM DESTA PEÇA {aviso}:\n- Assunto: {t.Tema}\nO elemento principal continua o que o estilo pede neste prompt; a mensagem orienta o contexto em volta dele.";
        if (nivel == "cena") return $"A MENSAGEM DESTA PEÇA — o que a imagem tem de contar {aviso}:\n- Assunto: {t.Tema}\n- Cena: {t.Cena}\nA composição continua a pedida neste prompt; esta mensagem decide O QUE a cena mostra.";
        return $"A MENSAGEM DESTA PEÇA — é o que a imagem tem de mostrar {aviso}:\n- Assunto: {t.Tema}\n- Cena: {t.Cena}\n- Objeto em destaque: {t.Objeto}\nA imagem nasce DESTA mensagem e do negócio do cliente. Um elemento que não dá pra explicar por eles está errado, por mais bonito que seja.";
    }

    /// <summary>Os objetos das peças irmãs, pelo prompt salvo (o LOTE NÃO REPETE O OBJETO).</summary>
    public static List<string> ObjetosDoLote(IEnumerable<string?> prompts, int teto = 6)
    {
        var vistos = new List<string>();
        foreach (var p in prompts)
        {
            if (p is null) continue;
            var m = Regex.Match(p, @"^- O OBJETO desta peça: (.+?)\.?\s*$", RegexOptions.Multiline);
            if (m.Success && !vistos.Contains(m.Groups[1].Value.Trim())) vistos.Add(m.Groups[1].Value.Trim());
        }
        return vistos.Take(teto).ToList();
    }

    /* ---- o prompt de imagem (prompt.ts + diagramacao-centralizada.ts), modo SEM TEXTO ---- */

    static string CorEscuraDaMarca(IEnumerable<string> cores)
    {
        double? Lum(string cor)
        {
            var h = cor.Trim().TrimStart('#');
            if (Regex.IsMatch(h, "^[0-9a-f]{3}$", RegexOptions.IgnoreCase)) h = string.Concat(h.Select(c => $"{c}{c}"));
            var m = Regex.Match(h, "^([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})(?:[0-9a-f]{2})?$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return (0.2126 * Convert.ToInt32(m.Groups[1].Value, 16) + 0.7152 * Convert.ToInt32(m.Groups[2].Value, 16) + 0.0722 * Convert.ToInt32(m.Groups[3].Value, 16)) / 255;
        }
        var validas = cores.Select(c => (C: c.Trim(), L: Lum(c))).Where(x => x.L is not null).ToList();
        if (validas.Count == 0) return "";
        var escura = validas.MinBy(x => x.L)!;
        return escura.L < 0.35 ? escura.C : "";
    }

    static string BlocoFundo() => """
        ══ FUNDO — LIBERDADE CRIATIVA ORIENTADA À CONVERSÃO ══
        VOCÊ tem liberdade para criar o fundo mais forte para ESTE cliente. Antes de decidir, ANALISE o contexto fornecido: segmento/nicho do cliente, ICP (público-alvo), a dor/promessa da copy, a paleta e a identidade visual da marca nos assets. Com base nisso, projete um fundo que MAXIMIZE conversão.
        Como decidir (pense como diretor de arte de performance):
          - Escolha CLARO ou ESCURO pelo que dá mais impacto e contraste para a marca e o segmento. Nas referências que mais convertem, o ESCURO PREMIUM domina (preto, azul-marinho, verde-escuro, grafite) com textura tech + glow da marca; o CLARO aparece em segmentos de saúde/consumo/varejo (branco/cinza-claro com gradiente suave da marca). Use o julgamento.
          - Textura recomendada no fundo: grid fino de quadradinhos/pontos/linhas em baixíssima opacidade (3-6%), OU gradiente profundo da cor da marca, OU ambiente/cena real do segmento (escritório, obra, farmácia, restaurante) desfocado atrás com overlay de cor da marca. Sempre sutil.
          - GLOW da cor primária da marca em 1-2 cantos ou atrás do elemento visual dá o toque "premium".
          - A cor primária da marca aparece como ACENTO forte (ícones, formas, linhas, detalhes gráficos — nesta peça NÃO há headline nem botão desenhados), amarrando o criativo à identidade.

        REGRAS INEGOCIÁVEIS DO FUNDO (liberdade não quebra estas):
          - Legibilidade acima de tudo: contraste texto/fundo mínimo 4.5:1. Se o fundo for movimentado atrás do texto, escureça/clareie/desfoque aquela região para o texto respirar.
          - Coerência: fundo claro → texto escuro; fundo escuro → texto claro. Nunca texto da mesma família tonal do fundo.
          - O fundo APOIA, não compete: nunca ofusca o elemento visual nem o bloco de texto.
          - PROIBIDO: poluição visual que atrapalhe a leitura, ruído excessivo, paleta alheia à marca.
        """;

    static string BlocoFundoFoto() => """
        ══ FUNDO — O AMBIENTE REAL DO CLIENTE ══
        O fundo é o próprio ambiente do negócio do cliente, fotografado pela mesma câmera do elemento principal e desfocado atrás dele. Nada de textura tech, grid, glow, partículas, fundo de estúdio ou cor chapada. A cor da marca entra num degradê sobre a área do texto (ver ESTILO FOTOGRAFIA, no fim).

        REGRAS INEGOCIÁVEIS DO FUNDO:
          - Legibilidade acima de tudo: contraste texto/fundo mínimo 4.5:1 — é o degradê da marca que garante isso na área do texto.
          - O fundo APOIA, não compete: nunca ofusca o elemento principal nem a área do texto.
          - PROIBIDO: poluição visual, ruído excessivo, paleta alheia à marca nos elementos gráficos.
        """;

    static string BlocoTopo() =>
        $"1. {MarcaFaixaTopo} ⚠️ (primeiros {FaixaLogoFeed}px no feed / {FaixaLogoStory}px no story) — DEIXAR COMPLETAMENTE VAZIA, só o fundo (cor/gradiente). Essa faixa é o RESPIRO reservado onde o usuário vai posicionar a logo real DEPOIS, no estúdio (a geração sai SEM logo). NÃO desenhar nenhuma logo, ícone, texto de marca, watermark ou elemento de identidade visual aqui — e NENHUM texto da peça (headline/sub) pode entrar nessa faixa. ⛔ E NUNCA escrever esta instrução na arte: nada de \"logo\", \"sua logo aqui\", \"espaço reservado\" nem o nome do cliente desenhado como marca — a faixa é fundo puro, invisível pra quem vê a peça. ⛔ A headline SÓ PODE COMEÇAR ABAIXO dos {FaixaLogoFeed}px (feed) / {FaixaLogoStory}px (story) — assim a logo que o usuário colar no estúdio não cobre o texto.";

    static string BlocoSemTexto(bool foto) => $$"""
        ⛔⛔ REGRA MAIS IMPORTANTE DESTA PEÇA: NÃO DESENHE NENHUM TEXTO. ⛔⛔
        Esta arte é um FUNDO. O texto (headline, subheadline, CTA) será aplicado DEPOIS, por software, como camada tipográfica real. Se você desenhar qualquer letra, a peça é descartada.

        O QUE ISSO PROÍBE — nenhuma exceção:
        - headline, subheadline, chamada, manchete, slogan, tagline;
        - botão de CTA com palavra dentro (nem "Saiba mais", nem seta com rótulo);
        - selo, badge, pill, etiqueta ou banner COM PALAVRA (formas geométricas puras, sem letra, são permitidas);
        - watermark, assinatura, nome de marca, endereço, telefone, site, @arroba;
        - número solto, porcentagem, preço ou métrica escrita;
        - texto DENTRO da cena: rótulo em gráfico, coluna de planilha, menu de dashboard, placa, crachá, capa de caderno, tela de celular. Telas e monitores aparecem com GRÁFICOS, BARRAS e ÍCONES — nunca com palavras legíveis;
        - letras decorativas, lorem ipsum, rabisco que imite escrita.
        Se a composição parecer "faltando alguma coisa" sem texto, ela está CERTA: o vazio é onde o texto vai entrar.

        ══ ÁREA RESERVADA PRO TEXTO — o que fazer com ela ══
        Logo ABAIXO da faixa de respiro do topo, a região onde o texto entraria fica CALMA e PRONTA pra receber tipografia:
        - fundo liso ou com variação MUITO suave nessa faixa (gradiente leve, textura em 3-6%) — nada de detalhe fotográfico, rosto, objeto contrastado ou linha forte atravessando;
        - tonalidade UNIFORME nessa região, pra qualquer texto por cima manter contraste: se o fundo é escuro, escuro parelho; se é claro, claro parelho. NUNCA metade clara e metade escura na mesma faixa;
        - essa faixa ocupa aproximadamente a metade SUPERIOR da peça e a largura quase toda.
        {{(foto ? "\"Calma\" NÃO quer dizer chapada e sem graça: a peça continua premium — é a foto do ambiente com o degradê da marca por cima — só que o objeto e os elementos de contraste ficam FORA da faixa do texto." : "\"Calma\" NÃO quer dizer chapada e sem graça: a peça continua premium, com textura tech sutil, glow da marca e a cena — só que o miolo da cena e os elementos de contraste ficam FORA da faixa do texto.")}}

        ══ O RESTO DA PEÇA ══
        O elemento visual (cena, device, produto, pessoa) ocupa a metade INFERIOR e é o protagonista visual — é ele que carrega a peça, já que não há texto. Capriche: composição, iluminação, profundidade, integração com o fundo.
        """;

    const string RegraTelaSemTexto = """
        - A TELA FICA SEMPRE LIGADA E COM CONTEÚDO. ⛔ PROIBIDO tela preta, apagada, em standby, vazia, coberta de reflexo ou totalmente desfocada: device de tela apagada faz a peça parecer defeituosa.
        - Desenhe uma INTERFACE PLAUSÍVEL do produto, coerente com o SEGMENTO do cliente e com a MENSAGEM da copy: dashboard com 2-4 cards de métrica, um gráfico simples (linha, barra ou rosca), uma lista curta de itens, um mapa, uma conversa ou uma tela de app — o que fizer sentido pro que a copy promete.
        - Estilo de produto real: layout limpo e moderno, hierarquia clara, respiro, sombras suaves. A COR DA MARCA aparece como acento na UI (gráfico, botão, badge ativo).
        - ⛔ NENHUMA LETRA E NENHUM NÚMERO NA TELA — nem rótulo curto, nem valor, nem legenda, nem título de card. A regra "nenhum texto na peça" vale DENTRO da tela também: a interface aparece só com blocos, barras, linhas, colunas, fatias, ícones e formas. Onde normalmente haveria uma palavra ou um número, deixe a forma (uma barra cinza, um bloco arredondado, um ícone).
        - ⛔ PROIBIDO inventar nome de app, logotipo, marca, empresa ou domínio dentro da tela — inclusive o do próprio cliente. A UI é ANÔNIMA: só a estrutura da interface, sem assinatura.
        - A tela é APOIO da composição: nítida e legível, mas nunca rouba a atenção da headline.
        """;

    static string ElementoPrincipal(string cena, Cliente c, TemaDaPeca? tema)
    {
        var nome = c.Nome; var icp = DescreverIcp(c);
        var tela = "- NÃO há screenshot real do sistema do cliente disponível — então DESENHE a interface, seguindo as regras abaixo.\n" + RegraTelaSemTexto.Trim();
        return cena switch
        {
            "laptop" => $"ELEMENTO PRINCIPAL — NOTEBOOK/LAPTOP:\n- Notebook moderno (estilo MacBook Pro ou similar), tela ligada.\n{tela}\n- PROPORÇÃO REALISTA do notebook (13 a 16 polegadas): teclado, tela e dobradiça em proporção correta — NUNCA em miniatura, esticado, achatado nem distorcido. Se houver mãos na cena, ficam SOBRE o teclado, nunca digitando na mesa vazia.\n- Posicionado no canto inferior, parcialmente cortado pela borda inferior ou lateral (estilo \"mockup integrado\").\n- Tela legível com UI sutil, mas o laptop NÃO domina a peça.\n- Equilibrado com o bloco textual no topo.",
            "smartphone" => $"ELEMENTO PRINCIPAL — SMARTPHONE:\n- Smartphone moderno (estilo iPhone Pro) em pé ou levemente inclinado, tela ligada.\n{tela}\n- Poucos elementos de apoio (no máx. 2-3 cards/ícones discretos simulando funcionalidades REAIS do produto — ver ELEMENTOS DE APOIO), MUITO sutis. SEM partículas brilhantes, SEM glow forte, SEM efeitos gráficos que tinjam ou escondam o ambiente real atrás.\n- O ambiente atrás do celular é uma CENA REAL e profissional coerente com o ICP (escritório, etc.), nítida e com cores naturais.\n- Posicionado no canto inferior/lateral da peça, parcialmente cortado.\n- Os elementos de apoio ficam em segundo plano e não competem com o texto.",
            "objeto-simbolo" => "ELEMENTO PRINCIPAL — OBJETO-SÍMBOLO FOTOGRAFADO NO AMBIENTE DO CLIENTE (metáfora da mensagem; sem device e sem pessoa como herói):\n- O herói é UM OBJETO REAL do mundo do cliente, FOTOGRAFADO de verdade no ambiente do negócio dele — quem conhece o negócio reconhece o lugar.\n"
                + (tema is not null ? $"- O OBJETO desta peça: {tema.Objeto}.\n- O AMBIENTE onde ele é fotografado: {tema.Cena}.\n- Use exatamente este objeto, neste ambiente — só um AJUSTE DO GESTOR, se houver, manda mais que isto. Não some um segundo objeto em destaque."
                    : $"- O objeto sai do NEGÓCIO do cliente (o que ele vende e pra quem; público: {icp}): uma coisa real do dia a dia desse negócio, cujo estado mostra uma dor ou uma promessa dele.\n- Proibido o objeto genérico de \"negócios\" ou de \"segurança digital\", o símbolo de banco de imagem que serviria pra qualquer empresa: é o que sai quando a imagem não sabe de que cliente é.\n- Proibido papel escrito ou impresso como objeto: vira letra desenhada na arte.")
                + "\n- Estilo: fotografia publicitária de revista, câmera de verdade (lente 50-85mm, f/2.8), luz natural ou do próprio ambiente, profundidade de campo rasa: o objeto nítido abaixo da área do texto e o ambiente reconhecível, desfocado atrás.\n- O objeto está APOIADO numa superfície real do ambiente, com sombra de contato e marcas de uso reais. Nunca flutuando, nunca em pedestal.\n- NÃO use notebook, celular, tela nem pessoa como herói. Pessoas do ambiente podem aparecer ao fundo, desfocadas, como contexto.\n- ⛔ PROIBIDA a cara de render: 3D, CGI, ilustração, objeto flutuando, pedestal, glow, partículas, raios de luz, fundo preto infinito de estúdio, superfícies lisas demais.",
            "comparativo" => $"ELEMENTO PRINCIPAL — COMPARATIVO / ANTES → DEPOIS (dois estados contrastantes):\n- A área visual (abaixo do bloco de texto) é DIVIDIDA em DOIS estados que contam a transformação da copy. Divisão lado a lado OU cima/baixo, com separação visual clara (linha ou seta; sem selo com palavra, a peça não tem texto).\n- ESTADO A — SEM a solução (a dor da copy): cena/elementos em tom FRIO e dessaturado (cinza/azulado), sinais de caos: alertas, planilhas soltas, ferramentas desconectadas, retrabalho.\n- ESTADO B — COM a solução (a promessa da copy): cena organizada, tom QUENTE/positivo com a cor da marca, tudo conectado num painel único, resultado visível.\n- O conteúdo de cada lado sai do NICHO do cliente ({icp}) e da MENSAGEM da copy — nunca genérico. Se houver tela do sistema, ela aparece no estado B (a solução).\n- O CONTRASTE entre os dois estados é o herói: mesmo enquadramento nos dois lados pra a diferença saltar. Sem virar checklist nem lista de bullets.\n- Limpo e profissional, sem exagero.",
            "conceito-3d" => $"ELEMENTO PRINCIPAL — DIAGRAMA CONCEITUAL 3D (sem device e sem pessoa como protagonista):\n- NÃO use notebook, celular nem pessoa como elemento principal. Aqui o herói visual é uma CENA 3D que ILUSTRA O CONCEITO da copy (o processo, o ecossistema, a rede, o fluxo, o ganho) do cliente {nome} — nicho: {icp}.\n- ESCOLHA UMA das 3 composições abaixo (VARIE entre as peças, não repita sempre a mesma):\n  (a) CONSTELAÇÃO / REDE: nós circulares de vidro brilhante com ÍCONES (features, integrações, canais) conectados por linhas com glow a um HUB central. O hub representa a plataforma do cliente.\n  (b) CUBOS / NÓS FLUTUANTES: cubos e formas geométricas translúcidas (vidro), com ícones dentro, flutuando e conectados por linhas/pontos de luz — como um grafo de dados suspenso sobre a cena.\n  (c) DIORAMA ISOMÉTRICO LOW-POLY: uma \"fatia\" 3D isométrica do mundo do cliente (galpão, lojas, frota, obra, farmácia, rede de pontos) com setas de fluxo coloridas conectando as etapas do processo. Estilo low-poly limpo, miniatura.\n- Os ícones/nós/etapas devem refletir a MENSAGEM da copy (o que ela promete/resolve) e o NICHO do cliente — nunca elementos genéricos sem função.\n- Profundidade real: depth of field, glow sutil nas conexões, sombras coerentes. Pode flutuar sobre uma cena real do setor OU sobre fundo limpo.\n- A cor da marca ilumina as conexões/nós como acento. O conjunto ocupa o espaço restante da peça, sem competir com a headline.",
            "mockup-premium" => $"ELEMENTO PRINCIPAL — MOCKUP 3D REALISTA PREMIUM (render de estúdio, flutuante):\n- Um DISPOSITIVO 3D fotorrealista — VARIE entre notebook, tablet OU smartphone (alterne entre as peças, nunca repita sempre o mesmo) — em ângulo 3/4, FLUTUANDO ou apoiado sobre pedestais/planos geométricos, com sombra e reflexo realistas. Estilo render 3D de produto de agência premium.\n{tela}\n- Ao redor do dispositivo, 2-4 PROPS 3D FLUTUANTES coerentes com o NICHO do cliente ({icp}) e com a mensagem da copy: escolha objetos do universo do segmento. Os props reforçam o segmento, flutuam com profundidade, alguns parcialmente atrás do device.\n- Ícones/badges 3D flutuantes OPCIONAIS em vidro fosco (glassmorphism), poucos e elegantes: cada um é uma funcionalidade REAL do produto (ver ELEMENTOS DE APOIO), nunca ícone genérico.\n- Proporção do device SEMPRE realista (nunca esticar/distorcer). O device e os props complementam o texto, não competem com a headline.",
            _ => $"ELEMENTO PRINCIPAL — PESSOA DO ICP USANDO O SISTEMA:\n- Pessoa do ICP do cliente: {icp}\n- A pessoa está USANDO o sistema do cliente {nome} — pode estar:\n  (a) sentada à mesa, de FRENTE pra câmera, trabalhando no notebook (vemos a traseira da tampa; a tela fica virada pra ela)\n  (b) em pé apresentando dados em uma TV/monitor\n  (c) com o celular na mão olhando o app\n  (d) gesticulando em uma reunião com o dashboard atrás\n  (e) caminhando pelo ambiente de trabalho com tablet\n- PROPORCAO REALISTA do notebook: tamanho crível de um notebook real (13 a 16 polegadas) em relação à pessoa e à mesa — NUNCA em miniatura, esticado, achatado nem distorcido.\n- POSICAO DAS MAOS: se a pessoa estiver usando o notebook, as mãos ficam SOBRE o teclado/trackpad do próprio notebook. É PROIBIDO a pessoa \"digitar\" na mesa vazia.\n- Se a tela do device aparecer legível na cena:\n{tela}\n- VARIAR a posição/ângulo entre peças. Plano americano ou médio. Câmera lateral ou 3/4.\n- Expressão CONCORDANTE com a copy: dor → concentrada/preocupada. Solução → confiante/aliviada. Oferta → decidida.\n- Ambiente coerente com o segmento do ICP (escritório, loja, galpão, sala de reunião).\n- Os cards/ícones que flutuam ao redor da pessoa mostram o que o PRODUTO DO CLIENTE faz (ver ELEMENTOS DE APOIO): é o que faz a peça ser deste cliente e não de qualquer empresa.\n- Vestimenta business casual coerente com o ICP.\n- Iluminação cinematográfica, foto realista profissional. NUNCA stock photo genérico.",
        };
    }

    static string BlocoAreaReservada(CopyDaArte copy, string formato)
    {
        var story = formato == "story";
        var corpoHead = story ? 72 : 51; var corpoSub = story ? 48 : 27; var corpoCta = story ? 40 : 31;
        const double larguraDaColuna = 1080 * 0.92, caractere = 0.56;
        int Linhas(string t, int corpo) => Math.Max(1, (int)Math.Ceiling(t.Trim().Length * caractere * corpo / larguraDaColuna));
        var lh = Linhas(copy.Head, corpoHead); var ls = copy.Sub.Trim() == "" ? 0 : Linhas(copy.Sub, corpoSub);
        var total = (int)Math.Round(lh * corpoHead * 1.12) + (ls > 0 ? 28 + (int)Math.Round(ls * corpoSub * 1.5) : 0) + 34 + (int)Math.Round(64.0 * corpoCta / 30);
        return $"ÁREA RESERVADA (fica VAZIA — ⛔ não desenhe nada nela):\n- Altura aproximada a manter limpa: {total} px, começando logo abaixo da faixa de respiro do topo.\n- Ela é só o FUNDO continuando: tonalidade UNIFORME, sem detalhe visual competindo, sem elemento gráfico, sem forma e sem texto. É onde o software vai pousar a tipografia depois.\n- ⛔ Não desenhe caixas, molduras, retângulos, tarjas nem placeholders marcando essa área. Ela é só o fundo, contínuo com o resto da peça.";
    }

    /// <summary>O prompt da casa (buildPrompt + buildDiagramacaoCentralizada), SEM TEXTO, com a cena do rodízio e o tema.</summary>
    public static string Montar(CopyDaArte copy, Cliente c, MarcaDaArte? marca, string formato, string cena, TemaDaPeca? tema, string? ajuste = null)
    {
        var story = formato == "story";
        var foto = cena == "objeto-simbolo";
        var semDevice = foto;
        var cores = marca?.Cores.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        var paleta = cores.Count > 0 ? string.Join(", ", cores) : "SEM PALETA CADASTRADA — use SOMENTE neutros (branco, preto, tons de cinza) mais a cor dominante da logo/marca fornecida. NÃO invente cores de destaque.";
        var corCta = marca?.CorCta?.Trim() ?? "";
        var coresFundo = marca?.CoresFundo.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? [];
        var dimensoes = story ? "1080 x 1920 px (9:16)" : "1080 x 1350 px (4:5)";
        var contexto = ContextoVisual(c);
        var nivel = cena == "objeto-simbolo" ? "completo" : "assunto";
        var blocosDoCliente = string.Join("\n\n", new[] { contexto, tema is null ? "" : BlocoTemaDaPeca(tema, nivel) }.Where(x => x != ""));

        var sb = new StringBuilder();
        sb.Append($"""
            Crie um criativo estático publicitário no formato {dimensoes}, com visual premium, sofisticado, limpo, publicitário e de alta conversão.

            O resultado deve manter uma composição muito bem diagramada, com foco principal na leitura do texto (que entra depois, como camada), equilíbrio visual, poucos elementos bem escolhidos e uma cena complementar coerente com a copy.

            OBJETIVO:
            Criar um anúncio visualmente forte, organizado e refinado, onde a diagramação do texto seja resolvida primeiro, e a composição visual seja construída depois no espaço restante da peça. O criativo deve ter aparência premium, boa profundidade e contexto visual, mas sem poluição e sem excesso de elementos.

            ESTRUTURA DA PEÇA:
            - a área do texto (vazia nesta geração)
            - 1 elemento principal {(foto ? "(o objeto-símbolo, fotografado no mundo do cliente — NUNCA aparelho/tela)" : "(ex.: smartphone, notebook, dashboard, produto ou pessoa)")}
            - poucos elementos visuais de apoio relacionados ao contexto da copy

            ══ REFERÊNCIA DE ESTILO (biblioteca de criativos que converte) ══
            {(foto
                ? "Fotografia publicitária de revista, feita no ambiente real do cliente: câmera de verdade, luz do próprio lugar, profundidade de campo rasa, acabamento de campanha paga. A diferença desta peça: ela sai SEM TEXTO."
                : "Inspire-se na linguagem visual de anúncios SaaS/tech B2B premium: fundo com TEXTURA TECH sutil (grid fino de quadradinhos, pontos ou linhas em 3-6% de opacidade), GLOW suave da cor da marca em um ou dois cantos, profundidade, acabamento de campanha paga. A diferença desta peça: ela sai SEM TEXTO.")}

            {(foto ? BlocoFundoFoto() : BlocoFundo())}

            ══ LAYOUT ══
            {BlocoTopo()}

            {BlocoSemTexto(foto)}

            ══ REGRAS ABSOLUTAS ══
            - ⛔ NENHUM TEXTO, NENHUMA LETRA, NENHUM NÚMERO — em lugar nenhum da peça. É a regra que manda sobre todas as outras.
            - ⛔ Nenhuma logo, ícone de marca, watermark ou placeholder desenhado pela IA.
            - A faixa de respiro do topo continua VAZIA (é onde a logo real entra no estúdio).
            - A metade superior fica calma e de tonalidade uniforme; o elemento visual ocupa a metade inferior.
            - Paleta: só as cores da marca listadas abaixo. Cena fotográfica mantém as cores naturais do ambiente.
            - STORY (9:16): mesma estrutura, mais respiro, elemento visual ocupa ~42% da base, safe zone 100px.

            REGRAS FIXAS — SEM LOGO:
            - A peça é gerada SEM a logo do cliente. A logo real é aplicada DEPOIS, pelo usuário, no estúdio.
            - NÃO desenhe, adicione, invente ou posicione nenhuma logo, marca, watermark, assinatura ou elemento de identidade visual — em canto nenhum (nem inferior, nem topo).
            - ⛔ NUNCA transforme esta instrução em texto desenhado: a peça NÃO pode conter o nome do cliente escrito como marca nem rótulos como "logo", "sua logo aqui", "espaço reservado para a logo" — em NENHUMA área.

            REGRAS FIXAS PARA O ELEMENTO PRINCIPAL:
            - {(semDevice ? "O elemento principal" : "O smartphone, notebook ou elemento principal")} não deve chamar mais atenção do que o texto.
            - Deve complementar a mensagem visualmente, estar bem integrado ao restante da composição e ocupar o espaço restante da peça com equilíbrio e boa proporção. Deve parecer parte da cena, e não um objeto solto.{(semDevice ? "" : "\n- O aparelho (celular/notebook) deve ter PROPORÇÃO e TAMANHO REALISTAS. NUNCA esticar, distorcer ou ampliar o aparelho de forma exagerada — especialmente no formato vertical (9:16/Story), onde o device mantém o tamanho NATURAL e o respiro extra vem do espaço em volta.")}

            REGRAS IMPORTANTES:
            - Não adicionar bullets, listas de benefícios, checklists nem excesso de cards.
            - Não poluir visualmente. Não deixar a composição bagunçada.
            - Não permitir que o background roube atenção da área do texto, nem que o elemento principal domine a peça.
            - Não transformar o criativo em um template genérico de SaaS.

            {BlocoAreaReservada(copy, formato)}

            {blocosDoCliente}

            MARCA:
            {c.Nome}

            PALETA — USE EXCLUSIVAMENTE ESTAS CORES DA MARCA (é PROIBIDO inventar qualquer cor de destaque/acento fora desta lista):
            {paleta}
            - TODA cor de DESIGN da peça vem SÓ desta paleta: ícones, badges/pills, formas, linhas, molduras e qualquer fundo GRÁFICO/sólido. NUNCA use um azul, verde, laranja (ou qualquer cor) que NÃO esteja listado acima — nem como acento, nem em detalhe.
            - A cor da marca é o ACENTO: aplique-a nos destaques e elementos gráficos com equilíbrio, sem "inundar" a peça inteira.
            {(foto
                ? "- Nesta peça o fundo É uma FOTO real do ambiente do cliente: o cenário mantém as cores naturais, e a cor da marca entra no degradê da área do texto (ver ESTILO FOTOGRAFIA) e nos ELEMENTOS GRÁFICOS. Nunca invente cor de destaque fora da paleta."
                : "- ÚNICA exceção: quando o fundo for uma CENA FOTOGRÁFICA real (escritório, frota, obra, galpão), o cenário mantém as cores naturais do ambiente — mas os ELEMENTOS GRÁFICOS por cima continuam OBRIGATORIAMENTE na paleta acima.")}
            {(corCta != "" ? $"\nCOR DO CTA (OBRIGATÓRIA, para o software que entra depois): {corCta}." : "")}
            {(coresFundo.Count > 0 ? $"\nCOR DE FUNDO EXATA (INSTRUÇÃO CRÍTICA — prioridade máxima, sobrescreve tudo):\n- O background da peça DEVE ser preenchido com a cor SÓLIDA e PLANA: {coresFundo[0]}. Use EXATAMENTE este código HEX. Nenhuma variação, nenhuma gradação, nenhum filtro.\n- Todos os elementos são colocados SOBRE este fundo sólido {coresFundo[0]}." : "")}

            {ElementoPrincipal(cena, c, tema)}

            RESULTADO ESPERADO:
            Um FUNDO de criativo estático premium, equilibrado e bem diagramado, com composição CENTRALIZADA num eixo único: faixa de respiro vazia no topo (onde a logo real entra depois, no estúdio), a área reservada logo abaixo dela LIMPA e de tonalidade uniforme (o texto entra depois, por software), elemento visual ancorado na base e visualmente controlado, fundo premium construído depois da diagramação. ⛔ SEM nenhuma letra, nenhum número, nenhum botão e nenhuma logo/marca desenhada.
            """);
        if (foto)
        {
            var escura = CorEscuraDaMarca(coresFundo.Concat(cores));
            sb.Append($"\n\n══ ESTILO FOTOGRAFIA (vale sobre qualquer referência de estilo ou de fundo acima) ══\n- Esta peça é uma FOTOGRAFIA de verdade, não um render: nada de textura tech, grid, glow, partículas nem fundo de estúdio. O fundo é o AMBIENTE REAL do cliente, fotografado pela mesma câmera, desfocado atrás do objeto.\n- A foto ocupa a peça inteira. TODA a ÁREA DO TEXTO (a região reservada pro texto, descrita acima) recebe um degradê {(escura != "" ? $"da cor {escura} da marca" : "de um tom escuro neutro")}: quase sólido no começo, ainda forte e parelho até o FIM da área do texto, e só depois dela se dissolvendo na foto. É esse degradê que dá a cor da marca e a legibilidade do texto — suave e contínuo, nunca uma faixa ou tarja com borda.\n- Luz natural ou do próprio ambiente. Nada de luz de palco ou neon.");
        }
        if (!string.IsNullOrWhiteSpace(ajuste)) sb.Append($"\nAJUSTE PRIORITÁRIO DO GESTOR (aplicar sobre o resto): {ajuste!.Trim()}\n");
        return sb.ToString().Trim();
    }

    /* ---- o nome do arquivo e o caminho no Storage (route.ts, safeFilenameDesigner) ----- */

    public static string NomeDoArquivo(string funil, int numero, string formato)
    {
        var nome = $"Estatico {funil.ToUpperInvariant()} - Peca {numero}{(formato == "story" ? " - Story" : "")}.png";
        return Regex.Replace(nome, @"[^A-Za-z0-9._-]+", "_");
    }

    public static string CaminhoNoStorage(string? squadId, string slug, int loteNumero, string loteId, string funil, int numero, string formato) =>
        $"{(string.IsNullOrEmpty(squadId) ? "sem-squad" : squadId)}/{slug}/lote-{loteNumero}-{loteId[..Math.Min(8, loteId.Length)]}/{NomeDoArquivo(funil, numero, formato)}";

    /// <summary>As camadas de texto (`designer_pecas.texto_layout`), no formato que o estúdio do V1 lê.</summary>
    public static string TextoLayout(Layout layout, string corDestaque, string corBotao)
    {
        var itens = layout.Caixas.Select(c => new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString("n")[..14], ["papel"] = c.Papel, ["texto"] = c.Texto, ["x"] = c.X, ["y"] = c.Y, ["w"] = c.W,
            ["tamanho"] = c.Tamanho, ["peso"] = c.Peso, ["fonte"] = "poppins", ["cor"] = c.Cor, ["alinhamento"] = c.Alinhamento == "centro" ? "centro" : "esquerda",
            ["entrelinha"] = Diagramacao.Entrelinha, ["destaques"] = c.Destaques, ["corDestaque"] = corDestaque,
            ["fundo"] = c.Papel == "cta" ? corBotao : null, ["raio"] = c.Papel == "cta" ? 0.06 : 0, ["icone"] = c.Papel == "cta" ? "seta" : null, ["linhasDoModelo"] = c.Linhas.Count,
        }).ToList<object>();
        itens.Add(new Dictionary<string, object?> { ["tipo"] = "diagramacao-da-api", ["versao"] = 1, ["caixas"] = layout.Caixas.ToDictionary(c => c.Papel, c => new { x0 = c.X, y0 = c.Y, x1 = c.X + c.W, linhas = c.Linhas.Count }) });
        return JsonSerializer.Serialize(itens, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
