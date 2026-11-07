using BlueOS.Core.Modelos;
using BlueOS.Core.Panorama;

namespace BlueOS.Core.Api;

/* ============================================================================
   O PANORAMA NA DEMONSTRAÇÃO: 50 clientes do POSEIDON, o placar e os vistos.
   ----------------------------------------------------------------------------
   Os 5 clientes do fluxo (Nautilus, LogTech, Horizonte, Vita, Aurora) ficam
   como estão; entram 45 a mais, 41 em dia e os que contam a história do mapa:
   Ferraz (token vencido), Batcar (budget acabando), Leanspay (pedido pelo CS),
   AZ Collections (grupo frio). Aurora ganha o risco de churn do CS. Pontos:
   quando um alerta some entre uma leitura e outra, quem olha ganha os pontos
   da gravidade e o feed "acendeu hoje" registra.
   ============================================================================ */
public sealed partial class DemoApi
{
    readonly List<Visto> _vistos = new();
    readonly Dictionary<string, (string Assinatura, Gravidade Gravidade)> _ultimoAlerta = new();
    readonly List<Acendeu> _acendeu = new();
    readonly List<Acendeu> _aCaminho = new();
    int _pontos = 1240;
    const string Gestor = "Rafael";

    static readonly string[] NomesEmDia =
    [
        "Alvorada Café", "Bioma Pet", "Cedro Engenharia", "Delta Vistorias", "Estação Fit", "Fazenda Mirante", "Gran Vidros", "Helix Labs",
        "Ipê Construtora", "Jardim Secreto", "Kavalo Motos", "Lume Arquitetura", "Maré Alta Surf", "Nexo Contábil", "Oliva Cosméticos", "Pólo Odonto",
        "Quinta do Sol", "Rota Logística", "Safira Joias", "Terra Viva Agro", "Ultra Clean", "Vértice Imóveis", "Wave Idiomas", "Zênite Solar",
        "Amparo Saúde", "Brisa Hotéis", "Cosmos Eventos", "Dínamo Academia", "Essência Spa", "Farol Náutica", "Gaia Orgânicos", "Horta Urbana",
        "Ícone Estúdio", "Jurerê Pescados", "Kombi Food", "Lótus Yoga", "Mangue Bikes", "Nobre Advocacia", "Orla Veterinária", "Prisma Óptica", "Bonifq",
    ];
    static readonly string[] Nichos41 = ["alimentacao-food", "saude", "construcao-engenharia", "servicos-profissionais", "saude", "varejo-distribuicao", "industria-logistica", "tecnologia-infra-dev"];
    static readonly string[] Cores = ["#0e5fd8", "#1d7a5f", "#7c3aed", "#0891b2", "#b45309", "#be185d", "#4338ca", "#047857"];

