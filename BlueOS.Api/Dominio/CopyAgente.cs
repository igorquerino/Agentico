using System.Collections.Concurrent;
using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Copy;
using BlueOS.Core.Modelos;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O AGENTE DE COPY NA API: a porta de /api/os/copy/gerar e /api/os/copy/peca/
   {id}/ajustar do V1, com banco e modelo. As regras puras (system, pedidos,
   esquema, crivo) moram em BlueOS.Core/Copy/PromptDoCopy.cs.
   ----------------------------------------------------------------------------
   GERAR: abre o lote (`copy_lote`, status gerando) e os itens
   (`copy_lote_item`), pede ao modelo o JSON do lote inteiro, passa cada peça
   pelo crivo (regera UMA vez a reprovada), grava as peças (`copy_peca`, com a
   etiqueta: estrutura declarada, origem geral/nicho/v8, sha do vault, crivo) e
   fecha o lote (pronto, modelo, tokens). Erro no meio marca o lote `erro`.

   AJUSTAR: mesma régua e mesmo vault; a peça aprovada ou já publicada não se
   reescreve; a versão anterior vai para `copy_peca_versao`.

   O VAULT é lido do banco (trafego_vault_em_uso → trafego_vault_nota), como o
   V1; a RÉGUA V8 é o arquivo Regras/regua-copy-v8.md, o mesmo do V1.
   ============================================================================ */
public sealed record PedidoDeCopy(List<ItemDoPedido> Itens, string? Briefing, string? Referencia, string? Nome);

public sealed class CopyAgente(Db db, Ia ia, Registro registro, ILogger<CopyAgente> log)
{
    static readonly ConcurrentDictionary<string, (DateTime Em, SnapshotDoVault? Snap)> VaultCache = new();
    static string? _regua;

    /* ---- a régua e o vault ------------------------------------------------------- */

    public static string Regua()
    {
        if (_regua is not null) return _regua;
        var caminho = Path.Combine(AppContext.BaseDirectory, "Regras", "regua-copy-v8.md");
        if (!File.Exists(caminho)) caminho = Path.Combine(Directory.GetCurrentDirectory(), "Regras", "regua-copy-v8.md");
        _regua = File.Exists(caminho) ? File.ReadAllText(caminho) : throw new InvalidOperationException("a régua V8 (Regras/regua-copy-v8.md) não está junto da API.");
        return _regua;
    }

    /// <summary>O snapshot do vault em uso, por 10 minutos. Versão inválida (ou nenhuma) = sem vault: vale só a régua.</summary>
    public async Task<SnapshotDoVault?> VaultAsync()
    {
        if (VaultCache.TryGetValue("vault", out var g) && DateTime.UtcNow - g.Em < TimeSpan.FromMinutes(10)) return g.Snap;
        SnapshotDoVault? snap = null;
        try
        {
            var sha = (await db.ListaAsync("select u.sha from public.trafego_vault_em_uso u join public.trafego_vault_versao v on v.sha = u.sha where coalesce(v.valida, true) order by u.desde desc limit 1", r => r.S("sha"))).FirstOrDefault();
            if (!string.IsNullOrEmpty(sha))
            {
                var notas = await db.ListaAsync(Sql.Fmt($"select id, frontmatter::text as fm, corpo from public.trafego_vault_nota where sha = {sha} and (frontmatter->>'tipo' = 'nicho' or frontmatter->>'chave_copy' is not null)"),
                    r => (Id: r.S("id") ?? "", Fm: r.S("fm") ?? "{}", Corpo: r.S("corpo") ?? ""));
                snap = PromptDoCopy.Snapshot(sha, notas.Select(n => (n.Id, JsonDocument.Parse(n.Fm).RootElement.Clone(), n.Corpo)));
            }
        }
        catch (Exception e) { log.LogWarning("não consegui ler o vault: {Erro}", e.Message); }
        VaultCache["vault"] = (DateTime.UtcNow, snap);
        return snap;
    }

