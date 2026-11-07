using BlueOS.Core.Fluxo;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

namespace BlueOS.Core.Panorama;

/// <summary>Um ponto da linha do tempo do aprofundamento ("06/11 07:30 — carga diária: Meta recusou o token").</summary>
public sealed record Momento(DateTime Quando, string Texto, bool Hoje = false);

/// <summary>
/// Um problema do cliente, pronto para a estrela e para o aprofundamento: por quê, o que aconteceu, o que custa,
/// o que resolve. A <see cref="Assinatura"/> identifica o ESTADO que gerou o alerta: "marcar como visto" vale
/// enquanto a assinatura for a mesma; mudou o estado, o visto cai.
/// </summary>
public sealed record Alerta(
    Gravidade Gravidade,
    string Titulo,
    string Curto,
    string Motivo,
    string? OQueCusta,
    string? CustaDetalhe,
    string? OQueResolve,
    string? QuemPode,
    IReadOnlyList<Momento> LinhaDoTempo,
    DateTime? Desde,
    Estagio EtapaDoStart,
    string Assinatura);

/* ============================================================================
   O DIAGNÓSTICO: do cliente aos alertas, pela ordem de atenção.
   ----------------------------------------------------------------------------
   Regras puras, sem banco. Cada gatilho vem de um dado que o sistema já grava
   (fontes no EXPERIENCE.md, "Data contract"); o que não existe ainda fica em
   branco no cliente e simplesmente não dispara. O primeiro alerta da lista é
   o que pinta a estrela; os outros entram no aprofundamento.
   ============================================================================ */
public static class Diagnostico
{
    /// <summary>Gatilho do "cliente frio": 3 dias corridos sem mensagem no grupo (CsRegras, grupo frio).</summary>
    public const int DiasParaFrio = 3;
    /// <summary>"Budget acabando": a verba do mês termina dentro de tantos dias, com o mês ainda pela frente.</summary>
    public const int DiasDeFolgaDoBudget = 3;

