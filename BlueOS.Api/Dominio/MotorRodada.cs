using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

namespace BlueOS.Api.Dominio;

/* ============================================================================
   RODAR O MOTOR NA API: a porta de /api/trafego/motor/recomendacoes do V1 (a
   passada de decisão sobre a carga que já está no banco; a carga da Meta é o
   cron das 07:30 UTC, não esta rota).
   ----------------------------------------------------------------------------
   Para cada conjunto com entrega, a árvore (Core/Motor/Arvore) decide; a ação
   que pede gente (PAUSAR, DUPLICAR, ALERTAR, TROCAR_CRIATIVO) vira uma linha em
   `trafego_motor_recomendacao`, salvo quando já há a mesma recomendação
   pendente para o conjunto (repetida). O texto é a trilha da árvore (a leitura
   pelo modelo via OpenRouter, do V1, fica de fora: `texto_fonte = trilha`).
   Esta rota NÃO escreve na Meta: aplicar é o aceite do gestor (Executor).
   ============================================================================ */
public sealed class MotorRodada(Db db, Automacao automacao, CopyAgente copy, Registro registro, ILogger<MotorRodada> log)
{
    public async Task<(int Status, object Corpo)> RodarAsync(Cliente c, Guid por)
    {
        var inicio = DateTime.UtcNow;
        var aut = await db.UmAsync(Sql.Fmt($"select motor_automacao, motor_automacao_motivo from public.trafego_contas where cliente_slug = {c.Slug}"), r => new { Estado = r.S("motor_automacao"), Motivo = r.S("motor_automacao_motivo") });
        if (aut?.Estado is "pausada" or "cancelada")
            return (409, new { error = $"a automação deste cliente está {aut.Estado}{(aut.Motivo is not null ? $" ({aut.Motivo})" : "")}: o motor não lê nem age nele.", automacao = aut });

        var vaultSha = (await copy.VaultAsync())?.Sha ?? ParametrosMotor.PadraoDoVault.VaultSha;
        var naoAgiu = new List<string>(); var avaliados = new List<object>(); var gravadas = 0; var repetidas = 0;
        if (c.Regua is null || c.TetoMql is null or <= 0)
        {
            naoAgiu.Add("sem_regua_ou_teto");
            return (200, new { gravadas, repetidas, nao_agiu = naoAgiu, vault_sha = vaultSha, avaliados, recomendacoes = Array.Empty<object>() });
        }
        var ongoing = await automacao.OngoingAsync(c);
        var comEntrega = ongoing.Conjuntos.Where(cj => cj.DiasEntrega > 0).ToList();
        if (comEntrega.Count == 0)
        {
            naoAgiu.Add("sem_conjunto_com_entrega");
            return (200, new { gravadas, repetidas, nao_agiu = naoAgiu, vault_sha = vaultSha, avaliados, recomendacoes = Array.Empty<object>() });
        }
        var teto = Dinheiro.CentavosDe(c.TetoMql.Value);
        var clienteConverte = ongoing.Conjuntos.Any(cj => cj.Leads.Any(l => l.Situacao == Situacao.Mql));
        var pendentes = (await automacao.RecomendacoesAsync(c.Slug)).Where(r => r.Estado == EstadoDaRecomendacao.Pendente).ToList();
        var formatos = (await db.ListaAsync(Sql.Fmt($"select adset_id, formato from public.trafego_adset_estado where cliente_slug = {c.Slug}"), r => (Id: r.S("adset_id") ?? "", Formato: r.S("formato")))).ToDictionary(x => x.Id, x => x.Formato);

        foreach (var cj in comEntrega)
        {
            var entrada = Executor.EntradaDe(cj, c.Slug, formatos.GetValueOrDefault(cj.AdsetId));
            var d = Arvore.Decidir(entrada, new EntradaAoVivo(cj.Ativo, cj.DiarioCentavos), teto, clienteConverte, ParametrosMotor.PadraoDoVault);
            avaliados.Add(new { adset_id = cj.AdsetId, nome = cj.Nome, acao = d.Acao.ToString(), chaves = d.Chaves, segurou_por_pendentes = d.SegurouPorPendentes });
            if (d.Acao is not (Acao.PAUSAR or Acao.ALERTAR or Acao.DUPLICAR or Acao.TROCAR_CRIATIVO)) continue;
            if (pendentes.Any(r => r.MetaId == cj.AdsetId && r.Acao == d.Acao)) { repetidas++; continue; }
            var numeros = new
            {
                nome = cj.Nome, taxa = d.Numeros.Taxa, gasto = cj.GastoVidaCentavos / 100.0, multiplo = d.Numeros.Multiplo, avaliados = entrada.Avaliados, ponderado = d.Numeros.Ponderado,
                dias_entrega = cj.DiasEntrega, custoCentavos = d.Numeros.CustoCentavos, teto_centavos = teto, copia_diario_centavos = d.CopiaDiarioCentavos, segurou_por_pendentes = d.SegurouPorPendentes,
            };
            var texto = TextoDaTrilha(d, cj.Nome);
            var pend = d.Avisos.Select(a => a.Ref).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
            try
            {
                await db.ExecutarAsync(Sql.Fmt($"""
                    insert into public.trafego_motor_recomendacao (cliente_slug, nivel, meta_id, acao, numeros, regras_usadas, chaves, pendencias, vault_sha, contagem_mql, texto, texto_fonte)
                    values ({c.Slug}, 'conjunto', {cj.AdsetId}, {d.Acao.ToString()}, {new Jsonb(System.Text.Json.JsonSerializer.Serialize(numeros))}, {d.Chaves.ToArray()}, {d.Chaves.ToArray()}, {pend}, {vaultSha}, 'regua', {texto}, 'trilha')
                    """));
                gravadas++;
            }
            catch (Exception e) { log.LogError("a recomendação de {Adset} não gravou: {Erro}", cj.AdsetId, e.Message); }
        }
        var recs = await automacao.RecomendacoesAsync(c.Slug);
        await registro.TrilhaAsync("rodou_motor", por, c.Slug, "ok", null, "motor", null, new { via = "api", gravadas, repetidas, avaliados = avaliados.Count, vault = vaultSha[..Math.Min(7, vaultSha.Length)] }, duracaoMs: (int)(DateTime.UtcNow - inicio).TotalMilliseconds);
        return (200, new { gravadas, repetidas, nao_agiu = naoAgiu, vault_sha = vaultSha, avaliados, recomendacoes = recs.Select(r => new { id = r.Id, meta_id = r.MetaId, acao = r.Acao.ToString(), estado = r.Estado.ToString().ToLowerInvariant(), nome = r.Nome, texto = r.Texto }) });
    }

    /// <summary>O texto da recomendação a partir da trilha (as regras que dispararam), como o V1 escreve sem o modelo.</summary>
    public static string TextoDaTrilha(Decisao d, string nome)
    {
        var (rotulo, _) = Arvore.RotuloDaAcao(d.Acao);
        var disparou = d.Trilha.Where(t => t.Resultado == ResultadoDaRegra.Disparou).Select(t => t.Texto).ToList();
        var corpo = disparou.Count > 0 ? string.Join(" ", disparou) : d.Avisos.FirstOrDefault()?.Texto ?? "";
        return $"{rotulo} \"{nome}\": {corpo}";
    }
}
