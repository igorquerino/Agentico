using System.Globalization;
using System.Text;
using BlueOS.Core.Modelos;

namespace BlueOS.Core.Motor;

/* ============================================================================
   AVALIAR UM LEAD PELA RÉGUA: função pura, mesmo resultado toda vez.
   ----------------------------------------------------------------------------
   Porta de lib/trafego/motor/regua/avaliar.ts. A ordem do veredito:
     0. lead de vaga não é MQL (nome de campo de formulário de vaga);
     1. qualquer eliminatória: não é MQL;
     2. pergunta sem resposta: não é MQL (faltante_derruba, PEND-013);
     3. qualquer resposta não mapeada: PENDENTE (o gestor classifica);
     4. MQL, com o tier pelas fortes: 2 ou mais = A, 1 = B, 0 = C.
   Na CONTAGEM (Contar, a do motor e a do Ongoing), o lead derrubado só por
   campo que NEM VEIO no lead conta como pendente (contaComoPendente do V1).
   ============================================================================ */
public static class Regua
{
    public static readonly string[] MarcasDeVagaInequivocas =
        ["pretensao salarial", "expectativa de ganhos", "curriculo", "por que voce acredita que seria a pessoa certa"];
    public static readonly string[] MarcasDeVagaAmbiguas = ["a vaga", "home office", "comissoes"];

