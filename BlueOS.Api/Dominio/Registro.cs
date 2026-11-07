using System.Globalization;
using System.Text.Json;
using BlueOS.Api.Infra;
using BlueOS.Core.Executor;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O REGISTRO DO MOTOR: append-only, e o status é a última decisão. Porta de
   lib/trafego/motor/registro.ts, avisos.ts, log-acao.ts e trilha.ts.
   ----------------------------------------------------------------------------
   · `trafego_motor_decisao` nunca é atualizada: o estado de uma recomendação é
     a ÚLTIMA linha dela ('aceita'/'negada' do gestor; 'aplicada'/'falhou'/
     'incerto' do servidor);
   · `trafego_motor_aplicacao` é a ÚNICA que muda: a reserva nasce 'em_voo' e
     vira o desfecho. A chave primária é decidida no banco, e é isso que faz o
     aceite ser idempotente (duas requisições, UMA escrita);
   · `exigirAceite` é a trava: sem decisão 'aceita' registrada, não há escrita.
   ============================================================================ */
public sealed record Reserva(bool Ok, bool Retentativa, string? Motivo, string? Estado);
public sealed record EstadoDaAplicacao(string Estado, string EstadoNoBanco, string? ComecouEmTexto, DateTime? ComecouEm);
public sealed record RecomendacaoLida(Guid Id, string ClienteSlug, string Acao, string MetaId, JsonElement Numeros, IReadOnlyList<string> Chaves, string? VaultSha, string? Ultima);

