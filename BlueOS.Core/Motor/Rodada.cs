using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueOS.Core.Executor;
using BlueOS.Core.Modelos;

namespace BlueOS.Core.Motor;

/* ============================================================================
   A RODADA DO MOTOR, O QUE NELA É REGRA: a cadência, a entrada da árvore, o
   texto da trilha, o contrato de `numeros`, a leitura do registro e o dedup.
   ----------------------------------------------------------------------------
   Porta de lib/trafego/motor/recomendar.ts (cabeNaCadencia, textoDaTrilha, os
   `numeros` gravados) e registro.ts (o estado é a ÚLTIMA decisão; "idêntica"
   é (meta_id, acao) pendente). Puro: a API (MotorRodada, Automacao, o aceite
   do Executor) só lê e grava o banco em volta disto.
   ============================================================================ */

/// <summary>
/// Os números que a recomendação leva GRAVADOS (`trafego_motor_recomendacao.numeros`). É contrato: o aceite lê
/// `copia_diario_centavos` (sem ela a cópia sai com a verba do original), a memória lê `segurou_por_pendentes` (D5), e
/// o card lê o resto. Mudar um nome aqui quebra o teste de ida e volta, de propósito.
/// </summary>
public sealed record NumerosDaRecomendacao(
    [property: JsonPropertyName("nome")] string? Nome,
    [property: JsonPropertyName("ponderado")] double Ponderado,
    [property: JsonPropertyName("taxa")] double Taxa,
    [property: JsonPropertyName("custoCentavos")] long? CustoCentavos,
    [property: JsonPropertyName("multiplo")] double? Multiplo,
    [property: JsonPropertyName("dias_entrega")] int DiasEntrega,
    [property: JsonPropertyName("gasto")] double Gasto,
    [property: JsonPropertyName("avaliados")] int Avaliados,
    [property: JsonPropertyName("teto_centavos")] long? TetoCentavos,
    [property: JsonPropertyName("copia_diario_centavos")] long? CopiaDiarioCentavos,
    [property: JsonPropertyName("segurou_por_pendentes")] bool SegurouPorPendentes);

public static class Rodada
{
    /* ---- a cadência ------------------------------------------------------ */

    /// <summary>A ação vira recomendação? MANTER, AGUARDAR, SEM_BASE e SEM_HISTORICO não: não há o que aceitar.</summary>
    public static bool ViraRecomendacao(Acao a) => a is Acao.PAUSAR or Acao.ALERTAR or Acao.DUPLICAR or Acao.TROCAR_CRIATIVO;

    /// <summary>
    /// A ação cabe na cadência de hoje? (cabeNaCadencia.) PAUSAR e ALERTAR sempre: verba queimando sem MQL não espera.
    /// O resto só a partir do `nao_tocar` (7º dia) e, com memória, quando os dias CORRIDOS desde a última ação executada
    /// chegam ao `ciclo_leitura`; sem ação executada no conjunto, `(diasEntrega − 7) % 3 == 0`.
    /// </summary>
    public static bool CabeNaCadencia(Acao acao, int diasEntrega, ParametrosMotor p, MemoriaDoConjunto? memoria)
    {
        if (acao is Acao.PAUSAR or Acao.ALERTAR) return true;
        if (diasEntrega < p.NaoTocarDias) return false;
        if (memoria?.DiasDesdeUltimaAcao is { } desde) return desde >= p.CicloDias;
        return p.CicloDias > 0 && (diasEntrega - p.NaoTocarDias) % p.CicloDias == 0;
    }

    /* ---- a entrada da árvore --------------------------------------------- */

