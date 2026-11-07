using System.Text.Json;
using BlueOS.Core.Design;
using BlueOS.Core.Modelos;
using Xunit;

namespace BlueOS.Tests;

/* O Designer sem rede: o rodízio de cenas, o tema (travas), o prompt da casa, a diagramação e o caminho no Storage. */
public class PromptDaArteTests
{
    static Cliente Acme()
    {
        var c = new Cliente { Id = "1", Slug = "acme", Nome = "Acme" };
        c.Produto.NomeCorreto = "Acme Pay"; c.Produto.Descricao = "conciliação automática de recebíveis"; c.Produto.FuncionalidadesChave = "concilia o extrato; avisa a divergência";
        c.Icp.CargoDecisor = "CFO"; c.Icp.Segmento = "SaaS de cobrança"; c.Icp.DoresPrincipais.Add("fecha o mês na planilha");
        return c;
    }

    [Fact]
    public void A_cena_e_ancorada_no_funil_e_no_numero_e_desloca_pelo_lote()
    {
        Assert.Equal("laptop", PromptDaArte.CenaDaPeca(1, "TOFU", 0, null));
        Assert.Equal("smartphone", PromptDaArte.CenaDaPeca(1, "MOFU", 0, null));   /* offset 3 */
        Assert.Equal("mockup-premium", PromptDaArte.CenaDaPeca(1, "BOFU", 0, null)); /* offset 5 */
        Assert.Equal("icp", PromptDaArte.CenaDaPeca(1, "TOFU", 1, null));
        Assert.Equal("objeto-simbolo", PromptDaArte.CenaDaPeca(2, "MOFU", 0, "objeto-simbolo"));
        Assert.Equal("comparativo", PromptDaArte.CenaDaPeca(4, "MOFU", 0, "livre")); /* livre não é cena: rodízio */
    }

    [Fact]
    public void O_objeto_do_tema_cai_nas_travas_do_V1()
    {
        Assert.Equal("device: celular", PromptDaArte.ObjetoProibido("um celular na mesa", null));
        Assert.Equal("objeto genérico: cofre", PromptDaArte.ObjetoProibido("um cofre aberto", null));
        Assert.Null(PromptDaArte.ObjetoProibido("um cofre aberto", "a Acme vende cofres inteligentes"));
        Assert.Equal("papel escrito: planilha", PromptDaArte.ObjetoProibido("uma planilha impressa", null));
        Assert.Equal("escrita: anotado", PromptDaArte.ObjetoProibido("um quadro de horários anotado à mão", null));
        Assert.Null(PromptDaArte.ObjetoProibido("um colete de freelancer largado na caixa, sem crachá", null));
        Assert.Null(PromptDaArte.ObjetoProibido("uma caixa de papel toalha vazia no balcão", null));
    }

    [Fact]
    public void O_tema_que_cita_a_copy_e_descartado_e_o_json_vira_tema()
    {
        var tema = PromptDaArte.LerTema("""{"oferta":"x","lugar":"y","tema":"o fechamento que atrasa","objeto":"uma calculadora apagada","cena":"a mesa do financeiro às 19h, luz fria"}""");
        Assert.NotNull(tema); Assert.Equal("uma calculadora apagada", tema!.Objeto);
        Assert.False(PromptDaArte.CitaCopy(tema, ["Feche o mês em um dia, sem planilha", "O ERP manda, o Acme concilia.", "Ver como funciona"]));
        var copiado = new TemaDaPeca("feche o mês em um dia sem planilha nenhuma", "x", "y");
        Assert.True(PromptDaArte.CitaCopy(copiado, ["Feche o mês em um dia, sem planilha", "", ""]));
        Assert.Null(PromptDaArte.LerTema("""{"tema":"só o tema"}"""));
        Assert.Contains("- Objeto em destaque: uma calculadora apagada", PromptDaArte.BlocoTemaDaPeca(tema, "completo"));
        Assert.DoesNotContain("Objeto", PromptDaArte.BlocoTemaDaPeca(tema, "assunto"));
        Assert.Equal(["uma calculadora apagada"], PromptDaArte.ObjetosDoLote(["...\n- O OBJETO desta peça: uma calculadora apagada.\n..."]));
    }

