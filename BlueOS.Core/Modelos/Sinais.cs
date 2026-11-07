namespace BlueOS.Core.Modelos;

/* ============================================================================
   OS SINAIS DO CLIENTE QUE O PANORAMA LÊ ALÉM DO FLUXO.
   ----------------------------------------------------------------------------
   O estágio, a conta, o token e as recomendações já vivem no `Cliente`. Aqui
   fica o resto que o mapa estelar precisa e que a carteira comum não trazia:
   o que o Agente de CS gravou (nota, faixa, risco, grupo frio), o pedido do
   cliente (CS hoje; o portal do cliente, quando existir), a automação pausada
   e os KPIs do mês já lidos. A API preenche na carteira em UMA consulta; a
   demonstração semeia; o V1 deixa em branco (a tela degrada sem quebrar).
   ============================================================================ */
public sealed class SinaisDoCliente
{
    /* ---- Agente de CS (cs_analise do squad principal) ---- */
    /// <summary>Nota 0-100 (40% valor / 60% retenção). null = sem análise ou sem base.</summary>
    public int? NotaCs { get; set; }
    /// <summary>critico | atencao | saudavel | excelente | sem_base | sem_analise (CsRegras.FaixaDaNota).</summary>
    public string FaixaCs { get; set; } = "sem_analise";
    /// <summary>Rótulo do risco de churn gravado pelo CS: baixo | medio | alto | critico | sem_dados.</summary>
    public string? RiscoChurn { get; set; }
    /// <summary>Os sinais de churn que o CS listou, para o aprofundamento.</summary>
    public List<string> SinaisDeChurn { get; set; } = new();
    /// <summary>Variação da nota nos últimos 7 dias (tendencia_d7). null = sem histórico.</summary>
    public int? TendenciaD7 { get; set; }
    /// <summary>Dias corridos desde a última mensagem no grupo de WhatsApp do cliente (fuso de Brasília). null = sem grupo.</summary>
    public int? DiasSemMensagem { get; set; }
    /// <summary>Promessa do alinhamento comercial marcada como atrasada pelo CS, se houver.</summary>
    public string? PromessaAtrasada { get; set; }

    /* ---- pedido do cliente (CS hoje; portal do cliente no futuro) ---- */
    public string? PedidoDoCliente { get; set; }
    public DateTime? PedidoEm { get; set; }
    /// <summary>cs | portal</summary>
    public string? PedidoOrigem { get; set; }

    /* ---- automação ---- */
    /// <summary>trafego_contas.motor_automacao: null/"ligada" | "pausada" | "cancelada".</summary>
    public string? AutomacaoEstado { get; set; }
    public string? AutomacaoMotivo { get; set; }
    public DateTime? AutomacaoDesde { get; set; }
    /// <summary>Quando o agente rodou pela última vez para este cliente (trafego_auditoria / rodada).</summary>
    public DateTime? AgenteRodouEm { get; set; }
    /// <summary>Quando a carga da Meta foi lida pela última vez para este cliente.</summary>
    public DateTime? MetaLidaEm { get; set; }
    /// <summary>Quantos conjuntos seguem ativos na Meta (para "N conjuntos seguem ativos sem leitura").</summary>
    public int ConjuntosAtivos { get; set; }
    /// <summary>Verba diária somada dos conjuntos ativos, em centavos (o que custa rodar sem o agente olhar).</summary>
    public long VerbaDiariaCentavos { get; set; }

    /* ---- KPIs do mês já lidos (a API traz na carteira; a demo calcula) ---- */
    public KpisDoMes? Kpis { get; set; }

    /// <summary>Quando este cliente foi lido pela última vez em qualquer fonte: é o "sinal de vida" por cliente.</summary>
    public DateTime? LidoEm { get; set; }
}
