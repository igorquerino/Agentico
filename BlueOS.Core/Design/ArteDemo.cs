using System.Security;
using System.Text;
using BlueOS.Core.Modelos;

namespace BlueOS.Core.Design;

/* ============================================================================
   O AGENTE DESIGNER DE DEMONSTRAÇÃO: a peça de copy vira arte, em SVG.
   ----------------------------------------------------------------------------
   No V1 a imagem sai do modelo de imagem (lib/designer/openai.ts) e o texto é
   carimbado em camadas (texto-camada.ts), nos dois tamanhos da Meta: feed
   1080x1080 e story 1080x1920 (formato.ts). Aqui a diagramação é feita em SVG
   para o MVP andar sem chave de API: HEAD grande, SUB abaixo, CTA em botão e
   a marca do cliente no topo. O resultado é uma data URL que a tela e a
   publicação usam como a arte.
   ============================================================================ */
public static class ArteDemo
{
    static readonly (string A, string B)[] Paletas =
    [
        ("#0b1f3a", "#0e5fd8"), ("#101828", "#1d7a5f"), ("#1a1033", "#7c3aed"), ("#1f1302", "#c2410c"), ("#0f172a", "#0891b2"),
    ];

    public static Arte Gerar(Cliente c, PecaDeCopy peca, string formato, int lote, int indice)
    {
        var (largura, altura) = formato == "story" ? (1080, 1920) : (1080, 1080);
        var (corA, corB) = Paletas[(BlueOS.Core.Setup.Semente.De(c.Slug) + indice) % Paletas.Length];
        var marca = string.IsNullOrWhiteSpace(c.Produto.NomeCorreto) ? c.Nome : c.Produto.NomeCorreto!;
        var fonteHead = formato == "story" ? 78 : 64;
        var colunas = formato == "story" ? 20 : 24;
        var linhasHead = Quebrar(peca.Headline, colunas);
        var linhasSub = Quebrar(peca.Sub, formato == "story" ? 34 : 40);
        var topo = formato == "story" ? 420 : 250;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{largura}' height='{altura}' viewBox='0 0 {largura} {altura}'>");
        sb.Append($"<defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='{corA}'/><stop offset='1' stop-color='{corB}'/></linearGradient></defs>");
        sb.Append($"<rect width='{largura}' height='{altura}' fill='url(#g)'/>");
        sb.Append($"<circle cx='{largura - 140}' cy='{altura - 160}' r='260' fill='#ffffff' fill-opacity='0.06'/>");
        sb.Append($"<circle cx='120' cy='{altura - 80}' r='180' fill='#ffffff' fill-opacity='0.05'/>");
        /* a marca no topo (no V1 é a logo real carimbada; aqui é o nome) */
        sb.Append($"<rect x='72' y='72' width='18' height='54' rx='4' fill='#7dd3fc'/>");
        sb.Append($"<text x='110' y='116' font-family='Segoe UI, Inter, Arial, sans-serif' font-size='38' font-weight='700' fill='#ffffff' opacity='0.95'>{Esc(marca)}</text>");
        sb.Append($"<text x='{largura - 72}' y='116' text-anchor='end' font-family='Segoe UI, Inter, Arial, sans-serif' font-size='26' fill='#ffffff' opacity='0.6'>{peca.Etapa} · {Esc(peca.RotuloDoTipo)}</text>");
        /* HEAD */
        sb.Append($"<text x='72' y='{topo}' font-family='Segoe UI, Inter, Arial, sans-serif' font-size='{fonteHead}' font-weight='800' fill='#ffffff'>");
        for (var i = 0; i < linhasHead.Count; i++)
            sb.Append($"<tspan x='72' dy='{(i == 0 ? 0 : fonteHead + 10)}'>{Esc(linhasHead[i])}</tspan>");
        sb.Append("</text>");
        /* SUB */
        var ySub = topo + linhasHead.Count * (fonteHead + 10) + 40;
        sb.Append($"<text x='72' y='{ySub}' font-family='Segoe UI, Inter, Arial, sans-serif' font-size='34' fill='#e2e8f0'>");
        for (var i = 0; i < linhasSub.Count; i++)
            sb.Append($"<tspan x='72' dy='{(i == 0 ? 0 : 46)}'>{Esc(linhasSub[i])}</tspan>");
        sb.Append("</text>");
        /* CTA em botão */
        var yCta = Math.Min(altura - 200, ySub + linhasSub.Count * 46 + 70);
        var larguraCta = Math.Max(300, peca.Cta.Length * 22 + 80);
        sb.Append($"<rect x='72' y='{yCta}' width='{larguraCta}' height='88' rx='44' fill='#ffffff'/>");
        sb.Append($"<text x='{72 + larguraCta / 2}' y='{yCta + 56}' text-anchor='middle' font-family='Segoe UI, Inter, Arial, sans-serif' font-size='32' font-weight='700' fill='{corA}'>{Esc(peca.Cta)}</text>");
        sb.Append("</svg>");
        var dataUrl = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        return new Arte
        {
            Id = Guid.NewGuid().ToString("n"),
            PecaId = peca.Id,
            Etapa = peca.Etapa,
            Formato = formato,
            Titulo = $"{peca.Etapa} · peça {peca.Numero} · {formato} · lote {lote}",
            Url = dataUrl,
            Lote = lote,
        };
    }

    static List<string> Quebrar(string texto, int colunas)
    {
        var linhas = new List<string>();
        var atual = new StringBuilder();
        foreach (var palavra in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (atual.Length + palavra.Length + 1 > colunas && atual.Length > 0)
            {
                linhas.Add(atual.ToString());
                atual.Clear();
            }
            if (atual.Length > 0) atual.Append(' ');
            atual.Append(palavra);
        }
        if (atual.Length > 0) linhas.Add(atual.ToString());
        return linhas.Count == 0 ? [""] : linhas;
    }

    static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
