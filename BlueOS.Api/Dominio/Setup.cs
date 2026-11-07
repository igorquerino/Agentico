using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O SETUP DO CLIENTE NA API: o que o desktop ainda mandava ao V1 fora do piloto
   automático. Porta de /api/os/clientes/{id}/assets, /api/trafego/conectar-
   conta, /api/trafego/token-meta e /api/os/clientes/criar.
   ----------------------------------------------------------------------------
   · ASSETS: o arquivo vai ao bucket privado `brand-assets` em
     {cliente_id}/{tipo}/{nome}, e a linha em `cliente_asset` (upsert por
     cliente, tipo e nome); a prévia é uma URL assinada por 1 h;
   · CONTA: `trafego_contas` (upsert por slug), sem chamada à Meta, como o V1;
   · TOKEN DO SQUAD: conferido na Meta (/me e /me/permissions, com o escopo do
     MCP) antes de gravar em `trafego_integracoes`; a trilha guarda só o fim
     mascarado;
   · CRIAR CLIENTE: slug, duplicidade, `clientes` e o vínculo principal em
     `cliente_squads`; coordenador pra cima.
   ============================================================================ */
public sealed class Setup(Db db, SupabaseStorage storage, MetaGraph graph, Registro registro, ILogger<Setup> log)
{
    public const string BucketDaMarca = "brand-assets";
    const int TetoDoAsset = 10 * 1024 * 1024;
    static readonly HashSet<string> TiposDeAsset = ["logo_principal", "logo_variacao", "tela"];

    /* ---- assets ---------------------------------------------------------------- */

    public static string ChaveDoAsset(string clienteId, string tipo, string nome)
    {
        var limpo = nome.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in limpo) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
        var seguro = Regex.Replace(sb.ToString().ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-');
        if (seguro == "" || seguro == ".") seguro = "arquivo";
        return $"{clienteId}/{tipo}/{seguro}";
    }

    public async Task<(int Status, object Corpo)> SubirAssetAsync(Cliente c, string tipo, string nome, string mime, byte[] bytes, Guid por)
    {
        if (!TiposDeAsset.Contains(tipo)) return (400, new { error = "tipo do asset inválido: logo_principal, logo_variacao ou tela." });
        if (bytes.Length == 0) return (400, new { error = "o arquivo está vazio." });
        if (bytes.Length > TetoDoAsset) return (413, new { error = "o arquivo passa de 10 MB." });
        var mimeOk = mime.StartsWith("image/") || mime is "application/pdf" or "text/plain" or "text/markdown";
        if (!mimeOk) return (415, new { error = "só imagem, PDF ou texto." });
        var caminho = ChaveDoAsset(c.Id, tipo, nome);
        try { await storage.SubirAsync(BucketDaMarca, caminho, bytes, mime); }
        catch (Exception e) { return (502, new { error = $"o Storage recusou o arquivo: {e.Message}" }); }
        var nomeLimpo = Path.GetFileName(caminho);
        var id = (await db.ListaAsync(Sql.Fmt($"""
            insert into public.cliente_asset (cliente_id, tipo, nome, storage_path, mime, tamanho_bytes, origem)
            values ({Guid.Parse(c.Id)}, {tipo}::asset_tipo, {nomeLimpo}, {caminho}, {mime}, {bytes.Length}, 'manual')
            on conflict (cliente_id, tipo, nome) do update set storage_path = excluded.storage_path, mime = excluded.mime, tamanho_bytes = excluded.tamanho_bytes, criado_em = now(), em_uso = true
            returning asset_id::text as id
            """), r => r.S("id"))).FirstOrDefault();
        string? url = null;
        try { url = await storage.AssinarAsync(BucketDaMarca, caminho); } catch { }
        await registro.TrilhaAsync("subiu_asset", por, c.Slug, "ok", null, "asset", id, new { via = "api", tipo, nome = nomeLimpo, bytes = bytes.Length });
        return (200, new { asset_id = id, tipo, nome = nomeLimpo, mime, tamanho_bytes = bytes.Length, origem = "manual", em_uso = true, url });
    }

