namespace BlueOS.Core.Setup;

/* ============================================================================
   SEMENTE ESTAVEL para a demonstracao.
   ----------------------------------------------------------------------------
   `string.GetHashCode()` muda a cada processo no .NET (hash aleatorizado), entao
   a copy de demonstracao, as artes e a veiculacao simulada saiam diferentes a
   cada abertura do app e os testes ficavam intermitentes (o crivo reprovava a
   peca de BOFU numa rodada e aprovava na outra). FNV-1a de 32 bits e o mesmo
   numero em qualquer maquina e em qualquer execucao.
   ============================================================================ */
public static class Semente
{
    public static int De(string texto)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var ch in texto) { h ^= ch; h *= 16777619; }
            return (int)(h & 0x7fffffff);
        }
    }
}
