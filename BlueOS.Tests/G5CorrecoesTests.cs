using System.Text.Json;
using BlueOS.Core.Cs;
using Xunit;

namespace BlueOS.Tests;

/* As correções da revisão G5 (Agente de CS), nas regras puras que Cs.cs passou a chamar:
   o time que recebe a task (E-A), o silêncio do WhatsApp (E-B), o /me com squad repetido
   (E-C), o dossiê e o portão do Sem Performance (E-D) e o pilar com score em texto (E-H). */
public class G5CorrecoesTests
{
    const string Ghost = "ghost", Abyssal = "abyssal", Divers = "divers";

    /* ── E-A: pra quem a task do cliente pode ir ─────────────────────────────── */

    [Theory]
    [InlineData("global", false, "super_admin", "global")]
    [InlineData(null, true, "coordenador_departamento", "global")] // departamento conta como global (nivel-task.ts)
    [InlineData("tribo", false, "coordenador", "tribo")]
    [InlineData("squad", false, "head", "squad")]
    [InlineData("squad", false, "consultor_comercial", "squad")]
    [InlineData("squad", false, "gestor", "proprio")]
    [InlineData("squad", false, "operador", "proprio")]
    [InlineData("squad", false, "designer", "proprio")]
    [InlineData("squad", false, "editor", "proprio")]
    public void O_nivel_da_task_segue_a_regua_do_v1(string? escopo, bool departamento, string cargo, string esperado) =>
        Assert.Equal(esperado, CsRegras.NivelDaTask([new(escopo, departamento, cargo)]));

    [Fact]
    public void Sem_membro_ativo_o_nivel_e_proprio_e_o_melhor_vinculo_vence()
    {
        Assert.Equal(CsRegras.NivelProprio, CsRegras.NivelDaTask([]));
        Assert.Equal(CsRegras.NivelSquad, CsRegras.NivelDaTask([new("squad", false, "gestor"), new("squad", false, "head")]));
    }

    static readonly (string, bool)[] DoisSquadsVivos = [(Ghost, true), (Abyssal, true)];

    [Fact]
    public void Cliente_de_dois_squads_mostra_a_cada_head_so_o_time_dele()
    {
        Assert.Equal([Abyssal], CsRegras.SquadsQueRecebemATask(CsRegras.NivelSquad, DoisSquadsVivos, [Abyssal])!);
        Assert.Equal([Ghost], CsRegras.SquadsQueRecebemATask(CsRegras.NivelSquad, DoisSquadsVivos, [Ghost, Divers])!);
        Assert.Equal([Ghost, Abyssal], CsRegras.SquadsQueRecebemATask(CsRegras.NivelGlobal, DoisSquadsVivos, null)!);
        Assert.Equal([Ghost, Abyssal], CsRegras.SquadsQueRecebemATask(CsRegras.NivelTribo, DoisSquadsVivos, ["GHOST", "ABYSSAL"])!);
    }

    [Fact]
    public void Gestor_operador_designer_e_editor_e_head_de_outro_squad_recebem_lista_vazia()
    {
        Assert.Empty(CsRegras.SquadsQueRecebemATask(CsRegras.NivelProprio, DoisSquadsVivos, [Ghost])!);
        Assert.Empty(CsRegras.SquadsQueRecebemATask(CsRegras.NivelProprio, DoisSquadsVivos, null)!);
        Assert.Empty(CsRegras.SquadsQueRecebemATask(CsRegras.NivelSquad, DoisSquadsVivos, [Divers])!);
    }

    [Fact]
    public void Vinculo_encerrado_so_responde_quando_nao_sobrou_nenhum_vigente()
    {
        // Mudou do DIVERS para o GHOST: o head do DIVERS não atribui mais (E11b), o do GHOST sim.
        (string, bool)[] mudou = [(Divers, false), (Ghost, true)];
        Assert.Empty(CsRegras.SquadsQueRecebemATask(CsRegras.NivelSquad, mudou, [Divers])!);
        Assert.Equal([Ghost], CsRegras.SquadsQueRecebemATask(CsRegras.NivelSquad, mudou, [Ghost])!);
        // Churn: só vínculos encerrados, e o squad de onde saiu segue com o seletor.
        (string, bool)[] churn = [(Divers, false), (Ghost, false)];
        Assert.Equal([Divers], CsRegras.SquadsQueRecebemATask(CsRegras.NivelSquad, churn, [Divers])!);
    }

