namespace BlueOS.Core.Modelos;

/* ============================================================================
   OS PARÂMETROS DO MOTOR: o que o vault (trafego-vault) entrega ao motor.
   ----------------------------------------------------------------------------
   Espelho de `ParametrosMotor` em lib/trafego/motor/contrato.ts. Dinheiro em
   CENTAVOS INTEIROS dentro do motor (AD-MT-10); o vault fala em reais.
   Os valores padrão são os da versão 9715c94 do vault, a que está em produção
   desde 09/10/2026 (mapa de implementação do motor, 11/10).
   ============================================================================ */
public sealed record Faixas(
    long MinimoCentavos,
    long F2AcimaDeCentavos,
    long F3DesdeCentavos,
    IReadOnlyDictionary<Etapa, double> SplitF2,
    IReadOnlyDictionary<Etapa, double> SplitF3);

public sealed record ConjuntosPorBudget(long DivisorCentavos, int MofuMin, int MofuMax, int BofuMax, int TofuMax);
public sealed record FaixaConjunto(long MinCentavos, long MaxCentavos);
public sealed record PublicosMofu(IReadOnlyList<string> Ordem, int AdvantageAudienceAdvantage, int AdvantageAudienceDemais);
public sealed record PesosTier(double A, double B, double C);
public sealed record Corte(double AlertarEm, double PausarEm);
public sealed record Escala(double TaxaMin, int PisoAvaliados);

public sealed class ParametrosMotor
{
    public required Faixas Faixas { get; init; }
    public required ConjuntosPorBudget Conjuntos { get; init; }
    public required FaixaConjunto Conjunto { get; init; }
    public required long TetoMontagemCentavos { get; init; }
    public required long TofuMinimoDiaCentavos { get; init; }
    public required PublicosMofu PublicosMofu { get; init; }
    public required PesosTier Pesos { get; init; }
    public required Corte Corte { get; init; }
    public required int NaoTocarDias { get; init; }
    public required int CicloDias { get; init; }
    public required Escala Escala { get; init; }
    public required long CopiaIncrementoCentavos { get; init; }
    public required double TrocaMultiploTeto { get; init; }
    public required double AlertaBudgetFracao { get; init; }
    /// <summary>modo_execucao.pendente_segura_dias (opcional no vault). Ausente = ciclo_leitura.</summary>
    public int? PendenteSeguraDias { get; init; }
    public string VaultSha { get; init; } = "9715c94";

    /// <summary>Os números do vault em produção (versão 9715c94).</summary>
    public static ParametrosMotor PadraoDoVault { get; } = new()
    {
        Faixas = new Faixas(
            MinimoCentavos: 1500_00,
            F2AcimaDeCentavos: 3000_00,
            F3DesdeCentavos: 7500_00,
            SplitF2: new Dictionary<Etapa, double> { [Etapa.MOFU] = 0.78, [Etapa.BOFU] = 0.22 },
            SplitF3: new Dictionary<Etapa, double> { [Etapa.TOFU] = 0.05, [Etapa.MOFU] = 0.75, [Etapa.BOFU] = 0.20 }),
        Conjuntos = new ConjuntosPorBudget(DivisorCentavos: 20_00, MofuMin: 2, MofuMax: 4, BofuMax: 2, TofuMax: 1),
        Conjunto = new FaixaConjunto(MinCentavos: 20_00, MaxCentavos: 100_00),
        TetoMontagemCentavos = 50_00,
        TofuMinimoDiaCentavos = 20_00,
        PublicosMofu = new PublicosMofu(
            Ordem: ["advantage", "interesses_nicho", "interesses_alternativos", "lookalike"],
            AdvantageAudienceAdvantage: 1, AdvantageAudienceDemais: 0),
        Pesos = new PesosTier(1.0, 0.8, 0.6),
        Corte = new Corte(AlertarEm: 1.0, PausarEm: 1.5),
        NaoTocarDias = 7,
        CicloDias = 3,
        Escala = new Escala(TaxaMin: 0.6, PisoAvaliados: 10),
        CopiaIncrementoCentavos = 15_00,
        TrocaMultiploTeto = 1.5,
        AlertaBudgetFracao = 0.2,
        PendenteSeguraDias = 3,
    };
}

/* ---- o planejador ---------------------------------------------------------- */

public sealed record EntradaDoPlano(long BudgetMensalCentavos, string FonteBudget, bool TemLista, bool TemSiteOuTag);
public sealed record Idade(int Min, int Max);
public sealed record ConjuntoPlanejado(Etapa Etapa, string Publico, long DiarioCentavos, Idade Idade, int AdvantageAudience);
public sealed record Reservado(Etapa Etapa, long DiarioCentavos, string Motivo, string Chave);

public sealed record Plano(
    FaixaId Faixa,
    long DiarioCentavos,
    IReadOnlyList<ConjuntoPlanejado> Conjuntos,
    IReadOnlyList<Reservado> Reservado,
    long NaoAlocadoCentavos,
    IReadOnlyList<string> Avisos,
    IReadOnlyList<string> ChavesUsadas);

