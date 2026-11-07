using BlueOS.Core.Modelos;
using BlueOS.Core.Panorama;

namespace BlueOS.Core.Api;

/// <summary>
/// O Panorama montado NO APP a partir da carteira da fonte (V1 e API .NET, enquanto a API não tem a rota
/// `/api/panorama`): os vistos ficam na memória da sessão e o placar sai zerado. Os sinais do CS, o pedido do
/// cliente e a automação pausada chegam quando a carteira da API passar a trazê-los (EXPERIENCE.md, "Data contract").
/// </summary>
public sealed class PanoramaLocal(string gestor)
{
    readonly List<Visto> _vistos = new();
    readonly List<Acendeu> _aCaminho = new();

    public async Task<PanoramaDoSquad> MontarAsync(IBlueApi api, string? squad)
    {
        var agora = DateTime.Now;
        var carteira = await api.ListarClientesAsync();
        var ultimaRodada = carteira.Select(c => c.UltimaRodada).Where(d => d is not null).Max();
        var sinais = new List<SinalDeVida>
        {
            new("carteira", Montador.HaQuanto(agora, agora), false),
            new("Meta (gasto, leads)", "pela carga diária do V1 (07:30 UTC)", false),
            new("agente de tráfego", ultimaRodada is null ? "sem rodada nesta sessão" : Montador.HaQuanto(ultimaRodada, agora, feminino: false).Replace("lido", "rodou"), false),
            new("grupos do CS", "ainda não chega ao app", false),
        };
        var placar = new Placar(gestor, 0, 0, 0, 0, 0, [], [], [], 0, 0, 0, _aCaminho.ToList());
        return Montador.Montar(carteira, squad, api.Parametros, agora, _vistos, sinais, placar);
    }

    public void RegistrarACaminho(IReadOnlyList<Cliente> carteira, ParametrosMotor p, string clienteSlug, string acao)
    {
        var c = carteira.FirstOrDefault(x => x.Slug == clienteSlug);
        if (c is null) return;
        var alertas = Diagnostico.DoCliente(c, p, DateTime.Now);
        _aCaminho.RemoveAll(a => a.Cliente == c.Nome);
        _aCaminho.Insert(0, new(DateTime.Now, c.Nome, alertas.Count > 0 ? Gravidades.Pontos(alertas[0].Gravidade) : 10, acao));
    }

    public void MarcarVisto(string clienteSlug, string assinatura, bool visto)
    {
        _vistos.RemoveAll(v => v.ClienteSlug == clienteSlug);
        if (visto) _vistos.Add(new Visto(clienteSlug, assinatura, DateTime.Now, gestor));
    }
}
