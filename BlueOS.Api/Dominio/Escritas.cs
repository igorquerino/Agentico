using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Setup;

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
   Cada escrita é um lote numa transação: um round-trip em qualquer transporte.
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
        var u = Guid.Parse(esc.UserId);
        var temConta = await db.ListaAsync(Sql.Fmt($"select 1 as um from public.trafego_contas where cliente_slug = {slug}"));
        if (temConta.Count == 0 && !string.IsNullOrEmpty(nichoSlug)) return new(false, "o cliente não tem registro de tráfego: vincule a conta de anúncios antes de gravar o nicho.");
        var lote = new List<string>();
        if (temConta.Count > 0)
            lote.Add(Sql.Fmt($"update public.trafego_contas set nicho_slug = {(string.IsNullOrEmpty(nichoSlug) ? null : nichoSlug)}, atualizado_em = now(), atualizado_por = {u} where cliente_slug = {slug}"));
        lote.Add(Sql.Fmt($"""
            insert into public.cliente_estrategia (cliente_id, budget_ads_mensal, atualizado_em)
            select c.cliente_id, {budget}, now() from public.clientes c where c.cliente_slug = {slug}
            on conflict (cliente_id) do update set budget_ads_mensal = excluded.budget_ads_mensal, atualizado_em = now()
            """));
        await db.ExecutarLoteAsync(lote);
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
        var json = regua is null ? (Jsonb?)null : new Jsonb(JsonSerializer.Serialize(new
        {
            versao = 2,
            perguntas = regua.Perguntas.Select(p => new { nome = p.Nome.Trim(), chaves = p.Chaves, rotulo = p.Rotulo, forte = p.Forte, media = p.Media, elim = p.Elim }),
        }, Json));
        var n = await db.ExecutarAsync(Sql.Fmt($"update public.trafego_contas set regua_mql = {json}, teto_mql = {teto}, atualizado_em = now(), atualizado_por = {Guid.Parse(esc.UserId)} where cliente_slug = {slug}"));
        return n == 0 ? new(false, "o cliente não tem registro de tráfego: vincule a conta de anúncios antes do critério de MQL.") : Feito;
    }

    public async Task<ResultadoDaEscrita> FichaAsync(EscopoDaSessao esc, string slug, FichaPedido p)
    {
        var id = await db.UmAsync(Sql.Fmt($"select cliente_id::text as id from public.clientes where cliente_slug = {slug}"), r => r.S("id"));
        if (id is null) return new(false, "cliente não encontrado", 404);
        var cid = Guid.Parse(id);
        var lote = new List<string>();
        if (p.Ficha is { } f)
        {
            lote.Add(Sql.Fmt($"""
                update public.clientes set
                  cliente_responsavel = coalesce({Vazio(f.Responsavel)}, cliente_responsavel), cliente_contato_principal = coalesce({Vazio(f.ContatoPrincipal)}, cliente_contato_principal),
                  cliente_cnpj = coalesce({Vazio(f.Cnpj)}, cliente_cnpj), cliente_razao_social = coalesce({Vazio(f.RazaoSocial)}, cliente_razao_social),
                  cliente_whatsapp = coalesce({Vazio(f.Whatsapp)}, cliente_whatsapp),
                  cliente_whatsapp_origem = case when {Vazio(f.Whatsapp)} is null then cliente_whatsapp_origem else 'manual' end
                where cliente_id = {cid}
                """));
        }
        if (p.Icp is { } i)
        {
            lote.Add(Sql.Fmt($"""
                insert into public.cliente_icp (cliente_id, tipo, nivel_consciencia, temperatura, tamanho_mercado, persona, publico_alvo, obs, dores_principais, desejos_principais, canais_onde_estao, atualizado_em)
                values ({cid}, {Vazio(i.Tipo)}, {Vazio(i.NivelConsciencia)}, {Vazio(i.Temperatura)}, {Vazio(i.TamanhoMercado)}, {Vazio(i.Persona)}, {Vazio(i.PublicoAlvo)}, {Vazio(i.Obs)}, {i.DoresPrincipais.ToArray()}, {i.DesejosPrincipais.ToArray()}, {i.CanaisOndeEstao.ToArray()}, now())
                on conflict (cliente_id) do update set tipo = excluded.tipo, nivel_consciencia = excluded.nivel_consciencia, temperatura = excluded.temperatura,
                  tamanho_mercado = excluded.tamanho_mercado, persona = excluded.persona, publico_alvo = excluded.publico_alvo, obs = excluded.obs,
                  dores_principais = excluded.dores_principais, desejos_principais = excluded.desejos_principais, canais_onde_estao = excluded.canais_onde_estao, atualizado_em = now()
                """));
            var temRecorte = !string.IsNullOrWhiteSpace(i.Setor) || !string.IsNullOrWhiteSpace(i.Segmento) || !string.IsNullOrWhiteSpace(i.CargoDecisor) || !string.IsNullOrWhiteSpace(i.FaturamentoMinimo) || !string.IsNullOrWhiteSpace(i.Localizacao);
            if (!string.IsNullOrEmpty(i.SegmentoId) && Guid.TryParse(i.SegmentoId, out var sid))
                lote.Add(Sql.Fmt($"update public.cliente_icp_segmento set setor = {Vazio(i.Setor)}, descricao = {Vazio(i.Segmento)}, cargo_decisor = {Vazio(i.CargoDecisor)}, faturamento_minimo = {Vazio(i.FaturamentoMinimo)}, localizacao = {Vazio(i.Localizacao)} where segmento_id = {sid}"));
            else if (temRecorte)
                lote.Add(Sql.Fmt($"insert into public.cliente_icp_segmento (cliente_id, setor, descricao, cargo_decisor, faturamento_minimo, localizacao, ordem) values ({cid}, {Vazio(i.Setor)}, {Vazio(i.Segmento)}, {Vazio(i.CargoDecisor)}, {Vazio(i.FaturamentoMinimo)}, {Vazio(i.Localizacao)}, 1)"));
        }
        if (p.Produto is { } pr)
        {
            lote.Add(Sql.Fmt($"""
                insert into public.cliente_produto (cliente_id, nome_correto, descricao, modelo_negocio, diferenciais, funcionalidades_chave, objecoes_comuns, concorrentes, atualizado_em)
                values ({cid}, {Vazio(pr.NomeCorreto)}, {Vazio(pr.Descricao)}, {Vazio(pr.ModeloNegocio)}, {Linhas(pr.Diferenciais)}, {Linhas(pr.FuncionalidadesChave)}, {Linhas(pr.ObjecoesComuns)}, {Linhas(pr.Concorrentes)}, now())
                on conflict (cliente_id) do update set nome_correto = excluded.nome_correto, descricao = excluded.descricao, modelo_negocio = excluded.modelo_negocio,
                  diferenciais = excluded.diferenciais, funcionalidades_chave = excluded.funcionalidades_chave, objecoes_comuns = excluded.objecoes_comuns, concorrentes = excluded.concorrentes, atualizado_em = now()
                """));
        }
        if (p.Estrategia is { } e)
        {
            lote.Add(Sql.Fmt($"""
                insert into public.cliente_estrategia (cliente_id, promessa_principal, posicionamento, diferencial, funil_tipo, gatilhos, atualizado_em)
                values ({cid}, {Vazio(e.PromessaPrincipal)}, {Vazio(e.Posicionamento)}, {Vazio(e.Diferencial)}, {Vazio(e.FunilTipo)}, {Linhas(e.Gatilhos)}, now())
                on conflict (cliente_id) do update set promessa_principal = excluded.promessa_principal, posicionamento = excluded.posicionamento,
                  diferencial = excluded.diferencial, funil_tipo = excluded.funil_tipo, gatilhos = excluded.gatilhos, atualizado_em = now()
                """));
        }
        if (lote.Count == 0) return new(false, "nada pra atualizar");
        await db.ExecutarLoteAsync(lote);
        return Feito;
    }

    public async Task<ResultadoDaEscrita> ModoAsync(string slug, ModoDeExecucao modo)
    {
        var n = await db.ExecutarAsync(Sql.Fmt($"update public.trafego_contas set motor_modo = {modo.ToString().ToLowerInvariant()} where cliente_slug = {slug}"));
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
        await db.ExecutarLoteAsync([Sql.Fmt($"""
            insert into public.trafego_motor_chave_geral (id, ligada, motivo, por, mudado_em) values (1, {ligada}, {(ligada ? null : motivo?.Trim())}, {esc.UserId}, now())
            on conflict (id) do update set ligada = excluded.ligada, motivo = excluded.motivo, por = excluded.por, mudado_em = now()
            """)]);
        return Feito;
    }

    /// <summary>Negar, ou aceitar o que não escreve na Meta. O aceite de PAUSAR e DUPLICAR passa pelo V1 (executor).</summary>
    public async Task<ResultadoDaEscrita> DecidirAsync(EscopoDaSessao esc, string slug, string recomendacaoId, bool aceita, string? motivo)
    {
        if (!aceita && string.IsNullOrWhiteSpace(motivo)) return new(false, "negar pede motivo: é o que diz ao motor onde ele errou.");
        if (!Guid.TryParse(recomendacaoId, out var rid)) return new(false, "recomendação não encontrada", 404);
        var rec = await db.UmAsync(Sql.Fmt($"""
            select r.acao, (select d.decisao from public.trafego_motor_decisao d where d.recomendacao_id = r.id order by d.em desc limit 1) as ultima
            from public.trafego_motor_recomendacao r where r.id = {rid} and r.cliente_slug = {slug}
            """), r => new { Acao = r.S("acao") ?? "", Ultima = r.S("ultima") });
        if (rec is null) return new(false, "recomendação não encontrada", 404);
        if (rec.Ultima is "aplicada" or "incerto") return new(false, "esta recomendação já mexeu na conta: negar não desfaz. Desfazer é no Gerenciador.", 409);
        if (aceita && Enum.TryParse<Acao>(rec.Acao, out var a) && Arvore.Escreve(a))
            return new(false, "aceitar uma ação que escreve na Meta ainda passa pelo V1 (o executor não foi portado): o app faz isso pela fonte V1.", 409);
        if (aceita && !esc.PodeAplicarNaMeta) return new(false, "aceitar recomendação do motor é de head ou acima. Negar você pode.", 403);
        await db.ExecutarLoteAsync([Sql.Fmt($"insert into public.trafego_motor_decisao (recomendacao_id, decisao, por, motivo) values ({rid}, {(aceita ? "aceita" : "negada")}, {Guid.Parse(esc.UserId)}, {motivo?.Trim()})")]);
        return Feito;
    }

    static string? Vazio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    static string[] Linhas(string? v) => (v ?? "").Split('\n').Select(x => x.Trim()).Where(x => x != "").ToArray();
}
