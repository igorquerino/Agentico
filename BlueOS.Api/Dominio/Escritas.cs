using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
   · decisão     → Dominio/Executor.cs (o aceite aplica na Meta pelo executor)
   Cada escrita é um lote numa transação: um round-trip em qualquer transporte.

   O card Tráfego e o critério de MQL seguem o V1 (contas-v1.ts, mql/route.ts):
   campo que NÃO VEIO no corpo não é tocado, campo que veio null limpa; e a
   linha de `trafego_contas` nasce quando falta (upsert), com o squad PRINCIPAL
   vigente do cliente. O setup do motor deixa trilha na aba 'setup'
   (`mudou_setup_do_motor`), com antes e depois no modo e na chave geral.
   ============================================================================ */
public sealed record ResultadoDaEscrita(bool Ok, string? Erro = null, int Status = 400);

/// <summary>Um campo do corpo que distingue "não veio" (não mexe) de "veio, mesmo null" (grava; null limpa): o `undefined` do V1.</summary>
public readonly record struct Campo<T>(bool Veio, T Valor)
{
    public static Campo<T> NaoVeio => default;
    public static Campo<T> De(T valor) => new(true, valor);
}

public sealed class Escritas(Db db, ILogger<Escritas> log)
{
    static readonly ResultadoDaEscrita Feito = new(true);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly JsonSerializerOptions JsonDoCorpo = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    /// <summary>mql/route.ts do V1: um teto absurdo faz o motor nunca cortar nada.</summary>
    public const decimal TetoMqlMaximoReais = 100_000m;

    /// <summary>Aprovar uma peça de copy é decisão de gente (AD-5): copy_peca.aprovada, só se a peça é deste cliente.</summary>
    public async Task<ResultadoDaEscrita> AprovarPecaAsync(string slug, string pecaId, bool aprovada)
    {
        if (!Guid.TryParse(pecaId, out var id)) return new(false, "id da peça inválido");
        var ok = await db.ListaAsync(Sql.Fmt($"""
            update public.copy_peca p set aprovada = {aprovada}
            from public.copy_lote_item i join public.copy_lote l on l.lote_id = i.lote_id join public.clientes c on c.cliente_id = l.cliente_id
            where p.item_id = i.item_id and p.peca_id = {id} and c.cliente_slug = {slug}
            returning p.peca_id::text as peca_id
            """));
        return ok.Count == 0 ? new(false, "a peça não é deste cliente", 404) : Feito;
    }

    /* ---- o corpo: ausente, null ou valor --------------------------------------- */

    /// <summary>Um texto do corpo. Ausente = não mexe; null = limpa.</summary>
    public static (Campo<string?> Campo, string? Erro) TextoDoCorpo(JsonElement e, string rotulo) => e.ValueKind switch
    {
        JsonValueKind.Undefined => (Campo<string?>.NaoVeio, null),
        JsonValueKind.Null => (Campo<string?>.De(null), null),
        JsonValueKind.String => (Campo<string?>.De(e.GetString()), null),
        _ => (default, $"{rotulo}: valor inválido."),
    };

