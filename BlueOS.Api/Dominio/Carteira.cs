using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   A CARTEIRA E O CLIENTE, num round-trip cada.
   ----------------------------------------------------------------------------
   UMA consulta com joins 1:1 (trafego_contas, estratégia, ICP, recorte, produto,
   contrato) e agregações em JSON (assets, squads vigentes, publicação,
   recomendações pendentes). No detalhe entram ainda as artes do Designer e as
   recomendações com estado, também como JSON. O V1 fazia ~40 idas ao PostgREST.
   ============================================================================ */
public sealed class Carteira(Db db)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static string Consulta(EscopoDaSessao esc, string? slug, bool detalhe)
    {
        var ids = esc.SquadGuids;
        var squadsSql = esc.Global
            ? ""
            : Sql.Fmt($" and cs.squad_id = any({ids ?? []})");
        /* As artes e os lotes de copy entram na LISTA tambem (enxutos), porque o estagio do card depende deles:
           Copy pronta = peca aprovada por etapa; Design = artes + pilotos. No detalhe vem tudo. */
        var limiteArtes = detalhe ? 60 : 6;
        var limiteLotes = detalhe ? 20 : 5;
        var soAprovadas = detalhe ? "" : " and p.aprovada";
        var extras = $"""
            ,
               (select json_agg(json_build_object('id', p.id, 'lote', p.lote, 'numero', p.numero, 'funil', p.funil, 'formato', p.formato, 'url', p.storage_url, 'criado_em', p.created_at, 'copy_peca_id', p.copy_peca_id, 'template_id', p.template_id) order by p.created_at desc)
                  from (select x.id, y.numero as lote, x.numero, x.funil, x.formato, x.storage_url, x.created_at, x.copy_peca_id::text as copy_peca_id, x.template_id
                        from public.designer_pecas x join public.designer_lotes y on y.id = x.lote_id
                        where y.cliente_id = c.cliente_id and x.storage_url is not null order by x.created_at desc limit {limiteArtes}) p)::text as artes,
               (select json_agg(json_build_object('lote_id', l.lote_id, 'numero', l.numero, 'nome', l.nome, 'briefing', l.briefing, 'modelo', l.modelo, 'criado_em', l.criado_em,
                        'tokens_in', l.tokens_in, 'tokens_out', l.tokens_out,
                        'pecas', (select json_agg(json_build_object('peca_id', p.peca_id, 'tipo', i.tipo, 'funil', i.funil, 'ordem', p.ordem, 'headline', p.headline,
                                    'corpo', p.corpo, 'cta', p.cta, 'aprovada', p.aprovada, 'crivo_ok', p.crivo_ok, 'angulo_headline', p.angulo_headline,
                                    'papel_sub', p.papel_sub, 'tipo_cta', p.tipo_cta) order by i.tipo, i.funil, p.ordem)
                                  from public.copy_lote_item i join public.copy_peca p on p.item_id = i.item_id where i.lote_id = l.lote_id{soAprovadas})) order by l.criado_em)
                  from (select * from public.copy_lote k where k.cliente_id = c.cliente_id order by k.criado_em desc limit {limiteLotes}) l)::text as lotes
            """ + (!detalhe ? "" : """
            ,
               (select json_agg(r order by r.criado_em desc) from (
            """ + Automacao.RecsSql("c.cliente_slug") + """
               ) r)::text as recomendacoes
            """);
        var filtroSlug = slug is null ? "" : Sql.Fmt($" and c.cliente_slug = {slug}");
        return $"""
            select c.cliente_id::text as cliente_id, c.cliente_slug, c.cliente_nome, c.cliente_status_atual::text as cliente_status_atual, c.cliente_desde, c.cliente_criado_em,
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
                      where cs.cliente_id = c.cliente_id{squadsSql}
                        and (cs.cliente_squad_ate is null or not exists (select 1 from public.cliente_squads o where o.cliente_id = cs.cliente_id and o.cliente_squad_ate is null))
                        and coalesce(cs.cliente_squad_status::text, c.cliente_status_atual::text, '') <> 'churn'), array[]::text[]) as squads{extras}
            from public.clientes c
            left join public.trafego_contas tc on tc.cliente_slug = c.cliente_slug
            left join public.cliente_estrategia ce on ce.cliente_id = c.cliente_id
            left join public.cliente_icp ci on ci.cliente_id = c.cliente_id
            left join lateral (select * from public.cliente_icp_segmento s where s.cliente_id = c.cliente_id and s.arquivado_em is null order by s.ordem nulls last, s.segmento_id limit 1) seg on true
            left join public.cliente_produto cp on cp.cliente_id = c.cliente_id
            left join lateral (select * from public.cliente_contrato k where k.cliente_id = c.cliente_id order by k.atualizado_em desc limit 1) ct on true
            where c.cliente_arquivado_em is null and c.cliente_slug is not null{filtroSlug}
            """ + Escopo.FiltroDoAlcance(esc) + (slug is null ? "\norder by c.cliente_nome" : "\nlimit 1");
    }

    /// <summary>A carteira do escopo: global lê tudo; squad/tribo lê os clientes vinculados (vigente manda; na falta, o encerrado responde).</summary>
    public async Task<List<Cliente>> ListarAsync(EscopoDaSessao esc)
    {
        if (!esc.Global && (esc.SquadIds is null || esc.SquadIds.Count == 0)) return new();
        var lista = await db.ListaAsync(Consulta(esc, null, false), Ler);
        /* Churn em todo squad do recorte sai da lista de escolha (recorteDeChurn). */
        return lista.Where(c => c.Status != "churn" || c.Squads.Count > 0).ToList();
    }

    public async Task<Cliente?> ObterAsync(EscopoDaSessao esc, string slug)
    {
        if (!esc.Global && (esc.SquadIds is null || esc.SquadIds.Count == 0)) return null;
        var c = await db.UmAsync(Consulta(esc, slug, true), Ler);
        if (c is null) return null;
        return c;
    }

    internal static Cliente Ler(ILinha r)
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
                var tipo = Str(a, "tipo") switch { "logo_principal" => TipoDeAsset.LogoPrincipal, "logo_variacao" => TipoDeAsset.LogoVariacao, _ => TipoDeAsset.Tela };
                c.Assets.Add(new Asset
                {
                    Id = Str(a, "asset_id") ?? "", Tipo = tipo, Nome = Str(a, "nome") ?? "", Mime = Str(a, "mime") ?? "",
                    TamanhoBytes = a.TryGetProperty("tamanho_bytes", out var t) ? Num(a, "tamanho_bytes") : 0,
                    Origem = Str(a, "origem") ?? "manual", EmUso = a.TryGetProperty("em_uso", out var u) && u.ValueKind == JsonValueKind.True,
                });
            }
        }
        /* Os lotes de copy: do mais antigo ao mais novo na consulta; o número é o `copy_lote.numero` gravado pelo
           Agente de Copy (o mesmo na lista e no detalhe), e só o lote antigo sem número cai na posição.
           Na lista do cliente o mais novo vem primeiro, como o desktop já faz ao gerar. */
        var lotes = r.Json("lotes");
        if (!string.IsNullOrWhiteSpace(lotes))
        {
            var n = 0;
            foreach (var l in JsonDocument.Parse(lotes).RootElement.EnumerateArray())
            {
                n++;
                var lote = new LoteDeCopy
                {
                    Id = Str(l, "lote_id") ?? "", Numero = Num(l, "numero") is > 0 and var numero ? (int)numero : n, Modelo = Str(l, "modelo") ?? "", Briefing = Str(l, "briefing"), NichoSlug = c.NichoSlug,
                    CriadoEm = DateTime.TryParse(Str(l, "criado_em"), out var dt) ? dt : DateTime.MinValue, TokensIn = (int)Num(l, "tokens_in"), TokensOut = (int)Num(l, "tokens_out"),
                };
                if (l.TryGetProperty("pecas", out var ps) && ps.ValueKind == JsonValueKind.Array)
                    foreach (var p in ps.EnumerateArray())
                        lote.Pecas.Add(new PecaDeCopy
                        {
                            Id = Str(p, "peca_id") ?? "", Etapa = Enum.TryParse<Etapa>(Str(p, "funil"), true, out var et) ? et : Etapa.MOFU,
                            Tipo = Str(p, "tipo") == "video" ? TipoPeca.Video : TipoPeca.Estatico, Numero = (int)Num(p, "ordem"),
                            Headline = Str(p, "headline") ?? "", Sub = Str(p, "corpo") ?? "", Cta = Str(p, "cta") ?? "",
                            AnguloHeadline = Str(p, "angulo_headline") ?? "outro", PapelSub = Str(p, "papel_sub") ?? "fragmento", TipoCta = Str(p, "tipo_cta") ?? "outro",
                            Aprovada = p.TryGetProperty("aprovada", out var ap) && ap.ValueKind == JsonValueKind.True,
                            CrivoOk = p.TryGetProperty("crivo_ok", out var ck) && ck.ValueKind == JsonValueKind.True,
                        });
                c.Lotes.Insert(0, lote);
            }
        }
        var pub = r.Json("publicacao");
        if (!string.IsNullOrWhiteSpace(pub))
        {
            var p = JsonDocument.Parse(pub).RootElement;
            var publicacao = new Publicacao
            {
                EstruturaHash = Str(p, "estrutura_hash") ?? "", CampanhaId = Str(p, "campanha_id"), FormularioId = Str(p, "formulario_id"), Estado = Str(p, "estado") ?? "ok",
                VaultSha = Str(p, "vault_sha") ?? "", Erro = Str(p, "erro"),
                Em = DateTime.TryParse(Str(p, "criado_em"), out var dt) ? dt : DateTime.MinValue,
            };
            if (!string.IsNullOrEmpty(publicacao.CampanhaId) && !string.IsNullOrEmpty(c.Conta.AdAccountId))
                publicacao.Link = $"https://adsmanager.facebook.com/adsmanager/manage/campaigns?act={c.Conta.AdAccountId}&selected_campaign_ids={publicacao.CampanhaId}";
            if (p.TryGetProperty("conjuntos", out var cjs) && cjs.ValueKind == JsonValueKind.Array)
                foreach (var cj in cjs.EnumerateArray())
                    publicacao.Conjuntos.Add(new ConjuntoCriado(Str(cj, "nome") ?? "", Enum.TryParse<Etapa>(Str(cj, "etapa"), out var et) ? et : Etapa.MOFU, Str(cj, "publico") ?? "", Str(cj, "adset_id") ?? "", 0));
            if (p.TryGetProperty("avisos", out var avs) && avs.ValueKind == JsonValueKind.Array)
                foreach (var av in avs.EnumerateArray()) publicacao.Avisos.Add(av.GetString() ?? "");
            c.Publicacao = publicacao;
        }
        var artes = TryJson(r, "artes");
        if (!string.IsNullOrWhiteSpace(artes))
            foreach (var a in JsonDocument.Parse(artes).RootElement.EnumerateArray())
            {
                /* o Designer grava o funil em maiúsculas (MOFU/BOFU/TOFU/MISTO); umas poucas peças antigas vieram como "meio" (V0) */
                var funil = (Str(a, "funil") ?? "").ToUpperInvariant() switch { "MEIO" => "MOFU", "TOPO" => "TOFU", "FUNDO" => "BOFU", var f => f };
                c.Artes.Add(new Arte
                {
                    /* copy_peca_id (migration 0149 do V1) liga a arte à peça de copy: é por ele que o validador de assento sabe a etapa real */
                    Id = Str(a, "id") ?? "", PecaId = Str(a, "copy_peca_id") ?? "", Etapa = Enum.TryParse<Etapa>(funil, out var et) ? et : Etapa.MOFU, Formato = Str(a, "formato") ?? "feed",
                    Titulo = $"{funil} · peça {Num(a, "numero")} · {Str(a, "formato")} · lote {Num(a, "lote")}", Url = Str(a, "url") ?? "", Lote = (int)Num(a, "lote"),
                    CriadoEm = DateTime.TryParse(Str(a, "criado_em"), out var dt2) ? dt2 : DateTime.MinValue, Aprovada = true,
                    Template = Str(a, "template_id"),
                });
            }
        var recs = TryJson(r, "recomendacoes");
        if (!string.IsNullOrWhiteSpace(recs)) c.Recomendacoes = Automacao.ParseRecs(JsonDocument.Parse(recs).RootElement, c.Slug);
        else
        {
            var pendentes = (int)r.L("pendentes");
            for (var i = 0; i < pendentes; i++)
                c.Recomendacoes.Add(new Recomendacao { Id = $"pendente-{i}", ClienteSlug = c.Slug, MetaId = "", Nome = "", Acao = Acao.ALERTAR, Decisao = new Decisao(Acao.ALERTAR, [], [], [], new NumerosDaDecisao(0, 0, null, null), false, null, false) });
        }
        return c;
    }

    static string? TryJson(ILinha r, string col) { try { return r.Json(col); } catch { return null; } }
    static string? Str(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    /// <summary>Um inteiro vindo do JSON, tolerante: número com fração arredonda, texto numérico converte, o resto é 0.</summary>
    static long Num(JsonElement o, string n) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(n, out var v) ? v switch
    {
        { ValueKind: JsonValueKind.Number } when v.TryGetInt64(out var i) => i,
        { ValueKind: JsonValueKind.Number } => (long)Math.Round(v.GetDouble()),
        { ValueKind: JsonValueKind.String } => double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (long)Math.Round(d) : 0,
        _ => 0,
    } : 0;

    static string? Junta(ILinha r, string col)
    {
        var lista = r.Textos(col);
        if (lista.Count > 0) return string.Join("\n", lista);
        var s = r.S(col);
        return string.IsNullOrWhiteSpace(s) || s == "{}" || s == "[]" ? null : s;
    }
}