public sealed class Registro(Db db, ILogger<Registro> log)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly TimeSpan PrazoEmVoo = TimeSpan.FromMinutes(10);
    static readonly TimeSpan FolgaDeRelogio = TimeSpan.FromMinutes(2);

    static Jsonb J(object? v) => new(JsonSerializer.Serialize(v, Json));
    static readonly Jsonb Vazio = new("{}");

    /// <summary>A violação de unicidade, nos dois transportes: o SQLSTATE do Npgsql ou a frase do Postgres na Management API.</summary>
    static bool EhUnicidade(Exception e) =>
        (e is Npgsql.PostgresException p && p.SqlState == "23505") || e.Message.Contains("23505") || e.Message.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase);

    /* ---- a recomendação ------------------------------------------------------- */

    public async Task<RecomendacaoLida?> LerAsync(Guid recId) => await db.UmAsync(Sql.Fmt($"""
        select r.id::text as id, r.cliente_slug, r.acao, r.meta_id, r.numeros::text as numeros, r.chaves, r.vault_sha,
               (select d.decisao from public.trafego_motor_decisao d where d.recomendacao_id = r.id order by d.em desc, d.id desc limit 1) as ultima
        from public.trafego_motor_recomendacao r where r.id = {recId}
        """), r => new RecomendacaoLida(Guid.Parse(r.S("id")!), r.S("cliente_slug") ?? "", r.S("acao") ?? "", r.S("meta_id") ?? "",
            JsonDocument.Parse(string.IsNullOrWhiteSpace(r.Json("numeros")) ? "{}" : r.Json("numeros")!).RootElement.Clone(), r.Textos("chaves"), r.S("vault_sha"), r.S("ultima")));

    /// <summary>SEM ACEITE, SEM ESCRITA. Relê do banco: a escrita fica amarrada a uma linha GRAVADA de aceite.</summary>
    public async Task<RecomendacaoLida> ExigirAceiteAsync(Guid recId)
    {
        var rec = await LerAsync(recId) ?? throw new InvalidOperationException("recomendação não encontrada");
        if (rec.Ultima == "aplicada") throw new InvalidOperationException("esta recomendação já foi aplicada na Meta: não há o que aplicar de novo.");
        if (rec.Ultima != "aceita") throw new InvalidOperationException("esta recomendação não tem aceite registrado: não há o que aplicar.");
        return rec;
    }

    /* ---- a decisão (append-only) ---------------------------------------------- */

    public async Task<string> RegistrarDecisaoAsync(Guid recId, string decisao, Guid por, string? motivo, object? resultadoMeta)
    {
        if (decisao is not ("aceita" or "negada" or "aplicada" or "falhou" or "incerto")) throw new InvalidOperationException($"decisão inválida: {decisao}");
        if (decisao == "negada" && string.IsNullOrWhiteSpace(motivo)) throw new InvalidOperationException("negar pede motivo: é o que diz ao motor onde ele errou.");
        var l = await db.ListaAsync(Sql.Fmt($"""
            insert into public.trafego_motor_decisao (recomendacao_id, decisao, por, motivo, resultado_meta)
            values ({recId}, {decisao}, {por}, {motivo?.Trim()}, {(resultadoMeta is null ? (Jsonb?)null : J(resultadoMeta))})
            returning id::text as id
            """));
        return l.Count > 0 ? l[0].S("id") ?? "" : throw new InvalidOperationException("a decisão não voltou do banco");
    }

    /* ---- a reserva (a trava que torna o aceite idempotente) -------------------- */

    public async Task<EstadoDaAplicacao?> EstadoDaAplicacaoAsync(Guid recId)
    {
        var a = await db.UmAsync(Sql.Fmt($"select estado, comecou_em, comecou_em::text as comecou_txt from public.trafego_motor_aplicacao where recomendacao_id = {recId}"),
            r => new { Estado = r.S("estado") ?? "", Em = r.Dt("comecou_em"), Txt = r.S("comecou_txt") });
        if (a is null) return null;
        var baseEstado = new EstadoDaAplicacao(a.Estado, a.Estado, a.Txt, a.Em);
        if (a.Estado != "em_voo" || a.Em is null || DateTime.Now - a.Em.Value <= PrazoEmVoo) return baseEstado;
        /* 'em_voo' velho: com 'aceita' gravado depois do início, a Meta PODE ter sido chamada ('incerto'); sem nenhum,
           ela com certeza não foi ('sem_escrita', que reabre e deixa negar). */
        var aceitouDepois = await db.ListaAsync(Sql.Fmt($"select 1 as um from public.trafego_motor_decisao where recomendacao_id = {recId} and decisao = 'aceita' and em >= {a.Em.Value - FolgaDeRelogio}"));
        return baseEstado with { Estado = aceitouDepois.Count > 0 ? "incerto" : "sem_escrita" };
    }

    static string MotivoDoEstado(string estado) => estado switch
    {
        "aplicada" => "esta recomendação já foi aplicada na Meta.",
        "falhou_permanente" => "a última tentativa falhou por um erro que repetir não conserta (parâmetro, permissão ou objeto que não existe): a reserva não reabre. Rode o motor de novo quando a causa for corrigida.",
        "incerto" => "a última tentativa de aplicar não terminou com resposta da Meta: ela pode ter acontecido. Confira o conjunto no Gerenciador; para decidir de novo, rode o motor.",
        _ => "esta recomendação já está sendo aplicada agora.",
    };

    async Task<bool> FalhaPermanenteAsync(Guid recId)
    {
        var l = await db.ListaAsync(Sql.Fmt($"select 1 as um from public.trafego_motor_aplicacao where recomendacao_id = {recId} and estado in ('falhou', 'incerto') and (resultado->>'permanente') = 'true'"));
        return l.Count > 0;
    }

    /// <summary>`insert` PURO, e não upsert: quem perde a corrida leva o 23505 e nada escreve. Retentativa só depois de
    /// 'falhou' (não permanente) ou de 'sem_escrita', e decidida pelo banco também (`update ... where estado = ...`).</summary>
    public async Task<Reserva> ReservarAplicacaoAsync(Guid recId, string tipo, Guid por, string? alvoDuplicado)
    {
        var alvo = tipo == "duplicate_adset" ? alvoDuplicado : null;
        try
        {
            await db.ExecutarAsync(Sql.Fmt($"insert into public.trafego_motor_aplicacao (recomendacao_id, estado, tipo, por, comecou_em, alvo_duplicado, resultado) values ({recId}, 'em_voo', {tipo}, {por}, now(), {alvo}, {Vazio})"));
            return new Reserva(true, false, null, null);
        }
        catch (Exception e) when (EhUnicidade(e))
        {
            if (alvo is not null && e.Message.Contains("duplicar_em_voo"))
                return new Reserva(false, false, "já existe uma cópia deste conjunto sendo criada agora: uma cópia por original. Espere ela terminar.", "em_voo");
        }
        catch (Exception e) { return new Reserva(false, false, e.Message, "erro"); }

        if (await FalhaPermanenteAsync(recId)) return new Reserva(false, false, MotivoDoEstado("falhou_permanente"), "falhou_permanente");

        var reaberta = await db.ListaAsync(Sql.Fmt($"""
            update public.trafego_motor_aplicacao set estado = 'em_voo', tipo = {tipo}, por = {por}, comecou_em = now(), terminou_em = null, resultado = {Vazio}
            where recomendacao_id = {recId} and estado = 'falhou' returning recomendacao_id::text as id
            """));
        if (reaberta.Count > 0) return new Reserva(true, true, null, null);

        var atual = await EstadoDaAplicacaoAsync(recId);
        if (atual?.Estado == "sem_escrita" && atual.ComecouEmTexto is not null)
        {
            var destravada = await db.ListaAsync(Sql.Fmt($"""
                update public.trafego_motor_aplicacao set estado = 'em_voo', tipo = {tipo}, por = {por}, comecou_em = now(), terminou_em = null, resultado = {Vazio}
                where recomendacao_id = {recId} and estado = {atual.EstadoNoBanco} and comecou_em::text = {atual.ComecouEmTexto} returning recomendacao_id::text as id
                """));
            if (destravada.Count > 0) return new Reserva(true, true, null, null);
        }
        var estado = atual?.Estado ?? "em_voo";
        return new Reserva(false, false, MotivoDoEstado(estado), estado);
    }

    /// <summary>O desfecho da escrita, na mesma linha da reserva (`where estado = 'em_voo'`). Não lança: a escrita na
    /// Meta já aconteceu (ou não), e perder o desfecho não a desfaz.</summary>
    public async Task FecharAplicacaoAsync(Guid recId, string estado, object resultado)
    {
        try
        {
            var l = await db.ListaAsync(Sql.Fmt($"update public.trafego_motor_aplicacao set estado = {estado}, resultado = {J(resultado)}, terminou_em = now() where recomendacao_id = {recId} and estado = 'em_voo' returning recomendacao_id::text as id"));
            if (l.Count == 0) log.LogError("a aplicação {Rec} não estava em voo ao fechar como '{Estado}'.", recId, estado);
        }
        catch (Exception e) { log.LogError("não fechei a aplicação {Rec}: {Erro}", recId, e.Message); }
    }

    /* ---- o sino (avisos.ts) --------------------------------------------------- */

    /// <summary>Grava o aviso; o repetido do dia (tipo, alvo, dia da conta) não entra. Nunca lança. `true` só quando é novo.</summary>
    public async Task<bool> AvisarAsync(string slug, string tipo, string? alvo, string titulo, string texto, object? detalhe, Guid? recId)
    {
        var chave = $"{tipo}:{alvo ?? "-"}:{Fuso.DiaDaConta(DateTime.UtcNow):yyyy-MM-dd}";
        try
        {
            var l = await db.ListaAsync(Sql.Fmt($"""
                insert into public.trafego_motor_aviso (cliente_slug, tipo, chave, titulo, texto, detalhe, recomendacao_id)
                values ({slug}, {tipo}, {chave}, {titulo}, {texto}, {J(detalhe ?? new { })}, {recId})
                on conflict do nothing returning id::text as id
                """));
            return l.Count > 0;
        }
        catch (Exception e) { log.LogError("não gravei o aviso {Tipo} de {Slug}: {Erro}", tipo, slug, e.Message); return false; }
    }

    /* ---- o log de ação e a trilha --------------------------------------------- */

    /// <summary>Uma linha de `trafego_log_acao` (origem 'sistema'; `detalhe.via` é a porta). Só o sucesso vai aqui.</summary>
    public async Task LogAcaoAsync(string slug, string acao, string alvoTipo, string? alvoId, string? alvoNome, string ator, object detalhe)
    {
        try
        {
            await db.ExecutarAsync(Sql.Fmt($"insert into public.trafego_log_acao (cliente_slug, origem, ator, acao, alvo_tipo, alvo_id, alvo_nome, detalhe, ocorrido_em) values ({slug}, 'sistema', {ator}, {acao}, {alvoTipo}, {alvoId}, {alvoNome}, {J(detalhe)}, now())"));
        }
        catch (Exception e) { log.LogError("log não gravou ({Slug}, {Acao}): {Erro}", slug, acao, e.Message); }
    }

    /// <summary>A trilha (`trafego_auditoria`) registra o ato e a recusa, nunca o segredo. Nunca lança.</summary>
    public async Task TrilhaAsync(string evento, Guid? atorId, string? slug, string resultado, string? erro, string? alvoTipo, string? alvoId, object? detalhe, object? depois = null, int? duracaoMs = null, string origem = "tela")
    {
        try
        {
            await db.ExecutarAsync(Sql.Fmt($"""
                insert into public.trafego_auditoria (aba, evento, ator_id, origem, cliente_slug, alvo_tipo, alvo_id, depois, resultado, erro, duracao_ms, detalhe, ocorrido_em)
                values ('dashboard', {evento}, {atorId}, {origem}, {slug}, {alvoTipo}, {alvoId}, {(depois is null ? (Jsonb?)null : J(depois))}, {resultado}, {erro}, {duracaoMs}, {(detalhe is null ? (Jsonb?)null : J(detalhe))}, now())
                """));
        }
        catch (Exception e) { log.LogWarning("a trilha não gravou ({Evento}): {Erro}", evento, e.Message); }
    }

    public static string Texto(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