    [Fact]
    public void Cliente_sem_squad_cai_no_alcance_de_quem_pergunta()
    {
        Assert.Null(CsRegras.SquadsQueRecebemATask(CsRegras.NivelGlobal, [], null));
        Assert.Equal([Ghost], CsRegras.SquadsQueRecebemATask(CsRegras.NivelSquad, [], [Ghost])!);
        Assert.Empty(CsRegras.SquadsQueRecebemATask(CsRegras.NivelProprio, [], null)!);
    }

    /* ── E-B: a leitura do filtro quando o WhatsApp falha ────────────────────── */

    [Fact]
    public void Filtro_frio_sem_medicao_diz_que_nao_conseguiu_medir()
    {
        Assert.Equal(CsRegras.LeituraDoFiltroFrio, CsRegras.LeituraDoFiltro(true));
        Assert.StartsWith("NÃO CONSEGUI MEDIR", CsRegras.LeituraDoFiltro(false));
    }

    /* ── E-C: /me não cai com nome de squad repetido ─────────────────────────── */

    [Fact]
    public void Tribo_por_squad_com_nome_repetido_ou_vazio_nao_lanca_e_o_ultimo_vence()
    {
        var mapa = CsRegras.TriboPorSquad([("GHOST", "Mar"), ("GHOST", "Abismo"), ("", null), ("", "Costa"), ("DIVERS", null)]);
        Assert.Equal(3, mapa.Count);
        Assert.Equal("Abismo", mapa["GHOST"]);
        Assert.Equal("Costa", mapa[""]);
        Assert.Null(mapa["DIVERS"]);
    }

    /* ── E-D: qual squad o dossiê lê e quais abas ────────────────────────────── */

    sealed record V(string SquadId, bool Principal, bool Vigente) : CsRegras.IVinculoDoCliente;

    // Ordem do banco: principal primeiro, depois o mais recente.
    static readonly V[] Vinculos = [new(Ghost, true, true), new(Abyssal, false, true), new(Divers, false, false)];

    [Theory]
    [InlineData("abyssal", new[] { "ghost", "abyssal" }, "abyssal")] // pedido no alcance e vigente
    [InlineData("abyssal", new[] { "ghost" }, "ghost")]              // pedido fora do alcance: o principal
    [InlineData(null, new[] { "abyssal" }, "ghost")]                 // sem pedido: o principal, mesmo fora do alcance (V1)
    [InlineData("divers", new[] { "ghost", "divers" }, "ghost")]     // pedido de vínculo encerrado: o principal
    public void O_dossie_le_o_squad_pedido_so_se_ele_atende_hoje_e_esta_no_alcance(string? pedido, string[] alcance, string esperado)
    {
        var (noAlcance, squad, _) = CsRegras.EscolherSquadDoDossie(Vinculos, alcance, pedido);
        Assert.True(noAlcance);
        Assert.Equal(esperado, squad!.SquadId);
    }

    [Fact]
    public void As_abas_sao_os_vigentes_no_alcance_com_o_principal_primeiro()
    {
        Assert.Equal([Ghost, Abyssal], CsRegras.EscolherSquadDoDossie(Vinculos, null, null).Abas.Select(v => v.SquadId));
        Assert.Equal([Abyssal], CsRegras.EscolherSquadDoDossie(Vinculos, [Abyssal, Divers], null).Abas.Select(v => v.SquadId));
        V[] upsellPrimeiro = [new(Abyssal, false, true), new(Ghost, true, true)];
        Assert.Equal([Ghost, Abyssal], CsRegras.EscolherSquadDoDossie(upsellPrimeiro, null, null).Abas.Select(v => v.SquadId));
    }

    [Fact]
    public void Dossie_fora_do_alcance_e_sem_vigente()
    {
        // Só o vínculo encerrado com o DIVERS no alcance, havendo vigentes: fora (vínculo efetivo).
        Assert.False(CsRegras.EscolherSquadDoDossie(Vinculos, [Divers], null).NoAlcance);
        Assert.False(CsRegras.EscolherSquadDoDossie(Vinculos, [], null).NoAlcance);
        // Sem vigente nenhum (churn): o encerrado dá o alcance, mas não há squad nem aba.
        V[] churn = [new(Ghost, true, false), new(Divers, false, false)];
        var (noAlcance, squad, abas) = CsRegras.EscolherSquadDoDossie(churn, [Divers], Divers);
        Assert.True(noAlcance); Assert.Null(squad); Assert.Empty(abas);
    }