    void SemearPanorama()
    {
        var agora = Relogio.Agora;
        var p = Parametros;

        /* Aurora: o CS marcou risco crítico (ela chegou e ninguém respondeu no grupo). */
        var aurora = _clientes.First(c => c.Slug == "escola-aurora");
        aurora.Sinais.NotaCs = 45; aurora.Sinais.FaixaCs = "critico"; aurora.Sinais.RiscoChurn = "critico";
        aurora.Sinais.DiasSemMensagem = 11; aurora.Sinais.PromessaAtrasada = "onboarding em 7 dias";
        aurora.Sinais.SinaisDeChurn = ["11 dias sem resposta no grupo", "promessa de onboarding atrasada"];

        /* Horizonte, Vita e LogTech: nota do CS para o aprofundamento. */
        _clientes.First(c => c.Slug == "horizonte-imoveis").Sinais.NotaCs = 81;
        _clientes.First(c => c.Slug == "horizonte-imoveis").Sinais.FaixaCs = "excelente";
        _clientes.First(c => c.Slug == "clinica-vita").Sinais.NotaCs = 88;
        _clientes.First(c => c.Slug == "clinica-vita").Sinais.FaixaCs = "excelente";
        _clientes.First(c => c.Slug == "logtech-frotas").Sinais.NotaCs = 58;
        _clientes.First(c => c.Slug == "logtech-frotas").Sinais.FaixaCs = "atencao";
        _clientes.First(c => c.Slug == "nautilus-seguros").Sinais.NotaCs = 74;
        _clientes.First(c => c.Slug == "nautilus-seguros").Sinais.FaixaCs = "saudavel";

        /* Gravidade 1: o token do squad venceu e a carga de hoje recusou. */
        var ferraz = DemoFabrica.EmAutomacao("Ferraz Contábil", "#b45309", "contabil-financeiro", 3000, 85, 0.42, 0.45, p, agora, 160, ModoDeExecucao.Sombra, 58);
        ferraz.Conta.TokenValido = false; ferraz.Conta.TokenMotivo = "expirou em 05/11"; ferraz.Conta.TokenConferidoEm = agora.Date.AddDays(-1).AddHours(7.5);
        ferraz.Sinais.MetaLidaEm = agora.Date.AddDays(-1).AddHours(7.5); ferraz.Sinais.AgenteRodouEm = agora.Date.AddDays(-2).AddHours(7.5);
        ferraz.Sinais.ConjuntosAtivos = 3; ferraz.Sinais.VerbaDiariaCentavos = 7700;
        _clientes.Add(ferraz);

        /* Gravidade 2: o budget do mês acaba antes do mês. */
        var batcar = DemoFabrica.EmAutomacao("Batcar", "#4338ca", "automotivo", 8000, 150, 0.98, 1.0, p, agora, 300, ModoDeExecucao.Executa, 79);
        _clientes.Add(batcar);

        /* Gravidade 5: o cliente pediu pelo CS. */
        var leanspay = DemoFabrica.EmAutomacao("Leanspay", "#047857", "financeiro-credito-seguros", 2500, 120, 0.22, 0.3, p, agora, 90, ModoDeExecucao.Recomenda, 66);
        leanspay.Sinais.PedidoDoCliente = "quando sobe a campanha de novembro?"; leanspay.Sinais.PedidoEm = agora.AddHours(-15); leanspay.Sinais.PedidoOrigem = "cs";
        _clientes.Add(leanspay);

        /* Gravidade 6: grupo frio. */
        var az = DemoFabrica.EmAutomacao("AZ Collections", "#be185d", "servicos-profissionais", 3500, 150, 0.28, 0.3, p, agora, 75, ModoDeExecucao.Recomenda, 61);
        az.Sinais.DiasSemMensagem = 4;
        _clientes.Add(az);

        /* 41 em dia. */
        for (var i = 0; i < NomesEmDia.Length; i++)
        {
            var budget = 1500 + (i % 7) * 500 + (i % 3 == 0 ? 2000 : 0);
            var teto = 60 + (i % 5) * 20;
            /* dia 7 de 30: ~23% do budget gasto, R$/MQL um pouco abaixo do teto */
            var gastoFracao = 0.20 + (i % 6) * 0.015;
            var c = DemoFabrica.EmAutomacao(NomesEmDia[i], Cores[i % Cores.Length], Nichos41[i % Nichos41.Length], budget, teto,
                gastoFracao, gastoFracao / (0.62 + (i % 4) * 0.05), p, agora, 60 + i * 5, i % 3 == 0 ? ModoDeExecucao.Executa : i % 3 == 1 ? ModoDeExecucao.Sombra : ModoDeExecucao.Recomenda, 70 + (i % 4) * 6);
            _clientes.Add(c);
        }

        _acendeu.Add(new(agora.Date.AddHours(7.87), "Brisa Hotéis", 30, "Marina"));
        _acendeu.Add(new(agora.Date.AddDays(-1).AddHours(16.2), "Kombi Food", 40, Gestor));
        _acendeu.Add(new(agora.Date.AddDays(-1).AddHours(11.1), "Pólo Odonto", 25, "Caio"));
    }

