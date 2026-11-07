using BlueOS.Api.Infra;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   O ESCOPO DA SESSÃO, numa policy só (porta de lib/os/escopo-server.ts e de
   lib/trafego/escopo.ts do V1).
   ----------------------------------------------------------------------------
   · escopo global          → vê tudo, inclusive o squad interno (holding);
   · vínculo de departamento → todos os squads operacionais, sem o interno;
   · escopo tribo           → os squads da(s) tribo(s) e os squads sem tribo;
   · escopo squad           → só os próprios squads;
   · sem vínculo            → NÃO vê nada (fail-closed, desde 10/08/2026).
   O squad interno só entra por vínculo direto, nunca por agregação.
   Tudo sai em DUAS consultas (membros + squads): é o que vale por chamada.
   A resolução é pura (`Resolver`), e o resultado fica guardado na requisição:
   o filtro de sessão e a rota leem o mesmo escopo, sem consultar duas vezes.

   VER É UMA PERGUNTA, PODER É OUTRA. O escopo (global ou não) diz QUAIS
   clientes a pessoa alcança; o que ela pode FAZER decide só pelo cargo, como no
   V1 (permissao.ts, mql-criterio.ts). Global sem cargo de escrita (rh,
   financeiro, onboarding_manager) vê tudo e não escreve na Meta.
   ============================================================================ */
public sealed record EscopoDaSessao(string UserId, bool Global, IReadOnlyList<string>? SquadIds, IReadOnlyList<string> Papeis)
{
    /// <summary>lib/os/mql-criterio.ts, CARGOS_EDITAM_MQL: decide só pelo cargo.</summary>
    public bool PodeEditarMql => Papeis.Any(p => Escopo.CargosEditamMql.Contains(p));
    /// <summary>lib/trafego/permissao.ts, CARGOS_QUE_APLICAM: coordenador pra cima e os heads. Decide só pelo cargo.</summary>
    public bool PodeAplicarNaMeta => Papeis.Any(p => Escopo.CargosQueAplicam.Contains(p));
    /// <summary>lib/os/somente-leitura.ts: todos os papéis da pessoa só leem (o `rh`).</summary>
    public bool SomenteLeitura => Escopo.SomenteLeituraPelosPapeis(Papeis);
    public Guid[]? SquadGuids => SquadIds?.Select(Guid.Parse).ToArray();
}

/// <summary>Um vínculo ativo da pessoa (membros + roles).</summary>
public sealed record MembroDoEscopo(string? SquadId, string? TriboId, string? DepartamentoId, string? RoleEscopo, string? RoleSlug);
/// <summary>Um squad, com a tribo e a marca de interno.</summary>
public sealed record SquadDoEscopo(string Id, string Nome, string? TriboId, bool Interno);

public sealed class Escopo(Db db)
{
    public const string MotivoSemMql = "Alterar o critério de MQL é de gestor, head ou coordenador pra cima. Você continua vendo o critério normalmente.";
    /// <summary>lib/os/somente-leitura.ts, MOTIVO_SOMENTE_LEITURA.</summary>
    public const string MotivoSomenteLeitura = "Seu cargo enxerga o sistema inteiro, mas só altera o que é da sua área. Esta ação grava dado em outra, então ela é de quem opera ali. Se precisa mudar alguma coisa, peça a quem responde por ela.";
    public static readonly HashSet<string> CargosEditamMql = ["gestor", "operador", "head", "head_departamento", "coordenador", "coordenador_departamento", "gerente", "diretor", "ceo", "super_admin", "engenheiro_ia"];
    public static readonly HashSet<string> CargosQueAplicam = ["super_admin", "ceo", "diretor", "gerente", "engenheiro_ia", "coordenador", "coordenador_departamento", "head", "head_departamento"];
    /// <summary>lib/os/somente-leitura.ts, CARGOS_SOMENTE_LEITURA. A área própria do `rh` (/api/rh) não existe nesta API.</summary>
    public static readonly HashSet<string> CargosSomenteLeitura = ["rh"];

    /// <summary>somenteLeituraPelosPapeis do V1: só trava quem NÃO tem nenhum outro cargo; sem papel nenhum, não trava (o escopo já é vazio).</summary>
    public static bool SomenteLeituraPelosPapeis(IEnumerable<string>? papeis)
    {
        var ativos = (papeis ?? []).Select(p => (p ?? "").Trim().ToLowerInvariant()).Where(p => p != "").ToList();
        return ativos.Count > 0 && ativos.All(CargosSomenteLeitura.Contains);
    }

