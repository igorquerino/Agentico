using BlueOS.Core.Copy;
using BlueOS.Core.Modelos;

namespace BlueOS.Core.Api;

/* ============================================================================
   A PORTA ÚNICA DA TELA PARA OS DADOS (plano de migração: "web e desktop
   falam só com a API"). Duas implementações:
     · DemoApi  — em memória, com o fluxo inteiro simulado (sem chave, sem rede);
     · V1Api    — o cliente tipado das rotas do V1 (Next.js), pela sessão da pessoa.
   A tela não sabe qual está por trás: ela injeta IBlueApi e pronto.
   ============================================================================ */
public sealed record ResultadoDaEstrutura(bool Ok, EstruturaProposta? Estrutura, string? Falta, string? Motivo);
public sealed record TextoDoAnuncio(string Headline, string Corpo, string Cta);
public sealed record PedidoDePublicacao(string? PoliticaUrl, string? Link, string? FormularioId, TextoDoAnuncio? Texto);
public sealed record ResultadoDaPublicacao(bool Ok, string? Codigo, string? Motivo, bool Simulado, Publicacao? Publicacao, string? Recado);
public sealed record ResultadoDaConta(bool Ok, string? Erro);
public sealed record EstadoDoToken(bool? Valido, string? Motivo);

public interface IBlueApi
{
    /// <summary>"Demonstração" ou "V1 · http://...": o que a tela mostra no rodapé.</summary>
    string Origem { get; }
    bool EhDemo { get; }
    /// <summary>Os parâmetros do motor em uso (o snapshot do vault).</summary>
    ParametrosMotor Parametros { get; }
    /// <summary>Disparado quando qualquer dado mudou: a tela relê.</summary>
    event Action? Mudou;

    Task<IReadOnlyList<Cliente>> ListarClientesAsync();
    Task<Cliente?> ObterClienteAsync(string slug);
    Task<Cliente> CriarClienteAsync(string nome, string squad);

    /* ---- ficha ------------------------------------------------------------- */
    Task SalvarFichaAsync(Cliente c);
    Task SalvarCardTrafegoAsync(Cliente c, string? nichoSlug, decimal? budgetMensal);
    Task SalvarCriterioMqlAsync(Cliente c, ReguaV2? regua, decimal? tetoMql);

    /* ---- assets ------------------------------------------------------------ */
    Task<Asset> SubirAssetAsync(Cliente c, TipoDeAsset tipo, string nome, string mime, byte[] bytes);
    Task RemoverAssetAsync(Cliente c, string assetId);
    Task<ResultadoDaConta> ConectarContaAsync(Cliente c, string? adAccountId, string? pageId, string? pageNome);
    Task SalvarContaAsync(Cliente c);
    Task<EstadoDoToken> ConferirTokenAsync(Cliente c);
    /// <summary>Grava o token Meta do SQUAD do cliente na fonte (no V1: POST /api/trafego/token-meta, que
    /// pergunta à Meta antes de gravar). O app só repassa: nunca guarda o token.</summary>
    Task<EstadoDoToken> GravarTokenDoSquadAsync(Cliente c, string token);

    /* ---- copy -------------------------------------------------------------- */
    Task<LoteDeCopy> GerarCopyAsync(Cliente c, IReadOnlyList<GeradorDemo.Pedido> pedido, string? briefing);
    Task AjustarPecaAsync(Cliente c, string pecaId, string instrucao);
    Task AprovarPecaAsync(Cliente c, string pecaId, bool aprovada);

    /* ---- design ------------------------------------------------------------ */
    Task<IReadOnlyList<Arte>> GerarArtesAsync(Cliente c, IReadOnlyList<string> pecaIds, IReadOnlyList<string> formatos);
    Task AprovarArteAsync(Cliente c, string arteId, bool aprovada);
    Task EscolherPilotoAsync(Cliente c, string? arteId);

    /* ---- campanha (Start) ---------------------------------------------------- */
    Task<ResultadoDaEstrutura> MontarEstruturaAsync(Cliente c);
    Task<ResultadoDaPublicacao> PublicarAsync(Cliente c, PedidoDePublicacao pedido);

    /* ---- automação (Ongoing) ------------------------------------------------- */
    Task<Ongoing> LerOngoingAsync(Cliente c);
    Task<RodadaDoMotor> RodarMotorAsync(Cliente c);
    Task<IReadOnlyList<Recomendacao>> ListarRecomendacoesAsync(Cliente c);
    Task<Recomendacao> DecidirAsync(Cliente c, string recomendacaoId, bool aceita, string? motivo);
    Task MudarModoAsync(Cliente c, ModoDeExecucao modo);
    Task<(bool Ligada, string? Motivo)> ChaveGeralAsync();
    Task MudarChaveGeralAsync(bool ligada, string? motivo);

    /// <summary>Só na demonstração: ativa a campanha e gera N dias de veiculação para o Ongoing ter o que ler.</summary>
    bool PodeSimularVeiculacao { get; }
    Task SimularVeiculacaoAsync(Cliente c, int dias);
}
