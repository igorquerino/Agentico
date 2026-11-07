using System.Collections.Concurrent;
using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Copy;
using BlueOS.Core.Modelos;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O AGENTE DE COPY NA API: a porta de /api/os/copy/gerar e /api/os/copy/peca/
   {id}/ajustar do V1, com banco e modelo. As regras puras (system, pedidos,
   esquema, crivo, divisão) moram em BlueOS.Core/Copy/PromptDoCopy.cs.
   ----------------------------------------------------------------------------
   GERAR: abre o lote (`copy_lote`, status gerando) e os itens
   (`copy_lote_item`), pede ao modelo o JSON do lote inteiro, passa cada peça
   pelo crivo (o do vault e a régua em código, a mesma da publicação; regera
   UMA vez a reprovada), grava as peças (`copy_peca`, com a etiqueta: estrutura
   declarada, origem geral/nicho/v8, sha do vault, crivo) e fecha o lote
   (pronto, modelo, tokens). Erro em qualquer ponto depois de abrir o lote
   (inclusive nos itens) marca o lote `erro`.

   O PORTÃO: `crivo_ok` nulo (o crivo não rodou: sem versão do vault) NÃO é
   aprovação. A peça sai com o motivo "sem crivo" e `aprovavel = false`.

   AJUSTAR: mesma régua e mesmo vault; a peça aprovada ou já publicada não se
   reescreve (conferido ANTES do modelo e de novo no UPDATE, no mesmo comando
   que grava a versão anterior em `copy_peca_versao`); ajuste sem mudança não
   grava nada.

   O VAULT é lido do banco (trafego_vault_em_uso → trafego_vault_nota), como o
   V1; só a leitura que DEU CERTO entra no cache (falha de rede ou de limite
   não vira "sem vault": é 503). A RÉGUA V8 é o arquivo Regras/regua-copy-v8.md.

   O USO: cada chamada ao modelo entra no `ia_uso` (ok ou erro) DEPOIS de a
   resposta ser lida, com o cache cobrado pelo preço do cache (UsoDaIa).
   ============================================================================ */
public sealed record PedidoDeCopy(List<ItemDoPedido> Itens, string? Briefing, string? Referencia, string? Nome);

/// <summary>O vault não pôde ser lido agora (rede, limite da Management API). Não é "sem vault": quem chama responde 503.</summary>
public sealed class VaultIndisponivel(string mensagem, Exception? causa) : Exception(mensagem, causa);

