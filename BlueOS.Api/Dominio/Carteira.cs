using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;
using Npgsql;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   A CARTEIRA E O CLIENTE, num round-trip cada.
   ----------------------------------------------------------------------------
   A carteira é UMA consulta com joins 1:1 (trafego_contas, estratégia, ICP,
   recorte, produto, contrato) e agregações em JSON (assets, squads, publicação,
   recomendações pendentes). O V1 fazia isso em ~40 idas ao PostgREST por clique.
   ============================================================================ */
public sealed class Carteira(Db db)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    const string Sql = """
        select c.cliente_id::text, c.cliente_slug, c.cliente_nome, c.cliente_status_atual, c.cliente_desde, c.cliente_criado_em,
               c.cliente_responsavel, c.cliente_contato_principal, c.cliente_whatsapp, c.cliente_cnpj, c.cliente_razao_social,
               tc.ad_account_id, tc.page_id, tc.squad_nome, tc.nicho_slug, tc.teto_mql, tc.motor_modo, tc.regua_mql::text as regua_mql,
               ce.budget_ads_mensal, ce.promessa_principal, ce.posicionamento, ce.diferencial, ce.funil_tipo, ce.gatilhos,
               ci.tipo, ci.nivel_consciencia, ci.temperatura, ci.tamanho_mercado, ci.persona, ci.publico_alvo, ci.obs,
               ci.dores_principais, ci.desejos_principais, ci.canais_onde_estao,
               seg.segmento_id::text as segmento_id, seg.setor, seg.descricao as segmento, seg.cargo_decisor, seg.faturamento_minimo, seg.localizacao,
               cp.nome_correto, cp.descricao as produto_descricao, cp.modelo_negocio, cp.diferenciais, cp.funcionalidades_chave, cp.objecoes_comuns, cp.concorrentes,
               ct.modalidade::text as modalidade, ct.mrr, ct.valor_total, ct.data_vencimento,
               (select json_agg(json_build_object('asset_id', a.asset_id, 'tipo', a.tipo, 'nome', a.nome, 'mime', a.mime, 'tamanho_bytes', a.tamanho_bytes,
                        'origem', a.origem, 'em_uso', a.em_uso, 'criado_em', a.criado_em) order by a.tipo, a.criado_em)
                  from public.cliente_asset a where a.cliente_id = c.cliente_id)::text as assets,
               (select json_build_object('estado', p.estado, 'campanha_id', p.campanha_id, 'formulario_id', p.formulario_id, 'estrutura_hash', p.estrutura_hash,
                        'vault_sha', p.vault_sha, 'conjuntos', p.conjuntos, 'anuncios', p.anuncios, 'avisos', p.avisos, 'erro', p.erro, 'criado_em', p.criado_em)
                  from public.trafego_start_publicacao p where p.cliente_slug = c.cliente_slug order by p.criado_em desc limit 1)::text as publicacao,
               (select count(*) from public.trafego_motor_recomendacao r where r.cliente_slug = c.cliente_slug
                   and not exists (select 1 from public.trafego_motor_decisao d where d.recomendacao_id = r.id)) as pendentes,
               coalesce((select array_agg(distinct upper(trim(s.squad_nome)))
                  from public.cliente_squads cs join public.squads s on s.squad_id = cs.squad_id
                  where cs.cliente_id = c.cliente_id
                    and (@ids::uuid[] is null or cs.squad_id = any(@ids::uuid[]))
                    and (cs.cliente_squad_ate is null or not exists (select 1 from public.cliente_squads o where o.cliente_id = cs.cliente_id and o.cliente_squad_ate is null))
                    and coalesce(cs.cliente_squad_status::text, c.cliente_status_atual::text, '') <> 'churn'), '{}') as squads
        from public.clientes c
        left join public.trafego_contas tc on tc.cliente_slug = c.cliente_slug
        left join public.cliente_estrategia ce on ce.cliente_id = c.cliente_id
        left join public.cliente_icp ci on ci.cliente_id = c.cliente_id
        left join lateral (select * from public.cliente_icp_segmento s where s.cliente_id = c.cliente_id and s.arquivado_em is null order by s.ordem nulls last, s.segmento_id limit 1) seg on true
        left join public.cliente_produto cp on cp.cliente_id = c.cliente_id
        left join lateral (select * from public.cliente_contrato k where k.cliente_id = c.cliente_id order by k.atualizado_em desc limit 1) ct on true
        where c.cliente_arquivado_em is null and c.cliente_slug is not null
        """;

    /// <summary>A carteira do escopo: global lê tudo; squad/tribo lê os clientes vinculados (vigente manda; na falta, o encerrado responde).</summary>
    public async Task<List<Cliente>> ListarAsync(EscopoDaSessao esc)
    {
        if (!esc.Global && (esc.SquadIds is null || esc.SquadIds.Count == 0)) return new();
        var ids = esc.Global ? null : esc.SquadIds!.Select(Guid.Parse).ToArray();
        var filtro = esc.Global ? "" : """
            and exists (select 1 from public.cliente_squads cs where cs.cliente_id = c.cliente_id and cs.squad_id = any(@ids::uuid[])
                        and (cs.cliente_squad_ate is null or not exists (select 1 from public.cliente_squads o where o.cliente_id = cs.cliente_id and o.cliente_squad_ate is null)))
            """;
        var lista = await db.ListaAsync(Sql + filtro + " order by c.cliente_nome", Ler, ("ids", ids));
        /* Churn em todo squad do recorte sai da lista de escolha (recorteDeChurn). */
        return lista.Where(c => c.Status != "churn" || c.Squads.Count > 0).ToList();
    }

    public async Task<Cliente?> ObterAsync(EscopoDaSessao esc, string slug)
    {
        if (!esc.Global && (esc.SquadIds is null || esc.SquadIds.Count == 0)) return null;
        var ids = esc.Global ? null : esc.SquadIds!.Select(Guid.Parse).ToArray();
        var filtro = esc.Global ? " and c.cliente_slug = @slug" : """
            and c.cliente_slug = @slug
            and exists (select 1 from public.cliente_squads cs where cs.cliente_id = c.cliente_id and cs.squad_id = any(@ids::uuid[])
                        and (cs.cliente_squad_ate is null or not exists (select 1 from public.cliente_squads o where o.cliente_id = cs.cliente_id and o.cliente_squad_ate is null)))
            """;
        await using var cx = await db.AbrirAsync();
        var c = (await Db.ListaAsync(cx, Sql + filtro + " limit 1", Ler, ("ids", ids), ("slug", slug))).FirstOrDefault();
        if (c is null) return null;
        c.Artes = await Db.ListaAsync(cx, """
            select p.id::text, l.numero as lote, p.numero, p.funil, p.formato, p.storage_url, p.created_at
            from public.designer_pecas p join public.designer_lotes l on l.id = p.lote_id
            where l.cliente_id = @id::uuid and p.storage_url is not null
            order by p.created_at desc limit 60
            """, r => new Arte
            {
                Id = r.S("id")!, PecaId = "", Etapa = Enum.TryParse<Etapa>(r.S("funil")?.ToUpperInvariant(), out var et) ? et : Etapa.MOFU,
                Formato = r.S("formato") ?? "feed", Titulo = $"{(r.S("funil") ?? "").ToUpperInvariant()} · peça {r.L("numero")} · {r.S("formato")} · lote {r.L("lote")}",
                Url = r.S("storage_url") ?? "", Lote = (int)r.L("lote"), CriadoEm = r.Dt("created_at") ?? DateTime.MinValue, Aprovada = true,
            }, ("id", c.Id));
        c.Recomendacoes = await Automacao.RecomendacoesAsync(cx, slug);
        return c;
    }

    static Cliente Ler(NpgsqlDataReader r)
    {
        var c = new Cliente
        {
            Id = r.S("cliente_id")!, Slug = r.S("cliente_slug")!, Nome = r.S("cliente_nome") ?? r.S("cliente_slug")!,
            Status = r.S("cliente_status_atual") ?? "ativo",
            Desde = r.Data("cliente_desde") ?? (r.Dt("cliente_criado_em") is { } cr ? DateOnly.FromDateTime(cr) : DateOnly.FromDateTime(DateTime.Now)),
            Squads = r.Textos("squads"),
            NichoSlug = r.S("nicho_slug"), BudgetMensal = r.Dec("budget_ads_mensal"), TetoMql = r.Dec("teto_mql"),
            Modo = r.S("motor_modo") switch { "sombra" => ModoDeExecucao.Sombra, "executa" => ModoDeExecucao.Executa, _ => ModoDeExecucao.Recomenda },
        };
        c.Squad = c.Squads.FirstOrDefault() ?? r.S("squad_nome")?.ToUpperInvariant() ?? "sem squad";
        c.Ficha = new FichaCadastral
        {
            Responsavel = r.S("cliente_responsavel"), ContatoPrincipal = r.S("cliente_contato_principal"), Whatsapp = r.S("cliente_whatsapp"),
            Cnpj = r.S("cliente_cnpj"), RazaoSocial = r.S("cliente_razao_social"),
            Modalidade = r.S("modalidade"), Mrr = r.Dec("mrr"), ValorTotal = r.Dec("valor_total"), DataVencimento = r.Data("data_vencimento"),
        };
        c.Icp = new Icp
        {
            Tipo = r.S("tipo") ?? "B2B", NivelConsciencia = r.S("nivel_consciencia") ?? "3", Temperatura = r.S("temperatura") ?? "morno",
            TamanhoMercado = r.S("tamanho_mercado"), Persona = r.S("persona"), PublicoAlvo = r.S("publico_alvo"), Obs = r.S("obs"),
            DoresPrincipais = r.Textos("dores_principais"), DesejosPrincipais = r.Textos("desejos_principais"), CanaisOndeEstao = r.Textos("canais_onde_estao"),
            SegmentoId = r.S("segmento_id"), Setor = r.S("setor"), Segmento = r.S("segmento"), CargoDecisor = r.S("cargo_decisor"),
            FaturamentoMinimo = r.S("faturamento_minimo"), Localizacao = r.S("localizacao"),
        };
        c.Produto = new Produto
        {
            NomeCorreto = r.S("nome_correto"), Descricao = r.S("produto_descricao"), ModeloNegocio = r.S("modelo_negocio"),
            Diferenciais = Junta(r, "diferenciais"), FuncionalidadesChave = Junta(r, "funcionalidades_chave"),
            ObjecoesComuns = Junta(r, "objecoes_comuns"), Concorrentes = Junta(r, "concorrentes"),
        };
        c.Estrategia = new Estrategia
        {
            PromessaPrincipal = r.S("promessa_principal"), Posicionamento = r.S("posicionamento"), Diferencial = r.S("diferencial"),
            FunilTipo = r.S("funil_tipo"), Gatilhos = Junta(r, "gatilhos"),
        };
        c.Conta = new ContaDeAnuncios { AdAccountId = r.S("ad_account_id"), PageId = r.S("page_id") };
        var regua = r.Json("regua_mql");
        if (!string.IsNullOrWhiteSpace(regua))
        {
            try { c.Regua = JsonSerializer.Deserialize<ReguaV2>(regua, Json); } catch { c.Regua = null; }
            if (c.Regua is { Perguntas.Count: 0 }) c.Regua = null;
        }
        var assets = r.Json("assets");
        if (!string.IsNullOrWhiteSpace(assets))
        {
            foreach (var a in JsonDocument.Parse(assets).RootElement.EnumerateArray())
            {
                var tipo = a.GetProperty("tipo").GetString() switch { "logo_principal" => TipoDeAsset.LogoPrincipal, "logo_variacao" => TipoDeAsset.LogoVariacao, _ => TipoDeAsset.Tela };
                c.Assets.Add(new Asset
                {
                    Id = a.GetProperty("asset_id").GetString() ?? "", Tipo = tipo, Nome = a.GetProperty("nome").GetString() ?? "",
                    Mime = a.TryGetProperty("mime", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "",
                    TamanhoBytes = a.TryGetProperty("tamanho_bytes", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0,
                    Origem = a.TryGetProperty("origem", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "manual",
                    EmUso = a.TryGetProperty("em_uso", out var u) && u.ValueKind == JsonValueKind.True,
                });
            }
        }
        var pub = r.Json("publicacao");
        if (!string.IsNullOrWhiteSpace(pub))
        {
            var p = JsonDocument.Parse(pub).RootElement;
            string? S(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var publicacao = new Publicacao
            {
                EstruturaHash = S("estrutura_hash") ?? "", CampanhaId = S("campanha_id"), FormularioId = S("formulario_id"), Estado = S("estado") ?? "ok",
                VaultSha = S("vault_sha") ?? "", Erro = S("erro"),
                Em = p.TryGetProperty("criado_em", out var crEm) && DateTime.TryParse(crEm.GetString(), out var dt) ? dt : DateTime.MinValue,
            };
            if (!string.IsNullOrEmpty(publicacao.CampanhaId) && !string.IsNullOrEmpty(c.Conta.AdAccountId))
                publicacao.Link = $"https://adsmanager.facebook.com/adsmanager/manage/campaigns?act={c.Conta.AdAccountId}&selected_campaign_ids={publicacao.CampanhaId}";
            if (p.TryGetProperty("conjuntos", out var cjs) && cjs.ValueKind == JsonValueKind.Array)
                foreach (var cj in cjs.EnumerateArray())
                    publicacao.Conjuntos.Add(new ConjuntoCriado(cj.TryGetProperty("nome", out var n) ? n.GetString() ?? "" : "",
                        cj.TryGetProperty("etapa", out var e) && Enum.TryParse<Etapa>(e.GetString(), out var et) ? et : Etapa.MOFU,
                        cj.TryGetProperty("publico", out var pb) ? pb.GetString() ?? "" : "", cj.TryGetProperty("adset_id", out var ad) ? ad.GetString() ?? "" : "", 0));
            if (p.TryGetProperty("avisos", out var avs) && avs.ValueKind == JsonValueKind.Array)
                foreach (var av in avs.EnumerateArray()) publicacao.Avisos.Add(av.GetString() ?? "");
            c.Publicacao = publicacao;
        }
        var pendentes = (int)r.L("pendentes");
        for (var i = 0; i < pendentes; i++)
            c.Recomendacoes.Add(new Recomendacao { Id = $"pendente-{i}", ClienteSlug = c.Slug, MetaId = "", Nome = "", Acao = Acao.ALERTAR, Decisao = new Decisao(Acao.ALERTAR, [], [], [], new NumerosDaDecisao(0, 0, null, null), false, null, false) });
        return c;
    }

    static string? Junta(NpgsqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return null;
        var v = r.GetValue(i);
        return v is string[] arr ? (arr.Length == 0 ? null : string.Join("\n", arr)) : v.ToString();
    }
}
