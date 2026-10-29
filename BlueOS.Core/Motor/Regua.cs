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
   ============================================================================ */
public static class Regua
{
    public static readonly string[] MarcasDeVagaInequivocas =
        ["pretensao salarial", "expectativa de ganhos", "curriculo", "por que voce acredita que seria a pessoa certa"];
    public static readonly string[] MarcasDeVagaAmbiguas = ["a vaga", "home office", "comissoes"];

    /// <summary>Sem acento, sem caixa, espaços e `_` iguais (regua/normalizar.ts).</summary>
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

    static string? CampoDeLeadDeVaga(IReadOnlyDictionary<string, List<string>> respostas)
    {
        var ambiguas = new Dictionary<string, string>();
        foreach (var campo in respostas.Keys)
        {
            var n = Normalizar(campo);
            if (MarcasDeVagaInequivocas.Any(m => n.Contains(m))) return campo;
            var marca = MarcasDeVagaAmbiguas.FirstOrDefault(m => n.Contains(m));
            if (marca is not null && !ambiguas.ContainsKey(marca)) ambiguas[marca] = campo;
        }
        return ambiguas.Count >= 2 ? ambiguas.Values.First() : null;
    }

    enum Classe { Forte, Media, Elim, NaoMapeada }

    static Classe ClassificarValor(string valor, PerguntaDaRegua q)
    {
        var alvo = Normalizar(valor);
        if (q.Elim.Any(v => Normalizar(v) == alvo)) return Classe.Elim;
        if (q.Forte.Any(v => Normalizar(v) == alvo)) return Classe.Forte;
        if (q.Media.Any(v => Normalizar(v) == alvo)) return Classe.Media;
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
        new[] { q.Nome }.Concat(q.Chaves).Select(Normalizar).Where(c => c != "").Distinct().ToList();

    /// <summary>As respostas no formato {pergunta, resposta} viram campo e valores crus.</summary>
    public static Dictionary<string, List<string>> RespostasDoLead(IEnumerable<(string Pergunta, string Resposta)> lista)
    {
        var fora = new Dictionary<string, List<string>>();
        foreach (var (pergunta, resposta) in lista)
        {
            if (string.IsNullOrEmpty(pergunta)) continue;
            if (!fora.TryGetValue(pergunta, out var l)) fora[pergunta] = l = new();
            l.Add(resposta ?? "");
        }
        return fora;
    }

    public static ResultadoDaAvaliacao AvaliarLead(IReadOnlyDictionary<string, List<string>> respostas, ReguaV2 regua, bool faltanteDerruba = true)
    {
        var dadas = new Dictionary<string, List<string>>();
        foreach (var (campo, valores) in respostas)
        {
            var chave = Normalizar(campo);
            if (chave == "") continue;
            if (!dadas.TryGetValue(chave, out var l)) dadas[chave] = l = new();
            l.AddRange(valores.Where(v => !string.IsNullOrWhiteSpace(v)));
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

    /// <summary>A contagem oficial de um grupo de leads (contarPelaRegua).</summary>
    public static ContagemPelaRegua Contar(IEnumerable<IReadOnlyDictionary<string, List<string>>> leads, ReguaV2 regua)
    {
        int a = 0, b = 0, c = 0, avaliados = 0, pendentes = 0;
        foreach (var l in leads)
        {
            var r = AvaliarLead(l, regua);
            if (r.Situacao == Situacao.Pendente) { pendentes++; continue; }
            avaliados++;
            if (r.Tier == Tier.A) a++; else if (r.Tier == Tier.B) b++; else if (r.Tier == Tier.C) c++;
        }
        return new ContagemPelaRegua(a, b, c, a + b + c, avaliados, pendentes);
    }

    /// <summary>O que impede o rascunho da régua de ser salvo (setup/cadastro.ts).</summary>
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
                foreach (var r in lista.Select(Normalizar).Where(r => r != ""))
                {
                    if (listaDe.TryGetValue(r, out var onde) && onde != rotulo) problemas.Add($"a resposta \"{r}\" do campo \"{nome}\" está em duas listas ({onde} e {rotulo})");
                    listaDe[r] = rotulo;
                }
            var chave = Normalizar(nome);
            if (!vistos.Add(chave)) problemas.Add($"o campo \"{nome}\" aparece duas vezes na régua");
        }
        return problemas;
    }
}
