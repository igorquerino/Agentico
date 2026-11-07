using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueOS.Api.Infra;
using BlueOS.Core.Api;
using BlueOS.Core.Copy;
using BlueOS.Core.Meta;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Publicar;
using BlueOS.Core.Setup;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   PUBLICAR O START: a estrutura da tela vira campanha, formulário, conjuntos e
   anúncios na Meta, tudo PAUSADO. Porta de lib/trafego/publicar/start.ts e
   peca.ts (PR D da Automação, 12/10/2026).
   ----------------------------------------------------------------------------
   O QUE SOBE: 1 campanha ABO OUTCOME_LEADS; 1 formulário de cadastro criado DA
   RÉGUA v2 (ou um existente da página); N conjuntos, um por conjunto do
   planejador; N anúncios, cada um com a arte do piloto da SUA etapa e o texto
   da peça de copy daquela arte, congelado em `trafego_anuncio_peca`.

   POR QUE É IDEMPOTENTE: cada passo grava em `trafego_start_publicacao` o id
   que a Meta deu, debaixo do HASH da estrutura (o mesmo JSON do V1). O segundo
   clique acha a linha e RETOMA de onde parou; a estrutura já publicada volta
   como `ja_publicada`.

   ENSAIO ANTES DE CADA ESCRITA: campanha, conjunto e anúncio passam por
   `validate_only`. Formulário e criativo não ensaiam (a API não oferece).

   FORA DA PRODUÇÃO (AD-MT-20): esta API só fala com a homologação, então o
   Publicar faz o ensaio e PARA com `simulado: true`, salvo a conta de teste
   liberada em Meta:ContasTeste com Meta:EscritaForaDaProd, ou o squad liberado
   em Meta:Mcp:Squads. Nada aqui ativa nada.

   PELO MCP OFICIAL DA META (31/10/2026): o cliente de um squad em Mcp:Squads
   cria a campanha, os conjuntos e os anúncios (com o criativo inline) pelas
   ferramentas `ads_create_*` do MCP; a arte sobe pela URL do Storage
   (`ads_creative_upload_media`); o formulário da régua continua na Graph com
   o token da página (o MCP não cria formulário). O MCP cria em RASCUNHO: o
   último passo é `ads_activate_entity` com `publish_as_active = false`, que
   tira tudo do rascunho PAUSADO; a retomada descobre o que ainda é rascunho
   perguntando à Graph se o id existe ao vivo.
   ============================================================================ */