    /* ── E-D: o portão do Sem Performance ────────────────────────────────────── */

    static readonly (string, string?)[] Vigentes = [(Ghost, "GHOST"), (Abyssal, "ABYSSAL")];

    [Theory]
    [InlineData("head", new[] { "ghost" }, "ghost", 403, "coordenador")]          // tier 3
    [InlineData("gestor", new[] { "ghost" }, "ghost", 403, "coordenador")]        // tier 4
    [InlineData("coordenador", new[] { "divers" }, "ghost", 403, "squad fora")]   // squad fora do alcance
    [InlineData("coordenador", new[] { "divers" }, "divers", 403, "cliente fora")] // squad no alcance, cliente não
    [InlineData("coordenador", new[] { "ghost", "abyssal" }, "abyssal", 409, "sempre ligado")] // ABYSSAL é fixo
    public void O_portao_do_sem_performance_recusa_na_ordem_do_v1(string cargo, string[] alcance, string squad, int http, string trecho)
    {
        var recusa = CsRegras.PortaoDoSemPerformance([cargo], alcance, squad, Vigentes);
        Assert.NotNull(recusa);
        Assert.Equal(http, recusa!.Http);
        Assert.Contains(trecho, recusa.Erro);
    }

    [Fact]
    public void O_portao_do_sem_performance_deixa_passar_e_o_global_sem_vigente_cai_em_nao_atende()
    {
        Assert.Null(CsRegras.PortaoDoSemPerformance(["coordenador"], [Ghost], Ghost, Vigentes));
        Assert.Null(CsRegras.PortaoDoSemPerformance(["ceo"], null, "GHOST", Vigentes));
        var semVigente = CsRegras.PortaoDoSemPerformance(["ceo"], null, Ghost, []);
        Assert.Equal(403, semVigente!.Http); Assert.Contains("nao atende", semVigente.Erro);
        var restritoSemVigente = CsRegras.PortaoDoSemPerformance(["coordenador"], [Ghost], Ghost, []);
        Assert.Contains("cliente fora", restritoSemVigente!.Erro);
    }

    /* ── E-H: pilar com score em texto conta como fonte, como o Number() do V1 ─ */

    static string Sub(string score) =>
        """{"entrega":{"score":""" + score + ""","sem_dados":false},"performance":{"sem_dados":true},"relacionamento":{"sem_dados":true},"engajamento":{"sem_dados":true},"comercial":{"sem_dados":true}}""";

    [Theory]
    [InlineData("45", true)]
    [InlineData("\"45\"", true)]        // análise importada do V0
    [InlineData("\" 4.5e1 \"", true)]
    [InlineData("\"0x10\"", true)]
    [InlineData("\"\"", true)]          // Number("") === 0 no JS
    [InlineData("true", true)]
    [InlineData("\"45 pontos\"", false)]
    [InlineData("\"4,5\"", false)]
    [InlineData("\"Infinity\"", false)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    public void Score_conta_como_fonte_quando_o_js_le_um_numero_finito(string score, bool temFonte)
    {
        Assert.Equal(!temFonte, CsRegras.SemFonteNenhuma(CsRegras.LerSubScores(Sub(score))));
        var (nota, faixa) = CsRegras.NotaDaAnalise(20, 10, "baixo", Sub(score));
        Assert.Equal(temFonte, nota is not null);
        if (!temFonte) Assert.Equal("sem_base", faixa);
    }

    [Fact]
    public void Numero_finito_no_js_direto()
    {
        Assert.True(CsRegras.NumeroFinitoNoJs(JsonDocument.Parse("\"-12.5\"").RootElement));
        Assert.True(CsRegras.NumeroFinitoNoJs(JsonDocument.Parse("\".5\"").RootElement));
        Assert.False(CsRegras.NumeroFinitoNoJs(JsonDocument.Parse("\"1e400\"").RootElement));
        Assert.False(CsRegras.NumeroFinitoNoJs(JsonDocument.Parse("[45]").RootElement));
    }
}
