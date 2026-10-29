namespace BlueOS.Core.Modelos;

/* ============================================================================
   O CLIENTE, como o gestor de tráfego o vê: a ficha, os assets, a copy, as
   artes, a campanha e a automação, tudo num objeto só.
   ----------------------------------------------------------------------------
   Os nomes dos campos seguem as tabelas do V1 (clientes, cliente_icp,
   cliente_produto, cliente_estrategia, cliente_asset, trafego_contas,
   copy_peca, designer_pecas, trafego_start_publicacao, trafego_motor_*).
   ============================================================================ */
public sealed class Cliente
{
    public required string Id { get; init; }
    public required string Slug { get; set; }
    public required string Nome { get; set; }
    /// <summary>ativo | onboarding | pausado | aviso_previo | churn (cliente_status_atual).</summary>
    public string Status { get; set; } = "onboarding";
    public string Squad { get; set; } = "POSEIDON";
    public DateOnly Desde { get; set; }

    public FichaCadastral Ficha { get; set; } = new();
    public Icp Icp { get; set; } = new();
    public Produto Produto { get; set; } = new();
    public Estrategia Estrategia { get; set; } = new();

    /// <summary>O card Tráfego: nicho (trafego_contas.nicho_slug) e budget (cliente_estrategia.budget_ads_mensal).</summary>
    public string? NichoSlug { get; set; }
    public decimal? BudgetMensal { get; set; }

    /// <summary>O critério de MQL: a régua v2 (trafego_contas.regua_mql) e o teto por MQL do PPL.</summary>
    public ReguaV2? Regua { get; set; }
    public decimal? TetoMql { get; set; }

    public ContaDeAnuncios Conta { get; set; } = new();
    public List<Asset> Assets { get; } = new();

    public List<LoteDeCopy> Lotes { get; } = new();
    public List<Arte> Artes { get; } = new();
    /// <summary>A arte do passo 3 do Start: o criativo piloto (designer_peca_id).</summary>
    public string? ArtePilotoId { get; set; }

    public Publicacao? Publicacao { get; set; }

    public ModoDeExecucao Modo { get; set; } = ModoDeExecucao.Recomenda;
    public List<ConjuntoEmVeiculacao> Conjuntos { get; } = new();
    public List<Recomendacao> Recomendacoes { get; } = new();
    public List<AcaoDoAgente> Acoes { get; } = new();
    public DateTime? UltimaRodada { get; set; }

    public IEnumerable<PecaDeCopy> Pecas => Lotes.SelectMany(l => l.Pecas);
    public bool ContaConectada => !string.IsNullOrWhiteSpace(Conta.AdAccountId);
    public bool TemLogo => Assets.Any(a => a.Tipo == TipoDeAsset.LogoPrincipal);
    public Arte? ArtePiloto => Artes.FirstOrDefault(a => a.Id == ArtePilotoId);
}

public sealed class FichaCadastral
{
    public string? Responsavel { get; set; }
    public string? ContatoPrincipal { get; set; }
    public string? Whatsapp { get; set; }
    public string? Cnpj { get; set; }
    public string? RazaoSocial { get; set; }
    public string? Site { get; set; }
    /// <summary>mrr | tcv</summary>
    public string? Modalidade { get; set; }
    public decimal? Mrr { get; set; }
    public decimal? ValorTotal { get; set; }
    public DateOnly? DataVencimento { get; set; }
}

/// <summary>O ICP, como o editor de contexto do cliente pergunta (cliente-contexto-editor.tsx).</summary>
public sealed class Icp
{
    public string Tipo { get; set; } = "B2B";
    public string Temperatura { get; set; } = "morno";
    public string NivelConsciencia { get; set; } = "3";
    public string? Segmento { get; set; }
    public string? Setor { get; set; }
    public string? FaturamentoMinimo { get; set; }
    public string? CargoDecisor { get; set; }
    public string? Localizacao { get; set; }
    public string? Persona { get; set; }
    public string? PublicoAlvo { get; set; }
    public string? TamanhoMercado { get; set; }
    public string? Obs { get; set; }
    public List<string> DoresPrincipais { get; set; } = new();
    public List<string> DesejosPrincipais { get; set; } = new();
    public List<string> CanaisOndeEstao { get; set; } = new();
    /// <summary>O id do recorte do ICP principal no V1 (cliente_icp_segmento), para o PATCH gravar no mesmo.</summary>
    public string? SegmentoId { get; set; }