    public async Task<PanoramaDoSquad> LerPanoramaAsync(string? squad)
    {
        var agora = Relogio.Agora;
        /* Os 5 do fluxo têm conjuntos de verdade: os KPIs saem do Ongoing. Os outros já vêm prontos. */
        foreach (var c in _clientes.Where(c => c.Sinais.Kpis is null && c.Conjuntos.Count > 0))
            c.Sinais.Kpis = (await LerOngoingAsync(c)).Kpis;

        var placarBase = new Placar(Gestor, _pontos, 2, 4, 7, 24,
            [new("Marina", 1390, false), new(Gestor, _pontos, true), new("Caio", 980, false), new("Júlia", 870, false)],
            [], _acendeu.OrderByDescending(a => a.Quando).Take(6).ToList(), 0, 14, 79);

        var sinais = new List<SinalDeVida>
        {
            new("Meta (gasto, leads)", Montador.HaQuanto(agora.AddMinutes(-4), agora), false),
            new("token do squad", _clientes.Any(c => c.Conta.TokenValido == false) ? $"conferido {agora.Date.AddHours(8.03):HH:mm} · vencido" : $"conferido {agora.Date.AddHours(8.03):HH:mm}", _clientes.Any(c => c.Conta.TokenValido == false)),
            new("agente de tráfego", $"rodou {agora.Date.AddHours(7.5):HH:mm}", false),
            new("grupos do CS", Montador.HaQuanto(agora.AddMinutes(-1), agora, feminino: false) + "s", false),
            new("publicações", _clientes.Any(c => c.Publicacao is { Estado: "criando" or "parcial" }) ? "1 em andamento" : "nenhuma pendente", false),
        };

        var panorama = Montador.Montar(_clientes, squad, Parametros, agora, _vistos, sinais, placarBase);

        /* Pontos: um alerta que sumiu entre uma leitura e outra acendeu a estrela. */
        var ganhou = false;
        foreach (var e in panorama.Estrelas)
        {
            if (e.Principal is { } a) _ultimoAlerta[e.Slug] = (a.Assinatura, a.Gravidade);
            else if (_ultimoAlerta.Remove(e.Slug, out var antes))
            {
                var pts = Gravidades.Pontos(antes.Gravidade);
                _pontos += pts;
                _acendeu.Insert(0, new(agora, e.Nome, pts, Gestor));
                _aCaminho.RemoveAll(a => a.Cliente == e.Nome);
                ganhou = true;
            }
        }
        if (ganhou)
        {
            placarBase = placarBase with { Pontos = _pontos, Gestores = [new("Marina", 1390, false), new(Gestor, _pontos, true), new("Caio", 980, false), new("Júlia", 870, false)], AcendeuHoje = _acendeu.Take(6).ToList() };
            panorama = panorama with { Placar = placarBase };
        }
        var saude = panorama.Resumo.SaudePct ?? 0;
        var squads = new List<LinhaDoRanking> { new("ATLAS", 88, false), new(panorama.Squad, saude, true), new("TRITÃO", 76, false) };
        return panorama with { Placar = panorama.Placar with { Squads = squads.OrderByDescending(s => s.Valor).ToList(), ACaminho = _aCaminho.ToList() } };
    }

    public Task RegistrarACaminhoAsync(string clienteSlug, string acao)
    {
        var c = _clientes.FirstOrDefault(x => x.Slug == clienteSlug);
        if (c is null) return Task.CompletedTask;
        var alertas = Diagnostico.DoCliente(c, Parametros, DateTime.Now);
        var pts = alertas.Count > 0 ? Gravidades.Pontos(alertas[0].Gravidade) : 10;
        _aCaminho.RemoveAll(a => a.Cliente == c.Nome);
        _aCaminho.Insert(0, new(DateTime.Now, c.Nome, pts, acao));
        Avisar();
        return Task.CompletedTask;
    }

    public Task MarcarVistoAsync(string clienteSlug, string assinatura, bool visto)
    {
        _vistos.RemoveAll(v => v.ClienteSlug == clienteSlug);
        if (visto) _vistos.Add(new Visto(clienteSlug, assinatura, Relogio.Agora, Gestor));
        Avisar();
        return Task.CompletedTask;
    }
}
