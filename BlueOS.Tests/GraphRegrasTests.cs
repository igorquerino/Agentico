using BlueOS.Core.Meta;
using Xunit;

namespace BlueOS.Tests;

/* As regras da Graph sem rede (lib/trafego/aplicar-meta.ts, criar-meta.ts, graph.ts). */
public class GraphRegrasTests
{
    [Theory]
    [InlineData(190, null, ClasseDoErro.Credencial)]
    [InlineData(17, null, ClasseDoErro.Temporario)]
    [InlineData(100, null, ClasseDoErro.Permanente)]
    [InlineData(250, null, ClasseDoErro.Permanente)]
    [InlineData(null, "User request limit reached: too many calls", ClasseDoErro.Temporario)]
    [InlineData(999, "qualquer coisa", ClasseDoErro.Desconhecido)]
    public void A_classe_do_erro_sai_do_codigo_e_da_frase(int? codigo, string? msg, ClasseDoErro esperada) =>
        Assert.Equal(esperada, GraphRegras.ClasseDe(codigo, msg));

    [Fact]
    public void O_header_BUC_pede_espera_no_limite_e_pelos_minutos_declarados()
    {
        Assert.Equal(5 * 60_000, GraphRegras.EsperaPeloBUCMs("{\"123\":[{\"call_count\":100,\"total_cputime\":10,\"total_time\":10}]}"));
        Assert.Equal(3 * 60_000, GraphRegras.EsperaPeloBUCMs("{\"123\":[{\"call_count\":40,\"estimated_time_to_regain_access\":3}]}"));
        Assert.Equal(0, GraphRegras.EsperaPeloBUCMs("{\"123\":[{\"call_count\":40}]}"));
        Assert.Equal(0, GraphRegras.EsperaPeloBUCMs("isso não é json"));
        Assert.Equal(0, GraphRegras.EsperaPeloBUCMs(null));
    }

    [Fact]
    public void A_escrita_fora_da_producao_exige_a_variavel_E_a_conta_na_lista()
    {
        Assert.False(GraphRegras.EscritaLiberada(false, "123", "act_123"));
        Assert.False(GraphRegras.EscritaLiberada(true, "", "act_123"));
        Assert.False(GraphRegras.EscritaLiberada(true, null, "act_123"));
        Assert.True(GraphRegras.EscritaLiberada(true, "act_123, 456", "act_456"));
        Assert.True(GraphRegras.EscritaLiberada(true, "123", "act_123"));
        Assert.False(GraphRegras.EscritaLiberada(true, "123", "act_789"));
    }

    [Fact]
    public void O_corpo_de_cada_acao_e_o_da_porta_do_V1()
    {
        var pausar = GraphRegras.Montar("pause_adset", "120000", new Dictionary<string, object?>(), "act_1")!.Value;
        Assert.Equal("120000", pausar.Caminho);
        Assert.Equal("PAUSED", pausar.Corpo["status"]);

        var copia = GraphRegras.Montar("duplicate_adset", "120000", new Dictionary<string, object?>(), "1")!.Value;
        Assert.Equal("120000/copies", copia.Caminho);
        Assert.Equal("true", copia.Corpo["deep_copy"]);
        Assert.Equal("PAUSED", copia.Corpo["status_option"]);

        var verba = GraphRegras.Montar("set_daily_budget", "120000", new Dictionary<string, object?> { ["daily_budget_cents"] = 2500 }, "1")!.Value;
        Assert.Equal("2500", verba.Corpo["daily_budget"]);

        var conjunto = GraphRegras.Montar("create_adset", null, new Dictionary<string, object?> { ["name"] = "x", ["targeting"] = new Dictionary<string, object> { ["age_min"] = 25 } }, "1")!.Value;
        Assert.Equal("act_1/adsets", conjunto.Caminho);
        Assert.Equal("PAUSED", conjunto.Corpo["status"]);
        Assert.Equal("{\"age_min\":25}", conjunto.Corpo["targeting"]);

        Assert.Null(GraphRegras.Montar("apagar_tudo", "1", new Dictionary<string, object?>(), "1"));
    }

    [Fact]
    public void O_orcamento_pedido_e_lido_por_campo_e_nao_por_tipo()
    {
        Assert.Equal(3000, GraphRegras.OrcamentoPedido(new Dictionary<string, object?> { ["daily_budget"] = "3000" }));
        Assert.Equal(4000, GraphRegras.OrcamentoPedido(new Dictionary<string, object?> { ["lifetime_budget_cents"] = 4000 }));
        Assert.Null(GraphRegras.OrcamentoPedido(new Dictionary<string, object?> { ["name"] = "x" }));
    }

    [Fact]
    public void O_muro_do_app_em_desenvolvimento_e_decidido_pelo_subcode()
    {
        Assert.True(GraphRegras.EhAppEmDesenvolvimento(new ErroDaMeta("x", 100, 1885183)));
        Assert.True(GraphRegras.EhAppEmDesenvolvimento(new ErroDaMeta("App is in development mode", 100, null)));
        Assert.False(GraphRegras.EhAppEmDesenvolvimento(new ErroDaMeta("App is in development mode", 100, 5)));
        Assert.False(GraphRegras.EhAppEmDesenvolvimento(new ErroDaMeta("Invalid parameter", 100, null)));
        Assert.False(GraphRegras.EhAppEmDesenvolvimento(new InvalidOperationException("rede")));
    }

    [Fact]
    public void A_frase_da_Meta_nao_repete_o_titulo_e_poe_a_dica_do_App_Mode()
    {
        /* o título que é a primeira frase da mensagem repetida sai (print do gestor, 21/09) */
        Assert.Equal("Orçamento baixo demais para o conjunto", GraphRegras.FraseDaMeta("Invalid parameter", "Orçamento baixo demais para o conjunto", "Orçamento baixo demais"));
        Assert.Equal("Sem permissão — Você não administra esta conta", GraphRegras.FraseDaMeta("(#200) Permissions error", "Você não administra esta conta", "Sem permissão"));
        Assert.Equal("Invalid parameter", GraphRegras.FraseDaMeta("Invalid parameter", null, null));
        Assert.Equal("a Meta recusou", GraphRegras.FraseDaMeta(null, null, null));
        Assert.Contains("App Mode → Live", GraphRegras.FraseDaMeta("O app está em modo de desenvolvimento", null, null));
    }
}