    public static readonly string[] Tipos = ["B2B", "B2C", "B2B2C"];
    public static readonly string[] Temperaturas = ["frio", "morno", "quente"];
    public static readonly string[] Niveis = ["1", "2", "3", "4", "5"];
}

public sealed class Produto
{
    public string? NomeCorreto { get; set; }
    public string? Descricao { get; set; }
    public string? ModeloNegocio { get; set; }
    public string? Diferenciais { get; set; }
    public string? FuncionalidadesChave { get; set; }
    public string? ObjecoesComuns { get; set; }
    public string? Concorrentes { get; set; }
}

public sealed class Estrategia
{
    public string? PromessaPrincipal { get; set; }
    public string? Posicionamento { get; set; }
    public string? Diferencial { get; set; }
    public string? FunilTipo { get; set; }
    public string? Gatilhos { get; set; }
}

/// <summary>A conta de anúncios e o que o Start precisa dela (trafego_contas + o que a Meta exige).</summary>
public sealed class ContaDeAnuncios
{
    /// <summary>Só o número, sem o `act_` (conectar-conta/route.ts).</summary>
    public string? AdAccountId { get; set; }
    public string? PageId { get; set; }
    public string? PageNome { get; set; }
    /// <summary>Um formulário já existente da página, em vez de criar da régua.</summary>
    public string? FormularioId { get; set; }
    /// <summary>A Meta exige a política de privacidade do anunciante para criar o formulário.</summary>
    public string? PoliticaUrl { get; set; }
    /// <summary>O link do anúncio (site do cliente).</summary>
    public string? Link { get; set; }
    /// <summary>O cliente subiu lista de clientes ou leads (público personalizado do BOFU)?</summary>
    public bool TemLista { get; set; }
    /// <summary>Tem site com pixel ou tag para público de site no TOFU?</summary>
    public bool TemSiteOuTag { get; set; }
    /// <summary>true = a Meta aceitou o token do squad; false = recusou; null = não conferido.</summary>
    public bool? TokenValido { get; set; }
    public string? TokenMotivo { get; set; }
    public DateTime? TokenConferidoEm { get; set; }
    public string ActId => string.IsNullOrWhiteSpace(AdAccountId) ? "" : $"act_{AdAccountId}";
}

public sealed class Asset
{
    public required string Id { get; init; }
    public required TipoDeAsset Tipo { get; init; }
    public required string Nome { get; init; }
    public string Mime { get; init; } = "image/png";
    public long TamanhoBytes { get; init; }
    public string Origem { get; init; } = "manual";
    public DateTime CriadoEm { get; init; } = DateTime.Now;
    public bool EmUso { get; set; } = true;
    /// <summary>Preview (data URL) quando é imagem; no V1 é a URL assinada do bucket brand-assets.</summary>
    public string? Url { get; set; }

    public static string RotuloDoTipo(TipoDeAsset t) => t switch
    {
        TipoDeAsset.LogoPrincipal => "Logo principal",
        TipoDeAsset.LogoVariacao => "Logo variação",
        _ => "Tela do produto",
    };

    public static string SlugDoTipo(TipoDeAsset t) => t switch
    {
        TipoDeAsset.LogoPrincipal => "logo_principal",
        TipoDeAsset.LogoVariacao => "logo_variacao",
        _ => "tela",
    };
}

/* ---- copy --------------------------------------------------------------------- */

