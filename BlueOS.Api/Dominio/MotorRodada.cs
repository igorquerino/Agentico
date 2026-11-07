using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   RODAR O MOTOR NA API: a porta de /api/trafego/motor/recomendacoes do V1 (a
   passada de decisão sobre a carga que já está no banco; a carga da Meta é o
   cron das 07:30 UTC, não esta rota).
   ----------------------------------------------------------------------------
   A entrada de cada conjunto é a do V1 (conjuntos.ts + memoria.ts), lida por
   Automacao.OngoingAsync e montada em Core/Motor/Carga: a leitura do dia pela
   marca da carga, a foto de 36 h (status, verba e formato), a cobertura, os
   anúncios do último dia, a janela de 30 dias igual para gasto e leads, a
   campanha matriculada e a memória do motor (última ação, D5, cópias vivas).
   A reconferência do aceite (Executor) lê a MESMA entrada.
   Para cada conjunto com entrega a árvore decide; a ação que pede gente
   (PAUSAR, ALERTAR, DUPLICAR, TROCAR_CRIATIVO) vira linha em
   `trafego_motor_recomendacao` se couber na cadência (cabeNaCadencia) e se não
   houver a mesma (conjunto, ação) pendente.
   O QUE DIVERGE DO V1, de propósito: o texto é a trilha (a leitura pelo modelo
   via OpenRouter fica de fora: `texto_fonte = trilha`); os parâmetros são os
   do vault 9715c94 em código (ParametrosMotor.PadraoDoVault), e o `vault_sha`
   gravado é o DELES; `regras_usadas` guarda as chaves (o snapshot do vault
   não é lido aqui).
   Esta rota NÃO escreve na Meta: aplicar é o aceite do gestor (Executor).
   ============================================================================ */
