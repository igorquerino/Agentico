using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Setup;
using NpgsqlTypes;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   AS ESCRITAS: cada campo mora onde já morava no V1.
   ----------------------------------------------------------------------------
   · nicho       → trafego_contas.nicho_slug
   · budget      → cliente_estrategia.budget_ads_mensal
   · régua/teto  → trafego_contas.regua_mql / teto_mql
   · contato     → clientes.cliente_*
   · ICP         → cliente_icp (perfil) + cliente_icp_segmento (recorte principal)
   · produto     → cliente_produto;  estratégia → cliente_estrategia
   · modo        → trafego_contas.motor_modo;  chave geral → trafego_motor_chave_geral
   · decisão     → trafego_motor_decisao (append-only; o aceite que escreve na Meta
                   continua pelo V1 até o executor existir aqui)
   ============================================================================ */
public sealed record ResultadoDaEscrita(bool Ok, string? Erro = null, int Status = 400);

public sealed class Escritas(Db db)
{
    static readonly ResultadoDaEscrita Feito = new(true);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ResultadoDaEscrita> CardTrafegoAsync(EscopoDaSessao esc, string slug, string? nichoSlug, decimal? budget)
    {
        if (nichoSlug is not null && nichoSlug != "" && !Nichos.Valido(nichoSlug)) return new(false, $"nicho desconhecido: {nichoSlug}");
        if (budget is <= 0 or > Dinheiro.BudgetMaximoReais) return new(false, "o budget mensal tem de ser um valor em reais maior que zero e abaixo de R$ 10 milhões.");
        await using var cx = await db.AbrirAsync();
        await using var tx = await cx.BeginTransactionAsync();
        var n = await Db.ExecutarAsync(cx, tx, "update public.trafego_contas set nicho_slug = @n, atualizado_em = now(), atualizado_por = @u::uuid where cliente_slug = @s",
            ("n", string.IsNullOrEmpty(nichoSlug) ? null : nichoSlug), ("u", esc.UserId), ("s", slug));
        if (n == 0 && !string.IsNullOrEmpty(nichoSlug)) return new(false, "o cliente não tem registro de tráfego: vincule a conta de anúncios antes de gravar o nicho.");
        await Db.ExecutarAsync(cx, tx, """
            insert into public.cliente_estrategia (cliente_id, budget_ads_mensal, atualizado_em)
            select c.cliente_id, @b, now() from public.clientes c where c.cliente_slug = @s
            on conflict (cliente_id) do update set budget_ads_mensal = excluded.budget_ads_mensal, atualizado_em = now()
            """, ("b", budget), ("s", slug));
        await tx.CommitAsync();
        return Feito;
    }

