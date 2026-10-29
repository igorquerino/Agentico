using BlueOS.Core.Copy;
using BlueOS.Core.Design;
using BlueOS.Core.Fluxo;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Setup;

namespace BlueOS.Core.Api;

/* ============================================================================
   A DEMONSTRAÇÃO: o fluxo inteiro em memória, com as MESMAS regras do motor.
   ----------------------------------------------------------------------------
   O que é simulado está dito na tela: a escrita na Meta (campanha, conjuntos,
   anúncios), a carga diária (insights e leads) e os agentes de Copy e Design.
   O que NÃO é simulado: o planejador, a árvore de decisão, a régua de MQL, o
   crivo da copy e as travas (só em produção, head pra cima, tudo nasce PAUSED).
   ============================================================================ */
public sealed class DemoApi : IBlueApi
{
    readonly List<Cliente> _clientes = new();
    bool _chaveGeral = true;
    string? _motivoDaChave;
    static readonly Random Rnd = new(29);

    public string Origem => "Demonstração (dados em memória, Meta simulada)";
    public bool EhDemo => true;
    public ParametrosMotor Parametros { get; } = ParametrosMotor.PadraoDoVault;
    public bool PodeSimularVeiculacao => true;
    public event Action? Mudou;
    void Avisar() => Mudou?.Invoke();

    public DemoApi() => Semear();

    static Task<T> De<T>(T v) => Task.FromResult(v);
    static string NovoId() => Guid.NewGuid().ToString("n");
    static string IdDaMeta() => "120" + Rnd.NextInt64(100_000_000_000L, 999_999_999_999L).ToString();
    static DateOnly Hoje => DateOnly.FromDateTime(DateTime.Now);

    /* ---- clientes ------------------------------------------------------------ */

    public Task<IReadOnlyList<Cliente>> ListarClientesAsync() => De<IReadOnlyList<Cliente>>(_clientes.ToList());
    public Task<Cliente?> ObterClienteAsync(string slug) => De(_clientes.FirstOrDefault(c => c.Slug == slug));

    public Task<Cliente> CriarClienteAsync(string nome, string squad)
    {
        var slug = Slugify(nome);
        if (_clientes.Any(c => c.Slug == slug)) throw new InvalidOperationException($"já existe um cliente com o slug \"{slug}\".");
        var c = new Cliente { Id = NovoId(), Slug = slug, Nome = nome.Trim(), Squad = squad, Desde = Hoje, Status = "onboarding" };
        _clientes.Insert(0, c);
        Avisar();
        return De(c);
    }

    static string Slugify(string nome)
    {
        var s = Regua.Normalizar(nome).Replace(' ', '-');
        s = System.Text.RegularExpressions.Regex.Replace(s, "[^a-z0-9-]+", "-").Trim('-');
        return s.Length > 60 ? s[..60] : s;
    }

    /* ---- ficha --------------------------------------------------------------- */

    public Task SalvarFichaAsync(Cliente c) { Avisar(); return Task.CompletedTask; }

    public Task SalvarCardTrafegoAsync(Cliente c, string? nichoSlug, decimal? budgetMensal)
    {
        if (nichoSlug is not null && !Nichos.Valido(nichoSlug)) throw new ArgumentException($"nicho desconhecido: {nichoSlug}");
        c.NichoSlug = nichoSlug;
        c.BudgetMensal = budgetMensal;
        Avisar();
        return Task.CompletedTask;
    }

    public Task SalvarCriterioMqlAsync(Cliente c, ReguaV2? regua, decimal? tetoMql)
    {
        if (regua is not null)
        {
            var problemas = Regua.ProblemasDoRascunho(regua.Perguntas);
            if (problemas.Count > 0) throw new ArgumentException(string.Join(" ", problemas));
            regua.Perguntas = regua.Perguntas.Where(p => !string.IsNullOrWhiteSpace(p.Nome)).ToList();
        }
        c.Regua = regua;
        c.TetoMql = tetoMql;
        Avisar();
        return Task.CompletedTask;
    }

    /* ---- assets -------------------------------------------------------------- */

    public Task<Asset> SubirAssetAsync(Cliente c, TipoDeAsset tipo, string nome, string mime, byte[] bytes)
    {
        if (bytes.Length > 10 * 1024 * 1024) throw new ArgumentException("arquivo acima de 10MB");
        if (!System.Text.RegularExpressions.Regex.IsMatch(mime, @"^(image/(png|jpe?g|webp|svg\+xml)|application/pdf|text/(plain|markdown))$"))
            throw new ArgumentException("formato não aceito (imagem, PDF, txt/md)");
        /* Upsert por (cliente, TIPO, nome), como a migration 0057. */
        c.Assets.RemoveAll(a => a.Tipo == tipo && a.Nome == nome);
        var asset = new Asset
        {
            Id = NovoId(), Tipo = tipo, Nome = nome, Mime = mime, TamanhoBytes = bytes.Length, Origem = "manual",
            Url = mime.StartsWith("image/") ? $"data:{mime};base64,{Convert.ToBase64String(bytes)}" : null,
        };
        c.Assets.Add(asset);
        Avisar();
        return De(asset);
    }

    public Task RemoverAssetAsync(Cliente c, string assetId)
    {
        c.Assets.RemoveAll(a => a.Id == assetId);
        Avisar();
        return Task.CompletedTask;
    }

    public Task<ResultadoDaConta> ConectarContaAsync(Cliente c, string? adAccountId, string? pageId, string? pageNome)
    {
        if (!string.IsNullOrWhiteSpace(adAccountId))
        {
            var conta = adAccountId.Trim();
            if (conta.StartsWith("act_", StringComparison.OrdinalIgnoreCase)) conta = conta[4..];
            if (!System.Text.RegularExpressions.Regex.IsMatch(conta, @"^\d{6,20}$"))
                return De(new ResultadoDaConta(false, "a conta de anúncio é só o número que vem depois de `act_` no Gerenciador. Confere se não veio texto junto."));
            c.Conta.AdAccountId = conta;
        }
        else c.Conta.AdAccountId = null;
        c.Conta.PageId = string.IsNullOrWhiteSpace(pageId) ? null : pageId.Trim();
        c.Conta.PageNome = string.IsNullOrWhiteSpace(pageNome) ? null : pageNome.Trim();
        Avisar();
        return De(new ResultadoDaConta(true, null));
    }

    public Task SalvarContaAsync(Cliente c) { Avisar(); return Task.CompletedTask; }

