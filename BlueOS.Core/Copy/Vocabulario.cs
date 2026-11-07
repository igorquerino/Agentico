namespace BlueOS.Core.Copy;

/* ============================================================================
   O VOCABULÁRIO REAL DE CADA NICHO (camada C2 da régua V8).
   ----------------------------------------------------------------------------
   "Os nomes que só quem vive o dia a dia usa. Nunca o genérico quando existe o
   real do nicho." O gerador de demonstração compõe as peças a partir daqui e do
   contexto do cliente (ICP, produto, promessa). No V1, quem escreve é o modelo
   (lib/copy/gravador.ts); este vocabulário é o que deixa o MVP falar a língua
   do nicho sem chamar a API. Sem "prova com número": o demo escreve sob a marca
   de um cliente real, e número inventado ali seria afirmação falsa dele.
   ============================================================================ */
public sealed record VocabularioDoNicho(
    string Slug,
    string IcpSingular,
    string IcpPlural,
    IReadOnlyList<string> TermosReais,
    /// <summary>A situação específica que só o ICP reconhece (via B da R6): sintoma concreto.</summary>
    IReadOnlyList<string> Sintomas,
    /// <summary>O mecanismo da dor (C3): o porquê que o ICP nem sempre nomeou. Sem produto, sem marca.</summary>
    IReadOnlyList<string> MecanismosDaDor,
    /// <summary>O ganho concreto que a solução entrega (objeto do MOFU).</summary>
    IReadOnlyList<string> Ganhos);

