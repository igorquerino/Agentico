using BlueOS.Core.Fluxo;
using BlueOS.Core.Modelos;

namespace BlueOS.Core.Panorama;

/// <summary>O botão do card da carteira do Start diz a AÇÃO que resolve, não "abrir". (EXPERIENCE.md do Start, Voice and Tone.)</summary>
public sealed record AcaoDoCard(string Rotulo, Estagio Etapa, string? Resolve);

/// <summary>Os três trabalhos do Start: começar (cliente novo), terminar (travado no meio do fluxo) ou arrumar (em automação com problema).</summary>
public enum Trabalho { Comecar, Terminar, Arrumar }

public static class AcoesDoCard
{
    public static Trabalho TrabalhoDe(Cliente c, Estagio estagio) =>
        Estagios.CampanhaPublicada(c) ? Trabalho.Arrumar : estagio == Estagio.Chegada ? Trabalho.Comecar : Trabalho.Terminar;

    public static string RotuloDoTrabalho(Trabalho t) => t switch { Trabalho.Comecar => "começar", Trabalho.Terminar => "terminar", _ => "arrumar" };

    /// <summary>A ação que resolve o alerta principal (ou, sem alerta, a pendência do estágio).</summary>
    public static AcaoDoCard De(Cliente c, Estagio estagio, Alerta? principal, int decisoesPendentes)
    {
        if (principal is { } a)
        {
            switch (a.Gravidade)
            {
                case Gravidade.AutomacaoParou:
                    if (a.Assinatura.StartsWith("token-vencido")) return new("Trocar o token", Estagio.Assets, "trocar o token do squad");
                    if (a.Assinatura.StartsWith("publicacao-erro")) return new("Publicar de novo", Estagio.Campanha, "corrigir e publicar de novo");
                    return new("Religar a automação", Estagio.Automacao, "religar a automação do cliente");
                case Gravidade.Dinheiro:
                    if (a.Assinatura.StartsWith("budget")) return new("Rever o budget", Estagio.Automacao, "rever o budget ou pausar");
                    return new(decisoesPendentes > 0 ? $"Ver as {decisoesPendentes} decisões" : "Ver as recomendações", Estagio.Automacao, "olhar as recomendações do agente");
                case Gravidade.Churn:
                    return estagio == Estagio.Chegada ? new("▶ Start", Estagio.Chegada, "dar o Start e falar com o cliente") : new("Falar com o cliente", a.EtapaDoStart == Estagio.Chegada ? estagio : a.EtapaDoStart, "falar com o cliente hoje");
                case Gravidade.Travado:
                    return DoEstagio(c, estagio);
                case Gravidade.Pedido:
                    return new("Responder pelo CS", estagio, "responder o cliente");
                case Gravidade.Frio:
                    return new("Mandar mensagem", estagio, "mandar uma mensagem no grupo");
            }
        }
        if (decisoesPendentes > 0) return new($"Ver as {decisoesPendentes} decisões", Estagio.Automacao, "aceitar ou negar as decisões do agente");
        return DoEstagio(c, estagio);
    }

    static AcaoDoCard DoEstagio(Cliente c, Estagio estagio) => estagio switch
    {
        Estagio.Chegada => new("▶ Start", Estagio.Chegada, "dar o Start: o condutor preenche o que consegue"),
        Estagio.Assets => !c.ContaConectada ? new("Conectar a conta", Estagio.Assets, "conectar a conta de anúncios")
                        : !c.TemLogo ? new("Subir a logo", Estagio.Assets, "subir a logo principal")
                        : new("Conferir o token", Estagio.Assets, "conferir o token do squad"),
        Estagio.Ficha => new("Completar a ficha", Estagio.Ficha, "nicho, budget, régua e teto"),
        Estagio.Copy => c.Lotes.Count == 0 ? new("Gerar a copy", Estagio.Copy, "gerar o primeiro lote") : new("Aprovar a copy", Estagio.Copy, "aprovar uma peça por etapa"),
        Estagio.Design => c.Artes.Count == 0 ? new("Gerar as artes", Estagio.Design, "gerar as artes das peças aprovadas") : new("Escolher os pilotos", Estagio.Design, "um piloto por etapa, no assento certo"),
        Estagio.Campanha => new("Publicar a campanha", Estagio.Campanha, "publicar a estrutura na Meta, pausada"),
        _ => new("Abrir a automação", Estagio.Automacao, null),
    };
}