    /// <summary>
    /// A entrada da árvore a partir do conjunto como a carga o lê. A MESMA função na rodada e na reconferência do aceite.
    /// A contagem é a da situação de cada lead (a régua, com o campo que não veio como pendente); a memória é a que a
    /// carga calculou do registro (ou, na falta dela, a dos campos em dias, como a demonstração guarda).
    /// </summary>
    public static EntradaConjunto EntradaDe(ConjuntoEmVeiculacao cj, string slug, Formato formato, DateOnly hoje)
    {
        var leads = cj.Leads;
        var memoria = cj.Memoria ?? new MemoriaDoConjunto(
            cj.UltimaAcaoExecutada is { } ua ? hoje.DayNumber - ua.DayNumber : null,
            cj.PendenteDesde is { } pd ? hoje.DayNumber - pd.DayNumber : null,
            cj.CopiasVivas);
        return new EntradaConjunto(slug, cj.AdsetId, cj.Nome, formato, cj.AnunciosAtivos, cj.DiasEntrega, cj.GastoVidaCentavos, leads.Count,
            leads.Count(l => l.Situacao is Situacao.Mql or Situacao.NaoMql), leads.Count(l => l.Situacao == Situacao.Pendente),
            leads.Count(l => l.Situacao == Situacao.Mql && l.Tier == Tier.A), leads.Count(l => l.Situacao == Situacao.Mql && l.Tier == Tier.B),
            leads.Count(l => l.Situacao == Situacao.Mql && l.Tier == Tier.C),
            cj.Leitura, cj.CoberturaCompleta, memoria);
    }

    /* ---- o texto e os números gravados ------------------------------------ */

    /// <summary>O texto da recomendação sem modelo (textoDaTrilha do V1, como está): a primeira regra que disparou, as
    /// chaves e os AVISOS da árvore (cobertura, D5, PEND-014). O texto é a única coisa que guarda os avisos.</summary>
    public static string TextoDaTrilha(Decisao d, string? nome)
    {
        var alvo = !string.IsNullOrEmpty(nome) ? $"\"{nome}\"" : "este conjunto";
        var disparou = d.Trilha.FirstOrDefault(t => t.Resultado == ResultadoDaRegra.Disparou);
        var cabeca = disparou is not null ? $"{alvo}: {disparou.Texto}" : $"{alvo}: nenhuma regra disparou — {d.Acao.ToString().ToLowerInvariant()}.";
        var porRegra = d.Chaves.Count > 0 ? $" Regra: {string.Join(", ", d.Chaves)}." : "";
        var avisos = d.Avisos.Count > 0 ? " " + string.Join(" ", d.Avisos.Select(a => a.Texto)) : "";
        return $"{cabeca}{porRegra}{avisos}";
    }

    public static NumerosDaRecomendacao Numeros(Decisao d, EntradaConjunto e, string? nome, long? tetoCentavos) =>
        new(nome, d.Numeros.Ponderado, d.Numeros.Taxa, d.Numeros.CustoCentavos, d.Numeros.Multiplo, e.DiasEntrega, e.GastoCentavos / 100.0,
            e.Avaliados, tetoCentavos, d.CopiaDiarioCentavos, d.SegurouPorPendentes);

    public static string NumerosJson(NumerosDaRecomendacao n) => JsonSerializer.Serialize(n);

    /// <summary>As refs dos avisos, sem repetir: é o que vai em `pendencias`.</summary>
    public static string[] Pendencias(Decisao d) => d.Avisos.Select(a => a.Ref).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();

    /* ---- o dedup ---------------------------------------------------------- */

    /// <summary>"Idêntica" é (meta_id, ação) PENDENTE (registro.ts): os números mudam todo dia sem a recomendação mudar, e
    /// a decidida não bloqueia uma nova.</summary>
    public static string ChaveDeDedup(string metaId, string acao) => $"{metaId}::{acao}";

    public static bool Repetida(IReadOnlySet<string> pendentes, string metaId, Acao acao) => pendentes.Contains(ChaveDeDedup(metaId, acao.ToString()));

    /* ---- a leitura do registro -------------------------------------------- */

    /// <summary>A ação pelo NOME exato do enum. "0" não vira PAUSAR, e um nome que esta versão não conhece dá null.</summary>
    public static Acao? AcaoDoNome(string? nome) =>
        nome is not null && Enum.GetNames<Acao>().Contains(nome, StringComparer.Ordinal) ? Enum.Parse<Acao>(nome) : null;

    /// <summary>O estado pela última decisão: sem decisão é pendente; decisão que esta versão não conhece é Desconhecido
    /// (aparece, sem aceite), nunca pendente.</summary>
    public static EstadoDaRecomendacao EstadoDaDecisao(string? decisao) => decisao switch
    {
        null => EstadoDaRecomendacao.Pendente,
        "aceita" => EstadoDaRecomendacao.Aceita,
        "negada" => EstadoDaRecomendacao.Negada,
        "aplicada" => EstadoDaRecomendacao.Aplicada,
        "falhou" => EstadoDaRecomendacao.Falhou,
        "incerto" => EstadoDaRecomendacao.Incerto,
        _ => EstadoDaRecomendacao.Desconhecido,
    };