public static class Vocabulario
{
    static readonly Dictionary<string, VocabularioDoNicho> Mapa = new()
    {
        ["imobiliario"] = new("imobiliario", "dono de imobiliária", "imobiliárias",
            ["CRECI", "portal", "VGV", "captação", "visita", "proposta"],
            ["o corretor saiu e levou os contatos que só existiam no WhatsApp dele", "o lead do portal ficou 3 horas sem resposta e fechou com outra imobiliária", "a captação boa roda no feed e ninguém sabe qual corretor respondeu"],
            ["lead sem dono no CRM não é lead, é número no portal", "cada hora sem resposta no portal é o imóvel sendo mostrado pelo concorrente", "o VGV do mês depende de quem atendeu primeiro, não de quem anunciou mais"],
            ["cada lead do portal com dono, resposta e histórico", "a captação distribuída na hora, sem passar pelo WhatsApp de um corretor só", "visitas marcadas direto do anúncio, com o corretor avisado"]),
        ["financeiro-credito-seguros"] = new("financeiro-credito-seguros", "diretor de crédito", "financeiras",
            ["Open Finance", "esteira", "CET", "sinistro", "apólice", "inadimplência", "score"],
            ["a proposta que chegou pelo site parou na esteira esperando documento que ninguém pediu", "o segurado renovou com outra corretora porque a apólice venceu sem aviso", "o analista aprova em 2 dias o que o concorrente aprova em 10 minutos"],
            ["proposta parada na esteira vira inadimplência do concorrente, não do cliente", "renovação sem régua é apólice que vence no silêncio", "o score que chega por API decide antes do analista abrir a pasta"],
            ["a esteira de crédito completa em minutos, com o Open Finance lendo o extrato", "a renovação de cada apólice avisada 45 dias antes, automática", "o CET calculado na proposta, sem planilha paralela"]),
        ["saude"] = new("saude", "gestor de clínica", "clínicas",
            ["agenda", "convênio", "glosa", "TISS", "prontuário", "no-show", "retorno"],
            ["a agenda da terça tem 6 horários vazios e a recepção só descobre na segunda", "a glosa do convênio voltou 40 dias depois e ninguém sabe de qual guia", "o paciente faltou e o retorno nunca foi remarcado"],
            ["horário vazio na agenda é custo fixo pago sem consulta", "glosa que ninguém contesta é receita que a clínica já atendeu e não recebe", "no-show sem lembrete é a agenda decidindo pelo paciente"],
            ["a agenda cheia com confirmação automática no WhatsApp", "cada guia TISS conferida antes de ir ao convênio", "o retorno remarcado sozinho quando o paciente falta"]),
        ["tecnologia-infra-dev"] = new("tecnologia-infra-dev", "CTO", "times de tecnologia",
            ["deploy", "SLA", "incidente", "on-call", "pipeline", "observabilidade", "latência"],
            ["o deploy de sexta derrubou o checkout e o alerta chegou pelo cliente, não pelo monitor", "o on-call acordou 3 vezes na madrugada para o mesmo incidente", "o pipeline leva 40 minutos e o time empurra o merge para amanhã"],
            ["incidente descoberto pelo cliente é SLA quebrado antes do alerta tocar", "pipeline lento não atrasa o deploy: ele muda a decisão de deployar", "sem observabilidade, o mesmo incidente volta com outro nome"],
            ["o alerta antes do cliente perceber, com a causa na primeira tela", "o pipeline em 6 minutos, com o merge no mesmo dia", "o on-call sabendo o que mudou antes de abrir o terminal"]),
        ["industria-logistica"] = new("industria-logistica", "gestor de frota", "transportadoras",
            ["frota", "motor ocioso", "CT-e", "roteirização", "SLA de entrega", "telemetria", "diesel"],
            ["o caminhão ficou 2 horas com o motor ocioso no pátio e o diesel saiu da margem da viagem", "a entrega atrasou e o cliente soube antes da torre de controle", "a roteirização foi feita na planilha de ontem"],
            ["motor ocioso é diesel queimado sem carga, e ninguém vê na nota", "atraso sem aviso é SLA quebrado duas vezes: na entrega e na confiança", "roteiro de planilha ignora o trânsito de hoje"],
            ["cada parada da frota vista na hora, com o motorista avisado", "o cliente avisado do atraso antes de perguntar", "o roteiro refeito sozinho quando a janela de entrega muda"]),
        ["juridico"] = new("juridico", "sócio de escritório", "escritórios",
            ["prazo", "preclusão", "publicação", "petição", "intimação", "contencioso", "honorários"],
            ["a intimação saiu no diário e o prazo foi contado a partir do dia em que alguém viu", "a petição foi protocolada às 23h50 porque o prazo só apareceu na planilha de manhã", "o cliente do contencioso perguntou do processo e o estagiário buscou no site do tribunal"],
            ["prazo perdido é preclusão, e preclusão não tem recurso", "publicação lida com atraso é dia de prazo que o escritório deu ao outro lado", "processo sem andamento visível é cliente que liga para cobrar"],
            ["toda publicação lida e distribuída no mesmo dia, com o prazo já contado", "a petição com o prazo visível desde a intimação", "o cliente vendo o andamento sem ligar para o escritório"]),
        ["contabil-financeiro"] = new("contabil-financeiro", "sócio de contabilidade", "escritórios contábeis",
            ["SPED", "DCTF", "fechamento", "conciliação", "guia", "honorário", "eSocial"],
            ["o fechamento do mês atrasou porque o cliente mandou o extrato dia 12", "a guia saiu com multa porque o certificado venceu no dia", "o eSocial rejeitou o evento e o RH do cliente ligou primeiro"],
            ["fechamento que depende do extrato do cliente é fechamento no prazo do cliente", "guia com multa é honorário que o escritório devolve", "evento rejeitado sem aviso é obrigação acessória virando passivo"],
            ["a conciliação bancária feita sozinha, com o extrato chegando por API", "o certificado e as guias avisados antes do vencimento", "o eSocial validado antes de enviar"]),
        ["educacao"] = new("educacao", "diretor de escola", "escolas",
            ["matrícula", "rematrícula", "evasão", "inadimplência", "secretaria", "boletim", "turma"],
            ["a rematrícula abriu e 30% das famílias ainda não responderam a mensagem", "o aluno saiu e a escola soube pela transferência", "a secretaria atende 80 ligações por dia sobre o mesmo boleto"],
            ["família que não responde a rematrícula já está visitando outra escola", "evasão descoberta na transferência é 6 meses de sinais ignorados", "ligação sobre boleto é secretaria fazendo o trabalho do sistema"],
            ["a rematrícula fechada no WhatsApp, com a família respondendo em 2 toques", "o sinal de evasão visto 3 meses antes da transferência", "o boleto e a segunda via sem passar pela secretaria"]),
        ["varejo-distribuicao"] = new("varejo-distribuicao", "gerente comercial", "distribuidoras",
            ["CMV", "ruptura", "giro", "mix", "pedido mínimo", "positivação", "representante"],
            ["o item de maior giro ficou em ruptura por 4 dias e o cliente comprou do concorrente", "o representante visitou 40 clientes e positivou 9", "o pedido entrou errado no ERP e a entrega voltou"],
            ["ruptura no item de giro é cliente aprendendo a comprar de outro", "positivação baixa não é vendedor ruim, é rota sem dado", "pedido digitado duas vezes é erro contado em frete"],
            ["a ruptura avisada antes do estoque zerar", "a rota do representante montada pelo giro de cada cliente", "o pedido do cliente direto no ERP, sem redigitar"]),
        ["marketing-agencias"] = new("marketing-agencias", "dono de agência", "agências",
            ["churn", "retainer", "relatório", "CPL", "escopo", "aprovação", "job"],
            ["o cliente cancelou o retainer depois de 3 meses pedindo relatório", "o job foi aprovado no WhatsApp e refeito 4 vezes", "o CPL subiu e a agência soube na reunião mensal"],
            ["cliente que pede relatório já decidiu sair, só falta a data", "aprovação no WhatsApp é escopo que ninguém assinou", "CPL descoberto na reunião mensal é um mês de verba decidida no escuro"],
            ["o relatório do cliente pronto todo dia, sem o gestor montar", "a aprovação registrada, com versão e data", "o CPL alertado no dia em que sai da meta"]),
        ["rh-gestao-pessoas"] = new("rh-gestao-pessoas", "head de RH", "times de RH",
            ["turnover", "onboarding", "vaga", "admissão", "ponto", "clima", "headcount"],
            ["a vaga está aberta há 47 dias e o gestor contratou por indicação", "o funcionário pediu demissão no terceiro mês, depois do onboarding de 2 dias", "o ponto fechou com 60 inconsistências e o RH corrigiu uma a uma"],
            ["vaga aberta por 47 dias é o custo de quem está fazendo o trabalho de dois", "turnover no terceiro mês é onboarding que não aconteceu", "inconsistência de ponto é folha paga errada ou ação trabalhista"],
            ["a vaga preenchida em 18 dias com a triagem feita sozinha", "o onboarding de 30 dias acompanhado sem planilha", "o ponto fechado sem inconsistência, com o gestor aprovando pelo celular"]),
        ["construcao-engenharia"] = new("construcao-engenharia", "engenheiro de obra", "construtoras",
            ["cronograma", "diário de obra", "medição", "empreiteiro", "RDO", "aditivo", "canteiro"],
            ["a medição do empreiteiro veio 20% acima do que o diário de obra registrou", "o cronograma atrasou 3 semanas e o cliente soube no aditivo", "o RDO foi preenchido no fim do mês, de memória"],
            ["medição sem diário é empreiteiro medindo a si mesmo", "atraso descoberto no aditivo é margem que já foi embora", "RDO de memória não defende a construtora em nenhuma disputa"],
            ["o diário de obra preenchido no canteiro, com foto e hora", "a medição conferida contra o RDO antes de pagar", "o cronograma atualizado toda semana, com o cliente vendo"]),
        ["automotivo"] = new("automotivo", "dono de concessionária", "concessionárias",
            ["lead do portal", "test drive", "seminovo", "pós-venda", "revisão", "F&I", "estoque parado"],
            ["o lead do portal esperou 5 horas e comprou o seminovo em outra loja", "o cliente fez a primeira revisão na oficina da esquina", "o carro está há 94 dias no pátio e ninguém reprecificou"],
            ["lead de carro responde na primeira hora ou não responde a ninguém", "revisão perdida é pós-venda inteiro perdido", "estoque parado é capital pagando juros no pátio"],
            ["o lead do portal com vendedor avisado em 1 minuto", "a revisão lembrada pelo WhatsApp, com agendamento no mesmo toque", "o seminovo reprecificado pelo giro, não pelo chute"]),
        ["beleza-estetica"] = new("beleza-estetica", "dona de clínica de estética", "clínicas de estética",
            ["agenda", "pacote", "sessão", "recorrência", "avaliação", "no-show", "fidelização"],
            ["a cliente comprou o pacote de 10 sessões e sumiu na quarta", "a agenda de sábado lotou e a de terça ficou vazia", "a avaliação gratuita virou 3 horas de conversa e nenhum pacote"],
            ["pacote abandonado é receita adiantada que vira reclamação", "agenda vazia na terça é custo fixo sem sessão", "avaliação sem roteiro é tempo da esteticista sem venda"],
            ["cada sessão do pacote lembrada e remarcada sozinha", "a terça preenchida com oferta só para quem já é cliente", "a avaliação com roteiro, pacote sugerido e fechamento no mesmo dia"]),
        ["alimentacao-food"] = new("alimentacao-food", "dono de restaurante", "restaurantes",
            ["CMV", "ficha técnica", "delivery", "comanda", "taxa do app", "ticket médio", "desperdício"],
            ["o CMV subiu 4 pontos e o dono descobriu no fechamento do mês", "o app de delivery ficou com 27% do pedido e o cliente nem sabe o nome do restaurante", "a ficha técnica existe no caderno do chef"],
            ["CMV descoberto no fechamento é um mês de prato vendido com prejuízo", "pedido no app é cliente do app, não do restaurante", "ficha técnica no caderno é porção que varia com o cozinheiro"],
            ["o CMV de cada prato visto todo dia, com a compra do fornecedor", "o pedido direto, sem taxa do app, com o cliente cadastrado", "a ficha técnica na cozinha, com a porção padronizada"]),
        ["servicos-profissionais"] = new("servicos-profissionais", "sócio de consultoria", "consultorias",
            ["proposta", "escopo", "hora faturável", "retainer", "entregável", "pipeline", "follow-up"],
            ["a proposta ficou 3 semanas sem resposta e ninguém fez follow-up", "o projeto estourou 40 horas fora do escopo e ninguém cobrou", "o sócio sênior passa a segunda inteira montando proposta"],
            ["proposta sem follow-up é pipeline que o concorrente fecha", "hora fora do escopo não cobrada é margem doada", "sócio montando proposta é hora faturável virando custo"],
            ["o follow-up de cada proposta feito no prazo, sozinho", "as horas fora do escopo contadas e cobradas", "a proposta montada em 20 minutos, do modelo"]),
        ["outro"] = new("outro", "decisor", "empresas",
            ["processo", "indicador", "equipe", "margem", "prazo", "cliente", "operação"],
            ["o indicador que importa só aparece no fim do mês", "a equipe refaz o mesmo trabalho em duas planilhas", "o cliente soube do problema antes da operação"],
            ["indicador atrasado é decisão tomada no escuro", "retrabalho em planilha é margem saindo pela porta dos fundos", "cliente que avisa primeiro já está pensando em trocar"],
            ["o indicador certo visto todo dia", "um lugar só para o trabalho, sem planilha paralela", "o problema avisado antes do cliente perceber"]),
    };

    public static VocabularioDoNicho Para(string? nichoSlug) =>
        nichoSlug is not null && Mapa.TryGetValue(nichoSlug, out var v) ? v : Mapa["outro"];
}
