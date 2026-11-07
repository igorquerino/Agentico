using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueOS.Core.Modelos;

namespace BlueOS.Core.Copy;

/* ============================================================================
   O AGENTE DE COPY, SEM REDE: a porta de lib/copy/gravador.ts, prompt-vault.ts,
   contexto.ts, crivo.ts, etiqueta.ts e da rota /api/os/copy/gerar do V1.
   ----------------------------------------------------------------------------
   O SYSTEM tem ordem fixa (AD-2): a régua V8 (o arquivo, inteiro), as REGRAS
   FECHADAS do vault, a ORIENTAÇÃO (geral e de nicho), o contexto do cliente e,
   se houver, a ficha do nicho. O pedido vai na mensagem do usuário, com o
   briefing por último. O modelo devolve JSON no esquema, com a ESTRUTURA
   DECLARADA de cada peça; o crivo confere a declaração e o texto contra as
   proibições fechadas, regera UMA vez e grava `crivo_ok`.

   O vault vem do banco (trafego_vault_em_uso → trafego_vault_nota), nas mesmas
   tabelas que o V1 lê; quem lê é a API, aqui só se monta o texto.
   ============================================================================ */
public sealed record RegraDoVault(string Id, string Chave, string Tipo, string Status, string Titulo, string Corpo, IReadOnlyList<string> Etapas, IReadOnlyList<string> PapeisPreferidos);
public sealed record EstruturaDeNicho(string Etapa, string Parte, int Ordem, string Estrutura, string Base);
public sealed record NotaDeNicho(string Id, IReadOnlyList<EstruturaDeNicho> Estruturas, IReadOnlyList<EstruturaDeNicho> Evitar);
public sealed record SnapshotDoVault(string Sha, IReadOnlyList<RegraDoVault> Regras, IReadOnlyDictionary<string, NotaDeNicho> Nichos)
{
    public NotaDeNicho? Nicho(string? slug) => slug is null ? null : Nichos.TryGetValue($"NICHO-{slug}", out var n) ? n : null;
    public RegraDoVault? RegraDaChave(string chave) => Regras.FirstOrDefault(r => r.Chave == chave);
}

/// <summary>A estrutura declarada pelo modelo, conferida contra o vocabulário (etiqueta.ts).</summary>
public sealed record Declaracao(string AnguloHeadline, IReadOnlyList<string> FormaHeadline, string PapelSub, string TipoCta);

public sealed record RegraNoCrivo(string Chave, string Id, string Status, bool Ok, string? Motivo);
public sealed record ResultadoDoCrivo(string? Sha, bool? Ok, IReadOnlyList<RegraNoCrivo> Regras, IReadOnlyList<(string Chave, string Motivo)> Desligadas)
{
    public string? MotivoDaReprovacao => Ok == false ? string.Join("; ", Regras.Where(r => !r.Ok).Select(r => $"{r.Chave}: {r.Motivo}")) : null;
}

/// <summary>Uma peça como o modelo a devolve.</summary>
public sealed record PecaDoModelo(string Headline, string Corpo, string Cta, string? AnguloHeadline, IReadOnlyList<string>? FormaHeadline, string? PapelSub, string? TipoCta);

public sealed record ItemDoPedido(string Tipo, string Funil, int NPecas);

public static class PromptDoCopy
{
    public const string SlugGeral = "saas";
    public static readonly string[] ChavesCopy = ["nunca_reuniao_cta_mofu", "prefira_contexto_ou_fragmento_sub", "nunca_negacao_headline", "nunca_prova_sub_mofu", "pergunta_so_com_dor_concreta", "cta_pesa_pouco"];
    public static readonly string[] Etapas = ["TOFU", "MOFU", "BOFU"];
    public static readonly string[] Campos = ["headline", "corpo", "cta"];
    public const string InstrucaoDaDeclaracao =
        "Para CADA peça, declare a estrutura que usou, com os nomes do vocabulário: angulo_headline (um), forma_headline (lista, vazia quando não há negação, promessa com número nem vocativo), papel_sub (um) e tipo_cta (o tipo do texto do botão). Declare o que a peça É, não o que gostaria que fosse: a declaração passa pelo crivo junto com o texto.";
    const string SemVersao = "vault sem versão nesta base: nenhuma regra fechada nem orientação de nicho foi lida. Vale só a régua acima.";
    public const int TetoPorItem = 15;
    public const int TetoPorLote = 30;
    public const int LimiteBriefing = 4000;

    static readonly Dictionary<string, string> RotuloTipo = new() { ["estatico"] = "ESTÁTICO", ["video"] = "VÍDEO" };
    static readonly Dictionary<string, string> RotuloCampo = new() { ["headline"] = "HEADLINE", ["corpo"] = "CORPO", ["cta"] = "CTA" };
    static readonly Dictionary<string, string> Parte = new() { ["headline"] = "HEADLINE (ângulo)", ["sub"] = "SUB (papel)", ["cta"] = "CTA (tipo)" };

    /* ---- o vault, lido do JSON das notas ------------------------------------- */