    /// <summary>O registro do motor é append-only: o estado é a ÚLTIMA decisão (lib/trafego/motor/registro.ts). Lê as
    /// linhas de <c>Automacao.RecsSql</c> com o contrato de `numeros` (<see cref="NumerosDaRecomendacao"/>).</summary>
    public static List<Recomendacao> ParseRecs(JsonElement arr, string slug)
    {
        var saida = new List<Recomendacao>();
        if (arr.ValueKind != JsonValueKind.Array) return saida;
        foreach (var r in arr.EnumerateArray())
        {
            var acaoLida = AcaoDoNome(Str(r, "acao"));
            var acao = acaoLida ?? Acao.ALERTAR;
            double pond = 0, taxa = 0; long? custo = null, copia = null; double? mult = null; string? nome = null; var segurou = false;
            if (r.TryGetProperty("numeros", out var n) && n.ValueKind == JsonValueKind.Object)
            {
                if (n.TryGetProperty("ponderado", out var p) && p.ValueKind == JsonValueKind.Number) pond = p.GetDouble();
                if (n.TryGetProperty("taxa", out var t) && t.ValueKind == JsonValueKind.Number) taxa = t.GetDouble();
                /* o V1 grava custoCentavos como número livre (às vezes com fração): arredonda em vez de quebrar */
                if (n.TryGetProperty("custoCentavos", out var cc) && cc.ValueKind == JsonValueKind.Number) custo = Inteiro(cc);
                if (n.TryGetProperty("multiplo", out var m) && m.ValueKind == JsonValueKind.Number) mult = m.GetDouble();
                if (n.TryGetProperty("nome", out var nm) && nm.ValueKind == JsonValueKind.String) nome = nm.GetString();
                if (n.TryGetProperty("copia_diario_centavos", out var cd) && cd.ValueKind == JsonValueKind.Number && cd.GetDouble() > 0) copia = Inteiro(cd);
                segurou = n.TryGetProperty("segurou_por_pendentes", out var sp) && sp.ValueKind == JsonValueKind.True;
            }
            var chaves = r.TryGetProperty("chaves", out var ch) && ch.ValueKind == JsonValueKind.Array ? ch.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();
            var estado = EstadoDaDecisao(Str(r, "decisao"));
            if (acaoLida is null) estado = EstadoDaRecomendacao.Desconhecido;
            saida.Add(new Recomendacao
            {
                Id = Str(r, "id") ?? "", ClienteSlug = slug, CriadoEm = Data(r, "criado_em") ?? DateTime.MinValue, Nivel = Str(r, "nivel") ?? "conjunto",
                MetaId = Str(r, "meta_id") ?? "", Nome = nome ?? Str(r, "conjunto_nome") ?? Str(r, "meta_id") ?? "", Acao = acao,
                Decisao = new Decisao(acao, chaves, [], [], new NumerosDaDecisao(pond, taxa, custo, mult), acao == Acao.DUPLICAR, copia, segurou),
                Texto = Str(r, "texto") ?? "", TextoFonte = Str(r, "texto_fonte") ?? "trilha", VaultSha = Str(r, "vault_sha") ?? "", ContagemMql = Str(r, "contagem_mql") ?? "regua",
                Estado = estado,
                DecididaPor = Str(r, "por"), DecididaEm = Data(r, "em"), Motivo = Str(r, "motivo"),
                ResultadoMeta = r.TryGetProperty("resultado_meta", out var rm) && rm.ValueKind != JsonValueKind.Null ? rm.GetRawText() : null,
            });
        }
        return saida;
    }

    /* ---- o que a rodada devolve sobre cada conjunto lido ------------------- */