public sealed class CopyAgente(Db db, Ia ia, Registro registro, Segredos segredos, ILogger<CopyAgente> log)
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

    /// <summary>O snapshot do vault em uso, por 10 minutos. Versão inválida (ou nenhuma) = sem vault: vale só a régua, e a peça
    /// sai SEM crivo (não aprovável). Falha de leitura NÃO entra no cache: tenta de novo uma vez e, falhando, lança
    /// <see cref="VaultIndisponivel"/>.</summary>
    public async Task<SnapshotDoVault?> VaultAsync(CancellationToken ct = default)
    {
        if (VaultCache.TryGetValue("vault", out var g) && DateTime.UtcNow - g.Em < TimeSpan.FromMinutes(10)) return g.Snap;
        Exception? ultimo = null;
        for (var tentativa = 1; tentativa <= 2; tentativa++)
        {
            try
            {
                var snap = await LerVaultAsync();
                VaultCache["vault"] = (DateTime.UtcNow, snap);
                return snap;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ultimo = e;
                log.LogWarning("não consegui ler o vault (tentativa {Tentativa}): {Erro}", tentativa, e.Message);
                if (tentativa == 1) await Task.Delay(400, ct);
            }
        }
        throw new VaultIndisponivel($"não consegui ler o vault agora ({ultimo?.Message}): a copy não sai sem o crivo. Tente de novo em instantes.", ultimo);
    }

    async Task<SnapshotDoVault?> LerVaultAsync()
    {
        var sha = (await db.ListaAsync("select u.sha from public.trafego_vault_em_uso u join public.trafego_vault_versao v on v.sha = u.sha where coalesce(v.valida, true) order by u.desde desc limit 1", r => r.S("sha"))).FirstOrDefault();
        if (string.IsNullOrEmpty(sha)) return null;
        var notas = await db.ListaAsync(Sql.Fmt($"select id, frontmatter::text as fm, corpo from public.trafego_vault_nota where sha = {sha} and (frontmatter->>'tipo' = 'nicho' or frontmatter->>'chave_copy' is not null)"),
            r => (Id: r.S("id") ?? "", Fm: r.S("fm") ?? "{}", Corpo: r.S("corpo") ?? ""));
        /* uma nota ruim sai sozinha, com aviso: o vault inteiro não cai por ela */
        var lidas = new List<(string, JsonElement, string)>();
        foreach (var n in notas)
        {
            try { lidas.Add((n.Id, JsonDocument.Parse(n.Fm).RootElement.Clone(), n.Corpo)); }
            catch (JsonException e) { log.LogWarning("a nota {Nota} do vault tem frontmatter ilegível e ficou de fora: {Erro}", n.Id, e.Message); }
        }
        var snap = PromptDoCopy.Snapshot(sha, lidas);
        if (snap.Ignoradas.Count > 0) log.LogWarning("notas do vault ignoradas (tipo inesperado no frontmatter): {Notas}", string.Join(", ", snap.Ignoradas));
        return snap;
    }

    async Task<PromptDoCopy.Inteligencia?> InteligenciaAsync(string clienteId)
    {
        try
        {
            return await db.UmAsync(Sql.Fmt($"select voz_do_cliente, fundador, concorrencia_principal, personas_alvo from public.cliente_inteligencia where cliente_id = {Guid.Parse(clienteId)}"),
                r => new PromptDoCopy.Inteligencia(r.S("voz_do_cliente"), r.S("fundador"), r.S("concorrencia_principal"), r.Textos("personas_alvo")));
        }
        catch (Exception e)
        {
            log.LogWarning("a inteligência do cliente {Cliente} não foi lida (o prompt segue sem ela): {Erro}", clienteId, e.Message);
            return null;
        }
    }

    /* ---- gerar ---------------------------------------------------------------------- */

    public async Task<(int Status, object Corpo)> GerarAsync(Cliente c, PedidoDeCopy pedido, Guid por, CancellationToken ct = default)
    {
        if (!ia.Configurada) return (503, new { error = "sem ANTHROPIC_API_KEY na API: o Agente de Copy precisa da chave (Segredos:ANTHROPIC_API_KEY, variável de ambiente, ou Segredos:Arquivos apontando para o .env.local do V1)." });
        if (!PromptDoCopy.ClientePodeGerar(c.Status)) return (422, new { error = $"cliente {c.Status}: só cliente ativo ou em onboarding gera copy." });
        var avisos = new List<string>();
        var itens = PromptDoCopy.Normalizar(pedido.Itens ?? [], avisos);
        if (itens.Count == 0) return (400, new { error = "o pedido não tem itens válidos (tipo estatico|video, funil tofu|mofu|bofu|misto, n_pecas a partir de 1).", avisos });
        var total = itens.Sum(i => i.NPecas);
        if (total > PromptDoCopy.TetoPorLote) return (400, new { error = $"o lote pede {total} peças; o teto é {PromptDoCopy.TetoPorLote}.", avisos });
        var briefing = string.IsNullOrWhiteSpace(pedido.Briefing) ? null : pedido.Briefing.Trim();
        if (briefing is { Length: > PromptDoCopy.LimiteBriefing }) return (400, new { error = $"briefing acima de {PromptDoCopy.LimiteBriefing} caracteres." });
        var referencia = string.IsNullOrWhiteSpace(pedido.Referencia) ? null : pedido.Referencia.Trim();
        if (referencia is { Length: > PromptDoCopy.LimiteReferencia })
        {
            referencia = referencia[..PromptDoCopy.LimiteReferencia];
            avisos.Add($"a copy de referência passou de {PromptDoCopy.LimiteReferencia} caracteres e entrou cortada.");
        }

        var (contexto, temIcp) = PromptDoCopy.ContextoDoCliente(c, await InteligenciaAsync(c.Id));
        if (!temIcp && briefing is null && referencia is null)
            return (422, new { error = "o cliente não tem ICP preenchido (dores, tipo ou segmento) e não veio briefing nem referência: preencha a ficha, ou escreva um briefing." });

        SnapshotDoVault? snapshot;
        try { snapshot = await VaultAsync(ct); }
        catch (VaultIndisponivel e) { return (503, new { error = e.Message }); }
        var etapas = itens.Select(i => PromptDoCopy.EtapaDoFunil(i.Funil)).Distinct().ToList();
        var blocos = PromptDoCopy.BlocosDoVaultPara(snapshot, c.NichoSlug, etapas);
        var system = PromptDoCopy.SystemDoCopy(Regua(), blocos, contexto);
        var marca = PromptDoCopy.MarcaDoCliente(c);
        var squadId = await SquadAsync(c);

        /* a divisão geral/nicho por grupo (divisao.ts: 1 em 5 segue o nicho), contando o histórico do cliente desde a 1ª geral */
        var historico = await HistoricoDaDivisaoAsync(c.Id);
        var fontesPorItem = new List<(ItemDoPedido Item, IReadOnlyList<string?> Fontes)>();
        foreach (var i in itens)
        {
            var (f, depois) = PromptDoCopy.DivisaoDoGrupo(blocos, PromptDoCopy.EtapaDoFunil(i.Funil), i.NPecas, historico);
            historico = depois;
            fontesPorItem.Add((i, f));
        }

        /* ---- o lote, antes do modelo; os itens já dentro do try (falha marca o lote `erro`) ---- */
        string loteId; int numero; string nome;
        try
        {
            numero = (await db.ListaAsync(Sql.Fmt($"select coalesce(max(numero), 0) + 1 as n from public.copy_lote where cliente_id = {Guid.Parse(c.Id)}"), r => (int)r.L("n"))).FirstOrDefault(1);
            nome = string.IsNullOrWhiteSpace(pedido.Nome) ? $"Lote {numero}" : pedido.Nome.Trim();
            loteId = (await db.ListaAsync(Sql.Fmt($"insert into public.copy_lote (cliente_id, nome, numero, briefing, referencia, criado_por) values ({Guid.Parse(c.Id)}, {nome}, {numero}, {briefing}, {referencia}, {por}) returning lote_id::text as id"), r => r.S("id"))).FirstOrDefault()
                ?? throw new InvalidOperationException("não consegui abrir o lote de copy.");
        }
        catch (Exception e)
        {
            log.LogError("o lote de copy não abriu: {Erro}", e.Message);
            return (502, new { error = $"o Agente de Copy não abriu o lote: {e.Message}" });
        }

        var inicio = DateTime.UtcNow;
        try
        {
            var itensDb = new List<(string ItemId, ItemDoPedido Item, IReadOnlyList<string?> Fontes)>();
            foreach (var (item, fontes) in fontesPorItem)
            {
                var itemId = (await db.ListaAsync(Sql.Fmt($"insert into public.copy_lote_item (lote_id, tipo, funil, n_pecas) values ({Guid.Parse(loteId)}, {item.Tipo}::copy_tipo, {item.Funil}::copy_funil, {item.NPecas}) returning item_id::text as id"), r => r.S("id"))).FirstOrDefault()
                    ?? throw new InvalidOperationException("não consegui abrir o item do lote.");
                itensDb.Add((itemId, item, fontes));
            }

            var user = PromptDoCopy.PedidoDeGeracao(fontesPorItem, briefing, referencia);
            var (grupos, r) = await PedirAsync(c, por, squadId, "gerar-lote", system, user, PromptDoCopy.SchemaDoLote(), Math.Min(4000 + total * 1100, 32000), "criativo", PromptDoCopy.LerGrupos, ct);
            var pedidoDoGestor = briefing is null ? ((string, string)?)null : ("BRIEFING DO GESTOR", briefing);

            var regeradas = 0; var tokensIn = r.TokensIn; var tokensOut = r.TokensOut; var gravadas = 0;
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
                    var (decl, crivo, motivo) = PromptDoCopy.Avaliar(peca, etapa, snapshot, c.NichoSlug, item.Tipo, marca);
                    var regerada = false;
                    if (motivo is not null)
                    {
                        regerada = true; regeradas++;
                        var nova = await RegerarAsync(c, por, squadId, system, item.Tipo, etapa, peca, motivo, PromptDoCopy.Campos, pedidoDoGestor, "criativo", ct);
                        if (nova is { } n) { peca = n.Peca; tokensIn += n.TokensIn; tokensOut += n.TokensOut; (decl, crivo, motivo) = PromptDoCopy.Avaliar(peca, etapa, snapshot, c.NichoSlug, item.Tipo, marca); }
                    }
                    var (origem, nichoId) = PromptDoCopy.OrigemDaPeca(blocos, etapa, i < fontes.Count ? fontes[i] : null);
                    var crivoOk = decl is null ? false : crivo.Ok;
                    var pecaId = await GravarPecaAsync(itemId, i + 1, peca, decl, origem, nichoId, crivo, crivoOk, "geracao"); gravadas++;
                    pecasDoItem.Add(new
                    {
                        peca_id = pecaId, ordem = i + 1, headline = peca.Headline, corpo = peca.Corpo, cta = peca.Cta, aprovada = false,
                        angulo_headline = decl?.AnguloHeadline, forma_headline = decl?.FormaHeadline, papel_sub = decl?.PapelSub, tipo_cta = decl?.TipoCta,
                        origem, nicho_id = nichoId, vault_sha = crivo.Sha, crivo_ok = crivoOk, aprovavel = PromptDoCopy.CrivoLibera(crivoOk),
                        crivo_motivo = motivo ?? (crivoOk is null ? PromptDoCopy.MotivoSemCrivo : null), regerada,
                    });
                }
                saida.Add(new { item_id = itemId, tipo = item.Tipo, funil = item.Funil, n_pecas = item.NPecas, pecas = pecasDoItem });
                if (pecas.Count == 0) avisos.Add($"o modelo não devolveu peças para {item.Tipo} {item.Funil}");
                else if (pecas.Count < item.NPecas) avisos.Add($"o modelo devolveu {pecas.Count} de {item.NPecas} peça(s) para {item.Tipo} {item.Funil}");
            }
            if (gravadas == 0) throw new InvalidOperationException("modelo não devolveu peças pro pedido");
            if (snapshot is null) avisos.Add(PromptDoCopy.MotivoSemCrivo);

            await db.ExecutarAsync(Sql.Fmt($"update public.copy_lote set status = 'pronto', modelo = {r.Modelo}, tokens_in = {tokensIn}, tokens_out = {tokensOut} where lote_id = {Guid.Parse(loteId)}"));
            await registro.TrilhaAsync("gerou_copy", por, c.Slug, "ok", null, "copy_lote", loteId, new { via = "api", modelo = r.Modelo, pedidas = total, pecas = gravadas, regeradas, vault = snapshot?.Sha?[..Math.Min(7, snapshot.Sha.Length)] }, duracaoMs: (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
            return (200, new
            {
                lote_id = loteId, nome, numero, modelo = r.Modelo, tokens_in = tokensIn, tokens_out = tokensOut,
                vault = new { sha = snapshot?.Sha, nicho_id = blocos.NichoId, nicho_slug = c.NichoSlug, geral_id = blocos.GeralId, origem_por_etapa = blocos.OrigemPorEtapa, desligadas = blocos.Desligadas.Select(d => new { chave = d.Chave, motivo = d.Motivo }), regeradas },
                avisos, itens = saida,
            });
        }
        catch (Exception e)
        {
            log.LogError("o lote {Lote} falhou: {Erro}", loteId, e.Message);
            try { await db.ExecutarAsync(Sql.Fmt($"update public.copy_lote set status = 'erro', erro = {e.Message} where lote_id = {Guid.Parse(loteId)}")); }
            catch (Exception e2) { log.LogWarning("o lote {Lote} não foi marcado como erro: {Erro}", loteId, e2.Message); }
            await registro.TrilhaAsync("gerou_copy", por, c.Slug, "erro", e.Message, "copy_lote", loteId, new { via = "api" }, duracaoMs: (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
            return (502, new { error = $"o Agente de Copy falhou: {e.Message}", lote_id = loteId });
        }
    }

    /// <summary>Uma chamada ao modelo, lida e registrada: o `ia_uso` entra DEPOIS de a resposta ser lida (ok), ou como erro com o
    /// gasto que houve (recusa, JSON cortado, JSON que não se lê, falha da cadeia).</summary>
    async Task<(T Valor, RespostaDaIa Resposta)> PedirAsync<T>(Cliente c, Guid por, string? squadId, string operacao, IReadOnlyList<string> system, string user, Dictionary<string, object> schema, int maxTokens, string nivel, Func<string, T> ler, CancellationToken ct)
    {
        var inicio = DateTime.UtcNow;
        RespostaDaIa r;
        try { r = await ia.PedirJsonAsync(system, user, schema, maxTokens, nivel, ct); }
        catch (Exception e)
        {
            var paga = (e as ErroDaIa)?.Resposta;
            await UsoAsync(c, por, squadId, operacao, paga, paga?.Modelo ?? ia.Cadeia(nivel).FirstOrDefault(), "erro", e.Message, paga?.LatenciaMs ?? (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
            throw;
        }
        T valor;
        try { valor = ler(r.Texto); }
        catch (Exception e)
        {
            await UsoAsync(c, por, squadId, operacao, r, r.Modelo, "erro", $"a resposta do modelo não foi lida: {e.Message}", r.LatenciaMs);
            throw new ErroDaIa($"a resposta do modelo não foi lida ({e.Message}).", r, e);
        }
        await UsoAsync(c, por, squadId, operacao, r, r.Modelo, "ok", null, r.LatenciaMs);
        return (valor, r);
    }

    /// <summary>A regeração da peça reprovada, no nível de quem chamou: "criativo" no gerar, "ajuste" no ajustar (como o V1).</summary>
    async Task<(PecaDoModelo Peca, int TokensIn, int TokensOut)?> RegerarAsync(Cliente c, Guid por, string? squadId, IReadOnlyList<string> system, string tipo, string etapa, PecaDoModelo peca, string motivo, IReadOnlyList<string> campos, (string Rotulo, string Texto)? pedidoDoGestor, string nivel, CancellationToken ct)
    {
        try
        {
            var (nova, r) = await PedirAsync(c, por, squadId, nivel == "ajuste" ? "ajustar-peca-regerar" : "gerar-lote-regerar", system,
                PromptDoCopy.PedidoDeRegeracao(tipo, etapa, peca, motivo, campos, pedidoDoGestor), PromptDoCopy.SchemaDaRegeracao(campos), 2000, nivel,
                texto => JsonDocument.Parse(texto).RootElement is var root && root.TryGetProperty("peca", out var p) ? PromptDoCopy.LerPeca(p) : null, ct);
            if (nova is null) return null;
            /* só os campos pedidos mudam; os outros voltam como estavam */
            var headline = campos.Contains("headline") ? nova.Headline : peca.Headline;
            var corpo = campos.Contains("corpo") ? nova.Corpo : peca.Corpo;
            var cta = campos.Contains("cta") ? nova.Cta : peca.Cta;
            if (campos.Any(cp => string.IsNullOrWhiteSpace(cp == "headline" ? headline : cp == "corpo" ? corpo : cta))) return null;
            return (nova with { Headline = headline, Corpo = corpo, Cta = cta }, r.TokensIn, r.TokensOut);
        }
        catch (OperationCanceledException) { throw; }
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

    /// <summary>gravador.ts historicoDaDivisao: as origens geral/nicho do cliente em ordem de criação; o histórico conta da
    /// primeira geral em diante (divisao.ts historicoDasPecas).</summary>
    async Task<PromptDoCopy.Historico> HistoricoDaDivisaoAsync(string clienteId)
    {
        try
        {
            var origens = await db.ListaAsync(Sql.Fmt($"""
                select p.origem from public.copy_peca p join public.copy_lote_item i on i.item_id = p.item_id join public.copy_lote l on l.lote_id = i.lote_id
                where l.cliente_id = {Guid.Parse(clienteId)} and p.origem in ('geral','nicho') order by p.criado_em, p.peca_id
                """), r => r.S("origem"));
            return PromptDoCopy.HistoricoDasPecas(origens);
        }
        catch (Exception e)
        {
            log.LogWarning("o histórico da divisão do cliente {Cliente} não foi lido (a divisão conta do zero): {Erro}", clienteId, e.Message);
            return PromptDoCopy.Historico.Zero;
        }
    }

    /* ---- ajustar uma peça ------------------------------------------------------------- */

    public async Task<(int Status, object Corpo)> AjustarAsync(Cliente c, string pecaId, string instrucao, IReadOnlyList<string>? campos, Guid por, CancellationToken ct = default)
    {
        if (!ia.Configurada) return (503, new { error = "sem ANTHROPIC_API_KEY na API." });
        if (!PromptDoCopy.ClientePodeGerar(c.Status)) return (422, new { error = $"cliente {c.Status}: só cliente ativo ou em onboarding ajusta copy." });
        if (string.IsNullOrWhiteSpace(instrucao)) return (400, new { error = "a instrução do ajuste está vazia." });
        if (instrucao.Length > 2000) return (400, new { error = "instrução acima de 2000 caracteres." });
        var (pedidos, erroCampos) = PromptDoCopy.CamposDoAjuste(campos);
        if (erroCampos is not null) return (400, new { error = erroCampos, validos = PromptDoCopy.Campos });
        if (!Guid.TryParse(pecaId, out var pecaGuid)) return (404, new { error = "peça não encontrada" });

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
        if (PromptDoCopy.GuardaDoAjuste(p.Peca.Aprovada, p.Publicada) is { } guarda) return (409, new { error = guarda });

        SnapshotDoVault? snapshot;
        try { snapshot = await VaultAsync(ct); }
        catch (VaultIndisponivel e) { return (503, new { error = e.Message }); }
        var etapa = PromptDoCopy.EtapaDoFunil(p.Funil);
        var blocos = PromptDoCopy.BlocosDoVaultPara(snapshot, c.NichoSlug, [etapa]);
        var (contexto, _) = PromptDoCopy.ContextoDoCliente(c, await InteligenciaAsync(c.Id));
        var system = PromptDoCopy.SystemDoCopy(Regua(), blocos, contexto);
        var marca = PromptDoCopy.MarcaDoCliente(c);
        var squadId = await SquadAsync(c);
        var inicio = DateTime.UtcNow;
        try
        {
            var (nova, r) = await PedirAsync(c, por, squadId, "ajustar-peca", system, PromptDoCopy.PedidoDeAjuste(p.Tipo, p.Funil, p.Peca, p.Origem, blocos.GeralId is not null, p.Briefing, instrucao, pedidos), PromptDoCopy.SchemaDaPeca(pedidos), 3000, "ajuste",
                texto => PromptDoCopy.LerPeca(JsonDocument.Parse(texto).RootElement) ?? throw new InvalidOperationException("o modelo não devolveu a peça."), ct);
            var atual = new PecaDoModelo(p.Peca.Headline, p.Peca.Sub, p.Peca.Cta, p.Peca.AnguloHeadline, p.Peca.FormasHeadline, p.Peca.PapelSub, p.Peca.TipoCta);
            /* a trava do V1: só os campos pedidos, e só se o modelo os devolveu; nenhum = 502 */
            var devolvidos = pedidos.Where(cp => !string.IsNullOrWhiteSpace(cp == "headline" ? nova.Headline : cp == "corpo" ? nova.Corpo : nova.Cta)).ToList();
            if (devolvidos.Count == 0) return (502, new { error = "o modelo não devolveu nenhum dos campos pedidos", campos = pedidos });
            var ajustada = atual with
            {
                Headline = devolvidos.Contains("headline") ? nova.Headline : atual.Headline,
                Corpo = devolvidos.Contains("corpo") ? nova.Corpo : atual.Corpo,
                Cta = devolvidos.Contains("cta") ? nova.Cta : atual.Cta,
                AnguloHeadline = nova.AnguloHeadline, FormaHeadline = nova.FormaHeadline, PapelSub = nova.PapelSub, TipoCta = nova.TipoCta,
            };
            var (decl, crivo, motivo) = PromptDoCopy.Avaliar(ajustada, etapa, snapshot, c.NichoSlug, p.Tipo, marca);
            var regerada = false;
            if (motivo is not null)
            {
                regerada = true;
                var re = await RegerarAsync(c, por, squadId, system, p.Tipo, etapa, ajustada, motivo, pedidos, ("INSTRUÇÃO DO GESTOR", instrucao), "ajuste", ct);
                if (re is { } n) { ajustada = n.Peca; (decl, crivo, motivo) = PromptDoCopy.Avaliar(ajustada, etapa, snapshot, c.NichoSlug, p.Tipo, marca); }
            }
            var crivoOk = decl is null ? false : crivo.Ok;
            var motivoNaTela = motivo ?? (crivoOk is null ? PromptDoCopy.MotivoSemCrivo : null);

            /* AJUSTE SEM MUDANÇA não grava versão nem update */
            if (ajustada.Headline == atual.Headline && ajustada.Corpo == atual.Corpo && ajustada.Cta == atual.Cta)
            {
                await registro.TrilhaAsync("ajustou_copy", por, c.Slug, "ok", "sem mudança", "copy_peca", pecaId, new { via = "api", modelo = r.Modelo, campos = pedidos, regerada, sem_mudanca = true }, duracaoMs: (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
                return (200, new
                {
                    peca = new { peca_id = pecaId, headline = atual.Headline, corpo = atual.Corpo, cta = atual.Cta, aprovada = false, angulo_headline = p.Peca.AnguloHeadline, forma_headline = p.Peca.FormasHeadline, papel_sub = p.Peca.PapelSub, tipo_cta = p.Peca.TipoCta },
                    campos_ajustados = Array.Empty<string>(), sem_mudanca = true, modelo = r.Modelo, regerada,
                });
            }

            /* a versão anterior e o UPDATE num comando só (atômico), com a guarda de novo: a peça aprovada ou publicada
               enquanto o modelo rodava não é reescrita */
            var (origem, nichoId) = PromptDoCopy.OrigemDaPeca(blocos, etapa, p.Origem);
            var crivoJson = new Jsonb(PromptDoCopy.CrivoParaGravar(decl is null ? crivo with { Ok = false } : crivo, "ajuste"));
            var gravou = await db.ListaAsync(Sql.Fmt($"""
                with alvo as (
                  update public.copy_peca p set headline = {ajustada.Headline}, corpo = {ajustada.Corpo}, cta = {ajustada.Cta}, editada_em = now(),
                    angulo_headline = {decl?.AnguloHeadline}, forma_headline = {(decl is null ? (string[]?)null : decl.FormaHeadline.ToArray())}, papel_sub = {decl?.PapelSub}, tipo_cta = {decl?.TipoCta},
                    origem = {origem}, nicho_id = {nichoId}, vault_sha = {crivo.Sha}, crivo = {crivoJson}, crivo_ok = {crivoOk}
                  where p.peca_id = {pecaGuid} and not coalesce(p.aprovada, false)
                    and not exists (select 1 from public.trafego_anuncio_peca a where a.peca_id = p.peca_id)
                  returning p.peca_id
                ), versao as (
                  insert into public.copy_peca_versao (peca_id, headline, corpo, cta, origem, instrucao, criado_por)
                  select peca_id, {p.Peca.Headline}, {p.Peca.Sub}, {p.Peca.Cta}, 'ia', {instrucao.Trim()}, {por} from alvo
                  returning peca_id
                )
                select peca_id::text as id from alvo
                """), x => x.S("id"));
            if (gravou.Count == 0) return (409, new { error = "a peça foi aprovada ou publicada enquanto o ajuste rodava: não se reescreve. Desaprove e ajuste de novo." });
            await registro.TrilhaAsync("ajustou_copy", por, c.Slug, "ok", null, "copy_peca", pecaId, new { via = "api", modelo = r.Modelo, campos = pedidos, regerada }, duracaoMs: (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
            return (200, new
            {
                peca = new { peca_id = pecaId, headline = ajustada.Headline, corpo = ajustada.Corpo, cta = ajustada.Cta, aprovada = false, angulo_headline = decl?.AnguloHeadline, forma_headline = decl?.FormaHeadline, papel_sub = decl?.PapelSub, tipo_cta = decl?.TipoCta, crivo_ok = crivoOk, aprovavel = PromptDoCopy.CrivoLibera(crivoOk), crivo_motivo = motivoNaTela },
                campos_ajustados = devolvidos, modelo = r.Modelo, regerada,
            });
        }
        catch (Exception e)
        {
            await registro.TrilhaAsync("ajustou_copy", por, c.Slug, "erro", e.Message, "copy_peca", pecaId, new { via = "api" });
            return (502, new { error = $"o ajuste falhou: {e.Message}" });
        }
    }

    /* ---- o uso (ia_uso), como o V1 anota cada chamada --------------------------------- */

    /// <summary>O squad do cliente, só para o `ia_uso`: falha na leitura não derruba a geração (vai sem squad, com aviso no log).</summary>
    async Task<string?> SquadAsync(Cliente c)
    {
        try { return await Designer.SquadDoClienteAsync(db, c); }
        catch (Exception e) { log.LogWarning("o squad do cliente {Cliente} não foi lido para o ia_uso: {Erro}", c.Slug, e.Message); return null; }
    }

    Task UsoAsync(Cliente c, Guid por, string? squadId, string operacao, RespostaDaIa? r, string? modelo, string status, string? erro, int latenciaMs) =>
        UsoDaIa.GravarAsync(db, segredos, log, por, c.Id, squadId, "copy", operacao, modelo, status, erro, latenciaMs, r?.TokensIn ?? 0, r?.TokensOut ?? 0, 0,
            r is null ? 0m : CustoUsd(r.Modelo, r.TokensIn, r.TokensOut, r.CacheLidos, r.CacheGravados), "anthropic");

    /// <summary>O custo pela tabela única (UsoDaIa: Opus 5.5 4/20, Opus 5 5/25, Sonnet 5.x 2/10, Haiku 5.5 0,1/0,5, US$ por milhão),
    /// com o cache pelo preço dele (leitura 10%, escrita 125% da entrada). Modelo fora da tabela custa 0 (visível no registro).</summary>
    public static decimal CustoUsd(string modelo, int tokensIn, int tokensOut, int cacheLidos = 0, int cacheGravados = 0) =>
        UsoDaIa.CustoAnthropicUsd(modelo, tokensIn, tokensOut, cacheLidos, cacheGravados);
}