/// <summary>Uma peça de copy com a estrutura DECLARADA (copy por nicho, AD-4) e o veredito do crivo.</summary>
public sealed class PecaDeCopy
{
    public required string Id { get; init; }
    public required Etapa Etapa { get; init; }
    public required TipoPeca Tipo { get; init; }
    public int Numero { get; init; }
    /// <summary>HEAD (estático) ou HOOK (vídeo).</summary>
    public string Headline { get; set; } = "";
    /// <summary>SUB (estático) ou BODY (vídeo).</summary>
    public string Sub { get; set; } = "";
    public string Cta { get; set; } = "";
    public string AnguloHeadline { get; set; } = "dor";
    public List<string> FormasHeadline { get; set; } = new();
    public string PapelSub { get; set; } = "explica_como_funciona";
    public string TipoCta { get; set; } = "saiba_mais";
    public string? Icp { get; set; }
    /// <summary>A hipótese que esta peça testa (R8): uma variável, declarada.</summary>
    public string Hipotese { get; set; } = "";
    public bool CrivoOk { get; set; } = true;
    public string? MotivoDoCrivo { get; set; }
    public List<string> Regeneracoes { get; } = new();
    public bool Aprovada { get; set; }
    public int Versao { get; set; } = 1;

    public string RotuloDoTipo => Tipo == TipoPeca.Video ? "vídeo" : "estático";
    public string RotuloAbertura => Tipo == TipoPeca.Video ? "HOOK" : "HEAD";
    public string RotuloFecho => Tipo == TipoPeca.Video ? "BODY" : "SUB";
}

public sealed class LoteDeCopy
{
    public required string Id { get; init; }
    public int Numero { get; init; }
    public DateTime CriadoEm { get; init; } = DateTime.Now;
    public string Modelo { get; init; } = "";
    public string? Briefing { get; init; }
    public string VaultSha { get; init; } = "";
    public string? NichoSlug { get; init; }
    public string EstadoDoNicho { get; init; } = "ok";
    public List<PecaDeCopy> Pecas { get; } = new();
    public int TokensIn { get; init; }
    public int TokensOut { get; init; }
}

/* ---- design ------------------------------------------------------------------- */

public sealed class Arte
{
    public required string Id { get; init; }
    public required string PecaId { get; init; }
    public required Etapa Etapa { get; init; }
    /// <summary>feed (1080x1080) ou story (1080x1920), os dois tamanhos da Meta (designer/formato.ts).</summary>
    public required string Formato { get; init; }
    public required string Titulo { get; init; }
    /// <summary>Data URL ou URL assinada do bucket designer-files.</summary>
    public required string Url { get; init; }
    public int Lote { get; init; }
    public DateTime CriadoEm { get; init; } = DateTime.Now;
    public bool Aprovada { get; set; }
    public string Origem { get; init; } = "designer";
}

/* ---- publicação ---------------------------------------------------------------- */

public sealed record ConjuntoCriado(string Nome, Etapa Etapa, string Publico, string AdsetId, long DiarioCentavos);
public sealed record AnuncioCriado(string Nome, string AdsetId, string AdId, string PecaId);

public sealed class Publicacao
{
    public required string EstruturaHash { get; init; }
    public string? CampanhaId { get; set; }
    public string? FormularioId { get; set; }
    public string? Link { get; set; }
    public string NomeDaCampanha { get; init; } = "";
    public List<ConjuntoCriado> Conjuntos { get; } = new();
    public List<AnuncioCriado> Anuncios { get; } = new();
    public DateTime Em { get; init; } = DateTime.Now;
    public bool Pausado { get; set; } = true;
    public bool Simulado { get; init; }
    /// <summary>criando | parcial | ok | erro (trafego_start_publicacao.estado).</summary>
    public string Estado { get; set; } = "ok";
    public List<string> Avisos { get; } = new();
    public string? Erro { get; set; }
    public string VaultSha { get; init; } = "";
}

/* ---- automação ----------------------------------------------------------------- */

public sealed record DiaDeInsight(DateOnly Dia, long GastoCentavos, int Leads);
public sealed record Resposta(string Pergunta, string Valor);