    public static List<Alerta> DoCliente(Cliente c, ParametrosMotor p, DateTime agora)
    {
        var lista = new List<Alerta>();
        var estagio = Estagios.DoCliente(c, p);
        var s = c.Sinais;
        /* "Em automação" é ter a campanha publicada: um token vencido rebaixa o estágio para Assets,
           mas a conta continua rodando na Meta, e é exatamente isso que faz a automação ter parado. */
        var emAutomacao = Estagios.CampanhaPublicada(c);

        /* ---- 1 · automação parou ------------------------------------------------ */
        if (emAutomacao)
        {
            if (c.Conta.TokenValido == false)
            {
                var desde = c.Conta.TokenConferidoEm;
                var tl = new List<Momento>();
                if (desde is { } d0) tl.Add(new(d0, $"carga diária: a Meta recusou o token do squad{(c.Conta.TokenMotivo is null ? "" : $" ({c.Conta.TokenMotivo})")}"));
                if (s.ConjuntosAtivos > 0) tl.Add(new(desde?.AddMinutes(1) ?? agora, $"o agente não rodou; {s.ConjuntosAtivos} conjunto(s) seguem ativos sem leitura"));
                if (desde is { } d1 && agora.Date > d1.Date) tl.Add(new(agora, "carga de hoje: mesmo erro, o token continua vencido", Hoje: true));
                lista.Add(new(Gravidade.AutomacaoParou, "A automação parou", $"parou {Relativo(desde, agora)} · token vencido",
                    $"{Quando(desde, agora)}. Nenhuma decisão do agente desde então{(s.ConjuntosAtivos > 0 ? $"; {s.ConjuntosAtivos} conjunto(s) seguem ativos sem leitura" : "")}.",
                    s.VerbaDiariaCentavos > 0 ? $"{Dinheiro.ReaisRedondos(s.VerbaDiariaCentavos)}/dia rodando sem o agente olhar" : "a conta roda sem o agente olhar",
                    s.VerbaDiariaCentavos > 0 ? "verba diária somada dos conjuntos ativos" : null,
                    "o head do squad renova o token na Meta (Assets → token do squad)", $"head do {c.Squad} ou coordenador",
                    tl, desde, Estagio.Assets, $"token-vencido:{desde:yyyyMMddHHmm}"));
            }
            if (s.AutomacaoEstado is "pausada" or "cancelada")
            {
                var desde = s.AutomacaoDesde;
                lista.Add(new(Gravidade.AutomacaoParou, $"A automação está {s.AutomacaoEstado}", $"{s.AutomacaoEstado} {Relativo(desde, agora)}",
                    $"{Quando(desde, agora)}{(s.AutomacaoMotivo is null ? "" : $": {s.AutomacaoMotivo}")}. O agente não decide nada enquanto estiver assim.",
                    s.VerbaDiariaCentavos > 0 ? $"{Dinheiro.ReaisRedondos(s.VerbaDiariaCentavos)}/dia rodando sem o agente olhar" : null, null,
                    "religar a automação do cliente na aba Automação", "head pra cima",
                    [new(desde ?? agora, $"automação {s.AutomacaoEstado}{(s.AutomacaoMotivo is null ? "" : $": {s.AutomacaoMotivo}")}")],
                    desde, Estagio.Automacao, $"automacao-{s.AutomacaoEstado}:{desde:yyyyMMddHHmm}"));
            }
        }
        if (c.Publicacao is { Estado: "erro" } pub)
        {
            lista.Add(new(Gravidade.AutomacaoParou, "A publicação falhou", "publicação com erro",
                $"{Quando(pub.Em, agora)}: {pub.Erro ?? "a Meta recusou a campanha"}.", null, null,
                "corrigir o que a Meta apontou e publicar de novo (o Start retoma pelo hash)", "head pra cima",
                [new(pub.Em, $"Publicar: {pub.Erro ?? "erro"}")], pub.Em, Estagio.Campanha, $"publicacao-erro:{pub.Em:yyyyMMddHHmm}"));
        }

        /* ---- 2 · dinheiro ------------------------------------------------------- */
        if (emAutomacao && s.Kpis is { } k)
        {
            if (k.CustoPorMqlCentavos is { } cpm && k.TetoCentavos is { } teto && teto > 0 && cpm > teto)
            {
                var pct = (int)Math.Round((cpm - teto) * 100.0 / teto);
                var caros = c.Conjuntos.Count(cj => cj.Ativo == true && CustoDoConjunto(cj) is { } x && x > teto);
                lista.Add(new(Gravidade.Dinheiro, "R$ por MQL acima do teto", $"{Dinheiro.ReaisRedondos(cpm)}/MQL · teto {Dinheiro.ReaisRedondos(teto)}",
                    $"{Dinheiro.ReaisRedondos(cpm)} por MQL no mês, {pct}% acima do teto de {Dinheiro.ReaisRedondos(teto)}{(caros > 0 ? $"; {caros} conjunto(s) estão caros" : "")}.",
                    $"{Dinheiro.ReaisRedondos(k.GastoCentavos)} gastos para {k.Mql} MQL", $"meta do mês: {k.MetaMql?.ToString() ?? "sem meta"} MQL",
                    "olhar as recomendações do agente (trocar criativo, pausar o conjunto caro) ou revisar a régua", "você (negar) · head (aceitar)",
                    [new(agora, $"{k.Mql} MQL a {Dinheiro.ReaisRedondos(cpm)} cada, teto {Dinheiro.ReaisRedondos(teto)}", Hoje: true)],
                    null, Estagio.Automacao, $"cpm-acima:{k.Mes}:{pct / 5}"));
            }
            if (k.BudgetCentavos is { } budget && budget > 0)
            {
                var aviso = Arvore.AlertaBudget(k.GastoSemMqlCentavos, budget, p);
                if (aviso is not null)
                    lista.Add(new(Gravidade.Dinheiro, "Verba indo embora sem MQL", $"{Pct(k.GastoSemMqlCentavos, budget)} do budget sem MQL",
                        aviso.Texto, $"{Dinheiro.ReaisRedondos(k.GastoSemMqlCentavos)} em anúncio sem nenhum MQL", null,
                        "pausar os conjuntos que não dão MQL (o agente recomenda)", "você (negar) · head (aceitar)",
                        [new(agora, aviso.Texto, Hoje: true)], null, Estagio.Automacao, $"budget-sem-mql:{k.Mes}:{k.GastoSemMqlCentavos * 10 / budget}"));
                var diasDoMes = DateTime.DaysInMonth(agora.Year, agora.Month);
                var diaDoMes = agora.Day;
                var ritmo = diaDoMes > 0 ? (double)k.GastoCentavos / diaDoMes : 0;
                if (ritmo > 0 && k.GastoCentavos < budget)
                {
                    var diasAteAcabar = (int)Math.Floor((budget - k.GastoCentavos) / ritmo);
                    var faltam = diasDoMes - diaDoMes;
                    /* iminente: acaba nos próximos dias e o mês ainda tem chão pela frente */
                    if (diasAteAcabar <= DiasDeFolgaDoBudget && faltam > diasAteAcabar + 2)
                        lista.Add(new(Gravidade.Dinheiro, "O budget do mês acaba antes do mês", $"{Pct(k.GastoCentavos, budget)} do budget · acaba em {diasAteAcabar} dia(s)",
                            $"{Pct(k.GastoCentavos, budget)} do budget gasto no dia {diaDoMes} de {diasDoMes}; neste ritmo a verba acaba em {diasAteAcabar} dia(s) e faltam {faltam}.",
                            $"{Dinheiro.ReaisRedondos(budget - k.GastoCentavos)} para {faltam} dias", null,
                            "baixar a verba diária dos conjuntos ou combinar reforço com o cliente", "head pra cima (verba) · você (cliente)",
                            [new(agora, $"gasto {Dinheiro.ReaisRedondos(k.GastoCentavos)} de {Dinheiro.ReaisRedondos(budget)}", Hoje: true)],
                            null, Estagio.Automacao, $"budget-acaba:{k.Mes}:{diasAteAcabar}"));
                }
                else if (k.GastoCentavos >= budget && diaDoMes < diasDoMes - 1)
                    lista.Add(new(Gravidade.Dinheiro, "O budget do mês acabou", "100% do budget gasto",
                        $"A verba do mês ({Dinheiro.ReaisRedondos(budget)}) acabou no dia {diaDoMes} de {diasDoMes}.", null, null,
                        "combinar reforço com o cliente ou pausar até o mês virar", "você", [new(agora, "budget do mês esgotado", Hoje: true)],
                        null, Estagio.Automacao, $"budget-acabou:{k.Mes}"));
            }
        }

        /* ---- 3 · risco de churn ------------------------------------------------- */
        if (s.RiscoChurn is "alto" or "critico")
        {
            var sinais = s.SinaisDeChurn.Count > 0 ? string.Join("; ", s.SinaisDeChurn) : null;
            var partes = new List<string>();
            if (s.DiasSemMensagem is { } dsm && dsm >= DiasParaFrio) partes.Add($"{dsm} dias sem resposta no grupo");
            if (s.PromessaAtrasada is not null) partes.Add($"promessa atrasada: {s.PromessaAtrasada}");
            if (s.TendenciaD7 is { } t && t < 0) partes.Add($"nota {s.NotaCs + Math.Abs(t)} → {s.NotaCs} em 7 d");
            var motivo = partes.Count > 0 ? string.Join(", ", partes) : sinais ?? "o Agente de CS marcou o risco";
            lista.Add(new(Gravidade.Churn, $"Risco de churn {s.RiscoChurn}", $"churn {s.RiscoChurn} · {(partes.Count > 0 ? partes[0] : $"nota {s.NotaCs}")}",
                $"Nota do CS {s.NotaCs?.ToString() ?? "sem nota"} ({s.FaixaCs}). {Primeira(motivo)}.", null, null,
                "falar com o cliente hoje e fechar o próximo passo que o CS deixou", "você",
                [new(agora, motivo, Hoje: true)], null, Estagio.Chegada, $"churn:{s.RiscoChurn}:{s.NotaCs}"));
        }

        /* ---- 4 · travado numa etapa --------------------------------------------- */
        if (!emAutomacao && estagio < Estagio.Automacao && !c.Automacao.EmCurso)
        {
            var pend = Estagios.PendenciasDoEstagio(c, p);
            var desde = c.Desde.ToDateTime(TimeOnly.MinValue);
            var dias = Math.Max(0, (agora.Date - desde.Date).Days);
            var nome = Estagios.Descricao(estagio).Curto;
            var falta = pend.Count == 0 ? "" : $"falta {string.Join(", ", pend.Take(2))}{(pend.Count > 2 ? $" e mais {pend.Count - 2}" : "")}";
            var curto = estagio == Estagio.Chegada || estagio == Estagio.Assets
                ? $"chegou há {dias} d · {falta}"
                : $"{nome} há {dias} d · {falta}";
            lista.Add(new(Gravidade.Travado, $"Travado em {nome}", curto,
                $"Está em {nome} há {dias} dia(s). {(pend.Count > 0 ? "Falta " + string.Join(", ", pend) + "." : "")}",
                null, null, c.Automacao.PrecisaDeVoce ?? "resolver a pendência e dar Start (o condutor faz o resto)", "você",
                [new(desde, $"entrou em {nome}")], desde, estagio, $"travado:{estagio}:{string.Join("|", pend)}"));
        }

        /* ---- 5 · pedido do cliente ----------------------------------------------- */
        if (!string.IsNullOrWhiteSpace(s.PedidoDoCliente))
        {
            lista.Add(new(Gravidade.Pedido, "O cliente pediu", $"pediu pelo {(s.PedidoOrigem == "portal" ? "portal" : "CS")}: {Aspas(s.PedidoDoCliente, 42)}",
                $"Pelo {(s.PedidoOrigem == "portal" ? "portal do cliente" : "Agente de CS")}{(s.PedidoEm is { } pe ? $", {Quando(pe, agora)}" : "")}: \"{s.PedidoDoCliente}\".",
                null, null, "responder o cliente e, se for demanda, virar task", "você",
                [new(s.PedidoEm ?? agora, s.PedidoDoCliente)], s.PedidoEm, Estagio.Chegada, $"pedido:{s.PedidoEm:yyyyMMddHHmm}:{s.PedidoDoCliente.GetHashCode()}"));
        }

        /* ---- 6 · cliente frio ---------------------------------------------------- */
        if (s.DiasSemMensagem is { } frio && frio >= DiasParaFrio && !lista.Any(a => a.Gravidade == Gravidade.Churn))
        {
            lista.Add(new(Gravidade.Frio, "Grupo frio", $"frio {frio} d",
                $"{frio} dias corridos sem mensagem no grupo do cliente.", null, null,
                "mandar uma mensagem com o que aconteceu na conta esta semana", "você",
                [new(agora.AddDays(-frio), "última mensagem no grupo")], agora.AddDays(-frio), Estagio.Chegada, $"frio:{frio / 2}"));
        }

        return lista.OrderBy(a => a.Gravidade).ToList();
    }