    /// <summary>Um conjunto avaliado na resposta de /motor/rodar: a decisão inteira, com a trilha, para a tela explicar o
    /// silêncio (MANTER, AGUARDAR) e o porquê de cada recomendação.</summary>
    public static object AvaliadoNaResposta(string adsetId, string nome, Decisao d) => new
    {
        adset_id = adsetId, nome, acao = d.Acao.ToString(), chaves = d.Chaves,
        trilha = d.Trilha.Select(t => new { chave = t.Chave, resultado = t.Resultado.ToString(), texto = t.Texto }).ToList(),
        avisos = d.Avisos.Select(a => new { @ref = a.Ref, texto = a.Texto }).ToList(),
        numeros = new { ponderado = d.Numeros.Ponderado, taxa = d.Numeros.Taxa, custoCentavos = d.Numeros.CustoCentavos, multiplo = d.Numeros.Multiplo },
        tem_copia = d.TemCopia, copia_diario_centavos = d.CopiaDiarioCentavos, segurou_por_pendentes = d.SegurouPorPendentes,
    };

    /// <summary>Os `avaliados` da resposta de /motor/rodar de volta para <see cref="RodadaDoMotor.Avaliados"/>.</summary>
    public static List<(string AdsetId, string Nome, Decisao Decisao)> AvaliadosDaResposta(JsonElement resposta)
    {
        var saida = new List<(string, string, Decisao)>();
        if (resposta.ValueKind != JsonValueKind.Object || !resposta.TryGetProperty("avaliados", out var arr) || arr.ValueKind != JsonValueKind.Array) return saida;
        foreach (var a in arr.EnumerateArray())
        {
            var acao = AcaoDoNome(Str(a, "acao")) ?? Acao.AGUARDAR;
            var chaves = Lista(a, "chaves").Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToList();
            var trilha = Lista(a, "trilha").Select(t => new PassoDaTrilha(Str(t, "chave") ?? "", ResultadoDe(Str(t, "resultado")), Str(t, "texto") ?? "")).ToList();
            var avisos = Lista(a, "avisos").Select(x => new Aviso(Str(x, "ref") ?? "", Str(x, "texto") ?? "")).ToList();
            double pond = 0, taxa = 0; long? custo = null; double? mult = null;
            if (a.TryGetProperty("numeros", out var n) && n.ValueKind == JsonValueKind.Object)
            {
                if (n.TryGetProperty("ponderado", out var p) && p.ValueKind == JsonValueKind.Number) pond = p.GetDouble();
                if (n.TryGetProperty("taxa", out var t) && t.ValueKind == JsonValueKind.Number) taxa = t.GetDouble();
                if (n.TryGetProperty("custoCentavos", out var cc) && cc.ValueKind == JsonValueKind.Number) custo = Inteiro(cc);
                if (n.TryGetProperty("multiplo", out var m) && m.ValueKind == JsonValueKind.Number) mult = m.GetDouble();
            }
            long? copia = a.TryGetProperty("copia_diario_centavos", out var cd) && cd.ValueKind == JsonValueKind.Number ? Inteiro(cd) : null;
            var temCopia = a.TryGetProperty("tem_copia", out var tc) && tc.ValueKind == JsonValueKind.True;
            var segurou = a.TryGetProperty("segurou_por_pendentes", out var sp) && sp.ValueKind == JsonValueKind.True;
            var id = Str(a, "adset_id") ?? "";
            saida.Add((id, Str(a, "nome") ?? id, new Decisao(acao, chaves, trilha, avisos, new NumerosDaDecisao(pond, taxa, custo, mult), temCopia, copia, segurou)));
        }
        return saida;
    }

    /// <summary>O resultado do passo pelo nome do enum ou pela grafia do V1 (disparou, passou, nao_se_aplica, info).</summary>
    static ResultadoDaRegra ResultadoDe(string? s) => s switch
    {
        "Disparou" or "disparou" => ResultadoDaRegra.Disparou,
        "Passou" or "passou" => ResultadoDaRegra.Passou,
        "NaoSeAplica" or "nao_se_aplica" => ResultadoDaRegra.NaoSeAplica,
        _ => ResultadoDaRegra.Info,
    };

    /* ---- leitura de JSON --------------------------------------------------- */

    static long Inteiro(JsonElement v) => v.TryGetInt64(out var i) ? i : (long)Math.Round(v.GetDouble(), MidpointRounding.AwayFromZero);
    static IEnumerable<JsonElement> Lista(JsonElement o, string n) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    static string? Str(JsonElement o, string n) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.GetRawText() : null;

    /// <summary>Um instante do banco (timestamptz) no relógio da CONTA (BRT), não no do host.</summary>
    static DateTime? Data(JsonElement o, string n) =>
        DateTime.TryParse(Str(o, n), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? Fuso.NaConta(d) : null;
}
