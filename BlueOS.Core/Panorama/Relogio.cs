using BlueOS.Core.Executor;

namespace BlueOS.Core.Panorama;

/* ============================================================================
   O RELÓGIO DO PANORAMA.
   ----------------------------------------------------------------------------
   `Agora` é a hora local, a menos que alguém fixe outra com `Fixar` (os testes
   da demonstração, que contam a história do "dia 7 de 30"). O valor fixo vale
   só para o fluxo lógico que o fixou (AsyncLocal): testes em paralelo não se
   atrapalham. E o dia do visto é o dia em Brasília (EXPERIENCE.md: o visto
   expira à meia-noite de Brasília), não o do fuso de quem hospeda.
   ============================================================================ */
public static class Relogio
{
    static readonly AsyncLocal<DateTime?> Fixo = new();

    public static DateTime Agora => Fixo.Value ?? DateTime.Now;

    /// <summary>Fixa a hora até o Dispose. Para teste e demonstração; a tela nunca fixa.</summary>
    public static IDisposable Fixar(DateTime agora)
    {
        var antes = Fixo.Value;
        Fixo.Value = agora;
        return new Desfazer(() => Fixo.Value = antes);
    }

    /// <summary>O dia, em Brasília, de um instante (Utc converte de UTC; Local e sem fuso, do fuso do host).</summary>
    public static DateOnly DiaEmBrasilia(DateTime instante) => DateOnly.FromDateTime(Fuso.NaConta(instante));

    sealed class Desfazer(Action desfazer) : IDisposable
    {
        public void Dispose() => desfazer();
    }
}