    /// <summary>As notas de uma versão do vault (frontmatter + corpo) viram o snapshot que o prompt e o crivo usam.</summary>
    public static SnapshotDoVault Snapshot(string sha, IEnumerable<(string Id, JsonElement Frontmatter, string Corpo)> notas)
    {
        var regras = new List<RegraDoVault>(); var nichos = new Dictionary<string, NotaDeNicho>();
        foreach (var (id, fm, corpo) in notas)
        {
            if (fm.ValueKind != JsonValueKind.Object) continue;
            var tipo = S(fm, "tipo") ?? "";
            if (tipo == "nicho")
            {
                nichos[id] = new NotaDeNicho(id, Estruturas(fm, "estruturas"), Estruturas(fm, "evitar"));
                continue;
            }
            var chave = S(fm, "chave_copy");
            if (string.IsNullOrEmpty(chave)) continue;
            var etapas = fm.TryGetProperty("aplica_em", out var ap) && ap.ValueKind == JsonValueKind.Object && ap.TryGetProperty("etapa", out var et) && et.ValueKind == JsonValueKind.Array
                ? et.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList() : new List<string>();
            var papeis = fm.TryGetProperty("parametros", out var pr) && pr.ValueKind == JsonValueKind.Object && pr.TryGetProperty("papeis_sub", out var ps) && ps.ValueKind == JsonValueKind.Array
                ? ps.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList() : new List<string>();
            regras.Add(new RegraDoVault(id, chave, tipo, S(fm, "status") ?? "", S(fm, "titulo") ?? id, corpo, etapas, papeis));
        }
        return new SnapshotDoVault(sha, regras, nichos);
    }