    public async Task<(int Status, object Corpo)> RemoverAssetAsync(Cliente c, string assetId, Guid por)
    {
        if (!Guid.TryParse(assetId, out var g)) return (404, new { error = "asset não encontrado" });
        var a = await db.UmAsync(Sql.Fmt($"select storage_path, nome from public.cliente_asset where asset_id = {g} and cliente_id = {Guid.Parse(c.Id)}"), r => new { Path = r.S("storage_path"), Nome = r.S("nome") });
        if (a is null) return (404, new { error = "asset não encontrado" });
        await db.ExecutarAsync(Sql.Fmt($"delete from public.cliente_asset where asset_id = {g} and cliente_id = {Guid.Parse(c.Id)}"));
        try { if (a.Path is not null) await storage.ApagarAsync(BucketDaMarca, a.Path); } catch (Exception e) { log.LogWarning("o arquivo do asset {Id} não foi apagado do Storage: {Erro}", assetId, e.Message); }
        await registro.TrilhaAsync("removeu_asset", por, c.Slug, "ok", null, "asset", assetId, new { via = "api", nome = a.Nome });
        return (200, new { ok = true });
    }

    /// <summary>As URLs assinadas (1 h) dos assets do cliente, para a prévia na tela.</summary>
    public async Task<Dictionary<string, string>> UrlsDosAssetsAsync(Cliente c)
    {
        var saida = new Dictionary<string, string>();
        var linhas = await db.ListaAsync(Sql.Fmt($"select asset_id::text as id, storage_path from public.cliente_asset where cliente_id = {Guid.Parse(c.Id)}"), r => (Id: r.S("id") ?? "", Path: r.S("storage_path") ?? ""));
        foreach (var (id, path) in linhas)
        {
            if (id == "" || path == "") continue;
            try { saida[id] = await storage.AssinarAsync(BucketDaMarca, path); } catch { }
        }
        return saida;
    }

    /* ---- a conta de anúncio ---------------------------------------------------- */

    public async Task<(int Status, object Corpo)> ConectarContaAsync(Cliente c, string? adAccountId, string? pageId, string? pageNome, bool desconectar, Guid por)
    {
        string? act = null;
        if (!desconectar)
        {
            act = (adAccountId ?? "").Trim();
            if (act.StartsWith("act_", StringComparison.OrdinalIgnoreCase)) act = act[4..];
            if (!Regex.IsMatch(act, @"^\d{6,20}$")) return (400, new { error = "a conta de anúncio é o número do act_ (6 a 20 dígitos)." });
        }
        var squad = (c.Squad ?? "").Trim().ToUpperInvariant();
        var page = string.IsNullOrWhiteSpace(pageId) ? null : pageId.Trim();
        await db.ExecutarAsync(Sql.Fmt($"""
            insert into public.trafego_contas (cliente_slug, ad_account_id, page_id, squad_nome, atualizado_em, atualizado_por, origem)
            values ({c.Slug}, {act}, {page}, {squad}, now(), {por}, 'api')
            on conflict (cliente_slug) do update set ad_account_id = excluded.ad_account_id, page_id = coalesce(excluded.page_id, public.trafego_contas.page_id),
              squad_nome = coalesce(nullif(excluded.squad_nome, ''), public.trafego_contas.squad_nome), atualizado_em = now(), atualizado_por = excluded.atualizado_por, origem = 'api'
            """));
        await registro.TrilhaAsync(desconectar ? "desconectou_conta" : "conectou_conta", por, c.Slug, "ok", null, "conta", act, new { via = "api", page_id = page, page_nome = pageNome, squad });
        return (200, new { ok = true, ad_account_id = act, page_id = page });
    }

    /* ---- o token do squad --------------------------------------------------------- */

    public async Task<(string? Id, string Nome)?> SquadAsync(string nome)
    {
        var s = await db.UmAsync(Sql.Fmt($"select squad_id::text as id, squad_nome from public.squads where upper(trim(squad_nome)) = {nome.Trim().ToUpperInvariant()} and squad_arquivado_em is null limit 1"), r => new { Id = r.S("id"), Nome = r.S("squad_nome") ?? "" });
        return s is null ? null : (s.Id, s.Nome);
    }

    /// <summary>Confere o token do squad na Meta: /me (vale?) e /me/permissions (tem o escopo do MCP?).</summary>
    public async Task<object> ConferirTokenAsync(string squadNome)
    {
        var token = await db.UmAsync(Sql.Fmt($"select meta_access_token as t, atualizado_em from public.trafego_integracoes where upper(trim(squad_nome)) = {squadNome.Trim().ToUpperInvariant()}"), r => new { T = r.S("t"), Em = r.Dt("atualizado_em") });
        if (token is null || string.IsNullOrWhiteSpace(token.T)) return new { squad = squadNome.ToUpperInvariant(), tem_token = false, valido = (bool?)null, motivo = "sem token gravado para o squad", mcp = false };
        var (valido, motivo, mcp) = await ConferirNaMetaAsync(token.T);
        return new { squad = squadNome.ToUpperInvariant(), tem_token = true, valido, motivo, mcp, atualizado_em = token.Em };
    }