    public Task<EstadoDoToken> ConferirTokenAsync(Cliente c)
    {
        /* Na demonstração a Meta "responde" sempre que há conta: é o /me da Graph. */
        var ok = c.ContaConectada;
        c.Conta.TokenValido = ok ? true : null;
        c.Conta.TokenMotivo = ok ? null : "sem conta de anúncios vinculada: não há o que conferir";
        c.Conta.TokenConferidoEm = DateTime.Now;
        Avisar();
        return De(new EstadoDoToken(c.Conta.TokenValido, c.Conta.TokenMotivo));
    }

    /* ---- copy ---------------------------------------------------------------- */

    public Task<LoteDeCopy> GerarCopyAsync(Cliente c, IReadOnlyList<GeradorDemo.Pedido> pedido, string? briefing)
    {
        if (c.Status is not ("ativo" or "onboarding"))
            throw new InvalidOperationException($"Cliente {c.Status.Replace('_', ' ')}: copy nova é só pra cliente ativo.");
        var total = pedido.Sum(p => p.NPecas);
        if (total == 0) throw new ArgumentException("pedido vazio: informe ao menos 1 peça em algum tipo/funil");
        if (total > 30) throw new ArgumentException($"máximo de 30 peças por lote: o pedido soma {total}");
        if (pedido.Any(p => p.NPecas > 15)) throw new ArgumentException("o teto por item é 15 peças");
        var lote = GeradorDemo.Gerar(c, pedido, briefing, c.Lotes.Count + 1, Parametros.VaultSha);
        c.Lotes.Insert(0, lote);
        Avisar();
        return De(lote);
    }

    public Task AjustarPecaAsync(Cliente c, string pecaId, string instrucao)
    {
        var p = c.Pecas.First(x => x.Id == pecaId);
        GeradorDemo.Ajustar(p, instrucao, c.Produto.NomeCorreto ?? c.Nome);
        Avisar();
        return Task.CompletedTask;
    }

    public Task AprovarPecaAsync(Cliente c, string pecaId, bool aprovada)
    {
        var p = c.Pecas.First(x => x.Id == pecaId);
        if (aprovada && !p.CrivoOk) throw new InvalidOperationException("a peça não passou no crivo: ajuste antes de aprovar.");
        p.Aprovada = aprovada;
        Avisar();
        return Task.CompletedTask;
    }

    /* ---- design -------------------------------------------------------------- */

    public Task<IReadOnlyList<Arte>> GerarArtesAsync(Cliente c, IReadOnlyList<string> pecaIds, IReadOnlyList<string> formatos)
    {
        var lote = (c.Artes.Count == 0 ? 0 : c.Artes.Max(a => a.Lote)) + 1;
        var novas = new List<Arte>();
        var i = 0;
        foreach (var id in pecaIds)
        {
            var peca = c.Pecas.FirstOrDefault(p => p.Id == id);
            if (peca is null) continue;
            foreach (var f in formatos) novas.Add(ArteDemo.Gerar(c, peca, f, lote, i++));
        }
        c.Artes.InsertRange(0, novas);
        Avisar();
        return De<IReadOnlyList<Arte>>(novas);
    }

    public Task AprovarArteAsync(Cliente c, string arteId, bool aprovada)
    {
        var a = c.Artes.First(x => x.Id == arteId);
        a.Aprovada = aprovada;
        if (!aprovada && c.ArtePilotoId == arteId) c.ArtePilotoId = null;
        Avisar();
        return Task.CompletedTask;
    }

    public Task EscolherPilotoAsync(Cliente c, string? arteId)
    {
        c.ArtePilotoId = arteId;
        if (arteId is not null) c.Artes.First(a => a.Id == arteId).Aprovada = true;
        Avisar();
        return Task.CompletedTask;
    }

    /* ---- campanha (Start) ---------------------------------------------------- */

    public Task<ResultadoDaEstrutura> MontarEstruturaAsync(Cliente c)
    {
        if (c.BudgetMensal is null or <= 0) return De(new ResultadoDaEstrutura(false, null, "budget", "falta o budget mensal de ads no card Tráfego: sem ele não há faixa nem estrutura."));
        if (!Nichos.Valido(c.NichoSlug)) return De(new ResultadoDaEstrutura(false, null, "nicho", "falta o nicho no card Tráfego: é ele que nomeia os conjuntos e orienta a copy."));
        var e = Estrutura.Montar(c.BudgetMensal.Value, c.Conta.TemLista, c.Conta.TemSiteOuTag, c.TetoMql, c.NichoSlug, Parametros, Hoje);
        return De(new ResultadoDaEstrutura(true, e, null, null));
    }

