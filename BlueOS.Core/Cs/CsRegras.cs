using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueOS.Core.Cs;

/* ============================================================================
   AS REGRAS PURAS DO AGENTE DE CS — porte de lib/cs/* e lib/os/contrato.ts do V1.
   ----------------------------------------------------------------------------
   Nada aqui toca banco nem rede. O que decide a NOTA, a FAIXA, o alcance por
   squad, o dinheiro visto de um squad, o grupo frio, a situação do prazo e a
   validação das decisões humanas mora neste arquivo, uma vez só, para a lista,
   o dossiê, o alerta de churn e as escritas lerem a MESMA régua. No V1 cada
   tela que reduziu o histórico "na mão" chegou a um número diferente para o
   mesmo cliente; aqui a conta é uma e é testada (BlueOS.Tests/CsRegrasTests).
   ============================================================================ */
public static class CsRegras
{
    /* ── NOTA E FAIXA (lib/cs/nota.ts) ──────────────────────────────────────────
       Base: valor_gerado (-100..+100) mapeado para 0..100. O risco de churn pesa
       mais que o valor entregue (60% churn / 40% valor). Faixas (régua de 09/09/2026):
       0-49 crítico · 50-65 atenção · 66-79 saudável · 80-100 excelente. */
    public static int Nota(int? valorGerado, int? riscoChurn, string? churnLabel)
    {
        if (valorGerado is null) return 0;
        var basis = Math.Clamp((int)Math.Round((valorGerado.Value + 100) / 2.0, MidpointRounding.AwayFromZero), 0, 100);
        if (riscoChurn is null || string.Equals(churnLabel ?? "", "sem_dados", StringComparison.OrdinalIgnoreCase)) return basis;
        var retencao = 100 - Math.Clamp(riscoChurn.Value, 0, 100);
        return Math.Clamp((int)Math.Round(basis * 0.4 + retencao * 0.6, MidpointRounding.AwayFromZero), 0, 100);
    }

    public static string FaixaDaNota(int? nota) => nota switch
    {
        null => "sem_analise",
        >= 80 => "excelente",
        >= 66 => "saudavel",
        >= 50 => "atencao",
        _ => "critico",
    };

    public static readonly IReadOnlyDictionary<string, string> RotuloDaFaixa = new Dictionary<string, string>
    {
        ["excelente"] = "Excelente", ["saudavel"] = "Saudável", ["atencao"] = "Atenção", ["critico"] = "Crítico",
        ["sem_analise"] = "Sem análise", ["sem_base"] = "Sem base",
    };

    /// <summary>A cor da faixa, pelo nome que a tela e o chat usam ("vermelho", "azul"...). Cinza nas duas faixas sem nota.</summary>
    public static string ApelidoDaFaixa(string faixa) => faixa switch
    {
        "excelente" => "azul", "saudavel" => "verde", "atencao" => "amarelo", "critico" => "vermelho", _ => "cinza",
    };

    /* ── OS PILARES (lib/cs/pilares.ts) ─────────────────────────────────────────
       `sub_scores` chega como jsonb (ou como string JSON, às vezes com uma chave
       sobrando no fim). Uma análise com TODO pilar que conta sem fonte não vira
       nota: fica "sem_base", cinza e fora de toda média. Decide pelos pilares,
       nunca pelo rótulo `risco_churn_label`. */
    public static readonly string[] Pilares = ["performance", "relacionamento", "entrega", "engajamento", "comercial"];

    public static JsonElement? LerSubScores(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return null;
        var texto = bruto.Trim();
        for (var cortes = 0; cortes <= 2; cortes++)
        {
            try
            {
                using var doc = JsonDocument.Parse(texto);
                var raiz = doc.RootElement.Clone();
                if (raiz.ValueKind == JsonValueKind.String) return LerSubScores(raiz.GetString());
                return raiz.ValueKind == JsonValueKind.Object ? raiz : null;
            }
            catch (JsonException) { }
            if (!texto.EndsWith('}')) break;
            texto = texto[..^1].TrimEnd();
        }
        return null;
    }