    async Task<(bool Valido, string? Motivo, bool Mcp)> ConferirNaMetaAsync(string token)
    {
        try
        {
            var me = await graph.GetAsync("me?fields=id,name", token, 15);
            if (string.IsNullOrEmpty(MetaGraph.S(me, "id"))) return (false, "a Meta não reconheceu o token", false);
            var mcp = false;
            try
            {
                var p = await graph.GetAsync("me/permissions", token, 15);
                mcp = p.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array && d.EnumerateArray().Any(x => MetaGraph.S(x, "permission") == Core.Meta.McpRegras.Escopo && MetaGraph.S(x, "status") == "granted");
            }
            catch { }
            return (true, null, mcp);
        }
        catch (Core.Meta.ErroDaMeta e) { return (false, $"a Meta recusou o token: {e.Message}", false); }
        catch (Exception e) { return (false, $"não consegui conferir na Meta: {MetaGraph.Sanear(e.Message)}", false); }
    }

    public async Task<(int Status, object Corpo)> GravarTokenAsync(string squadNome, string token, Guid por)
    {
        token = (token ?? "").Trim();
        if (token.Length < 40) return (400, new { error = "o token é curto demais: cole o token inteiro." });
        var (valido, motivo, mcp) = await ConferirNaMetaAsync(token);
        if (!valido) return (422, new { error = motivo ?? "a Meta recusou o token" });
        var nome = squadNome.Trim().ToUpperInvariant();
        await db.ExecutarAsync(Sql.Fmt($"""
            insert into public.trafego_integracoes (squad_nome, meta_access_token, atualizado_em, atualizado_por, origem) values ({nome}, {token}, now(), {por}, 'setup')
            on conflict (squad_nome) do update set meta_access_token = excluded.meta_access_token, atualizado_em = now(), atualizado_por = excluded.atualizado_por, origem = 'setup'
            """));
        MetaEscrita.EsquecerCredenciais();
        await registro.TrilhaAsync("gravou_token_squad", por, null, "ok", null, "squad", nome, new { via = "api", token_fim = token[^6..], tamanho = token.Length, mcp });
        return (200, new { ok = true, squad = nome, valido = true, mcp });
    }

    /* ---- criar cliente ------------------------------------------------------------ */

    public static string Slugify(string nome)
    {
        var d = nome.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return Regex.Replace(sb.ToString(), @"[^a-z0-9]+", "-").Trim('-');
    }

    public async Task<(int Status, object Corpo)> CriarClienteAsync(string nome, string? squadNome, Guid por)
    {
        nome = (nome ?? "").Trim();
        if (nome.Length < 2) return (400, new { error = "o nome do cliente está vazio." });
        var slug = Slugify(nome);
        if (slug == "") return (400, new { error = "o nome não gera um slug válido." });
        var existe = await db.UmAsync(Sql.Fmt($"select cliente_slug, cliente_status_atual::text as st from public.clientes where cliente_slug = {slug} or lower(cliente_nome) = {nome.ToLowerInvariant()} limit 1"), r => new { Slug = r.S("cliente_slug"), St = r.S("st") });
        if (existe is not null) return (409, new { error = $"já existe um cliente com esse nome ou slug ({existe.Slug}, {existe.St}).", slug = existe.Slug });
        var id = (await db.ListaAsync(Sql.Fmt($"insert into public.clientes (cliente_nome, cliente_slug, cliente_status_atual) values ({nome}, {slug}, 'ativo'::status_cliente) returning cliente_id::text as id"), r => r.S("id"))).FirstOrDefault()
            ?? throw new InvalidOperationException("o cliente não gravou.");
        var squad = string.IsNullOrWhiteSpace(squadNome) ? null : await SquadAsync(squadNome);
        if (squad is { Id: not null })
            await db.ExecutarAsync(Sql.Fmt($"insert into public.cliente_squads (cliente_id, squad_id, cliente_squad_tipo, cliente_squad_desde) values ({Guid.Parse(id)}, {Guid.Parse(squad.Value.Id!)}, 'principal'::tipo_vinculo_squad, current_date)"));
        await registro.TrilhaAsync("criou_cliente", por, slug, "ok", null, "cliente", id, new { via = "api", nome, squad = squad?.Nome });
        return (200, new { cliente = new { cliente_id = id, cliente_nome = nome, cliente_slug = slug }, slug, squad = squad?.Nome, sem_squad = squad is null });
    }
}