/// <summary>Um conjunto em veiculação, como a carga diária o vê (trafego_adset_estado + trafego_insight_dia + trafego_lead).</summary>
public sealed class ConjuntoEmVeiculacao
{
    public required string AdsetId { get; init; }
    public required string Nome { get; init; }
    public required Etapa Etapa { get; init; }
    public string Publico { get; init; } = "";
    public long DiarioCentavos { get; set; }
    /// <summary>effective_status === ACTIVE. null = status não lido.</summary>
    public bool? Ativo { get; set; }
    public Formato Formato { get; set; } = Formato.Imagem;
    public int AnunciosAtivos { get; set; } = 1;
    public DateOnly? PrimeiraEntrega { get; set; }
    public List<DiaDeInsight> Dias { get; } = new();
    public List<LeadDaCentral> Leads { get; } = new();
    public bool CoberturaCompleta { get; set; } = true;
    public Leitura Leitura { get; set; } = Leitura.Medida;
    public DateOnly? UltimaAcaoExecutada { get; set; }
    public DateOnly? PendenteDesde { get; set; }
    public int CopiasVivas { get; set; }
    public bool CopiaDoMotor { get; init; }
    public string? OriginalId { get; init; }
    /// <summary>A peça de copy que o anúncio carrega (trafego_anuncio_peca).</summary>
    public string? PecaId { get; set; }

    public long GastoVidaCentavos => Dias.Sum(d => d.GastoCentavos);
    public int DiasEntrega => Dias.Count(d => d.GastoCentavos > 0);
}

public sealed class LeadDaCentral
{
    public required string Id { get; init; }
    public DateTime CriadoEm { get; init; }
    public string? AdsetId { get; init; }
    public string? Conjunto { get; set; }
    public string? AdId { get; init; }
    public List<Resposta> Respostas { get; init; } = new();
    public Situacao? Situacao { get; set; }
    public Tier? Tier { get; set; }
    public string? Motivo { get; set; }
    public List<RespostaNaoMapeada> NaoMapeadas { get; set; } = new();
    public List<string> Faltantes { get; set; } = new();
}

public sealed record KpisDoMes(
    string Mes,
    long GastoCentavos,
    long? BudgetCentavos,
    int DiasDoMes,
    int DiasLidos,
    int Leads,
    int Avaliados,
    int Pendentes,
    int NaoMql,
    int Mql,
    int MqlA, int MqlB, int MqlC,
    int? MetaMql,
    long? CustoPorMqlCentavos,
    long? TetoCentavos,
    long GastoSemMqlCentavos);

public sealed record AcaoDoAgente(DateTime Quando, string Tipo, string Texto, string? Estado, string? Por, string? Motivo);

public sealed record Ongoing(KpisDoMes Kpis, IReadOnlyList<LeadDaCentral> Leads, IReadOnlyList<AcaoDoAgente> Acoes, bool SemRegua, Aviso? AlertaBudget);

public sealed class Recomendacao
{
    public required string Id { get; init; }
    public required string ClienteSlug { get; init; }
    public DateTime CriadoEm { get; init; } = DateTime.Now;
    public string Nivel { get; init; } = "conjunto";
    public required string MetaId { get; init; }
    public required string Nome { get; init; }
    public required Acao Acao { get; init; }
    public required Decisao Decisao { get; init; }
    public string Texto { get; init; } = "";
    /// <summary>modelo | trilha: de onde veio o texto (gravado, 0146).</summary>
    public string TextoFonte { get; init; } = "trilha";
    public string VaultSha { get; init; } = "";
    public string ContagemMql { get; init; } = "regua";
    public EstadoDaRecomendacao Estado { get; set; } = EstadoDaRecomendacao.Pendente;
    public string? DecididaPor { get; set; }
    public DateTime? DecididaEm { get; set; }
    public string? Motivo { get; set; }
    public string? ResultadoMeta { get; set; }
}

public sealed record RodadaDoMotor(
    IReadOnlyList<Recomendacao> Recomendadas,
    int Gravadas,
    int Repetidas,
    IReadOnlyList<(string AdsetId, string Nome, Decisao Decisao)> Avaliados,
    IReadOnlyList<string> NaoAgiu,
    IReadOnlyList<string> Executadas,
    string? VaultSha);