    [Fact]
    public void O_prompt_da_casa_e_sem_texto_com_faixa_calma_cena_paleta_e_cliente()
    {
        var c = Acme();
        var marca = new MarcaDaArte(["#002418", "#8dae24"], null, null, [], null);
        var copy = new CopyDaArte(1, "MOFU", "Feche o mês em um dia", "O ERP manda, o Acme concilia.", "Ver como funciona");
        var p = PromptDaArte.Montar(copy, c, marca, "feed", "laptop", new TemaDaPeca("o fechamento que atrasa", "uma calculadora", "a mesa do financeiro"));
        Assert.StartsWith("Crie um criativo estático publicitário no formato 1080 x 1350 px (4:5)", p);
        Assert.Contains("NÃO DESENHE NENHUM TEXTO", p);
        Assert.Contains(PromptDaArte.MarcaFaixaTopo, p);
        Assert.Contains("ELEMENTO PRINCIPAL — NOTEBOOK/LAPTOP", p);
        Assert.Contains("USE EXCLUSIVAMENTE ESTAS CORES DA MARCA", p); Assert.Contains("#002418, #8dae24", p);
        Assert.Contains("O CLIENTE — é pra ELE que esta peça é feita", p); Assert.Contains("- O que vende: Acme Pay — conciliação automática de recebíveis", p);
        Assert.Contains("ELEMENTOS DE APOIO", p); Assert.Contains("concilia o extrato; avisa a divergência", p);
        Assert.Contains("A MENSAGEM DESTA PEÇA", p); Assert.DoesNotContain("Objeto em destaque", p); /* laptop: só o assunto */
        Assert.Contains("ÁREA RESERVADA (fica VAZIA", p);
        /* a copy NÃO entra no prompt de imagem: ela é camada */
        Assert.DoesNotContain("Feche o mês em um dia", p); Assert.DoesNotContain("Ver como funciona", p);

        var foto = PromptDaArte.Montar(copy, c, marca, "story", "objeto-simbolo", new TemaDaPeca("t", "uma calculadora", "a mesa"), "fundo mais claro");
        Assert.Contains("1080 x 1920 px (9:16)", foto);
        Assert.Contains("ESTILO FOTOGRAFIA", foto); Assert.Contains("da cor #002418 da marca", foto);
        Assert.Contains("- O OBJETO desta peça: uma calculadora.", foto);
        Assert.EndsWith("AJUSTE PRIORITÁRIO DO GESTOR (aplicar sobre o resto): fundo mais claro", foto);
        Assert.Contains("SEM PALETA CADASTRADA", PromptDaArte.Montar(copy, c, null, "feed", "icp", null));
    }

    [Fact]
    public void A_diagramacao_cabe_na_peca_e_vira_camadas_de_texto()
    {
        var l = Diagramacao.Centralizado("feed", "Feche o mês em um dia, sem planilha e sem susto no caixa", "O ERP manda, o Acme concilia cada recebível antes do fechamento.", "Ver como funciona", "#ffffff", "#8dae24", "#8dae24", "#0b1220", "esquerda", ["Feche", "susto"]);
        Assert.Equal((1080, 1350), (l.Largura, l.Altura));
        Assert.Equal(3, l.Caixas.Count);
        foreach (var cx in l.Caixas)
        {
            Assert.InRange(cx.X, 0, 1); Assert.InRange(cx.Y, Diagramacao.FaixaCalmaTopo, 1); Assert.InRange(cx.X + cx.W, 0, 1.0001);
        }
        Assert.True(l.Caixa("headline")!.Linhas.Count is >= 1 and <= 3);
        Assert.True(l.Caixa("sub")!.Linhas.Count <= 3);
        Assert.Equal("centro", l.Caixa("cta")!.Alinhamento);
        Assert.True(l.Caixa("cta")!.Y > l.Caixa("sub")!.Y && l.Caixa("sub")!.Y > l.Caixa("headline")!.Y);
        Assert.Equal(["Feche", "susto"], l.Caixa("headline")!.Destaques);
        Assert.Equal((1080, 1920), (Diagramacao.Centralizado("story", "a", "b", "c", "#fff", "#000", "#000", "#fff").Largura, Diagramacao.Centralizado("story", "a", "b", "c", "#fff", "#000", "#000", "#fff").Altura));

        var json = PromptDaArte.TextoLayout(l, "#8dae24", "#8dae24");
        var itens = JsonDocument.Parse(json).RootElement;
        Assert.Equal(4, itens.GetArrayLength());
        Assert.Equal("headline", itens[0].GetProperty("papel").GetString());
        Assert.Equal("poppins", itens[0].GetProperty("fonte").GetString());
        Assert.Equal("seta", itens[2].GetProperty("icone").GetString());
        Assert.Equal("diagramacao-da-api", itens[3].GetProperty("tipo").GetString());
        Assert.Equal(["Feche", "susto"], Diagramacao.DestaquesDaHeadline("Feche o mês com susto zero").Take(1).Concat(["susto"]));
    }

    [Fact]
    public void O_nome_e_o_caminho_no_Storage_sao_os_do_V1()
    {
        Assert.Equal("Estatico_MOFU_-_Peca_2.png", PromptDaArte.NomeDoArquivo("mofu", 2, "feed"));
        Assert.Equal("Estatico_BOFU_-_Peca_1_-_Story.png", PromptDaArte.NomeDoArquivo("BOFU", 1, "story"));
        Assert.Equal("c342404c-7f24-4599-a817-aaf55ce37385/savana-app/lote-1-3de34eb2/Estatico_MOFU_-_Peca_2.png",
            PromptDaArte.CaminhoNoStorage("c342404c-7f24-4599-a817-aaf55ce37385", "savana-app", 1, "3de34eb2-0cc4-4bde-8c4a-48185ec51cb1", "MOFU", 2, "feed"));
        Assert.StartsWith("sem-squad/x/lote-3-abcdefgh/", PromptDaArte.CaminhoNoStorage(null, "x", 3, "abcdefgh-ijkl", "TOFU", 1, "feed"));
    }
}