    public async Task<ResultadoDaPublicacao> PublicarAsync(Cliente c, PedidoDePublicacao pedido)
    {
        /* As mesmas travas do publicar/start.ts, na mesma ordem. */
        if (!c.ContaConectada) return new(false, "sem_conta", "este cliente não tem conta de anúncios vinculada.", false, null, null);
        if (string.IsNullOrWhiteSpace(c.Conta.PageId)) return new(false, "sem_pagina", "a conta não tem página do Facebook: o anúncio de cadastro precisa dela.", false, null, null);
        if (c.Regua is null || c.Regua.Perguntas.Count == 0) return new(false, "sem_regua", "sem régua v2 não há formulário: cada pergunta da régua vira pergunta do formulário.", false, null, null);
        var formularioId = pedido.FormularioId?.Trim();
        if (string.IsNullOrWhiteSpace(formularioId) && string.IsNullOrWhiteSpace(pedido.PoliticaUrl))
            return new(false, "sem_politica", "a Meta exige a URL da política de privacidade do anunciante para criar o formulário (ou informe um formulário que já existe na página).", false, null, null);
        if (string.IsNullOrWhiteSpace(pedido.Link) && string.IsNullOrWhiteSpace(pedido.PoliticaUrl))
            return new(false, "sem_link", "o anúncio precisa de um link (o site do cliente).", false, null, null);
        var arte = c.ArtePiloto;
        if (arte is null) return new(false, "sem_estrutura", "escolha o criativo piloto no passo Design antes de publicar.", false, null, null);
        var r = await MontarEstruturaAsync(c);
        if (!r.Ok || r.Estrutura is null || r.Estrutura.Conjuntos.Count == 0)
            return new(false, "sem_estrutura", r.Motivo ?? "a estrutura não tem conjuntos para publicar.", false, null, null);
        var e = r.Estrutura;
        var peca = c.Pecas.FirstOrDefault(p => p.Id == arte.PecaId);
        if (peca is not null && !peca.CrivoOk)
            return new(false, "publicacao_recusada", $"a peça da arte não passou no crivo ({peca.MotivoDoCrivo}). A publicação não sobe peça reprovada.", false, null, null);

        /* Idempotente: a mesma estrutura publicada volta como a mesma campanha. */
        if (c.Publicacao is { Estado: "ok" } pub && pub.EstruturaHash == e.Hash)
            return new(true, null, null, false, pub, "esta estrutura já tinha sido publicada: é a mesma campanha.");

        var act = c.Conta.ActId;
        var nova = new Publicacao
        {
            EstruturaHash = e.Hash, NomeDaCampanha = e.NomeDaCampanha ?? "", Simulado = true, Estado = "criando", VaultSha = e.VaultSha,
            CampanhaId = IdDaMeta(), FormularioId = formularioId ?? IdDaMeta(),
        };
        await Task.Delay(350);
        foreach (var cj in e.Conjuntos)
        {
            var adsetId = IdDaMeta();
            nova.Conjuntos.Add(new ConjuntoCriado(cj.Nome, cj.Etapa, cj.Publico, adsetId, cj.DiarioCentavos));
            nova.Anuncios.Add(new AnuncioCriado(cj.Anuncio, adsetId, IdDaMeta(), arte.PecaId));
            c.Conjuntos.Add(new ConjuntoEmVeiculacao
            {
                AdsetId = adsetId, Nome = cj.Nome, Etapa = cj.Etapa, Publico = cj.Publico, DiarioCentavos = cj.DiarioCentavos,
                Ativo = false, Formato = Formato.Imagem, AnunciosAtivos = 1, PecaId = arte.PecaId,
            });
        }
        nova.Link = $"https://adsmanager.facebook.com/adsmanager/manage/campaigns?act={c.Conta.AdAccountId}&selected_campaign_ids={nova.CampanhaId}";
        nova.Estado = "ok";
        nova.Avisos.Add("Meta simulada: nenhuma escrita real foi feita. No V1 a publicação só escreve em produção, com validate_only antes de cada escrita.");
        if (e.Conjuntos.Any(x => x.Publico.StartsWith("interesses")))
            nova.Avisos.Add("os conjuntos de interesse nascem com a segmentação base (BR, idade): defina os interesses no Gerenciador antes de ativar.");
        c.Publicacao = nova;
        c.Status = "ativo";
        c.Acoes.Insert(0, new AcaoDoAgente(DateTime.Now, "log", $"criou campanha \"{nova.NomeDaCampanha}\" com {nova.Conjuntos.Count} conjuntos e {nova.Anuncios.Count} anúncios, tudo PAUSED", "pelo Blue.OS (automacao_start)", "você", null));
        Avisar();
        return new(true, null, null, true, nova, "tudo nasceu PAUSADO. Reveja no Gerenciador antes de ativar.");
    }

    /* ---- automação ------------------------------------------------------------ */

    static IReadOnlyDictionary<string, List<string>> RespostasDe(LeadDaCentral l) =>
        Regua.RespostasDoLead(l.Respostas.Select(r => (r.Pergunta, r.Valor)));

    void AvaliarLeads(Cliente c)
    {
        foreach (var cj in c.Conjuntos)
            foreach (var l in cj.Leads)
            {
                l.Conjunto = cj.Nome;
                if (c.Regua is null) { l.Situacao = null; l.Tier = null; l.Motivo = null; continue; }
                var r = Regua.AvaliarLead(RespostasDe(l), c.Regua);
                l.Situacao = r.Situacao; l.Tier = r.Tier; l.Motivo = r.Motivo;
                l.NaoMapeadas = r.NaoMapeadas.ToList(); l.Faltantes = r.Faltantes.ToList();
            }
    }

    public Task<Ongoing> LerOngoingAsync(Cliente c)
    {
        AvaliarLeads(c);
        var hoje = Hoje;
        var mes = $"{hoje.Year:0000}-{hoje.Month:00}";
        var inicioDoMes = new DateOnly(hoje.Year, hoje.Month, 1);
        var desde = hoje.AddDays(-30);
        var insightsDoMes = c.Conjuntos.SelectMany(cj => cj.Dias.Select(d => (cj, d))).Where(x => x.d.Dia >= inicioDoMes).ToList();
        var gasto = insightsDoMes.Sum(x => x.d.GastoCentavos);
        var diasLidos = insightsDoMes.Select(x => x.d.Dia).Distinct().Count();
        var leadsDoMes = c.Conjuntos.SelectMany(cj => cj.Leads).Where(l => DateOnly.FromDateTime(l.CriadoEm) >= inicioDoMes).ToList();
        var contagem = c.Regua is null ? new ContagemPelaRegua(0, 0, 0, 0, 0, 0) : Regua.Contar(leadsDoMes.Select(RespostasDe), c.Regua);
        long? teto = c.TetoMql is > 0 ? Dinheiro.CentavosDe(c.TetoMql.Value) : null;
        long? budget = c.BudgetMensal is > 0 ? Dinheiro.CentavosDe(c.BudgetMensal.Value) : null;
        var gastoSemMql = c.Conjuntos
            .Where(cj => !cj.Leads.Any(l => l.Situacao == Situacao.Mql && DateOnly.FromDateTime(l.CriadoEm) >= inicioDoMes))
            .Sum(cj => cj.Dias.Where(d => d.Dia >= inicioDoMes).Sum(d => d.GastoCentavos));
        var kpis = new KpisDoMes(mes, gasto, budget, Math.Max(0, hoje.Day - 1), diasLidos,
            leadsDoMes.Count, contagem.Avaliados, contagem.Pendentes, Math.Max(0, contagem.Avaliados - contagem.Mql),
            contagem.Mql, contagem.MqlA, contagem.MqlB, contagem.MqlC,
            budget is not null && teto is not null ? (int)(budget / teto) : null,
            contagem.Mql > 0 ? gasto / contagem.Mql : null, teto, gastoSemMql);

        var central = c.Conjuntos.SelectMany(cj => cj.Leads).Where(l => DateOnly.FromDateTime(l.CriadoEm) >= desde)
            .OrderByDescending(l => l.CriadoEm).Take(150).ToList();

        var acoes = new List<AcaoDoAgente>();
        foreach (var rec in c.Recomendacoes.Where(r => r.Estado != EstadoDaRecomendacao.Pendente))
            acoes.Add(new AcaoDoAgente(rec.DecididaEm ?? rec.CriadoEm, "motor", $"{Arvore.RotuloDaAcao(rec.Acao).Rotulo.ToLowerInvariant()} em \"{rec.Nome}\"", rec.Estado.ToString().ToLowerInvariant(), rec.DecididaPor, rec.Motivo));
        acoes.AddRange(c.Acoes);
        acoes = acoes.OrderByDescending(a => a.Quando).Take(40).ToList();

        var alerta = budget is not null ? Arvore.AlertaBudget(gastoSemMql, budget.Value, Parametros) : null;
        return De(new Ongoing(kpis, central, acoes, c.Regua is null, alerta));
    }

