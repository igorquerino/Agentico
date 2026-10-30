using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Setup;

namespace BlueOS.Core.Fluxo;

/* ============================================================================
   O FLUXO ÚNICO DO GESTOR DE TRÁFEGO: em que estágio o cliente está.
   ----------------------------------------------------------------------------
   Chegada → Assets → Ficha → Copy → Design → Campanha → Automação.
   O estágio é o PRIMEIRO passo incompleto. Cada verificação devolve também as
   pendências em texto, que é o que o card e o stepper mostram.
   ============================================================================ */
public sealed record DescricaoDoEstagio(Estagio Estagio, string Titulo, string Curto, string Frase);

public static class Estagios
{
    public static readonly IReadOnlyList<DescricaoDoEstagio> Lista =
    [
        new(Estagio.Chegada, "Chegada do cliente", "Chegada", "O cliente entrou. O primeiro passo é cadastrar os assets."),
        new(Estagio.Assets, "Assets do cliente", "Assets", "Logo, telas do produto, conta de anúncios, página e o token do squad."),
        new(Estagio.Ficha, "Ficha do cliente", "Ficha", "O que o agente precisa: o card Tráfego (nicho e budget) e o critério de MQL (o formulário da Meta e o teto). Cadastro, ICP, produto e estratégia ficam aqui também, sem travar o fluxo."),
        new(Estagio.Copy, "Copy por nicho", "Copy", "O agente escreve as peças de TOFU, MOFU e BOFU que a faixa do budget pede. Você revisa e aprova."),
        new(Estagio.Design, "Design dos criativos", "Design", "O agente gera as artes das peças aprovadas. Você escolhe o criativo piloto."),
        new(Estagio.Campanha, "Campanha (Start)", "Campanha", "A estrutura sai pronta pelo budget e pelo nicho, e vai para a Meta pausada."),
        new(Estagio.Automacao, "Automação contínua", "Automação", "O agente lê a conta pela API da Meta, decide pelas regras e você valida."),
    ];

    public static DescricaoDoEstagio Descricao(Estagio e) => Lista[(int)e];

    public static (bool Ok, List<string> Pendencias) AssetsCompletos(Cliente c)
    {
        var p = new List<string>();
        if (!c.TemLogo) p.Add("logo principal");
        if (!c.ContaConectada) p.Add("conta de anúncios");
        if (string.IsNullOrWhiteSpace(c.Conta.PageId)) p.Add("página do Facebook");
        if (c.Conta.TokenValido != true) p.Add("token do squad conferido");
        return (p.Count == 0, p);
    }

    /// <summary>O que trava o fluxo na ficha: só o card Tráfego e o critério de MQL (Igor, 29/10/2026).
    /// Cadastro, ICP, produto e estratégia continuam na ficha, mas não seguram o agente.</summary>
    public static (bool Ok, List<string> Pendencias) FichaCompleta(Cliente c)
    {
        var p = new List<string>();
        if (!Nichos.Valido(c.NichoSlug)) p.Add("nicho");
        if (c.BudgetMensal is null or <= 0) p.Add("budget mensal");
        if (c.Regua is null || c.Regua.Perguntas.Count == 0) p.Add("régua de MQL");
        if (c.TetoMql is null or <= 0) p.Add("teto por MQL");
        return (p.Count == 0, p);
    }

    /// <summary>As etapas do funil que a faixa do budget pede: é o que o Copy gera e o que a campanha monta.</summary>
    public static IReadOnlyList<Etapa> EtapasExigidas(Cliente c, ParametrosMotor p)
    {
        if (c.BudgetMensal is null or <= 0) return [Etapa.MOFU];
        return Faixa.EtapasDaFaixa(Faixa.DoBudget(Dinheiro.CentavosDe(c.BudgetMensal.Value), p.Faixas));
    }

    public static (bool Ok, List<string> Pendencias) CopyPronta(Cliente c, ParametrosMotor p)
    {
        var pend = new List<string>();
        if (c.Lotes.Count == 0) { pend.Add("nenhum lote gerado"); return (false, pend); }
        foreach (var et in EtapasExigidas(c, p))
            if (!c.Pecas.Any(x => x.Etapa == et && x.Aprovada && x.CrivoOk)) pend.Add($"peça aprovada de {et}");
        return (pend.Count == 0, pend);
    }

    /// <summary>Design pronto = um criativo piloto para CADA etapa que a faixa do budget pede.</summary>
    public static (bool Ok, List<string> Pendencias) DesignPronto(Cliente c, ParametrosMotor p)
    {
        var pend = new List<string>();
        if (c.Artes.Count == 0) pend.Add("nenhuma arte gerada");
        else if (!c.Artes.Any(a => a.Aprovada)) pend.Add("nenhuma arte aprovada");
        foreach (var et in EtapasExigidas(c, p))
            if (c.PilotoDa(et) is null) pend.Add($"criativo piloto de {et}");
        return (pend.Count == 0, pend);
    }

    public static bool CampanhaPublicada(Cliente c) => c.Publicacao is { Estado: "ok" };

    public static Estagio DoCliente(Cliente c, ParametrosMotor p)
    {
        if (c.Assets.Count == 0 && !c.ContaConectada) return Estagio.Chegada;
        if (!AssetsCompletos(c).Ok) return Estagio.Assets;
        if (!FichaCompleta(c).Ok) return Estagio.Ficha;
        if (!CopyPronta(c, p).Ok) return Estagio.Copy;
        if (!DesignPronto(c, p).Ok) return Estagio.Design;
        if (!CampanhaPublicada(c)) return Estagio.Campanha;
        return Estagio.Automacao;
    }

    /// <summary>As pendências do estágio atual, em texto, para o card.</summary>
    public static List<string> PendenciasDoEstagio(Cliente c, ParametrosMotor p) => DoCliente(c, p) switch
    {
        Estagio.Chegada => ["cadastrar os assets"],
        Estagio.Assets => AssetsCompletos(c).Pendencias,
        Estagio.Ficha => FichaCompleta(c).Pendencias,
        Estagio.Copy => CopyPronta(c, p).Pendencias,
        Estagio.Design => DesignPronto(c, p).Pendencias,
        Estagio.Campanha => ["publicar a estrutura na Meta"],
        _ => [],
    };
}