    /// <summary>Um valor em reais do corpo: número, ou texto numérico (ponto decimal); vazio limpa.</summary>
    public static (Campo<decimal?> Campo, string? Erro) ReaisDoCorpo(JsonElement e, string rotulo)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Undefined: return (Campo<decimal?>.NaoVeio, null);
            case JsonValueKind.Null: return (Campo<decimal?>.De(null), null);
            case JsonValueKind.Number when e.TryGetDecimal(out var d): return (Campo<decimal?>.De(d), null);
            case JsonValueKind.String:
                var s = (e.GetString() ?? "").Trim();
                if (s == "") return (Campo<decimal?>.De(null), null);
                if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var t)) return (Campo<decimal?>.De(t), null);
                break;
        }
        return (default, $"{rotulo}: tem de ser um número em reais.");
    }

    /// <summary>A régua do corpo: objeto v2, ou null para tirar a régua.</summary>
    public static (Campo<ReguaV2?> Campo, string? Erro) ReguaDoCorpo(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Undefined: return (Campo<ReguaV2?>.NaoVeio, null);
            case JsonValueKind.Null: return (Campo<ReguaV2?>.De(null), null);
            case JsonValueKind.Object:
                try { return (Campo<ReguaV2?>.De(e.Deserialize<ReguaV2>(JsonDoCorpo)), null); }
                catch (JsonException ex) { return (default, $"régua inválida: {ex.Message}"); }
        }
        return (default, "régua inválida: precisa ser { versao: 2, perguntas: [...] } ou null.");
    }

    /// <summary>O teto por MQL: maior que zero e até R$ 100.000 (mql/route.ts do V1); null desliga o motor no cliente.</summary>
    public static string? ProblemaDoTeto(decimal? teto) => teto switch
    {
        null => null,
        <= 0 => "o teto por MQL tem de ser maior que zero (em R$), ou vazio para desligar.",
        > TetoMqlMaximoReais => "teto por MQL acima de R$ 100.000 — confere se não sobrou um zero.",
        _ => null,
    };

    /* ---- o nicho: a lista do código ou o catálogo ativo ------------------------- */

    /// <summary>nichoValido do V1 (nicho/catalogo.ts): os do código passam sem ir ao banco; os outros, só se ativos em
    /// `trafego_nicho`. `naDuvida`: com a leitura do catálogo falhada, o slug bem formado conta (só para LER, nunca para gravar).</summary>
    public async Task<bool> NichoValidoAsync(string? slug, bool naDuvida = false)
    {
        if (string.IsNullOrEmpty(slug)) return false;
        if (Nichos.Valido(slug)) return true;
        try
        {
            var l = await db.ListaAsync(Sql.Fmt($"select 1 as um from public.trafego_nicho where slug = {slug} and estado = 'ativo' limit 1"));
            return l.Count > 0;
        }
        catch (Exception e) when (e is not BancoNaoConfigurado)
        {
            log.LogWarning("não li o catálogo de nichos: {Erro}", e.Message);
            return naDuvida && System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$");
        }
    }

    /* ---- o card Tráfego e o critério de MQL --------------------------------------- */

    /// <summary>O squad PRINCIPAL vigente do cliente (nome em maiúsculas), ou null: é o que vai em `trafego_contas.squad_nome`.</summary>
    public static string SquadPrincipalSql(string slug) => Sql.Fmt($"""
        (select upper(trim(s.squad_nome)) from public.cliente_squads cs join public.squads s on s.squad_id = cs.squad_id join public.clientes c on c.cliente_id = cs.cliente_id
         where c.cliente_slug = {slug} and cs.cliente_squad_tipo = 'principal' and cs.cliente_squad_ate is null
         order by cs.cliente_squad_desde desc nulls last limit 1)
        """);

    /// <summary>Upsert de colunas do setup em `trafego_contas` (contas-v1.ts do V1): a linha nasce quando falta, com o
    /// squad principal; na linha que já existe, só as colunas dadas mudam. As colunas são nomes fixos do código.</summary>
    internal static string UpsertDaContaSql(string slug, Guid por, IReadOnlyList<(string Coluna, object? Valor)> campos)
    {
        var cols = string.Concat(campos.Select(c => ", " + c.Coluna));
        var vals = string.Concat(campos.Select(c => ", " + Sql.Lit(c.Valor)));
        var sets = string.Concat(campos.Select(c => $"{c.Coluna} = excluded.{c.Coluna}, "));
        return Sql.Fmt($"insert into public.trafego_contas (cliente_slug{new Raw(cols)}, squad_nome, atualizado_em, atualizado_por, origem) values ({slug}{new Raw(vals)}, {new Raw(SquadPrincipalSql(slug))}, now(), {por}, 'api')")
            + $"\non conflict (cliente_slug) do update set {sets}atualizado_em = now(), atualizado_por = excluded.atualizado_por";
    }

    public async Task<ResultadoDaEscrita> CardTrafegoAsync(EscopoDaSessao esc, string slug, Campo<string?> nicho, Campo<decimal?> budget)
    {
        if (!nicho.Veio && !budget.Veio) return new(false, "nada para salvar: mande o nicho ou o budget mensal.");
        var nichoSlug = string.IsNullOrEmpty(nicho.Valor) ? null : nicho.Valor;
        if (nicho.Veio && nichoSlug is not null && !await NichoValidoAsync(nichoSlug)) return new(false, $"nicho desconhecido: {nichoSlug}");
        if (budget.Veio && budget.Valor is <= 0 or > Dinheiro.BudgetMaximoReais) return new(false, "o budget mensal tem de ser um valor em reais maior que zero e abaixo de R$ 10 milhões.");
        var u = Guid.Parse(esc.UserId);
        var lote = new List<string>();
        if (nicho.Veio) lote.Add(UpsertDaContaSql(slug, u, [("nicho_slug", nichoSlug)]));
        if (budget.Veio)
            lote.Add(Sql.Fmt($"""
                insert into public.cliente_estrategia (cliente_id, budget_ads_mensal, atualizado_em)
                select c.cliente_id, {budget.Valor}, now() from public.clientes c where c.cliente_slug = {slug}
                on conflict (cliente_id) do update set budget_ads_mensal = excluded.budget_ads_mensal, atualizado_em = now()
                """));
        await db.ExecutarLoteAsync(lote);
        await TrilhaDoSetup.GravarAsync(db, log, "mudou_setup_do_motor", u, slug, detalhe: new
        {
            card = "trafego", porta = "api",
            nicho = nicho.Veio ? (object?)nichoSlug : "nao mexeu",
            budget_mensal = budget.Veio ? (object?)budget.Valor : "nao mexeu",
        });
        return Feito;
    }

    public async Task<ResultadoDaEscrita> CriterioMqlAsync(EscopoDaSessao esc, string slug, Campo<ReguaV2?> regua, Campo<decimal?> teto)
    {
        if (!regua.Veio && !teto.Veio) return new(false, "nada para salvar: mande a régua ou o teto por MQL.");
        var r = regua.Valor;
        if (regua.Veio && r is not null)
        {
            var problemas = Regua.ProblemasDoRascunho(r.Perguntas);
            if (problemas.Count > 0) return new(false, string.Join(" ", problemas));
            r.Perguntas = r.Perguntas.Where(p => !string.IsNullOrWhiteSpace(p.Nome)).ToList();
            if (r.Perguntas.Count == 0) r = null;
        }
        if (teto.Veio && ProblemaDoTeto(teto.Valor) is { } erroDoTeto) return new(false, erroDoTeto);
        var campos = new List<(string, object?)>();
        if (regua.Veio)
        {
            campos.Add(("regua_mql", r is null ? (Jsonb?)null : new Jsonb(JsonSerializer.Serialize(new
            {
                versao = 2,
                perguntas = r.Perguntas.Select(p => new { nome = p.Nome.Trim(), chaves = p.Chaves, rotulo = p.Rotulo, forte = p.Forte, media = p.Media, elim = p.Elim }),
            }, Json))));
            /* vigenciaAoSalvarARegua do V1: salvar não carimba a vigência; tirar a régua limpa a data junto */
            if (r is null) campos.Add(("regua_vigente_desde", null));
        }
        if (teto.Veio) campos.Add(("teto_mql", teto.Valor));
        var u = Guid.Parse(esc.UserId);
        await db.ExecutarLoteAsync([UpsertDaContaSql(slug, u, campos)]);
        await TrilhaDoSetup.GravarAsync(db, log, "mudou_setup_do_motor", u, slug, detalhe: new
        {
            porta = "api",
            regua = !regua.Veio ? "nao mexeu" : r is null ? "removida" : $"{r.Perguntas.Count} pergunta(s)",
            teto_mql = teto.Veio ? (object?)teto.Valor : "nao mexeu",
        });
        return Feito;
    }

    /* ---- a ficha ------------------------------------------------------------------ */

    /// <summary>parseWhatsapp do V1 (informacoes-cliente.ts): só dígitos, com DDI; 10 a 15 dígitos (E.164); celular BR
    /// sem DDI ganha o 55. Fora disso é recusa, não chute. Vazio = null.</summary>
    public static (bool Ok, string? Valor, string? Erro) WhatsappE164(string? bruto, string rotulo = "WhatsApp")
    {
        if (string.IsNullOrWhiteSpace(bruto)) return (true, null, null);
        var digitos = new string(bruto.Where(char.IsAsciiDigit).ToArray());
        if (digitos.Length is < 10 or > 15) return (false, null, $"{rotulo}: use DDD + número (ex.: 61 99582-1272).");
        var valor = digitos.Length <= 11 && !digitos.StartsWith("55", StringComparison.Ordinal) ? "55" + digitos : digitos;
        if (valor.Length > 15) return (false, null, $"{rotulo}: número longo demais.");
        return (true, valor, null);
    }

    /// <summary>O recorte do ICP, só se for DESTE cliente: o id do recorte vem do corpo.</summary>
    internal static string RecorteSql(Guid clienteId, Guid segmentoId, Icp i) => Sql.Fmt(
        $"update public.cliente_icp_segmento set setor = {Vazio(i.Setor)}, descricao = {Vazio(i.Segmento)}, cargo_decisor = {Vazio(i.CargoDecisor)}, faturamento_minimo = {Vazio(i.FaturamentoMinimo)}, localizacao = {Vazio(i.Localizacao)} where segmento_id = {segmentoId} and cliente_id = {clienteId}");

    public async Task<ResultadoDaEscrita> FichaAsync(EscopoDaSessao esc, string slug, FichaPedido p)
    {
        var id = await db.UmAsync(Sql.Fmt($"select cliente_id::text as id from public.clientes where cliente_slug = {slug}"), r => r.S("id"));
        if (id is null) return new(false, "cliente não encontrado", 404);
        var cid = Guid.Parse(id);
        var lote = new List<string>();
        if (p.Ficha is { } f)
        {
            var wa = WhatsappE164(f.Whatsapp);
            if (!wa.Ok) return new(false, wa.Erro);
            lote.Add(Sql.Fmt($"""
                update public.clientes set
                  cliente_responsavel = coalesce({Vazio(f.Responsavel)}, cliente_responsavel), cliente_contato_principal = coalesce({Vazio(f.ContatoPrincipal)}, cliente_contato_principal),
                  cliente_cnpj = coalesce({Vazio(f.Cnpj)}, cliente_cnpj), cliente_razao_social = coalesce({Vazio(f.RazaoSocial)}, cliente_razao_social),
                  cliente_whatsapp = coalesce({wa.Valor}, cliente_whatsapp),
                  cliente_whatsapp_origem = case when {wa.Valor} is null then cliente_whatsapp_origem else 'manual' end
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
                lote.Add(RecorteSql(cid, sid, i));
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

    /* ---- o modo e a chave geral ------------------------------------------------------ */

    public async Task<ResultadoDaEscrita> ModoAsync(EscopoDaSessao esc, string slug, ModoDeExecucao modo)
    {
        var novo = modo.ToString().ToLowerInvariant();
        var u = Guid.Parse(esc.UserId);
        /* o `antes` sai do mesmo statement: a CTE lê a foto de antes do update */
        var l = await db.ListaAsync(Sql.Fmt($"""
            with antes as (select motor_modo from public.trafego_contas where cliente_slug = {slug})
            update public.trafego_contas set motor_modo = {novo}, atualizado_em = now(), atualizado_por = {u} where cliente_slug = {slug}
            returning (select a.motor_modo from antes a limit 1) as antes
            """));
        if (l.Count == 0) return new(false, "o cliente não tem registro de tráfego: não há onde gravar o modo.");
        await TrilhaDoSetup.GravarAsync(db, log, "mudou_setup_do_motor", u, slug,
            antes: new { modo = l[0].S("antes") }, depois: new { modo = novo }, detalhe: new { via = "motor", o_que = "modo", porta = "api" });
        return Feito;
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
        var antes = await ChaveGeralAsync();
        await db.ExecutarLoteAsync([Sql.Fmt($"""
            insert into public.trafego_motor_chave_geral (id, ligada, motivo, por, mudado_em) values (1, {ligada}, {(ligada ? null : motivo?.Trim())}, {esc.UserId}, now())
            on conflict (id) do update set ligada = excluded.ligada, motivo = excluded.motivo, por = excluded.por, mudado_em = now()
            """)]);
        await TrilhaDoSetup.GravarAsync(db, log, "mudou_setup_do_motor", Guid.Parse(esc.UserId), null,
            antes: new { chave_geral = antes.Ligada }, depois: new { chave_geral = ligada, motivo = ligada ? null : motivo?.Trim() },
            detalhe: new { via = "motor", o_que = "chave_geral", porta = "api" });
        return Feito;
    }

    static string? Vazio(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    static string[] Linhas(string? v) => (v ?? "").Split('\n').Select(x => x.Trim()).Where(x => x != "").ToArray();
}

/// <summary>A trilha do SETUP (`trafego_auditoria`, aba 'setup'), no contrato do `registrar` do V1 (lib/trafego/trilha.ts):
/// é onde a auditoria do V1 procura modo, chave geral, régua, nicho e token. Registra o ato, nunca o segredo. Nunca lança.</summary>
public static class TrilhaDoSetup
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static Jsonb? J(object? v) => v is null ? null : new Jsonb(JsonSerializer.Serialize(v, Json));

    internal static string InsertSql(string evento, Guid? atorId, string? slug, string resultado, string? erro, string? alvoTipo, string? alvoId, string? alvoNome, object? antes, object? depois, object? detalhe, string? squadId) => Sql.Fmt($"""
        insert into public.trafego_auditoria (aba, evento, ator_id, origem, cliente_slug, squad_id, alvo_tipo, alvo_id, alvo_nome, antes, depois, resultado, erro, detalhe, ocorrido_em)
        values ('setup', {evento}, {atorId}, 'tela', {slug}, {squadId}, {alvoTipo}, {alvoId}, {alvoNome}, {J(antes)}, {J(depois)}, {resultado}, {erro}, {J(detalhe)}, now())
        """);

    public static async Task GravarAsync(Db db, ILogger log, string evento, Guid? atorId, string? slug, string resultado = "ok", string? erro = null,
        string? alvoTipo = null, string? alvoId = null, string? alvoNome = null, object? antes = null, object? depois = null, object? detalhe = null, string? squadId = null)
    {
        try { await db.ExecutarAsync(InsertSql(evento, atorId, slug, resultado, erro, alvoTipo, alvoId, alvoNome, antes, depois, detalhe, squadId)); }
        catch (Exception e) { log.LogWarning("a trilha do setup não gravou ({Evento}): {Erro}", evento, e.Message); }
    }
}