    /// <summary>Os métodos que mudam estado (ehMetodoDeEscrita do V1). OPTIONS fica de fora: é o preflight do CORS.</summary>
    public static bool EhMetodoDeEscrita(string? metodo) => (metodo ?? "").ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE";

    /* o escopo da requisição: o Escopo é scoped, então vale por uma chamada só */
    string? _userId;
    EscopoDaSessao? _guardado;

    public async Task<EscopoDaSessao> DoUsuarioAsync(string userId)
    {
        if (_guardado is not null && _userId == userId) return _guardado;
        var membros = await db.ListaAsync(Sql.Fmt($"""
            select m.squad_id::text as squad_id, m.tribo_id::text as tribo_id, m.departamento_id::text as departamento_id, r.role_escopo, r.role_slug
            from public.membros m left join public.roles r on r.role_id = m.role_id
            where m.user_id = {Guid.Parse(userId)} and m.membro_ate is null
            """), r => new MembroDoEscopo(r.S("squad_id"), r.S("tribo_id"), r.S("departamento_id"), r.S("role_escopo"), r.S("role_slug")));
        IReadOnlyList<SquadDoEscopo> squads = membros.Any(m => m.RoleEscopo == "global") ? [] : await db.ListaAsync(
            "select squad_id::text as squad_id, squad_nome, tribo_id::text as tribo_id, coalesce(squad_interno, false) as squad_interno from public.squads",
            r => new SquadDoEscopo(r.S("squad_id")!, r.S("squad_nome") ?? "", r.S("tribo_id"), r.B("squad_interno") ?? false));
        _guardado = Resolver(userId, membros, squads);
        _userId = userId;
        return _guardado;
    }

    /// <summary>A resolução pura: os vínculos ativos e os squads viram o escopo. Os squads só importam fora do global.</summary>
    public static EscopoDaSessao Resolver(string userId, IReadOnlyList<MembroDoEscopo> membros, IReadOnlyList<SquadDoEscopo> squads)
    {
        var papeis = membros.Select(m => m.RoleSlug).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();
        if (membros.Any(m => m.RoleEscopo == "global")) return new(userId, true, null, papeis);

        var internos = squads.Where(s => s.Interno).Select(s => s.Id).ToHashSet();

        if (membros.Any(m => m.DepartamentoId is not null))
            return new(userId, false, squads.Where(s => !internos.Contains(s.Id)).Select(s => s.Id).ToList(), papeis);

        var ids = membros.Where(m => m.SquadId is not null).Select(m => m.SquadId!).ToHashSet();
        var diretos = new HashSet<string>(ids);
        var tribos = membros.Where(m => m.RoleEscopo == "tribo" && m.TriboId is not null).Select(m => m.TriboId!).ToHashSet();
        if (tribos.Count > 0)
            foreach (var s in squads.Where(s => (s.TriboId is not null && tribos.Contains(s.TriboId)) || s.TriboId is null)) ids.Add(s.Id);
        foreach (var i in internos) if (!diretos.Contains(i)) ids.Remove(i);
        return new(userId, false, ids.ToList(), papeis);
    }

    /// <summary>O filtro SQL de "este cliente está no alcance", para colar num where sobre `c` (clientes).</summary>
    public static string FiltroDoAlcance(EscopoDaSessao esc) => esc.Global ? "" : Sql.Fmt($"""
         and exists (select 1 from public.cliente_squads cs where cs.cliente_id = c.cliente_id and cs.squad_id = any({esc.SquadGuids ?? []})
                     and (cs.cliente_squad_ate is null or not exists (select 1 from public.cliente_squads o where o.cliente_id = cs.cliente_id and o.cliente_squad_ate is null)))
        """);

    /// <summary>O cliente (slug) está no alcance? Decide pelo escopo, não pelo parâmetro.</summary>
    public async Task<bool> AlcancaAsync(EscopoDaSessao esc, string slug)
    {
        if (!esc.Global && (esc.SquadIds is null || esc.SquadIds.Count == 0)) return false;
        var l = await db.ListaAsync(Sql.Fmt($"select 1 as um from public.clientes c where c.cliente_slug = {slug} and c.cliente_arquivado_em is null") + FiltroDoAlcance(esc));
        return l.Count > 0;
    }
}