    EntradaConjunto EntradaDe(Cliente c, ConjuntoEmVeiculacao cj)
    {
        var contagem = c.Regua is null ? new ContagemPelaRegua(0, 0, 0, 0, 0, 0) : Regua.Contar(cj.Leads.Select(RespostasDe), c.Regua);
        var hoje = Hoje;
        var memoria = new MemoriaDoConjunto(
            cj.UltimaAcaoExecutada is null ? null : hoje.DayNumber - cj.UltimaAcaoExecutada.Value.DayNumber,
            cj.PendenteDesde is null ? null : hoje.DayNumber - cj.PendenteDesde.Value.DayNumber,
            cj.CopiasVivas);
        return new EntradaConjunto(c.Slug, cj.AdsetId, cj.Nome, cj.Formato, cj.AnunciosAtivos, cj.DiasEntrega, cj.GastoVidaCentavos,
            cj.Leads.Count, contagem.Avaliados, contagem.Pendentes, contagem.MqlA, contagem.MqlB, contagem.MqlC, cj.Leitura, cj.CoberturaCompleta, memoria);
    }

    static string TextoDaTrilha(Decisao d, string nome)
    {
        var (rotulo, _) = Arvore.RotuloDaAcao(d.Acao);
        var disparou = d.Trilha.Where(t => t.Resultado == ResultadoDaRegra.Disparou).Select(t => t.Texto).ToList();
        var corpo = disparou.Count > 0 ? string.Join(" ", disparou) : d.Avisos.FirstOrDefault()?.Texto ?? "";
        return $"{rotulo} \"{nome}\": {corpo}";
    }

    public Task<RodadaDoMotor> RodarMotorAsync(Cliente c)
    {
        AvaliarLeads(c);
        var naoAgiu = new List<string>();
        var avaliados = new List<(string, string, Decisao)>();
        var recomendadas = new List<Recomendacao>();
        var executadas = new List<string>();
        int gravadas = 0, repetidas = 0;
        c.UltimaRodada = DateTime.Now;

        if (c.Regua is null || c.TetoMql is null or <= 0)
        {
            naoAgiu.Add("sem_regua_ou_teto");
            return De(new RodadaDoMotor(recomendadas, 0, 0, avaliados, naoAgiu, executadas, Parametros.VaultSha));
        }
        var comEntrega = c.Conjuntos.Where(cj => cj.DiasEntrega > 0).ToList();
        if (comEntrega.Count == 0)
        {
            naoAgiu.Add("sem_conjunto_com_entrega");
            return De(new RodadaDoMotor(recomendadas, 0, 0, avaliados, naoAgiu, executadas, Parametros.VaultSha));
        }
        var teto = Dinheiro.CentavosDe(c.TetoMql.Value);
        var clienteConverte = c.Conjuntos.Any(cj => cj.Leads.Any(l => l.Situacao == Situacao.Mql));
        var efetivo = _chaveGeral ? c.Modo : (ModoDeExecucao?)null;

        foreach (var cj in comEntrega)
        {
            var entrada = EntradaDe(c, cj);
            var vivo = new EntradaAoVivo(cj.Ativo, cj.DiarioCentavos);
            var d = Arvore.Decidir(entrada, vivo, teto, clienteConverte, Parametros);
            avaliados.Add((cj.AdsetId, cj.Nome, d));
            if (d.SegurouPorPendentes && cj.PendenteDesde is null) cj.PendenteDesde = Hoje;
            if (!d.SegurouPorPendentes) cj.PendenteDesde = null;
            if (d.Acao is not (Acao.PAUSAR or Acao.ALERTAR or Acao.DUPLICAR or Acao.TROCAR_CRIATIVO)) continue;
            if (c.Recomendacoes.Any(r => r.MetaId == cj.AdsetId && r.Acao == d.Acao && r.Estado == EstadoDaRecomendacao.Pendente)) { repetidas++; continue; }
            var rec = new Recomendacao
            {
                Id = NovoId(), ClienteSlug = c.Slug, MetaId = cj.AdsetId, Nome = cj.Nome, Acao = d.Acao, Decisao = d,
                Texto = TextoDaTrilha(d, cj.Nome), TextoFonte = "trilha", VaultSha = Parametros.VaultSha,
            };
            c.Recomendacoes.Insert(0, rec);
            recomendadas.Add(rec);
            gravadas++;
            if (efetivo == ModoDeExecucao.Executa && Arvore.Escreve(d.Acao))
            {
                Aplicar(c, rec, "Blue.OS (cron)");
                executadas.Add(rec.Id);
            }
            else if (efetivo == ModoDeExecucao.Sombra && Arvore.Escreve(d.Acao))
            {
                c.Acoes.Insert(0, new AcaoDoAgente(DateTime.Now, "motor", $"sombra: teria {(d.Acao == Acao.PAUSAR ? "pausado" : "duplicado")} \"{cj.Nome}\"", "teria feito (sombra)", "Blue.OS (cron)", null));
            }
        }
        Avisar();
        return De(new RodadaDoMotor(recomendadas, gravadas, repetidas, avaliados, naoAgiu, executadas, Parametros.VaultSha));
    }

