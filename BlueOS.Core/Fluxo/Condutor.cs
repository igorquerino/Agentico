using BlueOS.Core.Api;
using BlueOS.Core.Copy;
using BlueOS.Core.Design;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Setup;

namespace BlueOS.Core.Fluxo;

/* ============================================================================
   O CONDUTOR: o Start automático de um cliente, a partir do card.
   ----------------------------------------------------------------------------
   O gestor confirma três coisas (nicho, budget e a régua de MQL), escolhe o
   modo do agente e aperta Start. Daí o condutor percorre o mesmo fluxo da tela,
   na mesma ordem (Estagios.DoCliente), preenchendo o que não depende de gente:
     ficha    → grava o que foi confirmado
     copy     → escreve a copy das etapas que a faixa pede e aprova o que passa no crivo
     design   → desenha um criativo por etapa e põe cada um na vaga certa (validador)
     campanha → publica o Start na Meta, pausado
     modo     → liga o agente no modo escolhido
   O que só gente resolve (logo e assets, conta de anúncios, política de
   privacidade) PARA o condutor com a mensagem "precisa de você: ...". Ele não
   inventa: budget e nicho vêm do que o gestor confirmou, a régua do critério
   gravado ou do formulário da Meta, as demais informações da ficha que o
   onboarding do V1 já preencheu (briefing, contrato, call de start).
   ============================================================================ */
public sealed record PlanoDoStart(string? NichoSlug, decimal? BudgetMensal, ReguaV2? Regua, decimal? TetoMql, ModoDeExecucao Modo);
public sealed record PedidoDeStart(Cliente Cliente, PlanoDoStart Plano);

/// <summary>O que o condutor propõe antes do Start, e de onde tirou cada coisa.</summary>
public sealed record SugestaoDoStart(
    string? NichoSlug, string OrigemDoNicho,
    decimal? BudgetMensal, string OrigemDoBudget,
    ReguaV2? Regua, string OrigemDaRegua, FormularioDaMeta? Formulario,
    decimal? TetoMql);

public sealed class Condutor(IBlueApi api)
{
    /* ---- a proposta ------------------------------------------------------------ */

    public static SugestaoDoStart Sugerir(Cliente c, IReadOnlyList<FormularioDaMeta>? formularios)
    {
        string? nicho; string origemNicho;
        if (Nichos.Valido(c.NichoSlug)) { nicho = c.NichoSlug; origemNicho = "card Tráfego"; }
        else
        {
            nicho = Nichos.Sugerir(string.Join(' ', c.Icp.Setor, c.Icp.Segmento, c.Icp.Persona, c.Produto.Descricao, c.Produto.ModeloNegocio, c.Nome));
            origemNicho = nicho == "outro" ? "nada na ficha apontou um nicho: confirme" : "ICP e produto da ficha (onboarding)";
        }
        var (budget, origemBudget) = c.BudgetMensal is > 0
            ? (c.BudgetMensal, "card Tráfego")
            : ((decimal?)null, "nenhuma fonte traz o budget de ads: dinheiro nunca entra por chute, confirme");

        ReguaV2? regua = null; var origemRegua = "sem régua: sem formulário na página para propor"; FormularioDaMeta? formulario = null;
        if (c.Regua is { Perguntas.Count: > 0 }) { regua = c.Regua; origemRegua = "critério de MQL gravado"; }
        else if (formularios is { Count: > 0 })
        {
            formulario = formularios
                .Where(f => f.Perguntas.Any(p => p.Opcoes.Count > 0))
                .OrderBy(f => f.Status == "ACTIVE" ? 0 : 1).ThenByDescending(f => f.Leads).FirstOrDefault();
            if (formulario is not null) { regua = ProporRegua(formulario); origemRegua = $"proposta pelo formulário \"{formulario.Nome}\" ({formulario.Leads} leads)"; }
        }
        return new(nicho, origemNicho, budget, origemBudget, regua, origemRegua, formulario, c.TetoMql);
    }

    static readonly string[] Negativas = ["outro", "outra", "outros", "nao", "nenhum", "nenhuma", "estudante", "curiosidade", "curioso", "so pesquisando", "pesquisando", "ainda nao", "sem interesse", "nao tenho", "nao possuo", "nao sei"];

