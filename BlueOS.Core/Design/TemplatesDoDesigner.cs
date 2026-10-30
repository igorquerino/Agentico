namespace BlueOS.Core.Design;

/* ============================================================================
   COMO O CRIATIVO SAI: o de-para do seletor do Agente Designer do V1.
   ----------------------------------------------------------------------------
   A rota /api/designer/gerar recebe UM id em `peca.template`, com quatro
   destinos (lib/designer/estilos-lista.ts):
     · "auto"            rodízio: o elemento visual gira por peça (padrão)
     · uma CENA          troca só o elemento visual (VARIANTS_CYCLE)
     · "centralizado"    o padrão da casa, explícito (não é cena do rodízio)
     · "livre"           sem layout: a IA compõe a peça do zero (modo-livre.ts)
   O prompt de cada id mora no servidor do V1; aqui só viaja o id. A lista é
   cópia de CENAS_META + MODO_LIVRE_META de 30/10/2026: se o V1 ganhar uma cena
   ou um layout de verdade (ESTILOS_META), entra aqui com o mesmo id.
   ============================================================================ */
public sealed record TemplateDoDesigner(string Id, string Nome, string Descricao, string Grupo);

public static class TemplatesDoDesigner
{
    public const string Auto = "auto";
    public const string Livre = "livre";

    public static readonly IReadOnlyList<TemplateDoDesigner> Lista =
    [
        new(Auto, "Auto (varia por peça)", "Sem cena fixa: o elemento visual gira por peça, para o lote não sair repetido. É o padrão.", "Cena"),
        new("laptop", "Notebook com a tela", "Notebook com a tela do produto.", "Cena"),
        new("smartphone", "Celular com a tela", "Celular com a tela do produto.", "Cena"),
        new("icp", "Pessoa do ICP", "Uma pessoa do público da marca em cena.", "Cena"),
        new("conceito-3d", "Conceito 3D", "Composição 3D abstrata do conceito.", "Cena"),
        new("objeto-simbolo", "Objeto símbolo", "Um objeto que simboliza a mensagem.", "Cena"),
        new("mockup-premium", "Mockup premium", "Mockup caro, com acabamento de estúdio.", "Cena"),
        new("comparativo", "Comparativo", "Antes e depois, lado a lado.", "Cena"),
        new("centralizado", "Centralizado (padrão da casa)", "Faixa de respiro no topo, headline, subheadline, CTA e o elemento visual na base. A cena vem do rodízio.", "Layout"),
        new(Livre, "Gerar livre", "Sem layout fixo: a IA compõe a peça do zero, uma diferente da outra. Copy, cores da marca e a faixa da logo continuam travadas.", "Livre"),
    ];

    public static TemplateDoDesigner PorId(string? id) => Lista.FirstOrDefault(t => t.Id == (id ?? Auto)) ?? Lista[0];
    public static string Nome(string? id) => PorId(id).Nome;
}