    void Aplicar(Cliente c, Recomendacao rec, string por)
    {
        var cj = c.Conjuntos.First(x => x.AdsetId == rec.MetaId);
        if (rec.Acao == Acao.PAUSAR)
        {
            cj.Ativo = false;
            cj.UltimaAcaoExecutada = Hoje;
            rec.Estado = EstadoDaRecomendacao.Aplicada;
            rec.ResultadoMeta = "pause_adset: effective_status PAUSED (simulado)";
            c.Acoes.Insert(0, new AcaoDoAgente(DateTime.Now, "log", $"mudou status conjunto · conjunto \"{cj.Nome}\" → PAUSED", $"pelo Blue.OS ({por})", por, null));
        }
        else if (rec.Acao == Acao.DUPLICAR)
        {
            var copia = new ConjuntoEmVeiculacao
            {
                AdsetId = IdDaMeta(), Nome = $"{cj.Nome} · cópia", Etapa = cj.Etapa, Publico = cj.Publico,
                DiarioCentavos = rec.Decisao.CopiaDiarioCentavos ?? cj.DiarioCentavos, Ativo = false, Formato = cj.Formato,
                AnunciosAtivos = 1, CopiaDoMotor = true, OriginalId = cj.AdsetId, PecaId = cj.PecaId,
            };
            c.Conjuntos.Add(copia);
            cj.CopiasVivas++;
            cj.UltimaAcaoExecutada = Hoje;
            rec.Estado = EstadoDaRecomendacao.Aplicada;
            rec.ResultadoMeta = $"duplicate_adset: cópia {copia.AdsetId} nasceu PAUSED com {Dinheiro.Reais(copia.DiarioCentavos)}/dia (simulado)";
            c.Acoes.Insert(0, new AcaoDoAgente(DateTime.Now, "log", $"criou conjunto · \"{copia.Nome}\" com {Dinheiro.Reais(copia.DiarioCentavos)}/dia, PAUSED", $"pelo Blue.OS ({por})", por, null));
        }
        rec.DecididaPor ??= por;
        rec.DecididaEm ??= DateTime.Now;
    }

    public Task<IReadOnlyList<Recomendacao>> ListarRecomendacoesAsync(Cliente c) => De<IReadOnlyList<Recomendacao>>(c.Recomendacoes.ToList());

    public Task<Recomendacao> DecidirAsync(Cliente c, string recomendacaoId, bool aceita, string? motivo)
    {
        var rec = c.Recomendacoes.FirstOrDefault(r => r.Id == recomendacaoId) ?? throw new KeyNotFoundException("recomendação não encontrada");
        if (!aceita && string.IsNullOrWhiteSpace(motivo)) throw new ArgumentException("negar pede motivo: é o que diz ao motor onde ele errou.");
        if (rec.Estado is EstadoDaRecomendacao.Aplicada or EstadoDaRecomendacao.Incerto)
            throw new InvalidOperationException("esta recomendação já mexeu na conta: negar não desfaz. Desfazer é no Gerenciador.");
        /* 1. a decisão é GRAVADA; 2. só então a escrita acontece (decisao/route.ts). */
        rec.Estado = aceita ? EstadoDaRecomendacao.Aceita : EstadoDaRecomendacao.Negada;
        rec.DecididaPor = "você";
        rec.DecididaEm = DateTime.Now;
        rec.Motivo = motivo;
        if (aceita && Arvore.Escreve(rec.Acao))
        {
            if (!_chaveGeral) { rec.Estado = EstadoDaRecomendacao.Falhou; rec.ResultadoMeta = "a chave geral do motor está desligada: nada escreve na Meta"; }
            else Aplicar(c, rec, "aceite na aba");
        }
        Avisar();
        return De(rec);
    }

    public Task MudarModoAsync(Cliente c, ModoDeExecucao modo) { c.Modo = modo; Avisar(); return Task.CompletedTask; }
    public Task<(bool Ligada, string? Motivo)> ChaveGeralAsync() => De((_chaveGeral, _motivoDaChave));
    public Task MudarChaveGeralAsync(bool ligada, string? motivo)
    {
        if (!ligada && string.IsNullOrWhiteSpace(motivo)) throw new ArgumentException("desligar pede motivo: é o que o próximo a abrir a aba vai ler.");
        _chaveGeral = ligada; _motivoDaChave = ligada ? null : motivo; Avisar();
        return Task.CompletedTask;
    }

    /* ---- a veiculação simulada ---------------------------------------------- */

    public Task SimularVeiculacaoAsync(Cliente c, int dias)
    {
        if (c.Publicacao is not { Estado: "ok" }) throw new InvalidOperationException("publique a campanha antes de simular a veiculação.");
        var inicio = Hoje.AddDays(-dias);
        var i = 0;
        foreach (var cj in c.Conjuntos.Where(x => !x.CopiaDoMotor))
        {
            cj.Ativo = true;
            cj.PrimeiraEntrega ??= inicio;
            var perfil = (i++ % 4) switch { 0 => "vence", 1 => "queima", 2 => "caro", _ => "normal" };
            SemearDias(c, cj, inicio, dias, perfil);
        }
        c.Acoes.Insert(0, new AcaoDoAgente(DateTime.Now, "log", $"ativou a campanha no Gerenciador e a carga leu {dias} dias de veiculação (simulado)", "lido da Meta (simulado)", "você", null));
        Avisar();
        return Task.CompletedTask;
    }

    void SemearDias(Cliente c, ConjuntoEmVeiculacao cj, DateOnly inicio, int dias, string perfil)
    {
        var rnd = new Random(cj.AdsetId.GetHashCode());
        var regua = c.Regua;
        for (var d = 0; d < dias; d++)
        {
            var dia = inicio.AddDays(d);
            if (cj.Dias.Any(x => x.Dia == dia)) continue;
            var gasto = (long)(cj.DiarioCentavos * (0.9 + rnd.NextDouble() * 0.2));
            var leadsHoje = perfil switch
            {
                "vence" => rnd.Next(1, 3),
                "queima" => rnd.NextDouble() < 0.15 ? 1 : 0,
                "caro" => rnd.NextDouble() < 0.7 ? 1 : 0,
                _ => rnd.NextDouble() < 0.5 ? 1 : 0,
            };
            cj.Dias.Add(new DiaDeInsight(dia, gasto, leadsHoje));
            for (var k = 0; k < leadsHoje; k++)
            {
                var tier = perfil switch
                {
                    "vence" => rnd.NextDouble() < 0.5 ? "A" : rnd.NextDouble() < 0.6 ? "B" : "C",
                    "queima" => "nao",
                    "caro" => rnd.NextDouble() < 0.25 ? "C" : "nao",
                    _ => rnd.NextDouble() < 0.4 ? "B" : rnd.NextDouble() < 0.5 ? "C" : "nao",
                };
                if (rnd.NextDouble() < 0.06) tier = "pendente";
                cj.Leads.Add(LeadComTier(regua, tier, dia, cj.AdsetId, rnd));
            }
        }
    }