    async Task<PromptDoCopy.Inteligencia?> InteligenciaAsync(string clienteId)
    {
        try
        {
            return await db.UmAsync(Sql.Fmt($"select voz_do_cliente, fundador, concorrencia_principal, personas_alvo from public.cliente_inteligencia where cliente_id = {Guid.Parse(clienteId)}"),
                r => new PromptDoCopy.Inteligencia(r.S("voz_do_cliente"), r.S("fundador"), r.S("concorrencia_principal"), r.Textos("personas_alvo")));
        }
        catch { return null; }
    }

    /* ---- gerar ---------------------------------------------------------------------- */

    public async Task<(int Status, object Corpo)> GerarAsync(Cliente c, PedidoDeCopy pedido, Guid por)
    {
        if (!ia.Configurada) return (503, new { error = "sem ANTHROPIC_API_KEY na API: o Agente de Copy precisa da chave (Segredos:ANTHROPIC_API_KEY, variável de ambiente, ou Segredos:Arquivos apontando para o .env.local do V1)." });
        if (c.Status is not ("ativo" or "onboarding")) return (422, new { error = $"cliente {c.Status}: só cliente ativo ou em onboarding gera copy." });
        var itens = PromptDoCopy.Normalizar(pedido.Itens);
        if (itens.Count == 0) return (400, new { error = "o pedido não tem itens (tipo, funil, n_pecas)." });
        var total = itens.Sum(i => i.NPecas);
        if (total > PromptDoCopy.TetoPorLote) return (400, new { error = $"o lote pede {total} peças; o teto é {PromptDoCopy.TetoPorLote}." });
        var briefing = string.IsNullOrWhiteSpace(pedido.Briefing) ? null : pedido.Briefing.Trim();
        if (briefing is { Length: > PromptDoCopy.LimiteBriefing }) return (400, new { error = $"briefing acima de {PromptDoCopy.LimiteBriefing} caracteres." });

        var (contexto, temIcp) = PromptDoCopy.ContextoDoCliente(c, await InteligenciaAsync(c.Id));
        if (!temIcp && briefing is null && string.IsNullOrWhiteSpace(pedido.Referencia))
            return (422, new { error = "o cliente não tem ICP preenchido (dores, tipo ou segmento) e não veio briefing nem referência: preencha a ficha, ou escreva um briefing." });

        var snapshot = await VaultAsync();
        var etapas = itens.Select(i => PromptDoCopy.EtapaDoFunil(i.Funil)).Distinct().ToList();
        var blocos = PromptDoCopy.BlocosDoVaultPara(snapshot, c.NichoSlug, etapas);
        var system = PromptDoCopy.SystemDoCopy(Regua(), blocos, contexto);

        /* a divisão geral/nicho por grupo (1 em 5 segue o nicho), contando o histórico do cliente */
        var historico = await HistoricoDaDivisaoAsync(c.Id);
        var fontesPorItem = new List<(ItemDoPedido Item, IReadOnlyList<string?> Fontes)>();
        var jaNicho = historico.Nicho; var jaTotal = historico.Total;
        foreach (var i in itens)
        {
            var f = PromptDoCopy.DivisaoDoGrupo(blocos, PromptDoCopy.EtapaDoFunil(i.Funil), i.NPecas, jaNicho, jaTotal);
            jaNicho += f.Count(x => x == "nicho"); jaTotal += f.Count(x => x is not null);
            fontesPorItem.Add((i, f));
        }

        /* ---- o lote e os itens, antes do modelo --------------------------------------- */
        var numero = (await db.ListaAsync(Sql.Fmt($"select coalesce(max(numero), 0) + 1 as n from public.copy_lote where cliente_id = {Guid.Parse(c.Id)}"), r => (int)r.L("n"))).FirstOrDefault(1);
        var nome = string.IsNullOrWhiteSpace(pedido.Nome) ? $"Lote {numero}" : pedido.Nome.Trim();
        var loteId = (await db.ListaAsync(Sql.Fmt($"insert into public.copy_lote (cliente_id, nome, numero, briefing, referencia, criado_por) values ({Guid.Parse(c.Id)}, {nome}, {numero}, {briefing}, {(string.IsNullOrWhiteSpace(pedido.Referencia) ? null : pedido.Referencia.Trim())}, {por}) returning lote_id::text as id"), r => r.S("id"))).FirstOrDefault()
            ?? throw new InvalidOperationException("não consegui abrir o lote de copy.");
        var itensDb = new List<(string ItemId, ItemDoPedido Item, IReadOnlyList<string?> Fontes)>();
        foreach (var (item, fontes) in fontesPorItem)
        {
            var itemId = (await db.ListaAsync(Sql.Fmt($"insert into public.copy_lote_item (lote_id, tipo, funil, n_pecas) values ({Guid.Parse(loteId)}, {item.Tipo}::copy_tipo, {item.Funil}::copy_funil, {item.NPecas}) returning item_id::text as id"), r => r.S("id"))).FirstOrDefault()
                ?? throw new InvalidOperationException("não consegui abrir o item do lote.");
            itensDb.Add((itemId, item, fontes));
        }

        var inicio = DateTime.UtcNow;
        try
        {
            var user = PromptDoCopy.PedidoDeGeracao(fontesPorItem, briefing, pedido.Referencia);
            var r = await ia.PedirJsonAsync(system, user, PromptDoCopy.SchemaDoLote(), Math.Min(4000 + total * 1100, 32000));
            await UsoAsync(c, por, "gerar-lote", r, "ok", null);
            var grupos = PromptDoCopy.LerGrupos(r.Texto);
            var pedidoDoGestor = briefing is null ? ((string, string)?)null : ("BRIEFING DO GESTOR", briefing);

            var linhas = new List<string>(); var regeradas = 0; var tokensIn = r.TokensIn; var tokensOut = r.TokensOut; var gravadas = 0;
            var saida = new List<object>();
            foreach (var (itemId, item, fontes) in itensDb)
            {
                var grupo = grupos.FirstOrDefault(g => g.Tipo == item.Tipo && g.Funil == item.Funil);
                var etapa = PromptDoCopy.EtapaDoFunil(item.Funil);
                var pecas = (grupo.Pecas ?? new()).Take(item.NPecas).ToList();
                var pecasDoItem = new List<object>();
                for (var i = 0; i < pecas.Count; i++)
                {
                    var peca = pecas[i];
                    var (decl, crivo, motivo) = PromptDoCopy.Avaliar(peca, etapa, snapshot);
                    var regerada = false;
                    if (motivo is not null)
                    {
                        regerada = true; regeradas++;
                        var nova = await RegerarAsync(c, por, system, item.Tipo, etapa, peca, motivo, PromptDoCopy.Campos, pedidoDoGestor);
                        if (nova is { } n) { peca = n.Peca; tokensIn += n.TokensIn; tokensOut += n.TokensOut; (decl, crivo, motivo) = PromptDoCopy.Avaliar(peca, etapa, snapshot); }
                    }
                    var (origem, nichoId) = PromptDoCopy.OrigemDaPeca(blocos, etapa, i < fontes.Count ? fontes[i] : null);
                    var crivoOk = decl is null ? false : crivo.Ok;
                    var pecaId = await GravarPecaAsync(itemId, i + 1, peca, decl, origem, nichoId, crivo, crivoOk, "geracao"); gravadas++;
                    pecasDoItem.Add(new
                    {
                        peca_id = pecaId, ordem = i + 1, headline = peca.Headline, corpo = peca.Corpo, cta = peca.Cta, aprovada = false,
                        angulo_headline = decl?.AnguloHeadline, forma_headline = decl?.FormaHeadline, papel_sub = decl?.PapelSub, tipo_cta = decl?.TipoCta,
                        origem, nicho_id = nichoId, vault_sha = crivo.Sha, crivo_ok = crivoOk, crivo_motivo = motivo, regerada,
                    });
                }
                saida.Add(new { item_id = itemId, tipo = item.Tipo, funil = item.Funil, n_pecas = item.NPecas, pecas = pecasDoItem });
                if (pecas.Count == 0) linhas.Add($"o modelo não devolveu peças para {item.Tipo} {item.Funil}");
            }
            if (gravadas == 0) throw new InvalidOperationException("modelo não devolveu peças pro pedido");

            await db.ExecutarAsync(Sql.Fmt($"update public.copy_lote set status = 'pronto', modelo = {r.Modelo}, tokens_in = {tokensIn}, tokens_out = {tokensOut} where lote_id = {Guid.Parse(loteId)}"));
            await registro.TrilhaAsync("gerou_copy", por, c.Slug, "ok", null, "copy_lote", loteId, new { via = "api", modelo = r.Modelo, pecas = total, regeradas, vault = snapshot?.Sha?[..7] }, duracaoMs: (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
            return (200, new
            {
                lote_id = loteId, nome, numero, modelo = r.Modelo, tokens_in = tokensIn, tokens_out = tokensOut,
                vault = new { sha = snapshot?.Sha, nicho_id = blocos.NichoId, nicho_slug = c.NichoSlug, geral_id = blocos.GeralId, origem_por_etapa = blocos.OrigemPorEtapa, desligadas = blocos.Desligadas.Select(d => new { chave = d.Chave, motivo = d.Motivo }), regeradas },
                avisos = linhas, itens = saida,
            });
        }
        catch (Exception e)
        {
            log.LogError("o lote {Lote} falhou: {Erro}", loteId, e.Message);
            try { await db.ExecutarAsync(Sql.Fmt($"update public.copy_lote set status = 'erro', erro = {e.Message} where lote_id = {Guid.Parse(loteId)}")); } catch { }
            await registro.TrilhaAsync("gerou_copy", por, c.Slug, "erro", e.Message, "copy_lote", loteId, new { via = "api" }, duracaoMs: (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
            return (502, new { error = $"o Agente de Copy falhou: {e.Message}", lote_id = loteId });
        }
    }

    async Task<(PecaDoModelo Peca, int TokensIn, int TokensOut)?> RegerarAsync(Cliente c, Guid por, IReadOnlyList<string> system, string tipo, string etapa, PecaDoModelo peca, string motivo, IReadOnlyList<string> campos, (string Rotulo, string Texto)? pedidoDoGestor)
    {
        try
        {
            var r = await ia.PedirJsonAsync(system, PromptDoCopy.PedidoDeRegeracao(tipo, etapa, peca, motivo, campos, pedidoDoGestor), PromptDoCopy.SchemaDaRegeracao(campos), 2000);
            await UsoAsync(c, por, "gerar-lote-regerar", r, "ok", null);
            var root = JsonDocument.Parse(r.Texto).RootElement;
            if (!root.TryGetProperty("peca", out var p)) return null;
            var nova = PromptDoCopy.LerPeca(p);
            if (nova is null) return null;
            /* só os campos pedidos mudam; os outros voltam como estavam */
            var headline = campos.Contains("headline") ? nova.Headline : peca.Headline;
            var corpo = campos.Contains("corpo") ? nova.Corpo : peca.Corpo;
            var cta = campos.Contains("cta") ? nova.Cta : peca.Cta;
            if (campos.Any(cp => string.IsNullOrWhiteSpace(cp == "headline" ? headline : cp == "corpo" ? corpo : cta))) return null;
            return (nova with { Headline = headline, Corpo = corpo, Cta = cta }, r.TokensIn, r.TokensOut);
        }
        catch (Exception e) { log.LogWarning("a regeração falhou: {Erro}", e.Message); return null; }
    }

    async Task<string> GravarPecaAsync(string itemId, int ordem, PecaDoModelo p, Declaracao? d, string origem, string? nichoId, ResultadoDoCrivo crivo, bool? crivoOk, string fase)
    {
        var crivoJson = new Jsonb(PromptDoCopy.CrivoParaGravar(d is null ? crivo with { Ok = false } : crivo, fase));
        var id = (await db.ListaAsync(Sql.Fmt($"""
            insert into public.copy_peca (item_id, ordem, headline, corpo, cta, angulo_headline, forma_headline, papel_sub, tipo_cta, origem, nicho_id, vault_sha, crivo, crivo_ok)
            values ({Guid.Parse(itemId)}, {ordem}, {p.Headline}, {p.Corpo}, {p.Cta}, {d?.AnguloHeadline}, {(d is null ? (string[]?)null : d.FormaHeadline.ToArray())}, {d?.PapelSub}, {d?.TipoCta}, {origem}, {nichoId}, {crivo.Sha}, {crivoJson}, {crivoOk})
            returning peca_id::text as id
            """), r => r.S("id"))).FirstOrDefault();
        return id ?? throw new InvalidOperationException("a peça não gravou.");
    }

    async Task<(int Nicho, int Total)> HistoricoDaDivisaoAsync(string clienteId)
    {
        try
        {
            var l = await db.ListaAsync(Sql.Fmt($"select count(*) filter (where p.origem = 'nicho') as nicho, count(*) filter (where p.origem in ('geral','nicho')) as total from public.copy_peca p join public.copy_lote_item i on i.item_id = p.item_id join public.copy_lote l on l.lote_id = i.lote_id where l.cliente_id = {Guid.Parse(clienteId)}"),
                r => ((int)r.L("nicho"), (int)r.L("total")));
            return l.FirstOrDefault();
        }
        catch { return (0, 0); }
    }

    /* ---- ajustar uma peça ------------------------------------------------------------- */

    public async Task<(int Status, object Corpo)> AjustarAsync(Cliente c, string pecaId, string instrucao, IReadOnlyList<string>? campos, Guid por)
    {
        if (!ia.Configurada) return (503, new { error = "sem ANTHROPIC_API_KEY na API." });
        if (string.IsNullOrWhiteSpace(instrucao)) return (400, new { error = "a instrução do ajuste está vazia." });
        if (instrucao.Length > 2000) return (400, new { error = "instrução acima de 2000 caracteres." });
        if (!Guid.TryParse(pecaId, out var pecaGuid)) return (404, new { error = "peça não encontrada" });
        var pedidos = (campos ?? PromptDoCopy.Campos).Where(x => PromptDoCopy.Campos.Contains(x)).Distinct().ToList();
        if (pedidos.Count == 0) pedidos = PromptDoCopy.Campos.ToList();

        var p = await db.UmAsync(Sql.Fmt($"""
            select p.peca_id::text as id, p.headline, p.corpo, p.cta, p.aprovada, p.angulo_headline, p.forma_headline, p.papel_sub, p.tipo_cta, p.origem, i.tipo::text as tipo, i.funil::text as funil, l.briefing, l.cliente_id::text as cliente_id,
                   exists (select 1 from public.trafego_anuncio_peca a where a.peca_id = p.peca_id) as publicada
            from public.copy_peca p join public.copy_lote_item i on i.item_id = p.item_id join public.copy_lote l on l.lote_id = i.lote_id where p.peca_id = {pecaGuid}
            """), r => new
            {
                Peca = new PecaDeCopy
                {
                    Id = r.S("id")!, Etapa = Enum.Parse<Etapa>(PromptDoCopy.EtapaDoFunil(r.S("funil") ?? "mofu")), Tipo = r.S("tipo") == "video" ? TipoPeca.Video : TipoPeca.Estatico,
                    Headline = r.S("headline") ?? "", Sub = r.S("corpo") ?? "", Cta = r.S("cta") ?? "", AnguloHeadline = r.S("angulo_headline") ?? "", PapelSub = r.S("papel_sub") ?? "", TipoCta = r.S("tipo_cta") ?? "",
                    FormasHeadline = r.Textos("forma_headline"), Aprovada = r.B("aprovada") ?? false,
                },
                Tipo = r.S("tipo") ?? "estatico", Funil = r.S("funil") ?? "mofu", Origem = r.S("origem"), Briefing = r.S("briefing"), ClienteId = r.S("cliente_id"), Publicada = r.B("publicada") ?? false,
            });
        if (p is null || p.ClienteId != c.Id) return (404, new { error = "peça não encontrada" });
        if (p.Peca.Aprovada) return (409, new { error = "a peça está aprovada: desaprove antes de ajustar." });
        if (p.Publicada) return (409, new { error = "a peça já está publicada num anúncio: não se reescreve copy no ar." });

        var snapshot = await VaultAsync();
        var etapa = PromptDoCopy.EtapaDoFunil(p.Funil);
        var blocos = PromptDoCopy.BlocosDoVaultPara(snapshot, c.NichoSlug, [etapa]);
        var (contexto, _) = PromptDoCopy.ContextoDoCliente(c, await InteligenciaAsync(c.Id));
        var system = PromptDoCopy.SystemDoCopy(Regua(), blocos, contexto);
        var inicio = DateTime.UtcNow;
        try
        {
            var r = await ia.PedirJsonAsync(system, PromptDoCopy.PedidoDeAjuste(p.Tipo, p.Funil, p.Peca, p.Origem, blocos.GeralId is not null, p.Briefing, instrucao, pedidos), PromptDoCopy.SchemaDaPeca(pedidos), 3000, "ajuste");
            await UsoAsync(c, por, "ajustar-peca", r, "ok", null);
            var nova = PromptDoCopy.LerPeca(JsonDocument.Parse(r.Texto).RootElement) ?? throw new InvalidOperationException("o modelo não devolveu a peça.");
            var atual = new PecaDoModelo(p.Peca.Headline, p.Peca.Sub, p.Peca.Cta, p.Peca.AnguloHeadline, p.Peca.FormasHeadline, p.Peca.PapelSub, p.Peca.TipoCta);
            var ajustada = atual with
            {
                Headline = pedidos.Contains("headline") && nova.Headline != "" ? nova.Headline : atual.Headline,
                Corpo = pedidos.Contains("corpo") && nova.Corpo != "" ? nova.Corpo : atual.Corpo,
                Cta = pedidos.Contains("cta") && nova.Cta != "" ? nova.Cta : atual.Cta,
                AnguloHeadline = nova.AnguloHeadline, FormaHeadline = nova.FormaHeadline, PapelSub = nova.PapelSub, TipoCta = nova.TipoCta,
            };
            var (decl, crivo, motivo) = PromptDoCopy.Avaliar(ajustada, etapa, snapshot);
            var regerada = false;
            if (motivo is not null)
            {
                regerada = true;
                var re = await RegerarAsync(c, por, system, p.Tipo, etapa, ajustada, motivo, pedidos, ("INSTRUÇÃO DO GESTOR", instrucao));
                if (re is { } n) { ajustada = n.Peca; (decl, crivo, motivo) = PromptDoCopy.Avaliar(ajustada, etapa, snapshot); }
            }
            var (origem, nichoId) = PromptDoCopy.OrigemDaPeca(blocos, etapa, p.Origem);
            var crivoOk = decl is null ? false : crivo.Ok;
            await db.ExecutarAsync(Sql.Fmt($"insert into public.copy_peca_versao (peca_id, headline, corpo, cta, origem, instrucao, criado_por) values ({pecaGuid}, {p.Peca.Headline}, {p.Peca.Sub}, {p.Peca.Cta}, 'ia', {instrucao.Trim()}, {por})"));
            await db.ExecutarAsync(Sql.Fmt($"""
                update public.copy_peca set headline = {ajustada.Headline}, corpo = {ajustada.Corpo}, cta = {ajustada.Cta}, editada_em = now(),
                  angulo_headline = {decl?.AnguloHeadline}, forma_headline = {(decl is null ? (string[]?)null : decl.FormaHeadline.ToArray())}, papel_sub = {decl?.PapelSub}, tipo_cta = {decl?.TipoCta},
                  origem = {origem}, nicho_id = {nichoId}, vault_sha = {crivo.Sha}, crivo = {new Jsonb(PromptDoCopy.CrivoParaGravar(decl is null ? crivo with { Ok = false } : crivo, "ajuste"))}, crivo_ok = {crivoOk}
                where peca_id = {pecaGuid}
                """));
            await registro.TrilhaAsync("ajustou_copy", por, c.Slug, "ok", null, "copy_peca", pecaId, new { via = "api", modelo = r.Modelo, campos = pedidos, regerada }, duracaoMs: (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
            return (200, new
            {
                peca = new { peca_id = pecaId, headline = ajustada.Headline, corpo = ajustada.Corpo, cta = ajustada.Cta, aprovada = false, angulo_headline = decl?.AnguloHeadline, forma_headline = decl?.FormaHeadline, papel_sub = decl?.PapelSub, tipo_cta = decl?.TipoCta, crivo_ok = crivoOk, crivo_motivo = motivo },
                campos_ajustados = pedidos, modelo = r.Modelo, regerada,
            });
        }
        catch (Exception e)
        {
            await registro.TrilhaAsync("ajustou_copy", por, c.Slug, "erro", e.Message, "copy_peca", pecaId, new { via = "api" });
            return (502, new { error = $"o ajuste falhou: {e.Message}" });
        }
    }

    /* ---- o uso (ia_uso), como o V1 anota cada chamada --------------------------------- */

    async Task UsoAsync(Cliente c, Guid por, string operacao, RespostaDaIa? r, string status, string? erro)
    {
        try
        {
            var custo = r is null ? 0m : CustoUsd(r.Modelo, r.TokensIn, r.TokensOut);
            await db.ExecutarAsync(Sql.Fmt($"insert into public.ia_uso (user_id, cliente_id, agente, operacao, modelo, status, erro, latencia_ms, tokens_in, tokens_out, imagens, custo_usd, ambiente, provedor) values ({por}, {Guid.Parse(c.Id)}, 'copy', {operacao}, {r?.Modelo}, {status}, {erro}, {r?.LatenciaMs ?? 0}, {r?.TokensIn ?? 0}, {r?.TokensOut ?? 0}, 0, {custo}, {db.Ref}, 'anthropic')"));
        }
        catch (Exception e) { log.LogWarning("ia_uso não gravou: {Erro}", e.Message); }
    }

    /// <summary>O custo pela tabela pública (US$ por milhão): Opus 5.5 4/20, Opus 5 5/25, Sonnet 5.5 e 5 2/10, Haiku 5.5 0,1/0,5.</summary>
    public static decimal CustoUsd(string modelo, int tokensIn, int tokensOut)
    {
        var (i, o) = modelo switch
        {
            var m when m.StartsWith("claude-opus-5-5") => (4m, 20m),
            var m when m.StartsWith("claude-opus") => (5m, 25m),
            var m when m.StartsWith("claude-sonnet") => (2m, 10m),
            var m when m.StartsWith("claude-haiku-5") => (0.1m, 0.5m),
            _ => (3m, 15m),
        };
        return Math.Round((tokensIn * i + tokensOut * o) / 1_000_000m, 6);
    }
}
