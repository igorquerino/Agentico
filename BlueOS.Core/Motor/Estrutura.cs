using BlueOS.Core.Modelos;
using BlueOS.Core.Setup;

namespace BlueOS.Core.Motor;

/* ============================================================================
   A ESTRUTURA PROPOSTA: o que a tela mostra depois do Start.
   ----------------------------------------------------------------------------
   Porta de lib/trafego/motor/planejador/estrutura.ts. Aqui o plano só ganha
   NOME e FORMA para a tela; a única conta nova é o MQL esperado.

   Convenção de nomes do Gerenciador (artefato de 06/10):
     campanha  [Blue] [BR] [ABO] [MOFU] [LEADS] [FORM-L] 06 Outubro | Estáticos
     conjunto  1 · Advantage+ (amplo) · imobiliário
     anúncio   AN · 1 · Advantage+ (amplo) · imobiliário
   ============================================================================ */
public static class Estrutura
{
    const int DiasNoMes = 30;
    static readonly Etapa[] OrdemDoFunil = [Etapa.TOFU, Etapa.MOFU, Etapa.BOFU];
    static readonly string[] Meses =
    [
        "Janeiro", "Fevereiro", "Março", "Abril", "Maio", "Junho",
        "Julho", "Agosto", "Setembro", "Outubro", "Novembro", "Dezembro",
    ];

    static readonly Dictionary<string, (string Curto, string Nome)> RotuloDoPublicoMapa = new()
    {
        ["advantage"] = ("Advantage+", "Advantage+ (amplo)"),
        ["interesses_nicho"] = ("Interesses do nicho", "Interesses do nicho"),
        ["interesses_alternativos"] = ("Interesses alternativos", "Interesses alternativos"),
        ["lookalike"] = ("Lookalike da lista", "Lookalike da lista"),
        ["interesses_decisor"] = ("Interesses de decisor", "Interesses de decisor"),
        ["personalizado_lista"] = ("Lista do cliente", "Remarketing (lista do cliente)"),
        ["interesses_amplos"] = ("Interesses amplos", "Interesses amplos"),
        ["interesses_amplos_e_site_ou_tag"] = ("Interesses amplos e site", "Interesses amplos e site"),
    };

    public static (string Curto, string Nome) RotuloDoPublico(string p)
    {
        if (RotuloDoPublicoMapa.TryGetValue(p, out var r)) return r;
        var t = p.Replace('_', ' ').Trim();
        var legivel = t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..];
        return (legivel, legivel);
    }

    /// <summary>O nome da campanha, com as etapas na ordem do funil e a data de Brasília.</summary>
    public static string NomeDaCampanha(IEnumerable<Etapa> etapas, DateOnly quando)
    {
        var lista = etapas.ToList();
        var ordenadas = OrdemDoFunil.Where(lista.Contains);
        return $"[\U0001F30A Blue] [BR] [ABO] [{string.Join("+", ordenadas)}] [LEADS] [FORM-L] {quando.Day:00} {Meses[quando.Month - 1]} | Estáticos";
    }

    public static EstruturaProposta Proposta(Plano plano, long budgetMensalCentavos, ParametrosMotor p, decimal? tetoMql, string? nichoSlug, DateOnly quando)
    {
        var nicho = nichoSlug is null ? null : Nichos.Rotulo(nichoSlug).ToLowerInvariant();
        var contagem = new Dictionary<Etapa, int> { [Etapa.TOFU] = 0, [Etapa.MOFU] = 0, [Etapa.BOFU] = 0 };

        var conjuntos = plano.Conjuntos.Select(c =>
        {
            contagem[c.Etapa] += 1;
            var k = contagem[c.Etapa];
            var numero = c.Etapa == Etapa.TOFU ? "0" : c.Etapa == Etapa.BOFU ? $"5.{k}" : k.ToString();
            var r = RotuloDoPublico(c.Publico);
            var nome = string.Join(" · ", new[] { numero, r.Nome, nicho }.Where(x => !string.IsNullOrEmpty(x)));
            return new ConjuntoProposto($"{c.Etapa} {k}", c.Etapa, c.Publico, r.Curto, c.DiarioCentavos, nome, $"AN · {nome}", c.Idade, c.AdvantageAudience);
        }).ToList();

        var etapas = OrdemDoFunil.Where(et => conjuntos.Any(c => c.Etapa == et)).ToList();
        var alocado = conjuntos.Sum(c => c.DiarioCentavos);
        var valores = conjuntos.Select(c => c.DiarioCentavos).ToList();

        /* MQL esperado = gasto planejado do mês ÷ teto por MQL do PPL. Sem teto, não há conta (PEND-003). */
        var teto = (double)(tetoMql ?? 0);
        int? mqlEsperado = conjuntos.Count > 0 && teto > 0
            ? (int)Math.Floor((alocado * DiasNoMes) / 100.0 / teto)
            : null;

        return new EstruturaProposta(
            FaixaId: plano.Faixa,
            FaixaRotulo: Faixa.Rotulo(plano.Faixa, p.Faixas),
            Rotulo: conjuntos.Count > 0 ? $"1-{conjuntos.Count}-1 · {string.Join(" + ", etapas)}" : "sem estrutura",
            BudgetMensalCentavos: budgetMensalCentavos,
            DiarioCentavos: plano.DiarioCentavos,
            AlocadoDiarioCentavos: alocado,
            Conjuntos: conjuntos,
            PorConjuntoMin: valores.Count > 0 ? valores.Min() : null,
            PorConjuntoMax: valores.Count > 0 ? valores.Max() : null,
            Reservado: plano.Reservado,
            NaoAlocadoCentavos: plano.NaoAlocadoCentavos,
            Avisos: plano.Avisos,
            MqlEsperado: mqlEsperado,
            NomeDaCampanha: conjuntos.Count > 0 ? NomeDaCampanha(etapas, quando) : null,
            ChavesUsadas: plano.ChavesUsadas,
            VaultSha: p.VaultSha);
    }

    /// <summary>Monta a estrutura de um cliente a partir do setup (publicar/estrutura-do-cliente.ts, simplificado).</summary>
    public static EstruturaProposta Montar(decimal budgetMensal, bool temLista, bool temSiteOuTag, decimal? tetoMql, string? nichoSlug, ParametrosMotor p, DateOnly quando)
    {
        var centavos = Dinheiro.CentavosDe(budgetMensal);
        var plano = Planejador.Planejar(new EntradaDoPlano(centavos, "estrategia", temLista, temSiteOuTag), p);
        return Proposta(plano, centavos, p, tetoMql, nichoSlug, quando);
    }
}