    /// <summary>Um lead cujas respostas dão o tier pedido na régua do cliente.</summary>
    static LeadDaCentral LeadComTier(ReguaV2? regua, string tier, DateOnly dia, string adsetId, Random rnd)
    {
        var respostas = new List<Resposta>();
        if (regua is not null)
        {
            var perguntas = regua.Perguntas;
            for (var i = 0; i < perguntas.Count; i++)
            {
                var q = perguntas[i];
                string Escolha(List<string> l, string fallback) => l.Count > 0 ? l[rnd.Next(l.Count)] : fallback;
                var valor = tier switch
                {
                    "A" => i < 2 ? Escolha(q.Forte, "forte") : Escolha(q.Media.Count > 0 ? q.Media : q.Forte, "média"),
                    "B" => i == 0 ? Escolha(q.Forte, "forte") : Escolha(q.Media.Count > 0 ? q.Media : q.Forte, "média"),
                    "C" => Escolha(q.Media.Count > 0 ? q.Media : q.Forte, "média"),
                    "pendente" => i == 0 ? "Outra resposta que a régua não prevê" : Escolha(q.Media.Count > 0 ? q.Media : q.Forte, "média"),
                    _ => i == perguntas.Count - 1 && q.Elim.Count > 0 ? Escolha(q.Elim, "elim") : Escolha(q.Media.Count > 0 ? q.Media : q.Forte, "média"),
                };
                if (tier == "nao" && !perguntas.Any(p => p.Elim.Count > 0) && i == 0) valor = "";
                respostas.Add(new Resposta(q.Nome, valor));
            }
        }
        return new LeadDaCentral
        {
            Id = "l_" + Guid.NewGuid().ToString("n")[..10],
            CriadoEm = dia.ToDateTime(new TimeOnly(8 + rnd.Next(0, 12), rnd.Next(0, 60))),
            AdsetId = adsetId,
            Respostas = respostas,
        };
    }

    /* ---- a semente: a carteira do squad POSEIDON em estágios diferentes ------- */

    static ReguaV2 ReguaDeSeguros() => new()
    {
        Perguntas =
        [
            new() { Nome = "cargo", Rotulo = "Qual o seu cargo?", Forte = ["Diretor", "Sócio", "Gerente de crédito"], Media = ["Coordenador", "Analista sênior"], Elim = ["Estudante", "Estagiário"] },
            new() { Nome = "volume_mensal", Rotulo = "Quantas propostas por mês?", Forte = ["Acima de 500", "100 a 500"], Media = ["30 a 100"], Elim = ["Até 30"] },
            new() { Nome = "momento", Rotulo = "Qual o seu momento?", Forte = ["Quero implantar em 30 dias"], Media = ["Estou pesquisando"], Elim = ["Só curiosidade"] },
        ],
    };

    static ReguaV2 ReguaImobiliaria() => new()
    {
        Perguntas =
        [
            new() { Nome = "cargo", Rotulo = "Qual o seu cargo?", Forte = ["Dono", "Sócio", "Diretor"], Media = ["Gerente", "Coordenador"], Elim = ["Corretor autônomo", "Estudante"] },
            new() { Nome = "corretores", Rotulo = "Quantos corretores?", Forte = ["Mais de 20", "11 a 20"], Media = ["5 a 10"], Elim = ["Até 4"] },
        ],
    };

    static ReguaV2 ReguaFrotas() => new()
    {
        Perguntas =
        [
            new() { Nome = "frota", Rotulo = "Quantos veículos sua empresa possui?", Forte = ["Mais de 50", "11 a 50"], Media = ["5 a 10"], Elim = ["Até 4"] },
            new() { Nome = "cargo", Rotulo = "Qual o seu cargo?", Forte = ["Dono", "Diretor de operações", "Gestor de frota"], Media = ["Supervisor"], Elim = ["Motorista", "Outro"] },
        ],
    };