    /// <summary>Sem acento, sem caixa, espaços e `_` iguais. NÃO é a normalização da régua: serve a tela (casar a régua
    /// com as opções do formulário), o slug e a sugestão de nicho. A régua casa por <see cref="NormalizarResposta"/>.</summary>
    public static string Normalizar(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        var t = sb.ToString().ToLowerInvariant().Replace('_', ' ');
        return string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>O `trim()` do JavaScript: espaço Unicode, tab, quebra de linha, NBSP e o BOM (U+FEFF) das pontas.</summary>
    static string TrimJs(string s)
    {
        static bool Branco(char c) => char.IsWhiteSpace(c) || c == '﻿';
        int i = 0, f = s.Length;
        while (i < f && Branco(s[i])) i++;
        while (f > i && Branco(s[f - 1])) f--;
        return s[i..f];
    }

    /// <summary>
    /// A normalização da RÉGUA, literal de regua/normalizar.ts: minúsculas, trim, `__` → `_` e os `_` das pontas fora,
    /// repetido até não mudar. O ACENTO FICA ("gestão" e "gestao" estão nas listas de propósito) e o `_` do meio não
    /// vira espaço. É o que casa resposta com lista e campo do lead com nome de pergunta.
    /// </summary>
    public static string NormalizarResposta(string? texto)
    {
        var s = texto ?? "";
        for (;;)
        {
            var proximo = TrimJs(s).ToLowerInvariant().Replace("__", "_").Trim('_');
            if (proximo == s) return s;
            s = proximo;
        }
    }

    /// <summary>O `semAcento` do detector de lead de vaga (avaliar.ts): sem acento, sem caixa, `_` como espaço.</summary>
    public static string SemAcento(string? s)
    {
        var d = (s ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (ch is < '̀' or > 'ͯ') sb.Append(ch);
        return sb.ToString().ToLowerInvariant().Replace('_', ' ');
    }

    static string? CampoDeLeadDeVaga(IReadOnlyDictionary<string, List<string>> respostas)
    {
        var ambiguas = new Dictionary<string, string>();
        foreach (var campo in respostas.Keys)
        {
            var n = SemAcento(campo);
            if (MarcasDeVagaInequivocas.Any(m => n.Contains(m))) return campo;
            var marca = MarcasDeVagaAmbiguas.FirstOrDefault(m => n.Contains(m));
            if (marca is not null && !ambiguas.ContainsKey(marca)) ambiguas[marca] = campo;
        }
        return ambiguas.Count >= 2 ? ambiguas.Values.First() : null;
    }

    enum Classe { Forte, Media, Elim, NaoMapeada }

    static Classe ClassificarValor(string valor, PerguntaDaRegua q)
    {
        var alvo = NormalizarResposta(valor);
        if (q.Elim.Any(v => NormalizarResposta(v) == alvo)) return Classe.Elim;
        if (q.Forte.Any(v => NormalizarResposta(v) == alvo)) return Classe.Forte;
        if (q.Media.Any(v => NormalizarResposta(v) == alvo)) return Classe.Media;
        return Classe.NaoMapeada;
    }

    static Classe ClassificarCampo(List<string> valores, PerguntaDaRegua q)
    {
        var classes = valores.Select(v => ClassificarValor(v, q)).ToHashSet();
        if (classes.Contains(Classe.Elim)) return Classe.Elim;
        if (classes.Contains(Classe.Forte)) return Classe.Forte;
        if (classes.Contains(Classe.Media)) return Classe.Media;
        return Classe.NaoMapeada;
    }

    public static IReadOnlyList<string> CamposDaPergunta(PerguntaDaRegua q) =>
        new[] { q.Nome }.Concat(q.Chaves ?? []).Select(NormalizarResposta).Where(c => c != "").Distinct().ToList();

    /// <summary>As respostas no formato {pergunta, resposta} viram campo e valores crus.</summary>
    public static Dictionary<string, List<string>> RespostasDoLead(IEnumerable<(string Pergunta, string Resposta)> lista) =>
        RespostasDoLead(lista.Select(x => new Resposta(x.Pergunta, x.Resposta)));

    /// <summary>
    /// As respostas do lead viram campo e valores crus (respostasDoLead do V1). O `Texto` (#5004) é o texto da opção que a
    /// carga grava ao lado do CÓDIGO quando o formulário tem chave por opção: ele entra como mais um valor do mesmo campo,
    /// e a régua casa pelo código OU pelo texto (a eliminatória continua ganhando).
    /// </summary>
    public static Dictionary<string, List<string>> RespostasDoLead(IEnumerable<Resposta> lista)
    {
        var fora = new Dictionary<string, List<string>>();
        foreach (var r in lista)
        {
            if (string.IsNullOrEmpty(r.Pergunta)) continue;
            if (!fora.TryGetValue(r.Pergunta, out var l)) fora[r.Pergunta] = l = new();
            var valor = r.Valor ?? "";
            l.Add(valor);
            var texto = r.Texto ?? "";
            if (TrimJs(valor) != "" && TrimJs(texto) != "" && NormalizarResposta(texto) != NormalizarResposta(valor)) l.Add(texto);
        }
        return fora;
    }

    /// <summary>As perguntas da régua cujo CAMPO não veio no lead, nem em branco (camposQueNaoVieram). Com os apelidos:
    /// o campo veio se QUALQUER nome da pergunta veio.</summary>
    public static IReadOnlyList<string> CamposQueNaoVieram(IReadOnlyDictionary<string, List<string>> respostas, ReguaV2 regua)
    {
        var vieram = respostas.Keys.Select(NormalizarResposta).Where(c => c != "").ToHashSet();
        return regua.Perguntas.Where(q => !CamposDaPergunta(q).Any(vieram.Contains)).Select(q => q.Nome).ToList();
    }

    /// <summary>
    /// O lead entra como PENDENTE na contagem do motor? (contaComoPendente do V1.) Sim quando a régua não o classificou, ou
    /// quando ele só caiu por campo que NEM VEIO: nome de campo errado na régua, lead de outro formulário ou lead antigo.
    /// Contá-lo como "não MQL" pausaria o conjunto por um erro de cadastro. A resposta EM BRANCO continua "não MQL".
    /// </summary>
    public static bool ContaComoPendente(IReadOnlyDictionary<string, List<string>> respostas, ResultadoDaAvaliacao r, ReguaV2 regua)
    {
        var naoVieram = CamposQueNaoVieram(respostas, regua).ToHashSet();
        var soPorCampoQueNaoVeio = r.Situacao == Situacao.NaoMql && r.Eliminatorias.Count == 0 && r.Faltantes.All(naoVieram.Contains);
        return r.Situacao == Situacao.Pendente || soPorCampoQueNaoVeio;
    }

    /// <summary>A situação do lead como a CONTAGEM a lê (a central do Ongoing e a entrada da árvore): a da régua, salvo o
    /// derrubado só por campo que não veio, que é pendente.</summary>
    public static Situacao SituacaoNaContagem(IReadOnlyDictionary<string, List<string>> respostas, ResultadoDaAvaliacao r, ReguaV2 regua) =>
        ContaComoPendente(respostas, r, regua) ? Situacao.Pendente : r.Situacao;

    public static ResultadoDaAvaliacao AvaliarLead(IReadOnlyDictionary<string, List<string>> respostas, ReguaV2 regua, bool faltanteDerruba = true)
    {
        var dadas = new Dictionary<string, List<string>>();
        foreach (var (campo, valores) in respostas)
        {
            var chave = NormalizarResposta(campo);
            if (chave == "") continue;
            if (!dadas.TryGetValue(chave, out var l)) dadas[chave] = l = new();
            l.AddRange(valores.Select(v => v ?? "").Where(v => TrimJs(v) != ""));
        }

        var eliminatorias = new List<string>();
        var faltantes = new List<string>();
        var naoMapeadas = new List<RespostaNaoMapeada>();
        var partes = new List<string>();
        var fortes = 0;

        var campoDeVaga = CampoDeLeadDeVaga(respostas);
        if (campoDeVaga is not null)
            return new(Situacao.NaoMql, false, null, 0, [campoDeVaga], [], [], $"não é MQL: lead de vaga (o formulário tem o campo \"{campoDeVaga}\")");

        foreach (var q in regua.Perguntas)
        {
            var valores = CamposDaPergunta(q).Select(c => dadas.GetValueOrDefault(c) ?? new()).FirstOrDefault(v => v.Count > 0) ?? new();
            if (valores.Count == 0) { faltantes.Add(q.Nome); partes.Add($"{q.Nome} sem resposta"); continue; }
            var classe = ClassificarCampo(valores, q);
            if (classe == Classe.Elim) { eliminatorias.Add(q.Nome); partes.Add($"{q.Nome} eliminatória"); continue; }
            if (classe == Classe.Forte) { fortes++; partes.Add($"{q.Nome} forte"); continue; }
            if (classe == Classe.Media) { partes.Add($"{q.Nome} média"); continue; }
            var junto = string.Join(", ", valores);
            naoMapeadas.Add(new RespostaNaoMapeada(q.Nome, junto.Length > 60 ? junto[..60] + "…" : junto));
            partes.Add($"{q.Nome} não mapeada");
        }

        var motivo = string.Join(" · ", partes);
        if (eliminatorias.Count > 0)
            return new(Situacao.NaoMql, false, null, fortes, eliminatorias, faltantes, naoMapeadas, $"não é MQL: {motivo}");
        if (faltanteDerruba && faltantes.Count > 0)
            return new(Situacao.NaoMql, false, null, fortes, eliminatorias, faltantes, naoMapeadas, $"não é MQL (sem resposta): {motivo}");
        if (naoMapeadas.Count > 0)
            return new(Situacao.Pendente, false, null, fortes, eliminatorias, faltantes, naoMapeadas, $"pendente: {motivo}");
        var tier = fortes >= 2 ? Tier.A : fortes == 1 ? Tier.B : Tier.C;
        return new(Situacao.Mql, true, tier, fortes, eliminatorias, faltantes, naoMapeadas, $"MQL {tier}: {motivo}");
    }

    /// <summary>A contagem oficial de um grupo de leads (contarPelaRegua): MQL e não MQL são avaliados; a resposta fora das
    /// listas e o lead derrubado só por campo que não veio são pendentes.</summary>
    public static ContagemPelaRegua Contar(IEnumerable<IReadOnlyDictionary<string, List<string>>> leads, ReguaV2 regua)
    {
        int a = 0, b = 0, c = 0, avaliados = 0, pendentes = 0;
        foreach (var l in leads)
        {
            var r = AvaliarLead(l, regua);
            if (ContaComoPendente(l, r, regua)) { pendentes++; continue; }
            avaliados++;
            if (r.Tier == Tier.A) a++; else if (r.Tier == Tier.B) b++; else if (r.Tier == Tier.C) c++;
        }
        return new ContagemPelaRegua(a, b, c, a + b + c, avaliados, pendentes);
    }

    /// <summary>O que impede o rascunho da régua de ser salvo (setup/cadastro.ts, problemasDoRascunhoDaRegua), pela mesma
    /// normalização da régua. Os apelidos (`Chaves`) são nomes de campo também: um campo é de uma pergunta só.</summary>
    public static List<string> ProblemasDoRascunho(IEnumerable<PerguntaDaRegua> perguntas)
    {
        var problemas = new List<string>();
        var vistos = new HashSet<string>();
        var i = 0;
        foreach (var p in perguntas)
        {
            i++;
            var nome = (p.Nome ?? "").Trim();
            var temResposta = p.Forte.Concat(p.Media).Concat(p.Elim).Any(r => !string.IsNullOrWhiteSpace(r));
            if (nome == "" && !temResposta) continue;
            if (nome == "") { problemas.Add($"a pergunta {i} tem respostas mas não tem o nome do campo"); continue; }
            if (!temResposta) problemas.Add($"o campo \"{nome}\" não tem nenhuma resposta classificada");
            var listaDe = new Dictionary<string, string>();
            foreach (var (lista, rotulo) in new[] { (p.Forte, "forte"), (p.Media, "média"), (p.Elim, "eliminatória") })
                foreach (var v in lista)
                {
                    var r = NormalizarResposta(v);
                    if (r == "") continue;
                    if (listaDe.TryGetValue(r, out var onde) && onde != rotulo) problemas.Add($"a resposta \"{(v ?? "").Trim()}\" do campo \"{nome}\" está em duas listas ({onde} e {rotulo})");
                    else listaDe[r] = rotulo;
                }
            var chave = NormalizarResposta(nome);
            if (!vistos.Add(chave)) problemas.Add($"o campo \"{nome}\" aparece duas vezes na régua");
            foreach (var c in p.Chaves ?? [])
            {
                var n = NormalizarResposta(c);
                if (n == "" || n == chave) continue;
                if (!vistos.Add(n)) problemas.Add($"o campo \"{(c ?? "").Trim()}\" aparece duas vezes na régua (como nome ou outro nome de outra pergunta)");
            }
        }
        return problemas;
    }
}