    public async Task<ResultadoDaEscrita> CriterioMqlAsync(EscopoDaSessao esc, string slug, ReguaV2? regua, decimal? teto)
    {
        if (regua is not null)
        {
            var problemas = Regua.ProblemasDoRascunho(regua.Perguntas);
            if (problemas.Count > 0) return new(false, string.Join(" ", problemas));
            regua.Perguntas = regua.Perguntas.Where(p => !string.IsNullOrWhiteSpace(p.Nome)).ToList();
            if (regua.Perguntas.Count == 0) regua = null;
        }
        if (teto is <= 0) return new(false, "o teto por MQL tem de ser maior que zero.");
        var json = regua is null ? null : JsonSerializer.Serialize(new
        {
            versao = 2,
            perguntas = regua.Perguntas.Select(p => new { nome = p.Nome.Trim(), chaves = p.Chaves, rotulo = p.Rotulo, forte = p.Forte, media = p.Media, elim = p.Elim }),
        }, Json);
        await using var cx = await db.AbrirAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("update public.trafego_contas set regua_mql = @r, teto_mql = @t, atualizado_em = now(), atualizado_por = @u::uuid where cliente_slug = @s", cx);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("r", NpgsqlDbType.Jsonb) { Value = (object?)json ?? DBNull.Value });
        cmd.Parameters.AddWithValue("t", (object?)teto ?? DBNull.Value);
        cmd.Parameters.AddWithValue("u", esc.UserId);
        cmd.Parameters.AddWithValue("s", slug);
        var n = await cmd.ExecuteNonQueryAsync();
        return n == 0 ? new(false, "o cliente não tem registro de tráfego: vincule a conta de anúncios antes do critério de MQL.") : Feito;
    }

    public async Task<ResultadoDaEscrita> FichaAsync(EscopoDaSessao esc, string slug, FichaPedido p)
    {
        await using var cx = await db.AbrirAsync();
        await using var tx = await cx.BeginTransactionAsync();
        var id = (await Db.ListaAsync(cx, "select cliente_id::text as id from public.clientes where cliente_slug = @s", r => r.S("id"), ("s", slug))).FirstOrDefault();
        if (id is null) return new(false, "cliente não encontrado", 404);
        if (p.Ficha is { } f)
        {
            await Db.ExecutarAsync(cx, tx, """
                update public.clientes set
                  cliente_responsavel = coalesce(@resp, cliente_responsavel), cliente_contato_principal = coalesce(@cont, cliente_contato_principal),
                  cliente_cnpj = coalesce(@cnpj, cliente_cnpj), cliente_razao_social = coalesce(@rz, cliente_razao_social),
                  cliente_whatsapp = coalesce(@wa, cliente_whatsapp), cliente_whatsapp_origem = case when @wa is null then cliente_whatsapp_origem else 'manual' end
                where cliente_id = @id::uuid
                """, ("resp", Vazio(f.Responsavel)), ("cont", Vazio(f.ContatoPrincipal)), ("cnpj", Vazio(f.Cnpj)), ("rz", Vazio(f.RazaoSocial)), ("wa", Vazio(f.Whatsapp)), ("id", id));
        }
        if (p.Icp is { } i)
        {
            await Db.ExecutarAsync(cx, tx, """
                insert into public.cliente_icp (cliente_id, tipo, nivel_consciencia, temperatura, tamanho_mercado, persona, publico_alvo, obs, dores_principais, desejos_principais, canais_onde_estao, atualizado_em)
                values (@id::uuid, @tipo, @nivel, @temp, @tam, @persona, @pub, @obs, @dores, @desejos, @canais, now())
                on conflict (cliente_id) do update set tipo = excluded.tipo, nivel_consciencia = excluded.nivel_consciencia, temperatura = excluded.temperatura,
                  tamanho_mercado = excluded.tamanho_mercado, persona = excluded.persona, publico_alvo = excluded.publico_alvo, obs = excluded.obs,
                  dores_principais = excluded.dores_principais, desejos_principais = excluded.desejos_principais, canais_onde_estao = excluded.canais_onde_estao, atualizado_em = now()
                """, ("id", id), ("tipo", Vazio(i.Tipo)), ("nivel", Vazio(i.NivelConsciencia)), ("temp", Vazio(i.Temperatura)), ("tam", Vazio(i.TamanhoMercado)),
                ("persona", Vazio(i.Persona)), ("pub", Vazio(i.PublicoAlvo)), ("obs", Vazio(i.Obs)),
                ("dores", i.DoresPrincipais.ToArray()), ("desejos", i.DesejosPrincipais.ToArray()), ("canais", i.CanaisOndeEstao.ToArray()));
            var temRecorte = !string.IsNullOrWhiteSpace(i.Setor) || !string.IsNullOrWhiteSpace(i.Segmento) || !string.IsNullOrWhiteSpace(i.CargoDecisor) || !string.IsNullOrWhiteSpace(i.FaturamentoMinimo) || !string.IsNullOrWhiteSpace(i.Localizacao);
            if (!string.IsNullOrEmpty(i.SegmentoId))
                await Db.ExecutarAsync(cx, tx, "update public.cliente_icp_segmento set setor = @setor, descricao = @desc, cargo_decisor = @cargo, faturamento_minimo = @fat, localizacao = @loc where segmento_id = @sid::uuid",
                    ("setor", Vazio(i.Setor)), ("desc", Vazio(i.Segmento)), ("cargo", Vazio(i.CargoDecisor)), ("fat", Vazio(i.FaturamentoMinimo)), ("loc", Vazio(i.Localizacao)), ("sid", i.SegmentoId));
            else if (temRecorte)
                await Db.ExecutarAsync(cx, tx, "insert into public.cliente_icp_segmento (cliente_id, setor, descricao, cargo_decisor, faturamento_minimo, localizacao, ordem) values (@id::uuid, @setor, @desc, @cargo, @fat, @loc, 1)",
                    ("id", id), ("setor", Vazio(i.Setor)), ("desc", Vazio(i.Segmento)), ("cargo", Vazio(i.CargoDecisor)), ("fat", Vazio(i.FaturamentoMinimo)), ("loc", Vazio(i.Localizacao)));
        }
        if (p.Produto is { } pr)
        {
            await Db.ExecutarAsync(cx, tx, """
                insert into public.cliente_produto (cliente_id, nome_correto, descricao, modelo_negocio, diferenciais, funcionalidades_chave, objecoes_comuns, concorrentes, atualizado_em)
                values (@id::uuid, @nome, @desc, @modelo, @dif, @func, @obj, @conc, now())
                on conflict (cliente_id) do update set nome_correto = excluded.nome_correto, descricao = excluded.descricao, modelo_negocio = excluded.modelo_negocio,
                  diferenciais = excluded.diferenciais, funcionalidades_chave = excluded.funcionalidades_chave, objecoes_comuns = excluded.objecoes_comuns, concorrentes = excluded.concorrentes, atualizado_em = now()
                """, ("id", id), ("nome", Vazio(pr.NomeCorreto)), ("desc", Vazio(pr.Descricao)), ("modelo", Vazio(pr.ModeloNegocio)),
                ("dif", Linhas(pr.Diferenciais)), ("func", Linhas(pr.FuncionalidadesChave)), ("obj", Linhas(pr.ObjecoesComuns)), ("conc", Linhas(pr.Concorrentes)));
        }
        if (p.Estrategia is { } e)
        {
            await Db.ExecutarAsync(cx, tx, """
                insert into public.cliente_estrategia (cliente_id, promessa_principal, posicionamento, diferencial, funil_tipo, gatilhos, atualizado_em)
                values (@id::uuid, @prom, @pos, @dif, @funil, @gat, now())
                on conflict (cliente_id) do update set promessa_principal = excluded.promessa_principal, posicionamento = excluded.posicionamento,
                  diferencial = excluded.diferencial, funil_tipo = excluded.funil_tipo, gatilhos = excluded.gatilhos, atualizado_em = now()
                """, ("id", id), ("prom", Vazio(e.PromessaPrincipal)), ("pos", Vazio(e.Posicionamento)), ("dif", Vazio(e.Diferencial)), ("funil", Vazio(e.FunilTipo)), ("gat", Linhas(e.Gatilhos)));
        }
        await tx.CommitAsync();
        return Feito;
    }

    public async Task<ResultadoDaEscrita> ModoAsync(string slug, ModoDeExecucao modo)
    {
        var n = await db.ExecutarAsync("update public.trafego_contas set motor_modo = @m where cliente_slug = @s", ("m", modo.ToString().ToLowerInvariant()), ("s", slug));
        return n == 0 ? new(false, "o cliente não tem registro de tráfego: não há onde gravar o modo.") : Feito;
    }

    public async Task<(bool Ligada, string? Motivo, string? Por, DateTime? MudadoEm)> ChaveGeralAsync()
    {
        var l = await db.UmAsync("select ligada, motivo, por, mudado_em from public.trafego_motor_chave_geral where id = 1",
            r => new { Ligada = r.B("ligada") ?? true, Motivo = r.S("motivo"), Por = r.S("por"), Em = r.Dt("mudado_em") });
        return l is null ? (true, null, null, null) : (l.Ligada, l.Motivo, l.Por, l.Em);
    }

    public async Task<ResultadoDaEscrita> MudarChaveGeralAsync(EscopoDaSessao esc, bool ligada, string? motivo)
    {
        if (!ligada && string.IsNullOrWhiteSpace(motivo)) return new(false, "desligar pede motivo: é o que o próximo a abrir a aba vai ler.");
        await db.ExecutarAsync("""
            insert into public.trafego_motor_chave_geral (id, ligada, motivo, por, mudado_em) values (1, @l, @m, @u, now())
            on conflict (id) do update set ligada = excluded.ligada, motivo = excluded.motivo, por = excluded.por, mudado_em = now()
            """, ("l", ligada), ("m", ligada ? null : motivo?.Trim()), ("u", esc.UserId));
        return Feito;
    }

    /// <summary>Negar, ou aceitar o que não escreve na Meta. O aceite de PAUSAR e DUPLICAR passa pelo V1 (executor).</summary>
    public async Task<ResultadoDaEscrita> DecidirAsync(EscopoDaSessao esc, string slug, string recomendacaoId, bool aceita, string? motivo)
    {
        if (!aceita && string.IsNullOrWhiteSpace(motivo)) return new(false, "negar pede motivo: é o que diz ao motor onde ele errou.");
        var rec = await db.UmAsync("select acao from public.trafego_motor_recomendacao where id = @id::uuid and cliente_slug = @s", r => new { Acao = r.S("acao") ?? "" }, ("id", recomendacaoId), ("s", slug));
        if (rec is null) return new(false, "recomendação não encontrada", 404);
        var ja = await db.UmAsync("select decisao from public.trafego_motor_decisao where recomendacao_id = @id::uuid order by em desc limit 1", r => new { D = r.S("decisao") ?? "" }, ("id", recomendacaoId));
        if (ja is { D: "aplicada" or "incerto" }) return new(false, "esta recomendação já mexeu na conta: negar não desfaz. Desfazer é no Gerenciador.", 409);
        if (aceita && Enum.TryParse<Acao>(rec.Acao, out var a) && Arvore.Escreve(a))
            return new(false, "aceitar uma ação que escreve na Meta ainda passa pelo V1 (o executor não foi portado): o app faz isso pela fonte V1.", 409);
        if (aceita && !esc.PodeAplicarNaMeta) return new(false, "aceitar recomendação do motor é de head ou acima. Negar você pode.", 403);
        await db.ExecutarAsync("insert into public.trafego_motor_decisao (recomendacao_id, decisao, por, motivo) values (@id::uuid, @d, @u::uuid, @m)",
            ("id", recomendacaoId), ("d", aceita ? "aceita" : "negada"), ("u", esc.UserId), ("m", motivo?.Trim()));
        return Feito;
    }

    static string? Vazio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    static string[] Linhas(string? v) => (v ?? "").Split('\n').Select(x => x.Trim()).Where(x => x != "").ToArray();
}
