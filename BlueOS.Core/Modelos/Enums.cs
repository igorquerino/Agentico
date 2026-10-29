namespace BlueOS.Core.Modelos;

/// <summary>Etapa do funil. Os nomes são os do motor do V1 (lib/trafego/motor/contrato.ts).</summary>
public enum Etapa { TOFU, MOFU, BOFU }

/// <summary>A faixa do budget mensal (planejador/faixa.ts).</summary>
public enum FaixaId { AbaixoMinimo, F1Mofu, F2MofuBofu, F3Completo }

/// <summary>A ação da árvore de otimização sobre um conjunto.</summary>
public enum Acao { PAUSAR, ALERTAR, AGUARDAR, DUPLICAR, TROCAR_CRIATIVO, MANTER, SEM_BASE, SEM_HISTORICO }

public enum Formato { Imagem, Video, Misto, Desconhecido }

/// <summary>Estado da leitura dos leads do conjunto. Vencida = a foto passou de 36 h.</summary>
public enum Leitura { Medida, SemAcesso, Incompleta, Vencida }

public enum Tier { A, B, C }

public enum Situacao { Mql, NaoMql, Pendente }

/// <summary>Modo de execução do motor por cliente (motor/execucao.ts).</summary>
public enum ModoDeExecucao { Recomenda, Sombra, Executa }

/// <summary>O estágio do cliente no fluxo único do gestor de tráfego. A ordem é a do fluxo.</summary>
public enum Estagio { Chegada = 0, Assets = 1, Ficha = 2, Copy = 3, Design = 4, Campanha = 5, Automacao = 6 }

public enum TipoPeca { Estatico, Video }

public enum EstadoDaRecomendacao { Pendente, Aceita, Negada, Aplicada, Falhou, Incerto }

public enum TipoDeAsset { LogoPrincipal, LogoVariacao, Tela }

public enum ResultadoDaRegra { Disparou, Passou, NaoSeAplica, Info }
