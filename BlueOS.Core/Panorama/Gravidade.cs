namespace BlueOS.Core.Panorama;

/* ============================================================================
   A ORDEM DE ATENÇÃO DO PANORAMA, fechada com o Igor em 07/11/2026.
   ----------------------------------------------------------------------------
   1 automação parou  > 2 dinheiro > 3 risco de churn > 4 travado numa etapa
   > 5 pedido do cliente > 6 cliente frio. Dinheiro ficou ACIMA de churn
   ("perder um mês custa agora; o churn dá tempo de reagir") e pedido do
   cliente ABAIXO de travado ("um pedido espera um dia; uma etapa travada
   segura a geração de leads"). Só a gravidade 1 interrompe o gestor; o resto
   espera ele olhar o mapa.
   ============================================================================ */
public enum Gravidade
{
    AutomacaoParou = 1,
    Dinheiro = 2,
    Churn = 3,
    Travado = 4,
    Pedido = 5,
    Frio = 6,
}

public static class Gravidades
{
    public sealed record Descricao(Gravidade Gravidade, string Rotulo, string Curto, int Pontos, int Anel, string Cor);

    /// <summary>Anel 1 = centro (1-2), 2 = meio (3-4), 3 = borda interna (5-6); o anel 4 é dos saudáveis.</summary>
    public static readonly IReadOnlyList<Descricao> Lista =
    [
        new(Gravidade.AutomacaoParou, "automação parou", "parou", 60, 1, "#FF5C6C"),
        new(Gravidade.Dinheiro, "dinheiro", "dinheiro", 40, 1, "#FF7D6B"),
        new(Gravidade.Churn, "risco de churn", "churn", 30, 2, "#FFB454"),
        new(Gravidade.Travado, "travado numa etapa", "travado", 20, 2, "#FFD166"),
        new(Gravidade.Pedido, "pedido do cliente", "pedido", 10, 3, "#C3A4FF"),
        new(Gravidade.Frio, "cliente frio", "frio", 10, 3, "#8FA3D9"),
    ];

    public static Descricao De(Gravidade g) => Lista[(int)g - 1];
    public static int Pontos(Gravidade g) => De(g).Pontos;
    public static int Anel(Gravidade g) => De(g).Anel;
    public static string Rotulo(Gravidade g) => De(g).Rotulo;
    /// <summary>Só a gravidade 1 puxa o gestor fora do Panorama (faixa e sino em qualquer tela).</summary>
    public static bool Interrompe(Gravidade g) => g == Gravidade.AutomacaoParou;
}