public sealed class MotorRodada(Db db, Automacao automacao, Registro registro, ILogger<MotorRodada> log)
{
    public async Task<(int Status, object Corpo)> RodarAsync(Cliente c, Guid por)
    {
        var inicio = DateTime.UtcNow;
        var p = ParametrosMotor.PadraoDoVault;
        /* o sha dos parâmetros que DECIDEM, não o vivo do vault: a auditoria diz de qual metodologia a recomendação saiu */
        var vaultSha = p.VaultSha;
        int Duracao() => (int)(DateTime.UtcNow - inicio).TotalMilliseconds;

        var aut = await db.UmAsync(Sql.Fmt($"select motor_automacao, motor_automacao_motivo from public.trafego_contas where cliente_slug = {c.Slug}"), r => new { Estado = r.S("motor_automacao"), Motivo = r.S("motor_automacao_motivo") });
        if (aut?.Estado is "pausada" or "cancelada")
        {
            var erro = $"a automação deste cliente está {aut.Estado}{(aut.Motivo is not null ? $" ({aut.Motivo})" : "")}: o motor não lê nem age nele.";
            await registro.TrilhaAsync("rodou_motor", por, c.Slug, "negado", erro, "motor", null, new { via = "api", nao_agiu = new[] { $"automacao_{aut.Estado}" }, vault = Curto(vaultSha) }, duracaoMs: Duracao());
            return (409, new { error = erro, automacao = aut });
        }

        var naoAgiu = new List<string>(); var avaliados = new List<object>(); var falharam = new List<string>();
        int gravadas = 0, repetidas = 0, foraDaCadencia = 0;
        async Task<(int, object)> SemAgir(string motivo)
        {
            naoAgiu.Add(motivo);
            await registro.TrilhaAsync("rodou_motor", por, c.Slug, "ok", null, "motor", null, new { via = "api", gravadas, repetidas, avaliados = 0, nao_agiu = naoAgiu, vault = Curto(vaultSha) }, duracaoMs: Duracao());
            return (200, new { gravadas, repetidas, nao_agiu = naoAgiu, vault_sha = vaultSha, avaliados, recomendacoes = Array.Empty<object>() });
        }

        /* o teto que serve é o que dá pelo menos 1 centavo: R$ 0,004 não é teto */
        var teto = Dinheiro.TetoEmCentavos(c.TetoMql);
        if (c.Regua is null || teto is null) return await SemAgir("sem_regua_ou_teto");
        var ongoing = await automacao.OngoingAsync(c);
        var comEntrega = ongoing.Conjuntos.Where(cj => cj.DiasEntrega > 0).ToList();
        if (comEntrega.Count == 0) return await SemAgir("sem_conjunto_com_entrega");

        /* "o cliente converte?" é sobre a conta (a campanha matriculada), nos conjuntos que o motor lê */
        var clienteConverte = comEntrega.Any(cj => cj.Leads.Any(l => l.Situacao == Situacao.Mql));
        var pendentes = await automacao.PendentesAsync(c.Slug);

        foreach (var cj in comEntrega)
        {
            var entrada = Executor.EntradaDe(cj, c.Slug, null);
            var d = Arvore.Decidir(entrada, new EntradaAoVivo(cj.Ativo, cj.DiarioCentavos), teto, clienteConverte, p);
            avaliados.Add(Rodada.AvaliadoNaResposta(cj.AdsetId, cj.Nome, d));
            if (!Rodada.ViraRecomendacao(d.Acao)) continue;
            if (!Rodada.CabeNaCadencia(d.Acao, cj.DiasEntrega, p, entrada.Memoria)) { foraDaCadencia++; continue; }
            if (Rodada.Repetida(pendentes, cj.AdsetId, d.Acao)) { repetidas++; continue; }
            var numeros = Rodada.NumerosJson(Rodada.Numeros(d, entrada, cj.Nome, teto));
            var texto = Rodada.TextoDaTrilha(d, cj.Nome);
            var acao = d.Acao.ToString();
            try
            {
                /* o "não existe a mesma pendente" vai também no próprio insert: duas rodadas juntas não gravam duas */
                var n = await db.ExecutarAsync(Sql.Fmt($"""
                    insert into public.trafego_motor_recomendacao (cliente_slug, nivel, meta_id, acao, numeros, regras_usadas, chaves, pendencias, vault_sha, contagem_mql, texto, texto_fonte)
                    select {c.Slug}, 'conjunto', {cj.AdsetId}, {acao}, {new Jsonb(numeros)}, {d.Chaves.ToArray()}, {d.Chaves.ToArray()}, {Rodada.Pendencias(d)}, {vaultSha}, 'regua', {texto}, 'trilha'
                    where not exists (select 1 from public.trafego_motor_recomendacao x where x.cliente_slug = {c.Slug} and x.meta_id = {cj.AdsetId} and x.acao = {acao}
                                      and not exists (select 1 from public.trafego_motor_decisao y where y.recomendacao_id = x.id))
                    """));
                if (n > 0) { gravadas++; pendentes.Add(Rodada.ChaveDeDedup(cj.AdsetId, acao)); }
                else repetidas++;
            }
            catch (Exception e)
            {
                falharam.Add(cj.AdsetId);
                log.LogError("a recomendação {Acao} de {Adset} não gravou: {Erro}", acao, cj.AdsetId, e.Message);
            }
        }
        var recs = await automacao.RecomendacoesAsync(c.Slug);
        /* uma PAUSAR que não gravou não pode sair como "ok": a trilha diz quais conjuntos ficaram sem a recomendação */
        var erroDaRodada = falharam.Count > 0 ? $"{falharam.Count} recomendação(ões) não gravaram (conjuntos {string.Join(", ", falharam)}): rode o motor de novo." : null;
        await registro.TrilhaAsync("rodou_motor", por, c.Slug, falharam.Count > 0 ? "erro" : "ok", erroDaRodada, "motor", null,
            new { via = "api", gravadas, repetidas, fora_da_cadencia = foraDaCadencia, avaliados = avaliados.Count, falharam, vault = Curto(vaultSha) }, duracaoMs: Duracao());
        return (200, new
        {
            gravadas, repetidas, fora_da_cadencia = foraDaCadencia, falharam, erro = erroDaRodada, nao_agiu = naoAgiu, vault_sha = vaultSha, avaliados,
            recomendacoes = recs.Select(r => new { id = r.Id, meta_id = r.MetaId, acao = r.Acao.ToString(), estado = r.Estado.ToString().ToLowerInvariant(), nome = r.Nome, texto = r.Texto }),
        });
    }

    static string Curto(string sha) => sha[..Math.Min(7, sha.Length)];

    /// <summary>O texto da recomendação a partir da trilha, como o V1 escreve sem o modelo (Core/Motor/Rodada).</summary>
    public static string TextoDaTrilha(Decisao d, string nome) => Rodada.TextoDaTrilha(d, nome);
}