    static List<EstruturaDeNicho> Estruturas(JsonElement fm, string campo) =>
        fm.TryGetProperty(campo, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new EstruturaDeNicho(S(x, "etapa") ?? "", S(x, "parte") ?? "", x.TryGetProperty("ordem", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0, S(x, "estrutura") ?? "", S(x, "base") ?? "")).ToList()
            : new();

    static string? S(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /* ---- os blocos do system (prompt-vault.ts) --------------------------------- */

    public static string BlocoDeRegras(SnapshotDoVault? snapshot, out List<(string Chave, string Motivo)> desligadas)
    {
        desligadas = new();
        if (snapshot is null) return $"REGRAS FECHADAS (vault sem versão)\n{SemVersao}";
        var linhas = new List<string>();
        foreach (var chave in ChavesCopy)
        {
            var r = snapshot.RegraDaChave(chave);
            if (r is null) { desligadas.Add((chave, "ausente")); continue; }
            linhas.Add(LinhaDaRegra(r));
        }
        var texto = new List<string>
        {
            $"REGRAS FECHADAS (vault {snapshot.Sha[..Math.Min(7, snapshot.Sha.Length)]})",
            "O que o estudo de copy fechou. A precedência é fixa: PROIBIDO acima > régua > PREFERÊNCIA > briefing do gestor > ORIENTAÇÃO (geral e de nicho). O briefing nunca libera uma proibição. Uma peça que fira um PROIBIDO é reprovada pelo crivo e regerada.",
        };
        texto.AddRange(linhas.Count > 0 ? linhas : ["(nenhuma regra de copy com chave nesta versão do vault)"]);
        if (desligadas.Count > 0) texto.Add($"Sem regra viva nesta versão: {string.Join(", ", desligadas.Select(d => d.Chave))}.");
        return string.Join("\n", texto);
    }

    static string LinhaDaRegra(RegraDoVault r)
    {
        var corpo = Regex.Replace(r.Corpo, @"\s+", " ").Trim();
        if (r.Tipo == "proibicao" && r.Status == "fechada") return $"- PROIBIDO ({r.Id}, {r.Chave}): {r.Titulo}. {corpo}";
        if (r.Status == "hipotese") return $"- PREFERÊNCIA, ainda hipótese ({r.Id}, {r.Chave}): {r.Titulo}. {corpo}";
        var extra = r.PapeisPreferidos.Count > 0 ? $" Papéis de SUB preferidos: {string.Join(", ", r.PapeisPreferidos.Select(Copy.Estruturas.Legivel))}." : "";
        return $"- ORIENTAÇÃO ({r.Id}, {r.Chave}): {r.Titulo}. {corpo}{extra}";
    }

    static bool TemBaseNaEtapa(NotaDeNicho n, string etapa) => n.Estruturas.Any(e => e.Etapa == etapa);

    static IEnumerable<string> LinhasDaEtapa(NotaDeNicho n, string etapa)
    {
        yield return $"{etapa}:";
        foreach (var parte in new[] { "headline", "sub", "cta" })
        {
            var lista = n.Estruturas.Where(e => e.Etapa == etapa && e.Parte == parte).OrderBy(e => e.Ordem).ToList();
            if (lista.Count == 0) continue;
            yield return $"  {Parte[parte]}: {string.Join(" · ", lista.Select(e => $"{e.Ordem}. {e.Estrutura} ({e.Base})"))}";
        }
    }

    static string? LinhaDoEvitar(NotaDeNicho n, IReadOnlyList<string> etapas)
    {
        var lista = n.Evitar.Where(e => etapas.Contains(e.Etapa)).ToList();
        return lista.Count == 0 ? null : "EVITAR: " + string.Join(" · ", lista.Select(e => $"{e.Etapa} {Parte[e.Parte]}: {e.Estrutura}"));
    }

    /// <summary>ORIENTAÇÃO DE NICHO: a ordem das estruturas por etapa. `origem` por etapa: nicho | v8 | sem_base.</summary>
    public static string BlocoDeNicho(SnapshotDoVault? snapshot, string? nichoSlug, IReadOnlyList<string> etapas, IReadOnlyDictionary<string, bool>? baseGeral, out Dictionary<string, string> origem, out string? nichoId)
    {
        origem = Etapas.ToDictionary(e => e, _ => "sem_base"); nichoId = null;
        var comGeral = baseGeral is not null;
        var semNicho = comGeral ? "todas as peças seguem a orientação geral" : "vale só a régua e as regras fechadas";
        if (snapshot is null) return $"ORIENTAÇÃO DE NICHO (vault sem versão)\n{SemVersao}";
        if (string.IsNullOrWhiteSpace(nichoSlug)) return $"ORIENTAÇÃO DE NICHO\nsem nicho: preencha o card Tráfego do cliente. {(comGeral ? "Até lá, todas as peças seguem a orientação geral." : "Vale só a régua e as regras fechadas.")}";
        var nota = snapshot.Nicho(nichoSlug);
        if (nota is null) return $"ORIENTAÇÃO DE NICHO\nsem nota para o nicho \"{nichoSlug}\" nesta versão do vault: {semNicho}.";
        nichoId = nota.Id;
        var linhas = new List<string>
        {
            $"ORIENTAÇÃO DE NICHO ({nota.Id}, vault {snapshot.Sha[..7]})",
            "A ordem das estruturas que funcionam neste nicho, por etapa e parte da peça. 1 é a melhor. `ampla` = base larga no estudo; `indicacao` = base estreita, vale como pista. A estrutura é o MOLDE da frase (o ângulo da headline, o papel da sub, o tipo do CTA), nunca uma frase pronta. Siga a ordem quando o briefing não pedir outra coisa; nunca contra um PROIBIDO.",
        };
        foreach (var etapa in etapas)
        {
            if (!TemBaseNaEtapa(nota, etapa))
            {
                origem[etapa] = "v8";
                linhas.Add($"{etapa}: sem base no nicho; {(baseGeral is not null && baseGeral.TryGetValue(etapa, out var b) && b ? "vale a orientação geral" : "vale a régua")}.");
                continue;
            }
            origem[etapa] = "nicho";
            linhas.AddRange(LinhasDaEtapa(nota, etapa));
        }
        if (LinhaDoEvitar(nota, etapas) is { } evitar) linhas.Add(evitar);
        return string.Join("\n", linhas);
    }

    /// <summary>ORIENTAÇÃO GERAL (a nota NICHO-saas): vazia quando a versão não a tem.</summary>
    public static string BlocoGeral(SnapshotDoVault? snapshot, IReadOnlyList<string> etapas, out Dictionary<string, bool> baseGeral, out string? geralId)
    {
        baseGeral = Etapas.ToDictionary(e => e, _ => false); geralId = null;
        var nota = snapshot?.Nicho(SlugGeral);
        if (nota is null) return "";
        geralId = nota.Id;
        var linhas = new List<string>
        {
            $"ORIENTAÇÃO GERAL ({nota.Id})",
            "Vale para todo cliente: todos são SaaS, e esta é a ordem das estruturas que funcionam no geral, por etapa e parte da peça.",
        };
        foreach (var etapa in etapas)
        {
            baseGeral[etapa] = TemBaseNaEtapa(nota, etapa);
            if (!baseGeral[etapa]) { linhas.Add($"{etapa}: sem base na orientação geral; vale a régua."); continue; }
            linhas.AddRange(LinhasDaEtapa(nota, etapa));
        }
        if (LinhaDoEvitar(nota, etapas) is { } evitar) linhas.Add(evitar);
        return string.Join("\n", linhas);
    }

    public sealed record BlocosDoVault(string? Sha, string RegrasFechadas, string Orientacao, string? NichoId, string? GeralId,
        IReadOnlyDictionary<string, (bool Geral, bool Nicho)> FontesPorEtapa, IReadOnlyDictionary<string, string> OrigemPorEtapa, IReadOnlyList<(string Chave, string Motivo)> Desligadas);

    /// <summary>Os blocos do vault, na ordem em que entram no system (depois da V8, antes do cliente).</summary>
    public static BlocosDoVault BlocosDoVaultPara(SnapshotDoVault? snapshot, string? nichoSlug, IReadOnlyList<string> etapas)
    {
        var regras = BlocoDeRegras(snapshot, out var desligadas);
        var geral = BlocoGeral(snapshot, etapas, out var baseGeral, out var geralId);
        if (geralId is null)
        {
            var nicho = BlocoDeNicho(snapshot, nichoSlug, etapas, null, out var origemN, out var nichoId);
            var fontes = Etapas.ToDictionary(e => e, e => (false, origemN[e] == "nicho"));
            return new(snapshot?.Sha, regras, nicho, nichoId, null, fontes, origemN, desligadas);
        }
        var nichoTxt = BlocoDeNicho(snapshot, nichoSlug, etapas, baseGeral, out var origemNicho, out var nichoId2);
        var fontes2 = Etapas.ToDictionary(e => e, e => (baseGeral[e], origemNicho[e] == "nicho"));
        var origem = Etapas.ToDictionary(e => e, e => fontes2[e].Item1 ? "geral" : fontes2[e].Item2 ? "nicho" : "v8");
        var orientacao = string.Join("\n\n", new[]
        {
            $"ORIENTAÇÃO DO LOTE (vault {snapshot!.Sha[..7]})\nDuas orientações de estrutura, as duas sem frase pronta: a GERAL (todos os clientes juntos, porque todos são SaaS) e a DE NICHO (a do nicho do cliente). O pedido diz, grupo a grupo, quais peças seguem cada uma; peça sem essa indicação segue a geral. Cada peça segue a ordem da orientação dela quando o briefing não pedir outra coisa, e nunca contra um PROIBIDO.",
            geral, nichoTxt,
        }.Where(x => x != ""));
        return new(snapshot.Sha, regras, orientacao, nichoId2, geralId, fontes2, origem, desligadas);
    }

    /// <summary>A origem de UMA peça: a orientação que a divisão lhe deu, quando a etapa tem base nela; senão a da etapa.</summary>
    public static (string Origem, string? NichoId) OrigemDaPeca(BlocosDoVault b, string etapa, string? fonte)
    {
        (bool Geral, bool Nicho) f = b.FontesPorEtapa.TryGetValue(etapa, out var x) ? x : (false, false);
        if (fonte == "geral" && f.Geral) return ("geral", b.GeralId);
        if (fonte == "nicho" && f.Nicho) return ("nicho", b.NichoId);
        var origem = b.OrigemPorEtapa.TryGetValue(etapa, out var o) ? o : "sem_base";
        return (origem, origem == "geral" ? b.GeralId : b.NichoId);
    }

    /// <summary>A divisão geral/nicho de um grupo (divisao.ts): 1 peça em 5 segue o nicho, quando as duas têm base; sem geral, todas o nicho.</summary>
    public static List<string?> DivisaoDoGrupo(BlocosDoVault b, string etapa, int n, int jaDoNicho, int jaTotal)
    {
        (bool Geral, bool Nicho) f = b.FontesPorEtapa.TryGetValue(etapa, out var x) ? x : (false, false);
        if (!f.Geral && !f.Nicho) return Enumerable.Repeat<string?>(null, n).ToList();
        if (!f.Geral) return Enumerable.Repeat<string?>("nicho", n).ToList();
        if (!f.Nicho) return Enumerable.Repeat<string?>("geral", n).ToList();
        var saida = new List<string?>();
        for (var i = 0; i < n; i++)
        {
            var total = jaTotal + i + 1; var doNicho = jaDoNicho + saida.Count(s => s == "nicho");
            saida.Add(doNicho * 5 < total ? "nicho" : "geral");
        }
        /* a geral vem primeiro no pedido, o nicho depois (textoDaDivisao diz "as peças 1 a g seguem a geral") */
        return saida.OrderBy(s => s == "nicho" ? 1 : 0).ToList();
    }

    public static string TextoDaDivisao(IReadOnlyList<string?> fontes)
    {
        var n = fontes.Count; var g = fontes.Count(x => x == "geral"); var k = fontes.Count(x => x == "nicho");
        if (n == 0 || (g == 0 && k == 0)) return "";
        string Segue(int a, int b) => a == b ? $"a peça {a} segue" : $"as peças {a} a {b} seguem";
        if (g == n) return n == 1 ? "segue a ORIENTAÇÃO GERAL" : "todas seguem a ORIENTAÇÃO GERAL";
        if (k == n) return n == 1 ? "segue a ORIENTAÇÃO DE NICHO" : "todas seguem a ORIENTAÇÃO DE NICHO";
        return $"{Segue(1, g)} a ORIENTAÇÃO GERAL; {Segue(g + 1, n)} a ORIENTAÇÃO DE NICHO";
    }

    /* ---- o contexto do cliente (contexto.ts) ------------------------------------ */

    static string Linha(string rotulo, string? v) => string.IsNullOrWhiteSpace(v) ? "" : $"- {rotulo}: {v.Trim()}\n";
    static string ListaTxt(string rotulo, IEnumerable<string>? v)
    {
        var itens = (v ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        return itens.Count == 0 ? "" : $"- {rotulo}:\n" + string.Join("", itens.Select(x => $"  - {x}\n"));
    }
    static IEnumerable<string> Itens(string? texto) => string.IsNullOrWhiteSpace(texto) ? [] : texto.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('-', '•', ' ')).Where(x => x != "");

    public sealed record Inteligencia(string? VozDoCliente, string? Fundador, string? ConcorrenciaPrincipal, IReadOnlyList<string> PersonasAlvo);

    /// <summary>O contexto do cliente, em markdown, como `montarContexto` do V1. `TemIcp` é a régua de "dá pra gerar sem briefing".</summary>
    public static (string Texto, bool TemIcp) ContextoDoCliente(Cliente c, Inteligencia? n = null)
    {
        var i = c.Icp; var p = c.Produto; var e = c.Estrategia;
        var temIcp = i.DoresPrincipais.Count > 0 || !string.IsNullOrWhiteSpace(i.Tipo) || !string.IsNullOrWhiteSpace(i.Segmento);
        var t = new StringBuilder($"# CLIENTE: {c.Nome}\n\n");
        var recorte = Linha("Segmento", i.Segmento) + Linha("Setor", i.Setor) + Linha("Cargo decisor (a copy é DIRECIONADA a ele)", i.CargoDecisor) + Linha("Faturamento mínimo", i.FaturamentoMinimo) + Linha("Localização", i.Localizacao);
        var perfil = Linha("Tipo", i.Tipo) + Linha("Nível de consciência (1-5)", i.NivelConsciencia) + Linha("Temperatura", i.Temperatura) + Linha("Tamanho de mercado", i.TamanhoMercado)
            + Linha("Persona (quem decide, como pessoa)", i.Persona) + Linha("Público-alvo (quem a mídia paga alcança)", i.PublicoAlvo)
            + ListaTxt("Dores principais", i.DoresPrincipais) + ListaTxt("Desejos principais", i.DesejosPrincipais) + ListaTxt("Canais onde estão", i.CanaisOndeEstao) + Linha("Observações", i.Obs);
        if (recorte != "" || perfil != "") t.Append("## ICP 1 (público-alvo)\n").Append(recorte).Append(perfil);
        if (!string.IsNullOrWhiteSpace(p.NomeCorreto) || !string.IsNullOrWhiteSpace(p.Descricao))
        {
            t.Append("\n## Produto\n");
            t.Append(Linha("Nome correto", p.NomeCorreto)).Append(Linha("Descrição", p.Descricao)).Append(Linha("Modelo de negócio", p.ModeloNegocio));
            t.Append(ListaTxt("Diferenciais", Itens(p.Diferenciais))).Append(ListaTxt("Funcionalidades-chave", Itens(p.FuncionalidadesChave)));
            t.Append(ListaTxt("Objeções comuns (a copy deve neutralizá-las)", Itens(p.ObjecoesComuns))).Append(ListaTxt("Concorrentes", Itens(p.Concorrentes)));
        }
        if (!string.IsNullOrWhiteSpace(e.PromessaPrincipal) || !string.IsNullOrWhiteSpace(e.Posicionamento) || !string.IsNullOrWhiteSpace(e.Diferencial) || !string.IsNullOrWhiteSpace(e.Gatilhos))
        {
            t.Append("\n## Estratégia\n");
            t.Append(Linha("Promessa principal", e.PromessaPrincipal)).Append(Linha("Posicionamento", e.Posicionamento)).Append(Linha("Diferencial", e.Diferencial)).Append(Linha("Tipo de funil", e.FunilTipo));
            t.Append(ListaTxt("Gatilhos", Itens(e.Gatilhos)));
        }
        if (n is not null && (!string.IsNullOrWhiteSpace(n.VozDoCliente) || n.PersonasAlvo.Count > 0 || !string.IsNullOrWhiteSpace(n.Fundador)))
        {
            t.Append("\n## Inteligência\n");
            t.Append(Linha("Voz do cliente", n.VozDoCliente)).Append(Linha("Fundador", n.Fundador)).Append(Linha("Concorrência principal", n.ConcorrenciaPrincipal)).Append(ListaTxt("Personas-alvo", n.PersonasAlvo));
        }
        return (t.ToString(), temIcp);
    }

    /* ---- o system e os pedidos (gravador.ts, a rota gerar e a rota ajustar) ------ */

    /// <summary>Os blocos do system, na ordem fixa. O primeiro (a régua) leva cache.</summary>
    public static List<string> SystemDoCopy(string regua, BlocosDoVault blocos, string contexto, string? ficha = null)
    {
        var system = new List<string>
        {
            $"Você é o Agente de Copy da Blue Ocean. Siga RIGOROSAMENTE a régua abaixo — ela é a única doutrina válida. As regras são POR ESCOPO: aplique as de estático ao estático e as de vídeo ao vídeo, nunca cruzadas.\n\n{regua}",
            blocos.RegrasFechadas, blocos.Orientacao,
        };
        if (!string.IsNullOrWhiteSpace(contexto)) system.Add(contexto);
        if (!string.IsNullOrWhiteSpace(ficha)) system.Add(ficha!);
        return system;
    }

    public static string PedidoDeGeracao(IReadOnlyList<(ItemDoPedido Item, IReadOnlyList<string?> Fontes)> itens, string? briefing, string? referencia)
    {
        var pedidoTxt = string.Join("\n", itens.Select(x =>
        {
            var linha = $"- {x.Item.NPecas} peça(s) do tipo {RotuloTipo.GetValueOrDefault(x.Item.Tipo, x.Item.Tipo)} para o funil {x.Item.Funil.ToUpperInvariant()}";
            var quem = TextoDaDivisao(x.Fontes);
            return quem != "" ? $"{linha}: {quem}" : linha;
        }));
        return string.Join("\n", new[]
        {
            $"Gere EXATAMENTE este pedido (um grupo por linha, com a quantidade pedida):\n{pedidoTxt}",
            "\nDirecione cada peça ao CARGO DECISOR dos segmentos do cliente, usando as dores, o vocabulário real do nicho (C2) e o mecanismo (C3).",
            "Adeque cada peça à ETAPA do funil dela (TOFU = consciência, MOFU = consideração, BOFU = decisão).",
            "As peças de um mesmo grupo devem ser DISTINTAS entre si (ângulos diferentes dentro da estrutura), nunca a mesma peça repetida.",
            $"\n{InstrucaoDaDeclaracao}",
            string.IsNullOrWhiteSpace(referencia) ? "" : $"\nCOPY DE REFERÊNCIA (validada — modele estrutura/tom a partir dela, sem copiar literalmente):\n{referencia!.Trim()}",
            string.IsNullOrWhiteSpace(briefing) ? "" : $"\nBRIEFING DO GESTOR (prioridade sobre o contexto e sobre as orientações geral e de nicho; nunca sobre um PROIBIDO):\n{briefing!.Trim()}",
        });
    }

    public static string PedidoDeRegeracao(string tipo, string etapa, PecaDoModelo peca, string motivo, IReadOnlyList<string> campos, (string Rotulo, string Texto)? pedidoDoGestor)
    {
        var fora = Campos.Where(c => !campos.Contains(c)).ToList();
        return string.Join("\n", new[]
        {
            $"A peça abaixo, do tipo {RotuloTipo.GetValueOrDefault(tipo, tipo)} para o funil {etapa}, foi REPROVADA pelo crivo: {motivo}.",
            "Reescreva SÓ esta peça, para a mesma etapa e o mesmo ICP, sem ferir nenhum PROIBIDO das regras fechadas. Mantenha a dor e o mecanismo; mude o que o crivo apontou.",
            $"\nPEÇA REPROVADA:\nHEADLINE: {peca.Headline}\nSUB: {peca.Corpo}\nCTA: {peca.Cta}",
            fora.Count > 0 ? $"\nMUDE SOMENTE: {string.Join(", ", campos.Select(c => RotuloCampo[c]))}. NÃO ALTERE e NÃO devolva: {string.Join(", ", fora.Select(c => RotuloCampo[c]))}; eles ficam exatamente como estão acima." : "",
            $"\n{InstrucaoDaDeclaracao}",
            pedidoDoGestor is { } g && !string.IsNullOrWhiteSpace(g.Texto) ? $"\n{g.Rotulo} (nunca libera um PROIBIDO):\n{g.Texto}" : "",
        });
    }

    public static string PedidoDeAjuste(string tipo, string funil, PecaDeCopy p, string? origem, bool comGeral, string? briefingDoLote, string instrucao, IReadOnlyList<string> campos)
    {
        var estruturaHoje = string.Join(" · ", new[]
        {
            p.AnguloHeadline is { Length: > 0 } and not "outro" ? $"ângulo da headline {p.AnguloHeadline}" : null,
            p.FormasHeadline.Count > 0 ? $"forma {string.Join(", ", p.FormasHeadline)}" : null,
            p.PapelSub is { Length: > 0 } ? $"papel da sub {p.PapelSub}" : null,
            p.TipoCta is { Length: > 0 } and not "outro" ? $"tipo do CTA {p.TipoCta}" : null,
        }.Where(x => x is not null));
        var linhas = new List<string>
        {
            "Ajuste UMA peça de copy existente. Cirurgia pontual: mude só o que a instrução pede, preservando o ângulo, o ICP/cargo decisor e a etapa do funil.",
            $"\nPEÇA ATUAL (tipo {RotuloTipo.GetValueOrDefault(tipo, tipo)}, funil {funil.ToUpperInvariant()}):",
            $"HEADLINE: {p.Headline}", $"CORPO: {p.Sub}", $"CTA: {p.Cta}",
            estruturaHoje != "" ? $"ESTRUTURA DECLARADA HOJE: {estruturaHoje} (mantenha, salvo se a instrução pedir outra)" : "",
        };
        if (comGeral && origem is "geral" or "nicho") linhas.Add($"ORIENTAÇÃO QUE A PEÇA SEGUE: {(origem == "geral" ? "GERAL" : "DE NICHO")} (mantenha)");
        if (!string.IsNullOrWhiteSpace(briefingDoLote)) linhas.Add($"\nBRIEFING ORIGINAL DO LOTE:\n{briefingDoLote}");
        linhas.Add($"\nINSTRUÇÃO DO GESTOR (nunca libera um PROIBIDO das regras fechadas):\n{instrucao.Trim()}");
        linhas.Add(campos.Count < Campos.Length
            ? $"\nAJUSTE SOMENTE: {string.Join(", ", campos.Select(c => RotuloCampo[c]))}.\nNÃO ALTERE e NÃO devolva: {string.Join(", ", Campos.Where(c => !campos.Contains(c)).Select(c => RotuloCampo[c]))}. Eles ficam exatamente como estão acima e servem só de contexto pra o ajuste combinar com o resto da peça."
            : "\nDevolva a peça completa já ajustada (headline, corpo e cta — repita sem alterar os campos que a instrução não tocar).");
        linhas.Add($"\n{InstrucaoDaDeclaracao} Declare a peça INTEIRA como ela fica depois do ajuste, inclusive os campos que você não mexeu.");
        return string.Join("\n", linhas);
    }

    /* ---- o esquema de saída (JSON Schema) ----------------------------------------- */

    static readonly string[] Angulos = ["dor", "desejo_beneficio", "pergunta", "comando", "oferta", "prova_social", "afirmacao_generica", "curiosidade", "urgencia", "outro"];
    static readonly string[] Formas = ["negacao", "promessa_com_numero", "vocativo"];
    static readonly string[] Papeis = ["explica_como_funciona", "detalha_o_ganho", "contexto_da_dor", "instrucao", "oferta", "prova", "reforca_o_publico", "fragmento"];
    static readonly string[] Ctas = ["saiba_mais", "ver_conhecer", "testar", "falar", "agendar_solicitar", "comecar", "inscrever", "baixar", "comprar", "outro"];

    public static Dictionary<string, object> SchemaDaPeca(IReadOnlyList<string>? campos = null)
    {
        campos ??= Campos;
        var props = new Dictionary<string, object>();
        foreach (var c in campos) props[c] = new Dictionary<string, object> { ["type"] = "string" };
        props["angulo_headline"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = Angulos, ["description"] = "O ângulo da headline, no vocabulário" };
        props["forma_headline"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = Formas }, ["description"] = "As formas presentes na headline; vazio quando nenhuma" };
        props["papel_sub"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = Papeis, ["description"] = "O papel que a sub cumpre" };
        props["tipo_cta"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = Ctas, ["description"] = "O tipo do texto do botão" };
        return new Dictionary<string, object> { ["type"] = "object", ["properties"] = props, ["required"] = props.Keys.ToArray(), ["additionalProperties"] = false };
    }

    public static Dictionary<string, object> SchemaDoLote() => new()
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["grupos"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["tipo"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "estatico", "video" } },
                        ["funil"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "tofu", "mofu", "bofu", "misto" } },
                        ["pecas"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = SchemaDaPeca() },
                    },
                    ["required"] = new[] { "tipo", "funil", "pecas" }, ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new[] { "grupos" }, ["additionalProperties"] = false,
    };

    public static Dictionary<string, object> SchemaDaRegeracao(IReadOnlyList<string> campos) => new()
    {
        ["type"] = "object", ["properties"] = new Dictionary<string, object> { ["peca"] = SchemaDaPeca(campos) }, ["required"] = new[] { "peca" }, ["additionalProperties"] = false,
    };

    /* ---- a leitura do que o modelo devolveu ---------------------------------------- */

    public static PecaDoModelo? LerPeca(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        var formas = p.TryGetProperty("forma_headline", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").Distinct().ToList() : null;
        return new PecaDoModelo((S(p, "headline") ?? "").Trim(), (S(p, "corpo") ?? S(p, "sub") ?? "").Trim(), (S(p, "cta") ?? "").Trim(), S(p, "angulo_headline"), formas, S(p, "papel_sub"), S(p, "tipo_cta"));
    }

    /// <summary>Os grupos devolvidos: (tipo, funil, peças).</summary>
    public static List<(string Tipo, string Funil, List<PecaDoModelo> Pecas)> LerGrupos(string json)
    {
        var saida = new List<(string, string, List<PecaDoModelo>)>();
        var root = JsonDocument.Parse(json).RootElement;
        if (!root.TryGetProperty("grupos", out var grupos) || grupos.ValueKind != JsonValueKind.Array) return saida;
        foreach (var g in grupos.EnumerateArray())
        {
            var pecas = g.TryGetProperty("pecas", out var ps) && ps.ValueKind == JsonValueKind.Array ? ps.EnumerateArray().Select(LerPeca).Where(x => x is not null).Select(x => x!).ToList() : new();
            saida.Add(((S(g, "tipo") ?? "").ToLowerInvariant(), (S(g, "funil") ?? "").ToLowerInvariant(), pecas));
        }
        return saida;
    }

    /// <summary>A declaração, só se os quatro campos estão no vocabulário (etiqueta.ts: fora dele é null).</summary>
    public static Declaracao? DeclaracaoDe(PecaDoModelo p)
    {
        if (p.AnguloHeadline is null || !Angulos.Contains(p.AnguloHeadline) || p.PapelSub is null || !Papeis.Contains(p.PapelSub) || p.TipoCta is null || !Ctas.Contains(p.TipoCta)) return null;
        var formas = p.FormaHeadline ?? [];
        if (formas.Any(f => !Formas.Contains(f))) return null;
        return new Declaracao(p.AnguloHeadline, formas.Distinct().ToList(), p.PapelSub, p.TipoCta);
    }

    /* ---- o crivo (crivo.ts): a declaração e o texto, contra as proibições fechadas -- */

    static readonly Regex[] MarcadoresDeNegacao =
    [
        new(@"\bnao (perca|perde|deixe|gaste|espere|aceite|fique|pague|caia|corra|trabalhe|sofra|dependa) (mais|de|o|a|os|as|seu|sua|seus|suas|tempo|dinheiro|cliente|clientes|lead|leads|prazo)\b"),
        new(@"\bnunca mais\b"), new(@"\bjamais\b"), new(@"\bchega de\b"),
        new(@"\bnao (e|fica|precisa ser|precisa de ser|vai ser|deveria ser|deveria significar|deveria exigir) (tao |mais |um |uma )?(dificil|complicad|caro|cara|pesadelo|bicho|risco|curso|sobrecarregar)"),
        new(@"\bnao precisa (mais )?(ser|de|entender|conviver|se preocupar|sofrer|perder|fechar o mes|trocar)\b"),
        new(@"\bnao (adianta|resolve|resolvem|corrige|corrigem|organiza|organizam|vai travar|perde nenhum|perde nada|e a mesma coisa)\b"),
        new(@"\bmais (simples|facil) do que parece\b"),
        new(@"\bnenhum[a]? [a-z ]{0,30}(resolve|vira|organiza|corrige|adianta)\b"),
        new(@"\bnem tod[oa]s?\b"),
        new(@"\bnao (significa|existe|existem|trabalham|entende|entendem|da conta|dao conta|precisa parar|precisam parar)\b"),
        new(@"\bnao deveria[m]? (procurar|comecar|significar|exigir|ser uma|depender|perder|virar)\b"),
        new(@"^(nao e|nao sao|nao existe)\b"),
        new(@"[.!?]\s*nao (quem|para|organizar|qualificar|criar|triar|com)\b"),
        new(@",\s*nao (triar|quem|para)\b"),
        new(@",\s*nao[.!]?$"),
    ];
    static readonly Regex[] MarcadoresDeProva =
    [
        new(@"\b(ha|desde|com) (mais de )?\d+ anos\b"),
        new(@"\b\d+ anos de (experiencia|mercado|estrada|historia|atuacao)\b"),
        new(@"\b(mais de|\+|ja sao|sao) ?\d[\d.]*( mil)? (empresas|clientes|alunos|pacientes|profissionais|escritorios|clinicas|restaurantes|lojas|negocios|usuarios|imobiliarias|advogados|barbearias|contadores|construtoras)\b"),
        new(@"\b(empresas|clientes|escritorios|negocios|restaurantes|imobiliarias|estabelecimentos|redes|marcas)\b[a-z ,]{0,40}\b(ja )?(usam|escolheram|trocaram|confiam|aprovaram|atendid[oa]s)\b"),
        new(@"\b(\d+ )?(estrelas|avaliacoes|depoimentos|cases de sucesso|premiad[oa]|lider (de mercado|no brasil|em))\b"),
        new(@"\b\d+ ?% (dos|das|de) (clientes|empresas|alunos|usuarios)\b"),
    ];
    static readonly Regex[] MarcadoresDeReuniao =
    [
        new(@"\bdemonstra"), new(@"\bdemo\b"), new(@"\breuniao\b"), new(@"\b(agende|agendar|agendamos|agendamento)\b"), new(@"\bsolicit"), new(@"\bapresentacao\b"),
        new(@"\bconsultoria\b"), new(@"\bdiagnos"), new(@"\borcamento\b"), new(@"\bcotar\b"), new(@"\bcotacao\b"), new(@"\breserv"), new(@"\banalise (gratuita|gratis|sem custo)\b"),
    ];

    public static string SemAcento(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    static string? Acha(string texto, Regex[] marcadores)
    {
        var t = Regex.Replace(SemAcento(texto), @"\s+", " ").Trim();
        return marcadores.FirstOrDefault(m => m.IsMatch(t))?.ToString();
    }

    /// <summary>O resultado do crivo para UMA peça. Só proibição FECHADA reprova; hipótese só avisa; a regra tem de valer na etapa.</summary>
    public static ResultadoDoCrivo Crivar(PecaDoModelo peca, Declaracao? declaracao, string etapa, SnapshotDoVault? snapshot)
    {
        if (snapshot is null) return new(null, null, [], []);
        var regras = new List<RegraNoCrivo>(); var desligadas = new List<(string, string)>();
        foreach (var chave in new[] { "nunca_negacao_headline", "nunca_prova_sub_mofu", "nunca_reuniao_cta_mofu" })
        {
            var r = snapshot.RegraDaChave(chave);
            if (r is null) { desligadas.Add((chave, "ausente")); continue; }
            if (r.Etapas.Count > 0 && !r.Etapas.Contains(etapa)) continue;
            string? motivo = chave switch
            {
                "nunca_negacao_headline" => declaracao?.FormaHeadline.Contains("negacao") == true ? "a declaração traz negacao na headline"
                    : Acha(peca.Headline, MarcadoresDeNegacao) is { } m1 ? $"headline em negação (\"{m1}\")" : null,
                "nunca_prova_sub_mofu" => declaracao?.PapelSub == "prova" ? "a declaração traz prova na sub"
                    : Acha(peca.Corpo, MarcadoresDeProva) is { } m2 ? $"prova na sub (\"{m2}\")" : null,
                _ => declaracao?.TipoCta == "agendar_solicitar" ? "a declaração traz agendar_solicitar no CTA"
                    : Acha(peca.Cta, MarcadoresDeReuniao) is { } m3 ? $"pedido de reunião no CTA (\"{m3}\")" : null,
            };
            var reprova = motivo is not null && r.Tipo == "proibicao" && r.Status == "fechada";
            regras.Add(new RegraNoCrivo(chave, r.Id, r.Status, !reprova, motivo));
        }
        return new(snapshot.Sha, regras.All(x => x.Ok), regras, desligadas);
    }

    /// <summary>A avaliação completa: a declaração (fora do vocabulário reprova) e o crivo. `Motivo` nulo = passou.</summary>
    public static (Declaracao? Declaracao, ResultadoDoCrivo Crivo, string? Motivo) Avaliar(PecaDoModelo peca, string etapa, SnapshotDoVault? snapshot)
    {
        var d = DeclaracaoDe(peca);
        var crivo = Crivar(peca, d, etapa, snapshot);
        var motivo = d is null ? "a peça veio sem a estrutura declarada no vocabulário" : crivo.MotivoDaReprovacao;
        return (d, crivo, motivo);
    }

    /// <summary>O `crivo` jsonb da peça (etiqueta.ts: crivoParaGravar).</summary>
    public static string CrivoParaGravar(ResultadoDoCrivo r, string fase) => JsonSerializer.Serialize(new
    {
        sha = r.Sha,
        regras = r.Regras.Select(x => new { chave = x.Chave, id = x.Id, status = x.Status, ok = x.Ok, motivo = x.Motivo }),
        desligadas = r.Desligadas.Select(x => new { chave = x.Chave, motivo = x.Motivo }),
        em = DateTime.UtcNow.ToString("o"), fase,
    }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    /// <summary>misto: vídeo vai a TOFU/MOFU/BOFU, estático a MOFU/BOFU; o resto na primeira etapa (etapas.ts: repartirMisto).</summary>
    public static List<ItemDoPedido> Normalizar(IEnumerable<ItemDoPedido> itens)
    {
        var saida = new List<ItemDoPedido>();
        foreach (var i in itens)
        {
            var tipo = i.Tipo == "video" ? "video" : "estatico"; var funil = i.Funil.ToLowerInvariant();
            var n = Math.Clamp(i.NPecas, 1, TetoPorItem);
            if (funil != "misto") { saida.Add(new(tipo, funil is "tofu" or "bofu" ? funil : "mofu", n)); continue; }
            var etapas = tipo == "video" ? new[] { "tofu", "mofu", "bofu" } : ["mofu", "bofu"];
            var porEtapa = n / etapas.Length; var resto = n - porEtapa * etapas.Length;
            for (var k = 0; k < etapas.Length; k++) { var q = porEtapa + (k == 0 ? resto : 0); if (q > 0) saida.Add(new(tipo, etapas[k], q)); }
        }
        /* o mesmo (tipo, funil) só uma vez: soma */
        return saida.GroupBy(x => (x.Tipo, x.Funil)).Select(g => new ItemDoPedido(g.Key.Tipo, g.Key.Funil, Math.Min(TetoPorItem, g.Sum(x => x.NPecas)))).ToList();
    }

    public static string EtapaDoFunil(string funil) => funil.ToUpperInvariant() switch { "TOFU" => "TOFU", "BOFU" => "BOFU", _ => "MOFU" };
}