    public static bool SemFonteNenhuma(JsonElement? sub)
    {
        if (sub is not { ValueKind: JsonValueKind.Object } raiz) return true;
        foreach (var k in Pilares)
        {
            if (!raiz.TryGetProperty(k, out var p) || p.ValueKind != JsonValueKind.Object) continue;
            if (p.TryGetProperty("desligado", out var d) && d.ValueKind == JsonValueKind.True) continue;
            var semDados = p.TryGetProperty("sem_dados", out var sd) && sd.ValueKind == JsonValueKind.True;
            if (!semDados && p.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number) return false;
        }
        return true;
    }

    /// <summary>A nota de UMA linha de análise. Nula (e "sem_base") quando não há valor_gerado ou nenhum pilar tem fonte.</summary>
    public static (int? Nota, string Faixa) NotaDaAnalise(int? valorGerado, int? riscoChurn, string? churnLabel, string? subScoresJson)
    {
        if (valorGerado is null || SemFonteNenhuma(LerSubScores(subScoresJson))) return (null, "sem_base");
        var nota = Nota(valorGerado, riscoChurn, churnLabel);
        return (nota, FaixaDaNota(nota));
    }

    /* ── CARGOS E ALCANCE (lib/rotina/hierarquia.ts, lib/os/cargos-admin.ts) ─── */
    static readonly Dictionary<string, int> TierPorCargo = new()
    {
        ["super_admin"] = 1, ["ceo"] = 1, ["diretor"] = 1, ["gerente"] = 1, ["engenheiro_ia"] = 1,
        ["coordenador"] = 2, ["coordenador_departamento"] = 2,
        ["head"] = 3, ["head_departamento"] = 3,
        ["gestor"] = 4, ["operador"] = 4, ["designer"] = 4, ["editor"] = 4, ["consultor_comercial"] = 4,
    };
    public const int TierSemHierarquia = 99;
    public static readonly HashSet<string> CargosQueAdministram = ["super_admin", "ceo", "diretor", "gerente", "coordenador_departamento", "head_departamento", "engenheiro_ia", "coordenador"];

    /// <summary>O MELHOR cargo da pessoa (menor número): quem acumula cargos entra pelo mais alto.</summary>
    public static int Tier(IEnumerable<string>? cargos)
    {
        var melhor = TierSemHierarquia;
        foreach (var c in cargos ?? [])
            if (TierPorCargo.TryGetValue((c ?? "").Trim().ToLowerInvariant(), out var t) && t < melhor) melhor = t;
        return melhor;
    }
    /// <summary>"Coordenador e acima": tier 1 ou 2.</summary>
    public static bool PodeMudarSemPerformance(IEnumerable<string>? cargos) => Tier(cargos) <= 2;
    public static bool PodeCriarCliente(IEnumerable<string>? cargos) => Tier(cargos) <= 2;
    public static bool PodeAdministrar(IEnumerable<string>? cargos) => (cargos ?? []).Any(c => CargosQueAdministram.Contains((c ?? "").Trim().ToLowerInvariant()));
    /// <summary>Setup do CS: quem administra, mais o head (só nos squads dele). lib/cs/setup-acesso.ts.</summary>
    public static bool PodeAbrirSetupCs(IEnumerable<string>? cargos) => PodeAdministrar(cargos) || (cargos ?? []).Any(c => (c ?? "").Trim().ToLowerInvariant() == "head");

    public const string MotivoSoCoordenador = "só coordenador e acima";

    /// <summary>lib/cs/escopo-cliente.ts: escopo global vê tudo; fora disso, vínculo por QUALQUER squad da sessão. Fail-closed.</summary>
    public static bool ClienteNoAlcance(IReadOnlyCollection<string>? squadIdsDaSessao, IEnumerable<string>? squadsDoCliente)
    {
        if (squadIdsDaSessao is null) return true;
        var permitidos = squadIdsDaSessao.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (squadsDoCliente ?? []).Any(permitidos.Contains);
    }