    static Asset LogoDemo(string nome, string cor, TipoDeAsset tipo = TipoDeAsset.LogoPrincipal)
    {
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='400' height='140' viewBox='0 0 400 140'><rect width='400' height='140' rx='18' fill='{cor}'/><text x='200' y='86' text-anchor='middle' font-family='Segoe UI, Arial' font-size='44' font-weight='800' fill='#fff'>{System.Security.SecurityElement.Escape(nome)}</text></svg>";
        return new Asset
        {
            Id = NovoId(), Tipo = tipo, Nome = $"{Slugify(nome)}-logo.svg", Mime = "image/svg+xml", TamanhoBytes = svg.Length, Origem = "manual",
            Url = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg)),
        };
    }

    void Semear()
    {
        /* 1. Cliente em AUTOMAÇÃO: o modelo da operação (squad POSEIDON). */
        var nautilus = new Cliente
        {
            Id = NovoId(), Slug = "nautilus-seguros", Nome = "Nautilus Seguros", Status = "ativo", Squad = "POSEIDON", Desde = Hoje.AddDays(-74),
            Ficha = new() { Responsavel = "Renata Aguiar", ContatoPrincipal = "renata@nautilusseguros.com.br", Whatsapp = "11 98877-1020", Cnpj = "12.345.678/0001-90", RazaoSocial = "Nautilus Corretora de Seguros Ltda", Site = "https://nautilusseguros.com.br", Modalidade = "mrr", Mrr = 4900 },
            Icp = new() { Tipo = "B2B", Temperatura = "morno", NivelConsciencia = "3", Segmento = "Corretoras e financeiras de médio porte", Setor = "Financeiro / seguros", FaturamentoMinimo = "R$ 5 milhões/ano", CargoDecisor = "Diretor de crédito", Localizacao = "Brasil", Persona = "Diretor que aprova a esteira e responde pela inadimplência", PublicoAlvo = "Financeiras e corretoras com esteira própria", TamanhoMercado = "3 mil financeiras e corretoras no Brasil", DoresPrincipais = ["a proposta que chegou pelo site parou na esteira esperando documento que ninguém pediu", "o segurado renovou com outra corretora porque a apólice venceu sem aviso"], DesejosPrincipais = ["a esteira de crédito completa em minutos, com o Open Finance lendo o extrato", "a renovação de cada apólice avisada 45 dias antes, automática"], CanaisOndeEstao = ["LinkedIn", "Meta", "eventos do setor"] },
            Produto = new() { NomeCorreto = "Nautilus Esteira", Descricao = "Esteira de crédito e renovação de apólices com Open Finance", ModeloNegocio = "SaaS mensal por volume de propostas", Diferenciais = "Open Finance nativo; régua de renovação automática", FuncionalidadesChave = "Esteira, score por API, renovação, CET", ObjecoesComuns = "Integração com o core bancário", Concorrentes = "Esteiras legadas" },
            Estrategia = new() { PromessaPrincipal = "aprove em minutos o que hoje leva dias", Posicionamento = "a esteira que lê o extrato antes do analista", Diferencial = "Open Finance nativo", FunilTipo = "leads por formulário nativo" },
            NichoSlug = "financeiro-credito-seguros", BudgetMensal = 6000, Regua = ReguaDeSeguros(), TetoMql = 60,
            Conta = new() { AdAccountId = "238190447551023", PageId = "104982716611234", PageNome = "Nautilus Seguros", PoliticaUrl = "https://nautilusseguros.com.br/privacidade", Link = "https://nautilusseguros.com.br", TemLista = true, TemSiteOuTag = true, TokenValido = true, TokenConferidoEm = DateTime.Now.AddHours(-3) },
            Modo = ModoDeExecucao.Recomenda,
        };
        nautilus.Assets.Add(LogoDemo("Nautilus", "#0e5fd8"));
        nautilus.Assets.Add(LogoDemo("Nautilus", "#0b1f3a", TipoDeAsset.LogoVariacao));
        _clientes.Add(nautilus);
        var loteN = GeradorDemo.Gerar(nautilus, [new(Etapa.MOFU, TipoPeca.Estatico, 3), new(Etapa.BOFU, TipoPeca.Estatico, 2)], null, 1, Parametros.VaultSha);
        foreach (var p in loteN.Pecas) p.Aprovada = p.CrivoOk;
        nautilus.Lotes.Add(loteN);
        var pecaPiloto = loteN.Pecas.First(p => p.Etapa == Etapa.MOFU && p.CrivoOk);
        var arteN = ArteDemo.Gerar(nautilus, pecaPiloto, "feed", 1, 0);
        arteN.Aprovada = true;
        nautilus.Artes.Add(arteN);
        nautilus.Artes.Add(ArteDemo.Gerar(nautilus, pecaPiloto, "story", 1, 1));
        nautilus.ArtePilotoId = arteN.Id;
        var estN = Estrutura.Montar(6000, true, true, 60, nautilus.NichoSlug, Parametros, Hoje.AddDays(-14));
        var pubN = new Publicacao { EstruturaHash = estN.Hash, NomeDaCampanha = estN.NomeDaCampanha ?? "", Simulado = true, Estado = "ok", VaultSha = estN.VaultSha, CampanhaId = IdDaMeta(), FormularioId = IdDaMeta(), Em = DateTime.Now.AddDays(-14) };
        var perfis = new[] { "vence", "queima", "caro", "aprendendo", "normal", "alerta" };
        var k = 0;
        foreach (var cj in estN.Conjuntos)
        {
            var adsetId = IdDaMeta();
            pubN.Conjuntos.Add(new ConjuntoCriado(cj.Nome, cj.Etapa, cj.Publico, adsetId, cj.DiarioCentavos));
            pubN.Anuncios.Add(new AnuncioCriado(cj.Anuncio, adsetId, IdDaMeta(), pecaPiloto.Id));
            var perfil = perfis[k++ % perfis.Length];
            var dias = perfil == "aprendendo" ? 1 : perfil == "alerta" ? 3 : 12;
            var conj = new ConjuntoEmVeiculacao { AdsetId = adsetId, Nome = cj.Nome, Etapa = cj.Etapa, Publico = cj.Publico, DiarioCentavos = cj.DiarioCentavos, Ativo = true, PrimeiraEntrega = Hoje.AddDays(-dias), PecaId = pecaPiloto.Id };
            nautilus.Conjuntos.Add(conj);
            SemearDias(nautilus, conj, Hoje.AddDays(-dias), dias, perfil == "aprendendo" || perfil == "alerta" ? "queima" : perfil);
        }
        pubN.Link = $"https://adsmanager.facebook.com/adsmanager/manage/campaigns?act={nautilus.Conta.AdAccountId}&selected_campaign_ids={pubN.CampanhaId}";
        nautilus.Publicacao = pubN;
        nautilus.Acoes.Add(new AcaoDoAgente(DateTime.Now.AddDays(-14), "log", $"criou campanha \"{pubN.NomeDaCampanha}\" com {pubN.Conjuntos.Count} conjuntos, tudo PAUSED", "pelo Blue.OS (automacao_start)", "Alef", null));
        nautilus.Acoes.Add(new AcaoDoAgente(DateTime.Now.AddDays(-13), "log", "mudou status campanha · campanha → ACTIVE", "lido da Meta", "Alef", null));
        /* O cron já rodou hoje: a carteira abre com as decisões do agente esperando o gestor. */
        RodarMotorAsync(nautilus).GetAwaiter().GetResult();

        /* 2. Cliente em CAMPANHA: copy aprovada, artes geradas, piloto escolhido, falta publicar. */
        var logtech = new Cliente
        {
            Id = NovoId(), Slug = "logtech-frotas", Nome = "LogTech Frotas", Status = "onboarding", Squad = "POSEIDON", Desde = Hoje.AddDays(-19),
            Ficha = new() { Responsavel = "Marcos Vilela", ContatoPrincipal = "marcos@logtechfrotas.com.br", Whatsapp = "41 99912-3344", Cnpj = "33.221.009/0001-44", RazaoSocial = "LogTech Tecnologia em Frotas Ltda", Site = "https://logtechfrotas.com.br", Modalidade = "mrr", Mrr = 3900 },
            Icp = new() { Tipo = "B2B", Temperatura = "morno", NivelConsciencia = "2", Segmento = "Transportadoras com frota própria", Setor = "Logística", FaturamentoMinimo = "R$ 10 milhões/ano", CargoDecisor = "Gestor de frota", Localizacao = "Sul e Sudeste", Persona = "Gestor que responde pelo diesel e pelo SLA", PublicoAlvo = "Transportadoras de 11 a 200 veículos", DoresPrincipais = ["o caminhão ficou 2 horas com o motor ocioso no pátio e o diesel saiu da margem da viagem", "a entrega atrasou e o cliente soube antes da torre de controle"], DesejosPrincipais = ["cada parada da frota vista na hora, com o motorista avisado", "o cliente avisado do atraso antes de perguntar"], CanaisOndeEstao = ["Meta", "WhatsApp", "feiras de transporte"] },
            Produto = new() { NomeCorreto = "LogTech Torre", Descricao = "Telemetria e torre de controle para frotas", ModeloNegocio = "SaaS por veículo", Diferenciais = "Alerta de motor ocioso em tempo real", FuncionalidadesChave = "Telemetria, roteirização, aviso ao cliente", ObjecoesComuns = "Instalação do rastreador", Concorrentes = "Rastreadores tradicionais" },
            Estrategia = new() { PromessaPrincipal = "corte o diesel ocioso em 30 dias", Posicionamento = "a torre que vê a frota antes do cliente ligar", Diferencial = "alerta em tempo real", FunilTipo = "leads por formulário nativo" },
            NichoSlug = "industria-logistica", BudgetMensal = 3000, Regua = ReguaFrotas(), TetoMql = 85,
            Conta = new() { AdAccountId = "551200398117655", PageId = "109773155008811", PageNome = "LogTech Frotas", PoliticaUrl = "https://logtechfrotas.com.br/privacidade", Link = "https://logtechfrotas.com.br", TemLista = false, TemSiteOuTag = true, TokenValido = true, TokenConferidoEm = DateTime.Now.AddHours(-1) },
        };
        logtech.Assets.Add(LogoDemo("LogTech", "#1d7a5f"));
        _clientes.Add(logtech);
        var loteL = GeradorDemo.Gerar(logtech, [new(Etapa.MOFU, TipoPeca.Estatico, 4)], "foco no diesel ocioso e no SLA de entrega", 1, Parametros.VaultSha);
        foreach (var p in loteL.Pecas) p.Aprovada = p.CrivoOk;
        logtech.Lotes.Add(loteL);
        var pl = loteL.Pecas.First(p => p.CrivoOk);
        var a1 = ArteDemo.Gerar(logtech, pl, "feed", 1, 0); a1.Aprovada = true;
        var a2 = ArteDemo.Gerar(logtech, pl, "story", 1, 1);
        var a3 = ArteDemo.Gerar(logtech, loteL.Pecas.Where(p => p.CrivoOk).Skip(1).First(), "feed", 1, 2);
        logtech.Artes.AddRange([a1, a2, a3]);
        logtech.ArtePilotoId = a1.Id;

        /* 3. Cliente em COPY: ficha completa, nenhum lote ainda. */
        var horizonte = new Cliente
        {
            Id = NovoId(), Slug = "horizonte-imoveis", Nome = "Horizonte Imóveis", Status = "onboarding", Squad = "POSEIDON", Desde = Hoje.AddDays(-9),
            Ficha = new() { Responsavel = "Paula Siqueira", ContatoPrincipal = "paula@horizonteimoveis.com.br", Whatsapp = "31 99655-7788", Cnpj = "08.775.102/0001-31", RazaoSocial = "Horizonte Negócios Imobiliários Ltda", Site = "https://horizonteimoveis.com.br", Modalidade = "tcv", ValorTotal = 18000 },
            Icp = new() { Tipo = "B2B", Temperatura = "frio", NivelConsciencia = "2", Segmento = "Imobiliárias com equipe de corretores", Setor = "Imobiliário", FaturamentoMinimo = "R$ 2 milhões/ano", CargoDecisor = "Dono de imobiliária", Localizacao = "Minas Gerais", Persona = "Dono que acompanha o VGV no WhatsApp", PublicoAlvo = "Imobiliárias de 5 a 40 corretores", DoresPrincipais = ["o corretor saiu e levou os contatos que só existiam no WhatsApp dele", "o lead do portal ficou 3 horas sem resposta e fechou com outra imobiliária"], DesejosPrincipais = ["cada lead do portal com dono, resposta e histórico"], CanaisOndeEstao = ["Meta", "portais", "Instagram"] },
            Produto = new() { NomeCorreto = "Horizonte CRM", Descricao = "CRM imobiliário com distribuição de leads dos portais", ModeloNegocio = "SaaS por corretor", Diferenciais = "distribuição automática do lead do portal", FuncionalidadesChave = "Integração com portais, agenda de visitas, histórico", ObjecoesComuns = "Migrar os contatos do WhatsApp", Concorrentes = "CRMs genéricos" },
            Estrategia = new() { PromessaPrincipal = "nenhum lead do portal sem dono", Posicionamento = "o CRM que responde antes do concorrente", Diferencial = "distribuição automática", FunilTipo = "leads por formulário nativo" },
            NichoSlug = "imobiliario", BudgetMensal = 4500, Regua = ReguaImobiliaria(), TetoMql = 70,
            Conta = new() { AdAccountId = "990214667320018", PageId = "112203455667788", PageNome = "Horizonte Imóveis", Link = "https://horizonteimoveis.com.br", PoliticaUrl = "https://horizonteimoveis.com.br/privacidade", TemLista = false, TemSiteOuTag = false, TokenValido = true, TokenConferidoEm = DateTime.Now.AddMinutes(-40) },
        };
        horizonte.Assets.Add(LogoDemo("Horizonte", "#7c3aed"));
        horizonte.Assets.Add(new Asset { Id = NovoId(), Tipo = TipoDeAsset.Tela, Nome = "painel-de-leads.png", Mime = "image/png", TamanhoBytes = 184_200, Origem = "manual" });
        _clientes.Add(horizonte);

        /* 4. Cliente em ASSETS: chegou, mandou a logo, falta o resto. */
        var vita = new Cliente
        {
            Id = NovoId(), Slug = "clinica-vita", Nome = "Clínica Vita", Status = "onboarding", Squad = "POSEIDON", Desde = Hoje.AddDays(-2),
            Ficha = new() { Responsavel = "Dr. Henrique Costa", ContatoPrincipal = "henrique@clinicavita.com.br", Site = "https://clinicavita.com.br" },
            Icp = new() { Tipo = "B2C", Temperatura = "morno", NivelConsciencia = "3" },
            NichoSlug = "saude",
        };
        vita.Assets.Add(LogoDemo("Vita", "#0891b2"));
        _clientes.Add(vita);

        /* 5. Cliente que acabou de CHEGAR. */
        _clientes.Add(new Cliente { Id = NovoId(), Slug = "escola-aurora", Nome = "Escola Aurora", Status = "onboarding", Squad = "POSEIDON", Desde = Hoje });
    }
}