    static long? CustoDoConjunto(ConjuntoEmVeiculacao cj)
    {
        var mql = cj.Leads.Count(l => l.Situacao == Situacao.Mql);
        return mql == 0 ? null : cj.GastoVidaCentavos / mql;
    }

    static string Pct(long parte, long todo) => todo <= 0 ? "0%" : $"{(int)Math.Round(parte * 100.0 / todo)}%";

    /// <summary>"ontem 07:30", "hoje 14:02", "06/11 07:30".</summary>
    public static string Relativo(DateTime? q, DateTime agora)
    {
        if (q is not { } d) return "";
        var dias = (agora.Date - d.Date).Days;
        return dias switch { 0 => $"hoje {d:HH:mm}", 1 => $"ontem {d:HH:mm}", _ => $"{d:dd/MM} {d:HH:mm}" };
    }

    static string Quando(DateTime? q, DateTime agora)
    {
        if (q is not { } d) return "sem hora registrada";
        var dias = (agora.Date - d.Date).Days;
        return dias switch { 0 => $"hoje às {d:HH:mm}", 1 => $"ontem, {d:dd/MM} às {d:HH:mm}", _ => $"em {d:dd/MM} às {d:HH:mm}" };
    }

    static string Primeira(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    static string Aspas(string s, int max) => s.Length <= max ? $"\"{s}\"" : $"\"{s[..max].TrimEnd()}…\"";
}