    /// <summary>Resposta que não qualifica: "Outro", "Não", "Estudante", "só pesquisando"... vira eliminatória; o resto, forte.
    /// É uma proposta: o gestor confere no card antes de gravar.</summary>
    public static bool RespostaNegativa(string valor)
    {
        var n = Regua.Normalizar(valor);
        return n.Length > 0 && Negativas.Any(x => n == x || n.StartsWith(x + " ") || n.StartsWith(x + "/") || n.StartsWith(x + ","));
    }

    public static ReguaV2 ProporRegua(FormularioDaMeta f)
    {
        var regua = new ReguaV2();
        foreach (var p in f.Perguntas.Where(x => x.Opcoes.Count > 0))
        {
            var q = new PerguntaDaRegua { Nome = p.Chave, Rotulo = p.Rotulo };
            foreach (var o in p.Opcoes)
            {
                var valor = string.IsNullOrWhiteSpace(o.Chave) ? o.Valor : o.Chave;
                if (RespostaNegativa(o.Valor)) q.Elim.Add(valor); else q.Forte.Add(valor);
            }
            regua.Perguntas.Add(q);
        }
        return regua;
    }

    /* ---- o Start ---------------------------------------------------------------- */

    public async Task ConduzirAsync(Cliente c, PlanoDoStart plano, Action<Cliente>? aoMudar = null, CancellationToken ct = default)
    {
        var e = c.Automacao;
        e.EmCurso = true; e.PrecisaDeVoce = null; e.Trilha.Clear(); e.IniciadoEm = DateTime.Now; e.TerminadoEm = null;
        void Passo(PassoDoStart p, string msg) { e.Passo = p; e.Mensagem = msg; e.Trilha.Add($"{DateTime.Now:HH:mm:ss} {msg}"); aoMudar?.Invoke(c); }
        void Pare(string oQueFalta) { e.PrecisaDeVoce = oQueFalta; e.Passo = PassoDoStart.Parou; e.Mensagem = "precisa de você: " + oQueFalta; e.Trilha.Add($"{DateTime.Now:HH:mm:ss} parou: {oQueFalta}"); }
        var p = api.Parametros;
        try
        {
            Passo(PassoDoStart.Lendo, "lendo o cliente inteiro");
            var cheio = await api.ObterClienteAsync(c.Slug);
            if (cheio is not null && !ReferenceEquals(cheio, c)) { cheio.Automacao = e; c = cheio; aoMudar?.Invoke(c); }

            /* 1. a ficha: o que o gestor confirmou no card */
            if (plano.NichoSlug != c.NichoSlug || plano.BudgetMensal != c.BudgetMensal)
            {
                Passo(PassoDoStart.Ficha, "gravando nicho e budget no card Tráfego");
                await api.SalvarCardTrafegoAsync(c, plano.NichoSlug, plano.BudgetMensal);
            }
            if (plano.Regua is { Perguntas.Count: > 0 } && (c.Regua is null || !ReferenceEquals(plano.Regua, c.Regua) || plano.TetoMql != c.TetoMql))
            {
                Passo(PassoDoStart.Ficha, "gravando o critério de MQL");
                await api.SalvarCriterioMqlAsync(c, plano.Regua, plano.TetoMql);
            }

            /* 2. o que só gente resolve: para e diz */
            var assets = Estagios.AssetsCompletos(c);
            if (!assets.Ok) { Pare("assets do cliente: " + string.Join(", ", assets.Pendencias)); return; }
            var ficha = Estagios.FichaCompleta(c);
            if (!ficha.Ok) { Pare("ficha: " + string.Join(", ", ficha.Pendencias)); return; }

            /* 3. a copy das etapas que a faixa pede */
            var exigidas = Estagios.EtapasExigidas(c, p);
            bool TemCopy(Etapa et) => c.Pecas.Any(x => x.Etapa == et && x.Aprovada && x.CrivoOk);
            var faltam = exigidas.Where(et => !TemCopy(et)).ToList();
            if (faltam.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                Passo(PassoDoStart.Copy, $"escrevendo a copy de {string.Join(", ", faltam)}");
                var lote = await api.GerarCopyAsync(c, faltam.Select(et => new GeradorDemo.Pedido(et, TipoPeca.Estatico, 3)).ToList(), null);
                /* aprovação é decisão de gente (AD-5): aqui quem decidiu foi o gestor ao apertar Start; só o que passou no crivo entra */
                foreach (var peca in lote.Pecas.Where(x => x.CrivoOk)) await api.AprovarPecaAsync(c, peca.Id, true);
                e.Trilha.Add($"{DateTime.Now:HH:mm:ss} copy: {lote.Pecas.Count(x => x.CrivoOk)} peça(s) aprovadas pelo crivo, {lote.Pecas.Count(x => !x.CrivoOk)} reprovada(s)");
                var ainda = exigidas.Where(et => !TemCopy(et)).ToList();
                if (ainda.Count > 0) { Pare($"a copy de {string.Join(", ", ainda)} não passou no crivo: ajuste na etapa Copy"); return; }
            }

            /* 4. um criativo por etapa, cada um na vaga certa */
            foreach (var et in exigidas)
            {
                ct.ThrowIfCancellationRequested();
                if (c.PilotoDa(et) is not null && Estagios.ValidarAssentos(c, p).First(a => a.Vaga == et).Ok) continue;
                Etapa EtapaDa(Arte a) => c.Pecas.FirstOrDefault(x => x.Id == a.PecaId)?.Etapa ?? a.Etapa;
                var arte = c.Artes.FirstOrDefault(a => a.Aprovada && EtapaDa(a) == et && a.Formato == "feed")
                        ?? c.Artes.FirstOrDefault(a => EtapaDa(a) == et && a.Formato == "feed");
                if (arte is null)
                {
                    var peca = c.Pecas.First(x => x.Etapa == et && x.Aprovada && x.CrivoOk);
                    Passo(PassoDoStart.Design, $"desenhando o criativo de {et} (1 a 3 min no Designer)");
                    var novas = await api.GerarArtesAsync(c, [peca.Id], ["feed"], TemplatesDoDesigner.Auto);
                    arte = novas.FirstOrDefault(a => a.Formato == "feed") ?? novas.FirstOrDefault();
                    if (arte is null) { Pare($"o Designer não devolveu a arte de {et}"); return; }
                }
                if (!arte.Aprovada) await api.AprovarArteAsync(c, arte.Id, true);
                await api.EscolherPilotoAsync(c, et, arte.Id);
                e.Trilha.Add($"{DateTime.Now:HH:mm:ss} piloto de {et}: {arte.Titulo}");
            }
            var errado = Estagios.ValidarAssentos(c, p).FirstOrDefault(a => !a.Ok);
            if (errado is not null) { Pare($"validador de assento: {errado.Motivo}"); return; }

            /* 5. o Start na Meta, pausado */
            if (!Estagios.CampanhaPublicada(c))
            {
                if (string.IsNullOrWhiteSpace(c.Conta.PoliticaUrl) && string.IsNullOrWhiteSpace(c.Conta.FormularioId))
                { Pare("a URL da política de privacidade do cliente (ou um formulário que já existe na página): a Meta exige para o formulário de cadastro. Está na etapa Campanha."); return; }
                ct.ThrowIfCancellationRequested();
                Passo(PassoDoStart.Campanha, "publicando o Start na Meta, tudo pausado");
                var pub = await api.PublicarAsync(c, new PedidoDePublicacao(c.Conta.PoliticaUrl, c.Conta.Link, c.Conta.FormularioId, null));
                if (!pub.Ok) { Pare($"o Publicar recusou [{pub.Codigo}]: {pub.Motivo}"); return; }
                e.Trilha.Add($"{DateTime.Now:HH:mm:ss} " + (pub.Simulado ? "campanha publicada (Meta simulada fora da produção)" : "campanha publicada na Meta, pausada"));
            }

            /* 6. o agente, no modo escolhido */
            Passo(PassoDoStart.Modo, $"ligando o agente no modo {Rotulo(plano.Modo)}");
            await api.MudarModoAsync(c, plano.Modo);
            e.Passo = PassoDoStart.Pronto; e.Mensagem = $"em automação, modo {Rotulo(plano.Modo)}";
            e.Trilha.Add($"{DateTime.Now:HH:mm:ss} pronto: agente no modo {Rotulo(plano.Modo)}");
        }
        catch (OperationCanceledException) { Pare("Start cancelado"); }
        catch (Exception ex) { Pare(ex.Message); }
        finally { e.EmCurso = false; e.TerminadoEm = DateTime.Now; aoMudar?.Invoke(c); }
    }

    public static string Rotulo(ModoDeExecucao m) => m switch { ModoDeExecucao.Sombra => "sombra", ModoDeExecucao.Executa => "automático", _ => "recomenda" };
}