    /* ── O PONTO DE VISTA DE SQUAD (lib/os/contrato.ts) ─────────────────────── */
    public sealed record Contrato(string? SquadId, string? Modalidade, decimal? Mrr, decimal? ValorTotal);
    public sealed record LeituraContrato(string Situacao, Contrato? Contrato, string? NoSquadId);

    /// <summary>O dinheiro do cliente visto de UM squad: próprio, incluso no contrato de outro, ou sem contrato.</summary>
    public static LeituraContrato LeituraDoSquad(IReadOnlyList<Contrato> contratos, string? squadId, string? squadPrincipalId)
    {
        static bool Mesmo(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var proprio = contratos.FirstOrDefault(c => Mesmo(c.SquadId, squadId));
        if (proprio is not null) return new("proprio", proprio, null);
        if (squadId is null)
        {
            var doPrincipal = contratos.FirstOrDefault(c => Mesmo(c.SquadId, squadPrincipalId));
            if (doPrincipal is not null) return new("proprio", doPrincipal, null);
            return contratos.Count > 0 ? new("proprio", contratos[0], null) : new("sem_contrato", null, null);
        }
        if (contratos.Count > 0)
        {
            var ondeMora = contratos.FirstOrDefault(c => Mesmo(c.SquadId, squadPrincipalId)) ?? contratos[0];
            return new("incluso", null, ondeMora.SquadId);
        }
        return new("sem_contrato", null, null);
    }

    /// <summary>De qual squad estou olhando: o pedido (se alcançado), senão o único squad da sessão, senão nenhum.</summary>
    public static string? PontoDeVista(IReadOnlyList<string>? squadIdsDaSessao, string? squadPedido)
    {
        if (!string.IsNullOrEmpty(squadPedido) && squadPedido != "todos")
            return squadIdsDaSessao is null || squadIdsDaSessao.Contains(squadPedido, StringComparer.OrdinalIgnoreCase) ? squadPedido : null;
        return squadIdsDaSessao?.Count == 1 ? squadIdsDaSessao[0] : null;
    }

    /// <summary>O recorte da lista: só com pedido explícito, e só de squad que a pessoa já alcança.</summary>
    public static string? RecorteDeSquad(IReadOnlyList<string>? squadIdsDaSessao, string? squadPedido)
    {
        if (string.IsNullOrEmpty(squadPedido) || squadPedido == "todos") return null;
        return squadIdsDaSessao is null || squadIdsDaSessao.Contains(squadPedido, StringComparer.OrdinalIgnoreCase) ? squadPedido : null;
    }

    /// <summary>`?squad=` aceita id ou nome (sem caixa). Nome que não existe cai em null.</summary>
    public static string? ResolverSquadPedido(string? pedido, IEnumerable<(string Id, string Nome)> squads)
    {
        if (string.IsNullOrWhiteSpace(pedido) || pedido == "todos") return null;
        var lista = squads.ToList();
        if (lista.Any(s => string.Equals(s.Id, pedido, StringComparison.OrdinalIgnoreCase))) return lista.First(s => string.Equals(s.Id, pedido, StringComparison.OrdinalIgnoreCase)).Id;
        var alvo = pedido.Trim().ToLowerInvariant();
        return lista.FirstOrDefault(s => (s.Nome ?? "").Trim().ToLowerInvariant() == alvo).Id;
    }

    /// <summary>lib/cs/dinheiro.ts: o texto do dinheiro na visão de um squad. Zero e ausência não viram número.</summary>
    public static string? Dinheiro(string? modalidade, decimal? mrr, decimal? valorContrato, string? mrrInclusoEm)
    {
        if (modalidade == "tcv") return valorContrato is > 0 ? $"R${Math.Round(valorContrato.Value)} (contrato)" : null;
        if (mrr is > 0) return $"R${Math.Round(mrr.Value)}/mês";
        if (!string.IsNullOrEmpty(mrrInclusoEm)) return $"incluso no contrato do {mrrInclusoEm}";
        return null;
    }

    /* ── DIAS EM BRASÍLIA (lib/os/data-br.ts) ───────────────────────────────────
       Dias de CALENDÁRIO no fuso de Brasília, não blocos de 24 h. O Brasil não tem
       horário de verão desde 2019, então UTC-3 fixo é o fuso. */
    public static readonly TimeSpan FusoBr = TimeSpan.FromHours(-3);
    public static DateOnly DiaBr(DateTime instante) => DateOnly.FromDateTime(ToUtc(instante) + FusoBr);
    static DateTime ToUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Local).ToUniversalTime(),
    };

    /* ── GRUPO FRIO (lib/cs/grupo-frio.ts): 3 dias corridos sem mensagem. Sem data não é frio. */
    public const int DiasGrupoFrio = 3;
    public static (bool Frio, int? Dias) GrupoFrio(DateTime? ultimaMensagemEm, DateTime agora)
    {
        if (ultimaMensagemEm is null) return (false, null);
        var dias = Math.Max(0, DiaBr(agora).DayNumber - DiaBr(ultimaMensagemEm.Value).DayNumber);
        return (dias >= DiasGrupoFrio, dias);
    }

    /* ── SITUAÇÃO DO PRAZO (lib/cs/situacao-prazo.ts) ──────────────────────────
       aberta no prazo (hoje incluso) → dentro_do_prazo · 1..5 dias → atrasada ·
       6+ → nao_entregue · finalizada até o prazo → entregue · depois → entregue_atrasado. */
    public const int DiasAteNaoEntregue = 5;
    public static (string Situacao, int? Dias) SituacaoDoPrazo(bool finalizada, DateOnly? prazo, DateTime? concluidaEm, bool conclusaoConfiavel, DateTime agora)
    {
        if (finalizada)
        {
            if (prazo is null || concluidaEm is null || !conclusaoConfiavel) return ("entregue", null);
            var atraso = DiaBr(concluidaEm.Value).DayNumber - prazo.Value.DayNumber;
            return atraso > 0 ? ("entregue_atrasado", atraso) : ("entregue", null);
        }
        if (prazo is null) return ("sem_prazo", null);
        var dif = DiaBr(agora).DayNumber - prazo.Value.DayNumber;
        if (dif <= 0) return ("dentro_do_prazo", -dif);
        return (dif <= DiasAteNaoEntregue ? "atrasada" : "nao_entregue", dif);
    }

    /// <summary>lib/cs/prazo-do-passo.ts: 'imediato' = hoje; 'Nd' = hoje+N; data ISO passa; o resto é sem prazo.</summary>
    public static DateOnly? PrazoDoPasso(string? entrada, DateTime agora)
    {
        var p = (entrada ?? "").Trim().ToLowerInvariant();
        if (Regex.IsMatch(p, @"^\d{4}-\d{2}-\d{2}$") && DateOnly.TryParseExact(p, "yyyy-MM-dd", out var d)) return d;
        if (p == "imediato") return DiaBr(agora);
        var m = Regex.Match(p, @"^(\d+)d$");
        if (m.Success) return DiaBr(agora).AddDays(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        return null;
    }

    /// <summary>lib/cs/titulo-do-passo.ts: título curto cortado na palavra (até 70) quando a IA não resume.</summary>
    public static string CorteDoTitulo(string texto)
    {
        if (texto.Length <= 70) return texto;
        var fatia = texto[..70];
        var esp = fatia.LastIndexOf(' ');
        return fatia[..(esp > 30 ? esp : 70)] + "…";
    }

    /// <summary>A resposta da IA só vale como título se for curta e limpa; senão o corte na palavra.</summary>
    public static string? TituloDaResposta(string? bruto)
    {
        if (bruto is null) return null;
        var t = Regex.Replace(bruto.Trim(), "^[\"']|[\"'.]$", "");
        return t.Length is > 0 and <= 90 ? t : null;
    }

    /// <summary>lib/cs/wa-match.ts normalize: minúsculas, sem acento, só letras/dígitos/espaço.</summary>
    public static string Normalizar(string s)
    {
        var semAcento = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) semAcento.Append(ch);
        return Regex.Replace(Regex.Replace(semAcento.ToString(), "[^a-z0-9\\s]", " "), "\\s+", " ").Trim();
    }
    public static string PrimeiroNome(string nome) => Normalizar(nome).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /* ── DECISÃO HUMANA (lib/cs/decisao-humana.ts) ─────────────────────────────
       O agente sugere; a pessoa responde AO LADO (jsonb indexado pela posição),
       nunca por cima do texto do agente. Merge por chave: a última gravação não
       apaga as decisões dos outros índices. */
    public static readonly string[] StatusPasso = ["fechado", "ignorado"];
    public static readonly string[] StatusPromessa = ["cumprida", "entregue_atrasado", "atrasada", "em_andamento", "nao_cumprida", "nao_se_aplica"];

    public sealed record Recusa(string Erro, int Http);
    public sealed record PedidoDeDecisao(int Indice, string Status, string? Justificativa);

    public static (PedidoDeDecisao? Pedido, Recusa? Recusa) ValidarDecisao(string? analiseId, int? indice, string? status, string? justificativa, string[] aceitos, bool exigeJustificativa)
    {
        if (string.IsNullOrWhiteSpace(analiseId)) return (null, new("informe analise_id", 400));
        if (indice is null or < 0) return (null, new("informe indice (inteiro, a posição do item na lista)", 400));
        var st = (status ?? "").Trim();
        if (!aceitos.Contains(st)) return (null, new($"status inválido — use {string.Join(" | ", aceitos)}", 400));
        var just = (justificativa ?? "").Trim();
        if (exigeJustificativa && just == "") return (null, new("justificativa obrigatória: diga por que este passo não se aplica mais", 400));
        return (new(indice.Value, st, just == "" ? null : just[..Math.Min(just.Length, 1000)]), null);
    }

    /// <summary>Quantos itens tem a lista jsonb (array) — 0 para nulo ou não-array.</summary>
    public static int TamanhoDaLista(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try { using var d = JsonDocument.Parse(json); return d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement.GetArrayLength() : 0; }
        catch (JsonException) { return 0; }
    }

    public static Recusa? ValidarIndice(int indice, int total) =>
        indice >= total ? new($"indice {indice} fora da lista ({total} item(ns) nesta análise)", 400) : null;

    /// <summary>O mapa antigo entra inteiro e só a chave decidida é reescrita. Devolve o JSON do mapa novo.</summary>
    public static string AplicarDecisao(string? mapaAtual, int indice, object decisao)
    {
        var mapa = new Dictionary<string, JsonElement>();
        if (!string.IsNullOrWhiteSpace(mapaAtual))
        {
            try
            {
                using var d = JsonDocument.Parse(mapaAtual);
                if (d.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in d.RootElement.EnumerateObject()) mapa[p.Name] = p.Value.Clone();
            }
            catch (JsonException) { }
        }
        mapa[indice.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToElement(decisao, JsonWeb);
        return JsonSerializer.Serialize(mapa, JsonWeb);
    }
    static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    /* ── O QUE O TIME REGISTRA (lib/cs/registro-do-time.ts) ─────────────────── */
    public const int TetoTexto = 8000;
    public static (string? Texto, Recusa? Recusa) ValidarTexto(string? v)
    {
        var t = (v ?? "").Trim();
        if (t == "") return (null, new("escreva a anotação: texto em branco não vira registro", 400));
        return (t[..Math.Min(t.Length, TetoTexto)], null);
    }

    static readonly Regex NumeroTexto = new(@"^\d{1,12}(?:[.,]\d{1,2})?$");
    /// <summary>Número ou texto na FORMA de número; "1e3", "0x10" e negativos são recusados.</summary>
    public static (decimal? Valor, Recusa? Recusa) ValidarValor(JsonElement? v)
    {
        var recusa = new Recusa("valor inválido: informe um número maior ou igual a zero", 400);
        decimal n;
        if (v is { ValueKind: JsonValueKind.Number } num && num.TryGetDecimal(out var d)) n = d;
        else if (v is { ValueKind: JsonValueKind.String } s && NumeroTexto.IsMatch(s.GetString()!.Trim()))
            n = decimal.Parse(s.GetString()!.Trim().Replace(',', '.'), CultureInfo.InvariantCulture);
        else return (null, recusa);
        if (n < 0) return (null, recusa);
        return (Math.Round(n, 2, MidpointRounding.AwayFromZero), null);
    }

    public sealed record PedidoUpsell(string? Slug, string Nome, decimal Valor, DateOnly? VendidoEm, string? Observacao);
    public static (PedidoUpsell? Pedido, Recusa? Recusa) ValidarPedidoUpsell(string? upsellSlug, string? upsellNome, JsonElement? valor, string? vendidoEm, string? observacao)
    {
        var slug = (upsellSlug ?? "").Trim();
        var nomeDigitado = (upsellNome ?? "").Trim();
        if (slug == "" && nomeDigitado == "") return (null, new("diga o que foi vendido: escolha um upsell do catálogo ou digite o nome", 400));
        var (v, recusa) = ValidarValor(valor);
        if (recusa is not null) return (null, recusa);
        DateOnly? data = vendidoEm is not null && Regex.IsMatch(vendidoEm.Trim(), @"^\d{4}-\d{2}-\d{2}$") && DateOnly.TryParseExact(vendidoEm.Trim(), "yyyy-MM-dd", out var dt) ? dt : null;
        var obs = string.IsNullOrWhiteSpace(observacao) ? null : observacao.Trim()[..Math.Min(observacao.Trim().Length, TetoTexto)];
        var nome = nomeDigitado != "" ? nomeDigitado : slug;
        return (new(slug == "" ? null : slug, nome[..Math.Min(nome.Length, 300)], v!.Value, data, obs), null);
    }

    /* ── SEM PERFORMANCE (lib/cs/sem-performance.ts): ABYSSAL e DIVERS são fixos, ligados para todo mundo. */
    public static readonly string[] SquadsSemPerformanceFixo = ["ABYSSAL", "DIVERS"];
    public static bool SquadSemPerformanceFixo(string? nomeDoSquad) => SquadsSemPerformanceFixo.Contains((nomeDoSquad ?? "").Trim().ToUpperInvariant());
    public static readonly string AvisoSquadFixo = $"sempre ligado no {string.Join(" e no ", SquadsSemPerformanceFixo)}";

    /* ── O ALERTA DE CHURN (app/api/cs/risco): crítico antes de alto, nota crescente, id desempata. */
    public sealed record Alerta(string ClienteId, string Nome, string? Risco, int Nota, string Faixa, string FaixaApelido, DateTime? GeradaEm, string Sumario);
    public static List<Alerta> OrdenarAlertas(IEnumerable<Alerta> alertas) => alertas
        .OrderBy(a => a.Risco == "critico" ? 0 : 1).ThenBy(a => a.Nota).ThenBy(a => a.ClienteId, StringComparer.Ordinal).ToList();
    public const int AlertasNaRespostaPadrao = 8;
    public static string CorteDeclarado(int mostrando, int total) => $"mostrando {mostrando} de {total} clientes em risco alto ou crítico. NUNCA diga que são só estes.";

    public static readonly string[] StatusDaCarteira = ["ativo", "onboarding", "aviso_previo", "churn", "pausado"];
    public static readonly string[] StatusDoRisco = ["ativo", "onboarding", "aviso_previo"];
    public const string LeituraDoFiltroFrio = "grupo frio MEDIDO: 3 dias corridos (fuso de Brasília) sem mensagem nenhuma no grupo, de qualquer lado.";
}