/// <summary>Um conjunto como a tela mostra depois do Start, já com os nomes da Meta.</summary>
public sealed record ConjuntoProposto(
    string Rotulo, Etapa Etapa, string Publico, string PublicoRotulo, long DiarioCentavos,
    string Nome, string Anuncio, Idade Idade, int AdvantageAudience);

public sealed record EstruturaProposta(
    FaixaId FaixaId,
    string FaixaRotulo,
    string Rotulo,
    long BudgetMensalCentavos,
    long DiarioCentavos,
    long AlocadoDiarioCentavos,
    IReadOnlyList<ConjuntoProposto> Conjuntos,
    long? PorConjuntoMin,
    long? PorConjuntoMax,
    IReadOnlyList<Reservado> Reservado,
    long NaoAlocadoCentavos,
    IReadOnlyList<string> Avisos,
    int? MqlEsperado,
    string? NomeDaCampanha,
    IReadOnlyList<string> ChavesUsadas,
    string VaultSha)
{
    /// <summary>Um hash da estrutura PROPOSTA (etapa, público, verba, budget e vault): é a chave de idempotência só da
    /// demonstração. NÃO é o hash que a API grava ao publicar: o Publicar real usa <c>StartRegras.HashDaEstrutura</c>
    /// (nomes, artes, texto e formulário), então não mostre este como "o hash da publicação".</summary>
    public string Hash
    {
        get
        {
            var texto = string.Join("|", Conjuntos.Select(c => $"{c.Etapa}:{c.Publico}:{c.DiarioCentavos}")) + "|" + BudgetMensalCentavos + "|" + VaultSha;
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(texto));
            return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        }
    }
}

/* ---- a árvore ---------------------------------------------------------------- */

/// <summary>O que o motor LEMBRA de um conjunto, em dias (Tráfego 3.3).</summary>
public sealed record MemoriaDoConjunto(int? DiasDesdeUltimaAcao, int? DiasSegurandoPorPendente, int CopiasVivas);

/// <summary>A vida do conjunto, de uma fonte só (AD-MT-04). Produzida pela carga diária.</summary>
public sealed record EntradaConjunto(
    string ClienteSlug,
    string AdsetId,
    string Nome,
    Formato Formato,
    int AnunciosAtivos,
    int DiasEntrega,
    long GastoCentavos,
    int Leads,
    int Avaliados,
    int Pendentes,
    int MqlA, int MqlB, int MqlC,
    Leitura Leitura,
    bool CoberturaCompleta,
    MemoriaDoConjunto? Memoria);

/// <summary>Lido na hora de recomendar e de executar (AD-MT-15). Ativo null = status não lido.</summary>
public sealed record EntradaAoVivo(bool? Ativo, long? OrcamentoDiarioCentavos);

public sealed record PassoDaTrilha(string Chave, ResultadoDaRegra Resultado, string Texto);
public sealed record Aviso(string Ref, string Texto);
public sealed record NumerosDaDecisao(double Ponderado, double Taxa, long? CustoCentavos, double? Multiplo);

public sealed record Decisao(
    Acao Acao,
    IReadOnlyList<string> Chaves,
    IReadOnlyList<PassoDaTrilha> Trilha,
    IReadOnlyList<Aviso> Avisos,
    NumerosDaDecisao Numeros,
    bool TemCopia,
    long? CopiaDiarioCentavos,
    bool SegurouPorPendentes);

/* ---- a régua ------------------------------------------------------------------ */

public sealed class PerguntaDaRegua
{
    public string Nome { get; set; } = "";
    public List<string> Chaves { get; set; } = new();
    public string? Rotulo { get; set; }
    public List<string> Forte { get; set; } = new();
    public List<string> Media { get; set; } = new();
    public List<string> Elim { get; set; } = new();

    public PerguntaDaRegua Clonar() => new()
    {
        Nome = Nome, Chaves = new(Chaves), Rotulo = Rotulo,
        Forte = new(Forte), Media = new(Media), Elim = new(Elim),
    };
}

public sealed class ReguaV2
{
    public int Versao { get; set; } = 2;
    public List<PerguntaDaRegua> Perguntas { get; set; } = new();
    public ReguaV2 Clonar() => new() { Versao = 2, Perguntas = Perguntas.Select(p => p.Clonar()).ToList() };
}

public sealed record RespostaNaoMapeada(string Pergunta, string Resposta);

public sealed record ResultadoDaAvaliacao(
    Situacao Situacao,
    bool Mql,
    Tier? Tier,
    int Fortes,
    IReadOnlyList<string> Eliminatorias,
    IReadOnlyList<string> Faltantes,
    IReadOnlyList<RespostaNaoMapeada> NaoMapeadas,
    string Motivo);

public sealed record ContagemPelaRegua(int MqlA, int MqlB, int MqlC, int Mql, int Avaliados, int Pendentes);