public sealed class Publicador(Db db, MetaGraph graph, MetaMcp mcp, MetaEscrita meta, Registro registro, ILogger<Publicador> log)
{
    static readonly JsonSerializerOptions Snake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNameCaseInsensitive = true,
    };
    const string Colunas = "id::text as id, estado, campanha_id, formulario_id, imagem_hash, conjuntos::text as conjuntos, anuncios::text as anuncios, avisos::text as avisos, erro, cliente_slug, estrutura_hash, atualizado_em::text as atualizado_txt";

    sealed record SituacaoDaCampanha(string F, string? Status, string? Motivo);
    sealed record Pub(bool Ok, PublicacaoPreparada? Preparada, string? Codigo, string? Motivo);

    /* ---- a publicação --------------------------------------------------------- */

    public async Task<ResultadoDoStart> PublicarAsync(Cliente c, PedidoDoStart p, Guid por)
    {
        var inicio = DateTime.UtcNow;
        var r = await PublicarDeFatoAsync(c, p, por);
        await registro.TrilhaAsync("criou_campanha", por, c.Slug, r.Ok ? "ok" : r.Simulado ? "ok" : "erro", r.Ok ? null : r.Motivo, "campanha",
            r.Ok ? r.CampanhaId : r.Parcial?.CampanhaId, null,
            r.Ok ? new { via = "automacao_start", rota = r.Via, conjuntos = r.Conjuntos.Count, anuncios = r.Anuncios.Count, ja_publicada = r.JaPublicada } : new { via = "automacao_start", rota = r.Via, codigo = r.Codigo, simulado = r.Simulado },
            (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
        if (r.Ok)
            r.Recado = r.SemCriativo
                ? (r.JaPublicada ? "esta estrutura já tinha sido publicada: é a mesma campanha." : "a estrutura nasceu PAUSADA. O criativo e o anúncio você monta no Gerenciador.")
                : (r.JaPublicada ? "esta estrutura já tinha sido publicada: é a mesma campanha." : "tudo nasceu PAUSADO. Reveja no Gerenciador antes de ativar.");
        return r;
    }

    async Task<ResultadoDoStart> PublicarDeFatoAsync(Cliente c, PedidoDoStart p, Guid por)
    {
        var hoje = DateOnly.FromDateTime(DateTime.Now);
        var slug = c.Slug;
        var parametros = ParametrosMotor.PadraoDoVault;

        /* ---- 1. a estrutura, a MESMA do Start ------------------------------------ */
        if (!c.ContaConectada) return ResultadoDoStart.Falha("sem_conta", "este cliente não tem registro de tráfego: conecte a conta de anúncios no Setup antes de publicar.");
        if (c.BudgetMensal is null or <= 0) return ResultadoDoStart.Falha("falta_setup", "sem budget mensal de ads: preencha no card Tráfego do cliente para o Start montar a estrutura.");
        var m = Estrutura.Montar(c.BudgetMensal.Value, c.Conta.TemLista, c.Conta.TemSiteOuTag, c.TetoMql, Nichos.Valido(c.NichoSlug) ? c.NichoSlug : null, parametros, hoje);
        if (m.Conjuntos.Count == 0) return ResultadoDoStart.Falha("sem_estrutura", "a estrutura não tem conjunto para publicar.");
        var (cred, erroCred) = await meta.CredenciaisDoClienteAsync(slug);
        if (cred is null) return ResultadoDoStart.Falha("sem_conta", erroCred ?? "sem conta de anúncio");
        /* A ROTA: o squad em Meta:Mcp:Squads escreve pelo MCP oficial da Meta; os outros pela Graph. */
        var peloMcp = cred.PeloMcp;
        var via = peloMcp ? "mcp" : "graph";
        var conversa = McpRegras.NovaConversa();
        var comuns = McpRegras.Comuns(conversa, McpRegras.Criacao, $"publicar a estrutura do Start do cliente {slug}: a campanha de cadastro, os conjuntos e os anúncios, tudo pausado");
        ResultadoDoStart Falha(string codigo, string motivo, string? hash = null, bool simulado = false, RegistroDoStart? parcial = null)
        {
            var f = ResultadoDoStart.Falha(codigo, motivo, hash, simulado, parcial); f.Via = via; return f;
        }

        /* A ETAPA DE CADA CONJUNTO, escolhida no passo 3 (26/10): valor fora de TOFU/MOFU/BOFU mantém a do planejador. */
        var conjuntosComEtapa = m.Conjuntos.Select((cj, i) =>
        {
            var pedida = (p.Etapas is { } e && i < e.Count ? e[i] ?? "" : "").ToUpperInvariant().Trim();
            return Enum.TryParse<Etapa>(pedida, out var et) && et != cj.Etapa ? cj with { Etapa = et, Rotulo = System.Text.RegularExpressions.Regex.Replace(cj.Rotulo, "^(TOFU|MOFU|BOFU)", et.ToString()) } : cj;
        }).ToList();
        var trocouEtapa = conjuntosComEtapa.Where((cj, i) => cj.Etapa != m.Conjuntos[i].Etapa).Any();
        var interessesDoConjunto = conjuntosComEtapa.Select((cj, i) => cj.Publico == "advantage" ? new List<InteresseDaMeta>() : StartRegras.InteressesLimpos(p.Interesses is { } li && i < li.Count ? li[i] : null)).ToList();
        var campanhaRecomendada = trocouEtapa ? Estrutura.NomeDaCampanha(conjuntosComEtapa.Select(x => x.Etapa), hoje) : m.NomeDaCampanha;

        /* AS ARTES, UMA POR CONJUNTO: pela lista na ordem (ciclo), ou pelo piloto de cada etapa (o desktop). Lista
           vazia é caminho válido (#1325): a publicação de estrutura, com o criativo ficando para o Gerenciador. */
        var artes = p.DesignerPecaIds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        string? ArteDoConjunto(int i)
        {
            if (p.Pilotos is { Count: > 0 } pil)
            {
                var et = conjuntosComEtapa[i].Etapa.ToString();
                return pil.TryGetValue(et, out var id) && !string.IsNullOrWhiteSpace(id) ? id.Trim() : null;
            }
            return artes.Count == 0 ? null : artes[i % artes.Count];
        }
        if (p.Pilotos is { Count: > 0 })
        {
            var faltando = conjuntosComEtapa.Select((cj, i) => (cj.Etapa, Arte: ArteDoConjunto(i))).Where(x => x.Arte is null).Select(x => x.Etapa.ToString()).Distinct().ToList();
            if (faltando.Count > 0) return ResultadoDoStart.Falha("sem_arte", $"escolha o criativo piloto de {string.Join(" e ", faltando)} no passo Design antes de publicar.");
            artes = conjuntosComEtapa.Select((_, i) => ArteDoConjunto(i)!).Distinct().ToList();
        }
        if (artes.Count > conjuntosComEtapa.Count) artes = artes.Take(conjuntosComEtapa.Count).ToList();
        var semCriativo = artes.Count == 0;

        /* OS NOMES DO PASSO 2 (tarefa #5001). */
        var finais = StartRegras.NomesFinais(campanhaRecomendada ?? $"[\U0001F30A Blue] {slug}", conjuntosComEtapa.Select(x => (x.Nome, x.Anuncio)).ToList(), p.Nomes);
        if (StartRegras.FaltaNosNomes(finais) is { } faltaNome) return ResultadoDoStart.Falha(faltaNome.Codigo, faltaNome.Motivo);
        var conjuntos = conjuntosComEtapa.Select((cj, i) => cj with { Nome = finais.Conjuntos[i], Anuncio = finais.Anuncios[i] }).ToList();
        var nomeDaCampanha = finais.Campanha;

        /* ---- 2. a página e o formulário ------------------------------------------- */
        var pageId = await PageIdDoClienteAsync(c, cred);
        if (string.IsNullOrEmpty(pageId))
            return Falha("sem_pagina", "não achei a página do Facebook do cliente: o cadastro não tem (page_id em trafego_contas) e nenhum anúncio da conta aponta para uma. O anúncio de cadastro precisa da página que o assina.");

        /* PELO MCP, ANTES DE QUALQUER COISA: a conta tem de estar no token e habilitada no MCP (lançamento gradual), e a
           página tem de ter aceitado os termos de anúncio de cadastro (o conjunto LEAD_GENERATION é recusado sem isso). */
        if (peloMcp)
        {
            try
            {
                var (serve, motivoDaConta, _) = await meta.ContaServeNoMcpAsync(cred, conversa);
                if (!serve) return Falha("sem_mcp", motivoDaConta + " Nada foi criado.");
                var (paginaOk, motivoDaPagina) = McpRegras.PaginaServe(await mcp.PaginasAsync(cred.Token, cred.Act, conversa), pageId);
                if (!paginaOk) return Falha(motivoDaPagina.Contains("termos") ? "sem_tos_leadgen" : "sem_pagina", motivoDaPagina + " Nada foi criado.");
            }
            catch (Exception e) { return Falha("sem_mcp", $"não consegui conferir a conta no MCP da Meta ({MetaGraph.Sanear(e.Message)}). Nada foi criado."); }
        }
        var formularioId = string.IsNullOrWhiteSpace(p.FormularioId) ? null : p.FormularioId.Trim();
        var politicaUrl = string.IsNullOrWhiteSpace(p.PoliticaUrl) ? null : p.PoliticaUrl.Trim();
        var linkPedido = string.IsNullOrWhiteSpace(p.Link) ? null : p.Link.Trim();
        if (formularioId is null && (c.Regua is null || c.Regua.Perguntas.Count == 0))
            return ResultadoDoStart.Falha("sem_regua", "o cliente não tem régua de MQL: é dela que o formulário é criado. Preencha no card do cliente, ou escolha um formulário existente da página.");
        if (StartRegras.FaltaNoPedido(linkPedido, politicaUrl, formularioId) is { } falta) return ResultadoDoStart.Falha(falta.Codigo, falta.Motivo);

        /* O formulário informado é conferido na Meta ANTES de qualquer escrita, com o token da página primeiro. Só
           quando a escrita está liberada: fora disso nada é escrito de qualquer jeito. */
        var escritaLiberada = meta.EscritaLiberada(cred);
        string? politicaDoFormulario = null;
        if (formularioId is not null && escritaLiberada)
        {
            var tokens = new List<string>();
            try { var pg = await graph.GetAsync($"{pageId}?fields=access_token", cred.Token); if (MetaGraph.S(pg, "access_token") is { } pt) tokens.Add(pt); } catch { }
            tokens.Add(cred.Token);
            JsonElement? doFormulario = null; var recusa = "";
            foreach (var campos in new[] { "id,page,privacy_policy_url", "id,page" })
            {
                foreach (var tk in tokens)
                {
                    try { doFormulario = await graph.GetAsync($"{formularioId}?fields={campos}", tk); break; }
                    catch (Exception e) { recusa = MetaGraph.Sanear(e.Message); }
                }
                if (doFormulario is not null) break;
            }
            if (doFormulario is null) return ResultadoDoStart.Falha("sem_formulario", $"não consegui ler o formulário {formularioId} na Meta ({recusa}): confira o id. Nada foi criado.");
            var f = doFormulario.Value;
            if (string.IsNullOrEmpty(MetaGraph.S(f, "id"))) return ResultadoDoStart.Falha("sem_formulario", $"o formulário {formularioId} não existe nesta conta. Nada foi criado.");
            var daPagina = f.TryGetProperty("page", out var pg2) ? MetaGraph.S(pg2, "id") : null;
            if (!string.IsNullOrEmpty(daPagina) && daPagina != pageId)
                return ResultadoDoStart.Falha("sem_formulario", $"o formulário {formularioId} é de outra página ({daPagina}), e o anúncio é assinado pela página {pageId}: a Meta recusaria o conjunto. Nada foi criado.");
            var pol = MetaGraph.S(f, "privacy_policy_url");
            if (!string.IsNullOrWhiteSpace(pol)) politicaDoFormulario = pol.Trim();
        }

        /* ---- 3. o texto de cada anúncio, ANTES de qualquer escrita ------------------ */
        var publicacoes = new List<PublicacaoPreparada>();
        if (!semCriativo)
        {
            for (var i = 0; i < conjuntos.Count; i++)
            {
                var pub = await PrepararPublicacaoAsync(c, conjuntos[i].Anuncio, conjuntos[i].Etapa, ArteDoConjunto(i), p.Texto);
                if (!pub.Ok) return ResultadoDoStart.Falha("publicacao_recusada", pub.Motivo ?? "a peça não pode subir");
                publicacoes.Add(pub.Preparada!);
            }
        }
        var link = StartRegras.DestinoDoAnuncio(linkPedido, politicaUrl ?? politicaDoFormulario, pageId);
        if (!StartRegras.EhLinkExterno(link))
            return ResultadoDoStart.Falha("sem_link", "o anúncio de cadastro precisa levar a um site externo, e não achei nenhum: o formulário escolhido não traz política de privacidade com URL. Escolha outro formulário, ou crie o formulário da régua e informe o endereço. Nada foi criado.");

        /* ---- 4. o hash e o registro ------------------------------------------------ */
        var paraHash = conjuntos.Select((cj, i) => (cj.Nome, cj.Etapa.ToString(), cj.Publico, cj.DiarioCentavos, (string?)(string.Join("+", interessesDoConjunto[i].Select(x => x.Id)) is { Length: > 0 } s ? s : null))).ToList();
        var hash = StartRegras.HashDaEstrutura(m.VaultSha, artes.Count > 0 ? string.Join(",", artes) : null, publicacoes.Count > 0 ? publicacoes[0].Linha.TextoHash : "sem-criativo", formularioId, paraHash);
        var existente = await AcharRegistroAsync(slug, hash);
        if (existente is { Estado: "parcial", CampanhaId: not null })
        {
            var sit = await SituacaoDaCampanhaAsync(cred, existente.CampanhaId);
            if (sit.F == "encerrada" && await EncerrarRegistroAsync(existente, sit.Status ?? "")) existente = await AcharRegistroAsync(slug, hash);
        }
        if (existente is { Estado: "ok" or "sem_criativo", CampanhaId: not null, FormularioId: not null })
            return new ResultadoDoStart
            {
                Ok = true, Via = via, JaPublicada = true, CampanhaId = existente.CampanhaId, FormularioId = existente.FormularioId, Conjuntos = existente.Conjuntos, Anuncios = existente.Anuncios,
                Link = StartRegras.LinkDoGerenciador(cred.Act, existente.CampanhaId), Avisos = existente.Avisos, EstruturaHash = hash, SemCriativo = existente.Estado == "sem_criativo",
            };
        if (existente?.CampanhaId is null)
        {
            var pelaMetade = await PublicacaoPelaMetadeAsync(cred, slug, hash);
            if (pelaMetade is { } pm)
            {
                var naMeta = pm.Meta.F == "nao_sei" ? $" Não consegui conferir na Meta se ela ainda existe ({pm.Meta.Motivo})." : $" Na Meta ela está {pm.Meta.Status}.";
                return ResultadoDoStart.Falha("em_andamento",
                    $"este cliente tem uma publicação pela metade (campanha {pm.Linha.CampanhaId}{(pm.Linha.Erro is not null ? $", parou em: {pm.Linha.Erro}" : "")}).{naMeta} Publicar outra estrutura agora criaria uma segunda campanha ao lado dela. Para desistir dela, arquive a campanha {pm.Linha.CampanhaId} no Gerenciador e publique de novo: o painel confere na Meta e encerra a publicação pela metade. Para terminá-la, publique a MESMA estrutura (mesma arte, texto, formulário e verba).",
                    hash, parcial: pm.Linha);
            }
        }
        var corpoDaCampanha = StartRegras.CorpoDaCampanha(nomeDaCampanha);
        var act = cred.Act; var token = cred.Token;

        /* ---- 5. fora da produção, só o ensaio ------------------------------------- */
        var pelaRota = peloMcp ? " pelo MCP oficial da Meta (conta habilitada no MCP, página com os termos de lead aceitos)" : "";
        if (!escritaLiberada)
        {
            try { await graph.PostAsync($"{act}/campaigns", corpoDaCampanha, token, validarSo: true); }
            catch (Exception e) { return Falha("ensaio", $"fora da produção: o ensaio da campanha na Meta falhou ({MetaGraph.Sanear(e.Message)}). Nada foi criado.", hash, simulado: true); }
            try { await graph.PostAsync($"{act}/adcreatives", StartRegras.CorpoDoEnsaioDoCriativo(nomeDaCampanha, pageId, link), token, validarSo: true); }
            catch (Exception e)
            {
                var muro = GraphRegras.EhAppEmDesenvolvimento(e);
                var peloPost = muro && !semCriativo && !peloMcp && await meta.TemEscopoDePostAsync(token);
                return Falha("ensaio", muro && peloMcp
                    ? $"fora da produção: a campanha passou no ensaio e o criativo foi recusado pelo App Mode do app do squad, mas pelo MCP o criativo é criado pelo app da própria Meta e o muro não se aplica. Em produção a campanha, o formulário, os {conjuntos.Count} conjunto(s) e os anúncios seriam criados pelo MCP e publicados pausados. Nada foi criado."
                    : peloPost
                    ? $"fora da produção: a campanha passou no ensaio e o criativo foi recusado pelo App Mode. Em produção a campanha, o formulário e os {conjuntos.Count} conjunto(s) seriam criados e pausados, e cada anúncio nasceria de um post não publicado na página do cliente, com o formulário no botão do criativo. Nada foi criado."
                    : muro
                    ? $"fora da produção: a campanha passou no ensaio e o criativo foi recusado pelo App Mode. Em produção a campanha, o formulário e os {conjuntos.Count} conjunto(s) seriam criados e pausados; o criativo e o anúncio ficam para o Gerenciador. Nada foi criado."
                    : $"fora da produção: a campanha passou no ensaio, mas o CRIATIVO foi recusado ({MetaGraph.Sanear(e.Message)}). Em produção esta publicação pararia aqui. Nada foi criado.", hash, simulado: true);
            }
            if (semCriativo) return Falha("sem_arte", "esta conta sobe criativo: escolha a arte do anúncio. Nada foi criado.", hash);
            return Falha("ensaio", $"fora da produção: a campanha \"{nomeDaCampanha}\" e o criativo passaram no ensaio da Meta{pelaRota}, com {conjuntos.Count} conjunto(s) a criar. Nada foi criado: a escrita só acontece com Meta:EscritaForaDaProd ligado e a conta {GraphRegras.SemAct(act)} em Meta:ContasTeste, ou o squad {cred.Squad ?? "?"} em Meta:Mcp:Squads.", hash, simulado: true);
        }

        /* ---- 6. valendo, passo a passo, gravando cada id --------------------------- */
        var daVez = await RegistroDaVezAsync(existente, slug, hash, m.VaultSha, por);
        if (daVez is null)
            return Falha("em_andamento", "esta mesma estrutura já está sendo publicada agora (outro clique, outra aba ou outra pessoa). Espere alguns minutos e confira no Gerenciador antes de publicar de novo: publicar junto criaria a campanha duas vezes.", hash);
        var reg = daVez;
        var rascunhos = new HashSet<string>();
        async Task<ResultadoDoStart> Parou(string motivo)
        {
            var criouFormulario = reg.FormularioId is not null && reg.FormularioId != formularioId;
            var criouAlgo = reg.CampanhaId is not null || criouFormulario || reg.ImagemHash is not null;
            reg.Estado = criouAlgo ? "parcial" : "erro"; reg.Erro = motivo;
            await GravarRegistroAsync(reg);
            var sufixo = criouAlgo
                ? (peloMcp ? " O que já foi criado está no rascunho do Gerenciador (ou pausado na conta); publicar de novo a mesma estrutura retoma de onde parou e publica o rascunho." : " O que já foi criado ficou pausado na conta; publicar de novo a mesma estrutura retoma de onde parou.")
                : " Nada foi criado na conta.";
            return Falha("meta", motivo + sufixo, hash, parcial: reg);
        }

        /* ---- o ensaio do criativo, antes de qualquer escrita ------------------------ */
        var criativoNaMao = false; var viaPostDaPagina = false;
        try
        {
            await graph.PostAsync($"{act}/adcreatives", StartRegras.CorpoDoEnsaioDoCriativo(nomeDaCampanha, pageId, link), token, validarSo: true);
            if (semCriativo)
            {
                const string motivo = "esta conta sobe criativo: escolha a arte do anúncio. Nada foi criado.";
                reg.Estado = "erro"; reg.Erro = motivo; await GravarRegistroAsync(reg);
                return Falha("sem_arte", motivo, hash);
            }
        }
        catch (Exception e)
        {
            if (!GraphRegras.EhAppEmDesenvolvimento(e)) return await Parou($"criativo: {MetaGraph.Sanear(e.Message)}");
            /* Pelo MCP o criativo é criado pelo app da própria Meta: o muro do App Mode do app do squad não se aplica. */
            if (peloMcp && !semCriativo) { }
            else
            {
                var jaPeloPost = reg.Anuncios.Any(x => !string.IsNullOrEmpty(x.PostId));
                if (!semCriativo && (jaPeloPost || await meta.TemEscopoDePostAsync(token))) viaPostDaPagina = true; else criativoNaMao = true;
            }
        }

        /* a campanha é criada DEPOIS do formulário e da arte; o ensaio dela vem antes de tudo */
        if (reg.CampanhaId is null)
        {
            try { await graph.PostAsync($"{act}/campaigns", corpoDaCampanha, token, validarSo: true); }
            catch (Exception e) { return await Parou($"campanha: {MetaGraph.Sanear(e.Message)}"); }
        }

        /* formulário */
        if (reg.FormularioId is null)
        {
            if (formularioId is not null) reg.FormularioId = formularioId;
            else
            {
                try
                {
                    var pg = await graph.GetAsync($"{pageId}?fields=access_token", token);
                    var tokenDaPagina = MetaGraph.S(pg, "access_token") ?? "";
                    if (tokenDaPagina == "") return await Parou("formulário: o token não administra a página do cliente. Crie o formulário no Gerenciador e escolha-o aqui como existente.");
                    var f = await graph.PostAsync($"{pageId}/leadgen_forms", new Dictionary<string, string>
                    {
                        ["name"] = StartRegras.NomeDoFormulario(hoje),
                        ["questions"] = StartRegras.Json(StartRegras.PerguntasDoFormulario(c.Regua!)),
                        ["privacy_policy"] = StartRegras.Json(new Dictionary<string, string> { ["url"] = politicaUrl! }),
                        ["follow_up_action_url"] = link, ["locale"] = "pt_BR",
                    }, tokenDaPagina);
                    reg.FormularioId = MetaGraph.S(f.Json, "id");
                    if (string.IsNullOrEmpty(reg.FormularioId)) return await Parou("formulário: a Meta não devolveu o id.");
                }
                catch (Exception e) { return await Parou($"formulário: {MetaGraph.Sanear(e.Message)}"); }
            }
            await GravarRegistroAsync(reg);
        }

        /* as artes na biblioteca da conta, uma subida por arte distinta */
        var hashPorArte = new Dictionary<string, string>();
        if (reg.ImagemHash is not null && artes.Count > 0) hashPorArte[artes[0]] = reg.ImagemHash;
        async Task<byte[]> BytesDaArte(string pecaId)
        {
            var url = await db.UmAsync(Sql.Fmt($"select storage_url from public.designer_pecas where id = {Guid.Parse(pecaId)}"), x => x.S("storage_url"));
            return await graph.LerImagemAsync(url);
        }
        async Task<string> HashDaArte(string pecaId)
        {
            if (hashPorArte.TryGetValue(pecaId, out var pronto)) return pronto;
            string? h = null;
            /* pelo MCP, a arte sobe pela URL pública do Storage; se o MCP recusar, os bytes vão pela Graph */
            if (peloMcp)
            {
                var url = await db.UmAsync(Sql.Fmt($"select storage_url from public.designer_pecas where id = {Guid.Parse(pecaId)}"), x => x.S("storage_url"));
                if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var up = await mcp.ChamarAsync("ads_creative_upload_media", McpRegras.ArgsDaImagem(act, url, $"{nomeDaCampanha} — arte {pecaId[..8]}", comuns), token, 120);
                    if (up.Ok) h = McpRegras.HashDaImagem(up.Dados);
                    else log.LogWarning("o MCP não subiu a arte {Peca} ({Erro}); vai pela Graph", pecaId, up.Erro);
                }
            }
            if (string.IsNullOrEmpty(h))
            {
                var bytes = await BytesDaArte(pecaId);
                var up = await graph.PostAsync($"{act}/adimages", new Dictionary<string, string> { ["bytes"] = Convert.ToBase64String(bytes) }, token, segundos: 90);
                if (up.Json.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Object)
                    foreach (var img in imgs.EnumerateObject()) { h = MetaGraph.S(img.Value, "hash"); if (h is not null) break; }
            }
            if (string.IsNullOrEmpty(h)) throw new InvalidOperationException("a Meta aceitou a imagem mas não devolveu o hash.");
            hashPorArte[pecaId] = h;
            return h;
        }
        if (!semCriativo)
            foreach (var pecaId in conjuntos.Select((_, i) => ArteDoConjunto(i)!).Distinct())
            {
                try { var h = await HashDaArte(pecaId); if (reg.ImagemHash is null) { reg.ImagemHash = h; await GravarRegistroAsync(reg); } }
                catch (Exception e) { return await Parou($"arte: {MetaGraph.Sanear(e.Message)}"); }
            }

        /* campanha (o ensaio dela já passou) */
        if (reg.CampanhaId is null)
        {
            try
            {
                if (peloMcp)
                {
                    var criada = McpRegras.ObjetoCriado(await McpAsync("ads_create_campaign", McpRegras.ArgsDaCampanha(act, corpoDaCampanha, comuns), token), "campaign_id");
                    reg.CampanhaId = criada.Id;
                    if (criada.Rascunho && criada.Id is not null) rascunhos.Add(criada.Id);
                    if (criada.ErrosAtivos is not null) reg.Avisos.Add($"a campanha ficou no rascunho com avisos da Meta: {criada.ErrosAtivos}");
                }
                else
                {
                    var cp = await graph.PostAsync($"{act}/campaigns", corpoDaCampanha, token);
                    reg.CampanhaId = MetaGraph.S(cp.Json, "id");
                }
                if (string.IsNullOrEmpty(reg.CampanhaId)) return await Parou("campanha: a Meta não devolveu o id.");
                await GravarRegistroAsync(reg);
            }
            catch (Exception e) { return await Parou($"campanha: {MetaGraph.Sanear(e.Message)}"); }
        }

        /* conjuntos */
        for (var i = 0; i < conjuntos.Count; i++)
        {
            var cj = conjuntos[i];
            if (reg.Conjuntos.FirstOrDefault(x => x.Nome == cj.Nome)?.AdsetId is not null) continue;
            var corpo = StartRegras.CorpoDoConjunto(cj, cj.Nome, reg.CampanhaId!, pageId, interessesDoConjunto[i]);
            try
            {
                string? adsetId;
                if (peloMcp)
                {
                    var criado = McpRegras.ObjetoCriado(await McpAsync("ads_create_ad_set", McpRegras.ArgsDoConjunto(act, corpo, comuns), token), "ad_set_id");
                    adsetId = criado.Id;
                    if (criado.Rascunho && adsetId is not null) rascunhos.Add(adsetId);
                    if (criado.ErrosAtivos is not null) reg.Avisos.Add($"o conjunto \"{cj.Nome}\" ficou no rascunho com avisos da Meta: {criado.ErrosAtivos}");
                }
                else
                {
                    await graph.PostAsync($"{act}/adsets", corpo, token, validarSo: true);
                    var a = await graph.PostAsync($"{act}/adsets", corpo, token);
                    adsetId = MetaGraph.S(a.Json, "id");
                }
                if (string.IsNullOrEmpty(adsetId)) return await Parou($"conjunto \"{cj.Nome}\": a Meta não devolveu o id.");
                reg.Conjuntos = reg.Conjuntos.Where(x => x.Nome != cj.Nome).Append(new ConjuntoCriadoNoStart(cj.Nome, cj.Etapa.ToString(), cj.Publico, adsetId)).ToList();
                if (cj.Publico != "advantage" && interessesDoConjunto[i].Count == 0)
                {
                    var aviso = $"o conjunto \"{cj.Nome}\" nasceu com a segmentação base (BR, {cj.Idade.Min}-{cj.Idade.Max}) e sem interesses: defina o público \"{cj.PublicoRotulo}\" no Gerenciador antes de ativar.";
                    if (!reg.Avisos.Contains(aviso)) reg.Avisos.Add(aviso);
                }
                await GravarRegistroAsync(reg);
            }
            catch (Exception e) { return await Parou($"conjunto \"{cj.Nome}\": {MetaGraph.Sanear(e.Message)}"); }
        }

        /* o criativo ficou com o gestor: acaba aqui, e bem */
        var campanhaId = reg.CampanhaId!; var formularioFinal = reg.FormularioId!;
        /* PELO MCP, O ÚLTIMO PASSO: o que ainda é rascunho sai do rascunho, PAUSADO. */
        async Task<string?> PublicarORascunho()
        {
            if (!peloMcp) return null;
            var aviso = await PublicarRascunhoAsync(act, token, reg, rascunhos, comuns);
            if (aviso is not null && !reg.Avisos.Contains(aviso)) reg.Avisos.Add(aviso);
            return aviso;
        }
        async Task<ResultadoDoStart> ComOGestor()
        {
            try { await PublicarORascunho(); }
            catch (Exception e) { return await Parou($"publicação do rascunho: {MetaGraph.Sanear(e.Message)}"); }
            reg.Estado = "sem_criativo"; reg.Erro = null; await GravarRegistroAsync(reg);
            return new ResultadoDoStart
            {
                Ok = true, Via = via, CampanhaId = campanhaId, FormularioId = formularioFinal, Conjuntos = reg.Conjuntos, Anuncios = reg.Anuncios,
                Link = StartRegras.LinkDoGerenciador(act, campanhaId), Avisos = reg.Avisos, EstruturaHash = hash, SemCriativo = true,
                CriativoNaMao = StartRegras.CriativoNaMao0(reg, publicacoes, paraHash, link, conjuntos.Select((_, i) => ArteDoConjunto(i) is { } id && hashPorArte.TryGetValue(id, out var hh) ? hh : null).ToList()),
            };
        }
        if (criativoNaMao) return await ComOGestor();
        async Task<ResultadoDoStart> PostRecusado(string motivo)
        {
            if (reg.Anuncios.Any(x => x.AdId is not null)) return await Parou(motivo);
            reg.Avisos.Add($"o post da página foi recusado ({motivo}); o criativo ficou com o gestor.");
            return await ComOGestor();
        }

        /* anúncios, um por conjunto, cada um com a arte que o gestor deu a ele */
        var tokenDaPaginaPost = "";
        for (var i = 0; i < conjuntos.Count; i++)
        {
            var cj = conjuntos[i]; var pub = publicacoes[i];
            var adsetId = reg.Conjuntos.FirstOrDefault(x => x.Nome == cj.Nome)?.AdsetId;
            if (adsetId is null) return await Parou($"anúncio \"{pub.Nome}\": o conjunto dele não foi criado.");
            var ja = reg.Anuncios.FirstOrDefault(x => x.AdsetId == adsetId);
            if (ja?.AdId is not null) continue;
            try
            {
                Dictionary<string, string> corpoDoCriativo;
                if (viaPostDaPagina)
                {
                    if (tokenDaPaginaPost == "")
                    {
                        var pg = await graph.GetAsync($"{pageId}?fields=access_token", token);
                        tokenDaPaginaPost = MetaGraph.S(pg, "access_token") ?? "";
                        if (tokenDaPaginaPost == "") return await PostRecusado("o token não administra a página do cliente, e sem isso não dá para criar o post do anúncio");
                    }
                    var postId = ja?.PostId ?? "";
                    if (postId == "")
                    {
                        var bytes = await BytesDaArte(ArteDoConjunto(i)!);
                        try { postId = await graph.PostarFotoNaPaginaAsync(pageId, tokenDaPaginaPost, StartRegras.LegendaDoPost(pub.Texto.Headline, pub.Texto.Corpo), bytes); }
                        catch (Exception e) { return await PostRecusado(MetaGraph.Sanear(e.Message)); }
                        reg.Anuncios = reg.Anuncios.Where(x => x.AdsetId != adsetId).Append(new AnuncioCriadoNoStart(pub.Nome, adsetId, null, false, postId)).ToList();
                        await GravarRegistroAsync(reg);
                    }
                    corpoDoCriativo = StartRegras.CorpoDoCriativoDoPost(pub.Nome, postId, pub.MetaCta ?? StartRegras.CtaPadrao, formularioFinal);
                }
                else
                {
                    var hashDeste = ArteDoConjunto(i) is { } id && hashPorArte.TryGetValue(id, out var hh) ? hh : reg.ImagemHash;
                    if (string.IsNullOrEmpty(hashDeste)) return await Parou($"anúncio \"{pub.Nome}\": a arte deste conjunto não tem hash na conta.");
                    corpoDoCriativo = StartRegras.CorpoDoCriativo(pub.Nome, pub, pageId, hashDeste, formularioFinal, link);
                }
                string? adId;
                if (peloMcp)
                {
                    /* o criativo vai INLINE no anúncio (object_story_spec ou object_story_id), criado pelo app da Meta */
                    var criado = McpRegras.ObjetoCriado(await McpAsync("ads_create_ad", McpRegras.ArgsDoAnuncio(act, adsetId, pub.Nome, corpoDoCriativo, comuns), token), "ad_id");
                    adId = criado.Id;
                    if (criado.Rascunho && adId is not null) rascunhos.Add(adId);
                    if (criado.ErrosAtivos is not null) reg.Avisos.Add($"o anúncio \"{pub.Nome}\" ficou no rascunho com avisos da Meta: {criado.ErrosAtivos}");
                }
                else
                {
                    var criativo = await graph.PostAsync($"{act}/adcreatives", corpoDoCriativo, token);
                    var corpoDoAnuncio = new Dictionary<string, string>
                    {
                        ["name"] = pub.Nome, ["adset_id"] = adsetId, ["creative"] = StartRegras.Json(new Dictionary<string, string> { ["creative_id"] = MetaGraph.S(criativo.Json, "id") ?? "" }), ["status"] = "PAUSED",
                    };
                    await graph.PostAsync($"{act}/ads", corpoDoAnuncio, token, validarSo: true);
                    var an = await graph.PostAsync($"{act}/ads", corpoDoAnuncio, token);
                    adId = MetaGraph.S(an.Json, "id");
                }
                if (string.IsNullOrEmpty(adId)) return await Parou($"anúncio \"{pub.Nome}\": a Meta não devolveu o id.");
                var congelou = await CongelarAsync(slug, pub, adId);
                if (!congelou) reg.Avisos.Add($"o anúncio \"{pub.Nome}\" foi criado, mas o registro do texto publicado não gravou.");
                var doPost = reg.Anuncios.FirstOrDefault(x => x.AdsetId == adsetId)?.PostId;
                reg.Anuncios = reg.Anuncios.Where(x => x.AdsetId != adsetId).Append(new AnuncioCriadoNoStart(pub.Nome, adsetId, adId, congelou, doPost)).ToList();
                await GravarRegistroAsync(reg);
            }
            catch (Exception e) { return await Parou($"anúncio \"{pub.Nome}\": {MetaGraph.Sanear(e.Message)}"); }
        }

        try { await PublicarORascunho(); }
        catch (Exception e) { return await Parou($"publicação do rascunho: {MetaGraph.Sanear(e.Message)}"); }
        reg.Estado = "ok"; reg.Erro = null;
        await GravarRegistroAsync(reg);
        return new ResultadoDoStart
        {
            Ok = true, Via = via, CampanhaId = campanhaId, FormularioId = formularioFinal, Conjuntos = reg.Conjuntos, Anuncios = reg.Anuncios,
            Link = StartRegras.LinkDoGerenciador(act, campanhaId), Avisos = reg.Avisos, EstruturaHash = hash,
        };
    }

    /* ---- o MCP oficial da Meta ------------------------------------------------------ */

    /// <summary>Uma ferramenta do MCP que tem de dar certo: a recusa vira ErroDaMeta (e o `Parou` registra).</summary>
    async Task<RespostaDoMcp> McpAsync(string ferramenta, Dictionary<string, object?> args, string token)
    {
        var r = await mcp.ChamarAsync(ferramenta, args, token, 120);
        if (r.Ok) return r;
        var classe = McpRegras.ClasseDoErroDoMcp(r.Categoria, r.Repetivel, r.Erro);
        throw new ErroDaMeta($"{McpRegras.FraseDoMcp(r.Erro)} [MCP {ferramenta}{(r.Categoria is not null ? $", {r.Categoria}" : "")}]", classe == ClasseDoErro.Temporario ? 2 : classe == ClasseDoErro.Credencial ? 190 : 100);
    }

    /// <summary>O id existe AO VIVO na conta? Um id de rascunho não existe para a Graph.</summary>
    async Task<bool> ExisteAoVivoAsync(string id, string token)
    {
        try { var j = await graph.GetAsync($"{id}?fields=id", token, 15); return MetaGraph.S(j, "id") == id; }
        catch (ErroDaMeta) { return false; }
    }

    /// <summary>Publica o rascunho do MCP: a campanha, os conjuntos e os anúncios que ainda não existem ao vivo, com
    /// `publish_as_active = false` (saem PAUSADOS). Espera a campanha aparecer na conta antes de dar por publicado.</summary>
    async Task<string?> PublicarRascunhoAsync(string act, string token, RegistroDoStart reg, HashSet<string> rascunhos, Dictionary<string, object?> comuns)
    {
        if (reg.CampanhaId is null) return null;
        var ids = new List<string>();
        foreach (var id in new[] { reg.CampanhaId }.Concat(reg.Conjuntos.Select(x => x.AdsetId)).Concat(reg.Anuncios.Select(x => x.AdId)).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct())
            if (rascunhos.Contains(id) || !await ExisteAoVivoAsync(id, token)) ids.Add(id);
        if (ids.Count == 0) return null;
        var r = await McpAsync("ads_activate_entity", McpRegras.ArgsDeAtivacao(act, "campaign", reg.CampanhaId, ids, false, comuns), token);
        log.LogInformation("MCP publicou o rascunho de {Campanha}: {Resposta}", reg.CampanhaId, r.Dados.GetRawText().Length > 600 ? r.Dados.GetRawText()[..600] : r.Dados.GetRawText());
        for (var i = 0; i < 10; i++)
        {
            if (await ExisteAoVivoAsync(reg.CampanhaId, token)) return $"publicado pelo MCP oficial da Meta: {ids.Count} objeto(s) saíram do rascunho e estão PAUSADOS na conta.";
            await Task.Delay(4000);
        }
        throw new ErroDaMeta("o MCP da Meta aceitou publicar o rascunho, mas a campanha ainda não apareceu ao vivo na conta: confira no Gerenciador e, se ela estiver lá, publique de novo a MESMA estrutura para a retomada fechar o registro.");
    }

    /* ---- a página do cliente (criativos-ativos.ts) ------------------------------ */

    async Task<string> PageIdDoClienteAsync(Cliente c, Credenciais cred)
    {
        if (!string.IsNullOrWhiteSpace(c.Conta.PageId)) return c.Conta.PageId.Trim();
        try
        {
            var d = await graph.GetAsync($"{cred.Act}/ads?fields=creative{{object_story_spec}}&limit=25", cred.Token, 20);
            var contagem = new Dictionary<string, int>();
            if (d.TryGetProperty("data", out var ads) && ads.ValueKind == JsonValueKind.Array)
                foreach (var ad in ads.EnumerateArray())
                    if (ad.TryGetProperty("creative", out var cr) && cr.TryGetProperty("object_story_spec", out var spec) && MetaGraph.S(spec, "page_id") is { } pid)
                        contagem[pid] = contagem.GetValueOrDefault(pid) + 1;
            var top = contagem.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            if (top is null) return "";
            try { await db.ExecutarAsync(Sql.Fmt($"update public.trafego_contas set page_id = {top} where cliente_slug = {c.Slug}")); } catch { }
            c.Conta.PageId = top;
            return top;
        }
        catch { return ""; }
    }

    /* ---- a peça (peca.ts): o crivo de novo e o texto congelado ------------------ */

    async Task<Pub> PrepararPublicacaoAsync(Cliente c, string nomeBase, Etapa etapaConjunto, string? designerPecaId, TextoDoAnuncio? textoManual)
    {
        Pub Manual(string? arteId)
        {
            if (textoManual is null || (textoManual.Headline.Trim() == "" && textoManual.Corpo.Trim() == ""))
                return new(false, null, "sem_texto", arteId is not null ? "a arte não saiu de uma peça de copy: digite o texto do anúncio" : "o anúncio precisa de texto");
            var t = new TextoDoAnuncio(textoManual.Headline.Trim(), textoManual.Corpo.Trim(), textoManual.Cta.Trim());
            return new(true, new PublicacaoPreparada(StartRegras.NomeComEtiqueta(nomeBase, null), t, null, new LinhaDaPeca(null, arteId, etapaConjunto, t.Headline, t.Corpo, t.Cta, StartRegras.HashDoTexto(t), null, null)), null, null);
        }
        if (designerPecaId is null || !Guid.TryParse(designerPecaId, out var arteGuid)) return Manual(null);

        var arte = await db.UmAsync(Sql.Fmt($"""
            select d.id::text as id, d.copy_peca_id::text as copy_peca_id, d.completed_at, cl.cliente_slug
            from public.designer_pecas d join public.designer_lotes l on l.id = d.lote_id left join public.clientes cl on cl.cliente_id = l.cliente_id
            where d.id = {arteGuid}
            """), r => new { Id = r.S("id")!, PecaId = r.S("copy_peca_id"), CompletedAt = r.Dt("completed_at"), Slug = r.S("cliente_slug") });
        if (arte is null) return new(false, null, "arte_nao_encontrada", "a arte escolhida não existe mais");
        if (arte.Slug != c.Slug) return new(false, null, "arte_de_outro_cliente", "a arte escolhida não é deste cliente");
        if (arte.PecaId is null || !Guid.TryParse(arte.PecaId, out var pecaGuid)) return Manual(arte.Id);

        var peca = await db.UmAsync(Sql.Fmt($"""
            select p.peca_id::text as peca_id, p.headline, p.corpo, p.cta, p.editada_em, p.angulo_headline, p.forma_headline, p.papel_sub, p.tipo_cta, i.funil, i.tipo
            from public.copy_peca p left join public.copy_lote_item i on i.item_id = p.item_id where p.peca_id = {pecaGuid}
            """), r => new
            {
                Id = r.S("peca_id")!, Headline = r.S("headline") ?? "", Corpo = r.S("corpo") ?? "", Cta = r.S("cta") ?? "", EditadaEm = r.Dt("editada_em"),
                Angulo = r.S("angulo_headline"), Formas = r.Textos("forma_headline"), Papel = r.S("papel_sub"), TipoCta = r.S("tipo_cta"), Funil = r.S("funil"), Tipo = r.S("tipo"),
            });
        /* COPY APAGADA NÃO TRAVA A PUBLICAÇÃO (23/10): cai no caminho da arte sem copy, o gestor digita o texto. */
        if (peca is null) return Manual(arte.Id);
        if (peca.EditadaEm is { } ed && arte.CompletedAt is { } ca && ed > ca)
            return new(false, null, "texto_mudou_depois_da_arte", "a copy desta peça mudou depois que a arte foi gerada: gere a arte de novo antes de publicar");

        var texto = new TextoDoAnuncio(peca.Headline.Trim(), peca.Corpo.Trim(), peca.Cta.Trim());
        var declaracao = StartRegras.DeclaracaoDe(peca.Angulo, peca.Formas, peca.Papel, peca.TipoCta);
        /* O crivo da PUBLICAÇÃO (AD-5): o crivo em código, com a etapa do CONJUNTO. */
        var pecaParaCrivo = new PecaDeCopy
        {
            Id = peca.Id, Etapa = etapaConjunto, Tipo = peca.Tipo == "video" ? TipoPeca.Video : TipoPeca.Estatico, Headline = texto.Headline, Sub = texto.Corpo, Cta = texto.Cta,
            AnguloHeadline = peca.Angulo ?? "outro", FormasHeadline = peca.Formas, PapelSub = peca.Papel ?? "fragmento", TipoCta = peca.TipoCta ?? "outro",
        };
        var problemas = Crivo.Conferir(pecaParaCrivo, c.Produto.NomeCorreto ?? c.Nome);
        var crivo = new { ok = problemas.Count == 0, regras = problemas, em = DateTime.UtcNow, fase = "publicacao", origem = "codigo" };
        if (problemas.Count > 0) return new(false, null, "crivo_reprovou", $"a peça não passou no crivo da publicação ({string.Join("; ", problemas)})");
        return new(true, new PublicacaoPreparada(StartRegras.NomeComEtiqueta(nomeBase, declaracao), texto, declaracao is null ? null : Estruturas.CtaParaMeta(declaracao.TipoCta),
            new LinhaDaPeca(peca.Id, arte.Id, etapaConjunto, texto.Headline, texto.Corpo, texto.Cta, StartRegras.HashDoTexto(texto), JsonSerializer.Serialize(crivo), ParametrosMotor.PadraoDoVault.VaultSha)), null, null);
    }

    /// <summary>Depois da Meta: a linha de `trafego_anuncio_peca` com o `ad_id`. Falhar aqui NÃO desfaz o anúncio.</summary>
    async Task<bool> CongelarAsync(string slug, PublicacaoPreparada pub, string adId)
    {
        var l = pub.Linha;
        try
        {
            await db.ExecutarAsync(Sql.Fmt($"""
                insert into public.trafego_anuncio_peca (ad_id, cliente_slug, peca_id, designer_peca_id, etapa_conjunto, headline, corpo, cta, texto_hash, crivo, vault_sha)
                values ({adId}, {slug}, {(l.PecaId is null ? null : Guid.Parse(l.PecaId))}, {(l.DesignerPecaId is null ? null : Guid.Parse(l.DesignerPecaId))}, {l.EtapaConjunto?.ToString()}, {l.Headline}, {l.Corpo}, {l.Cta}, {l.TextoHash}, {(l.CrivoJson is null ? (Jsonb?)null : new Jsonb(l.CrivoJson))}, {l.VaultSha})
                """));
            return true;
        }
        catch (Exception e) { log.LogError("anúncio {Ad} criado sem a linha de trafego_anuncio_peca: {Erro}", adId, e.Message); return false; }
    }

    /* ---- o registro (trafego_start_publicacao) ---------------------------------- */

    static RegistroDoStart LerRegistro(ILinha r) => new()
    {
        Id = r.S("id") ?? "", Estado = r.S("estado") ?? "criando", CampanhaId = Vazio(r.S("campanha_id")), FormularioId = Vazio(r.S("formulario_id")), ImagemHash = Vazio(r.S("imagem_hash")),
        Conjuntos = Lista<ConjuntoCriadoNoStart>(r.Json("conjuntos")), Anuncios = Lista<AnuncioCriadoNoStart>(r.Json("anuncios")), Avisos = Lista<string>(r.Json("avisos")),
        Erro = r.S("erro"), ClienteSlug = r.S("cliente_slug") ?? "", EstruturaHash = r.S("estrutura_hash") ?? "", AtualizadoEm = r.S("atualizado_txt") ?? "",
    };
    static string? Vazio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
    static List<T> Lista<T>(string? json) { try { return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<List<T>>(json, Snake) ?? new(); } catch { return new(); } }
    static Jsonb J(object v) => new(JsonSerializer.Serialize(v, Snake));

    async Task<RegistroDoStart?> AcharRegistroAsync(string slug, string hash) =>
        await db.UmAsync("select " + Colunas + " from public.trafego_start_publicacao" + Sql.Fmt($" where cliente_slug = {slug} and estrutura_hash = {hash} order by atualizado_em desc limit 1"), LerRegistro);

    async Task<RegistroDoStart?> AbrirRegistroAsync(string slug, string hash, string vaultSha, Guid por)
    {
        try
        {
            var l = await db.ListaAsync(Sql.Fmt($"insert into public.trafego_start_publicacao (cliente_slug, estrutura_hash, vault_sha, estado, por, conjuntos, anuncios, avisos) values ({slug}, {hash}, {vaultSha}, 'criando', {por.ToString()}, '[]'::jsonb, '[]'::jsonb, '[]'::jsonb)") + " returning " + Colunas, LerRegistro);
            return l.Count > 0 ? l[0] : throw new InvalidOperationException("não consegui abrir o registro da publicação: sem linha");
        }
        catch (Exception e) when (e.Message.Contains("23505") || e.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)) { return null; }
    }

    async Task<bool> TomarRegistroAsync(RegistroDoStart r)
    {
        var l = await db.ListaAsync(Sql.Fmt($"update public.trafego_start_publicacao set estado = 'criando', atualizado_em = now() where id = {Guid.Parse(r.Id)} and estado = {r.Estado} and atualizado_em::text = {r.AtualizadoEm} returning id::text as id"));
        return l.Count > 0;
    }

    async Task<RegistroDoStart?> RegistroDaVezAsync(RegistroDoStart? existente, string slug, string hash, string vaultSha, Guid por)
    {
        if (existente is null) return await AbrirRegistroAsync(slug, hash, vaultSha, por);
        var emCurso = existente.Estado == "criando" && DateTime.TryParse(existente.AtualizadoEm, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var em)
            && DateTime.UtcNow - em < StartRegras.PrazoDaPublicacao;
        if (emCurso || !await TomarRegistroAsync(existente)) return null;
        existente.Estado = "criando";
        return existente;
    }

    async Task GravarRegistroAsync(RegistroDoStart r)
    {
        try
        {
            await db.ExecutarAsync(Sql.Fmt($"""
                update public.trafego_start_publicacao set estado = {r.Estado}, campanha_id = {r.CampanhaId}, formulario_id = {r.FormularioId}, imagem_hash = {r.ImagemHash},
                  conjuntos = {J(r.Conjuntos)}, anuncios = {J(r.Anuncios)}, avisos = {J(r.Avisos)}, erro = {r.Erro}, atualizado_em = now() where id = {Guid.Parse(r.Id)}
                """));
        }
        catch (Exception e) { log.LogError("o registro {Id} não gravou: {Erro}", r.Id, e.Message); }
    }

    async Task<bool> EncerrarRegistroAsync(RegistroDoStart r, string status)
    {
        try
        {
            var l = await db.ListaAsync(Sql.Fmt($"""
                update public.trafego_start_publicacao set estado = 'erro', campanha_id = null, formulario_id = null, imagem_hash = null, conjuntos = '[]'::jsonb, anuncios = '[]'::jsonb, atualizado_em = now(),
                  erro = {$"encerrada: a campanha {r.CampanhaId} está {(status == "DELETED" ? "apagada" : "arquivada")} na Meta ({(r.Erro is not null ? $"tinha parado em: {r.Erro}" : "tinha parado no meio")})."}
                where id = {Guid.Parse(r.Id)} and estado = {r.Estado} and atualizado_em::text = {r.AtualizadoEm} returning id::text as id
                """));
            return l.Count > 0;
        }
        catch (Exception e) { log.LogError("não consegui encerrar o registro {Id}: {Erro}", r.Id, e.Message); return false; }
    }

    async Task<SituacaoDaCampanha> SituacaoDaCampanhaAsync(Credenciais cred, string campanhaId)
    {
        try
        {
            var cp = await graph.GetAsync($"{campanhaId}?fields=status,effective_status", cred.Token);
            var status = MetaGraph.S(cp, "effective_status") ?? MetaGraph.S(cp, "status") ?? "";
            if (status == "") return new("nao_sei", null, "a Meta não devolveu o status");
            var fim = new[] { MetaGraph.S(cp, "status"), MetaGraph.S(cp, "effective_status") }.FirstOrDefault(s => s is "ARCHIVED" or "DELETED");
            return fim is not null ? new("encerrada", fim, null) : new("viva", status, null);
        }
        catch (Exception e) { return new("nao_sei", null, MetaGraph.Sanear(e.Message)); }
    }

    /// <summary>A publicação deste cliente que ficou pela metade, de OUTRA estrutura: recusada, a menos que a campanha dela
    /// esteja arquivada ou apagada na Meta (aí encerra e segue).</summary>
    async Task<(RegistroDoStart Linha, SituacaoDaCampanha Meta)?> PublicacaoPelaMetadeAsync(Credenciais cred, string slug, string hash)
    {
        var linhas = await db.ListaAsync("select " + Colunas + " from public.trafego_start_publicacao" + Sql.Fmt($" where cliente_slug = {slug} and estado = 'parcial' order by atualizado_em desc limit 20"), LerRegistro);
        foreach (var l in linhas)
        {
            if (l.EstruturaHash == hash || l.CampanhaId is null) continue;
            var sit = await SituacaoDaCampanhaAsync(cred, l.CampanhaId);
            if (sit.F == "encerrada" && await EncerrarRegistroAsync(l, sit.Status ?? "")) continue;
            return (l, sit);
        }
        return null;
    }
}
