using BlueOS.Core.Copy;
using BlueOS.Core.Design;
using BlueOS.Core.Fluxo;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

namespace BlueOS.Core.Api;

/// <summary>
/// Fabrica clientes de demonstração em estágios conhecidos, com as MESMAS regras do fluxo (Estagios.DoCliente
/// confere de verdade). Usada pelo DemoApi para encher a carteira do Panorama e pelos testes.
/// </summary>
public static class DemoFabrica
{
    static readonly Random Rnd = new(7);
    static string NovoId() => Guid.NewGuid().ToString("n");
    static string IdDaMeta() => "120" + Rnd.NextInt64(100_000_000_000L, 999_999_999_999L);
    static DateOnly Hoje => DateOnly.FromDateTime(DateTime.Now);

    public static string Slugify(string nome)
    {
        var s = Regua.Normalizar(nome).Replace(' ', '-');
        s = System.Text.RegularExpressions.Regex.Replace(s, "[^a-z0-9-]+", "-").Trim('-');
        return s.Length > 60 ? s[..60] : s;
    }

    public static Asset Logo(string nome, string cor, TipoDeAsset tipo = TipoDeAsset.LogoPrincipal)
    {
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='400' height='140' viewBox='0 0 400 140'><rect width='400' height='140' rx='18' fill='{cor}'/><text x='200' y='86' text-anchor='middle' font-family='Segoe UI, Arial' font-size='44' font-weight='800' fill='#fff'>{System.Security.SecurityElement.Escape(nome)}</text></svg>";
        return new Asset
        {
            Id = NovoId(), Tipo = tipo, Nome = $"{Slugify(nome)}-logo.svg", Mime = "image/svg+xml", TamanhoBytes = svg.Length, Origem = "manual",
            Url = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg)),
        };
    }

    static ReguaV2 ReguaPadrao() => new()
    {
        Perguntas =
        [
            new() { Nome = "cargo", Rotulo = "Qual o seu cargo?", Forte = ["Dono", "Sócio", "Diretor"], Media = ["Gerente", "Coordenador"], Elim = ["Estudante", "Estagiário"] },
            new() { Nome = "momento", Rotulo = "Qual o seu momento?", Forte = ["Quero começar em 30 dias"], Media = ["Estou pesquisando"], Elim = ["Só curiosidade"] },
        ],
    };

    /// <summary>Um cliente em AUTOMAÇÃO: assets, ficha, copy aprovada por etapa da faixa, uma arte por etapa no assento certo e
    /// a campanha publicada (simulada). Os KPIs do mês entram prontos em `Sinais.Kpis` (a demo não precisa de 60 dias de insights).</summary>
    public static Cliente EmAutomacao(string nome, string cor, string nicho, decimal budget, decimal teto, double gastoFracao, double mqlFracao,
        ParametrosMotor p, DateTime agora, int diasDeCasa = 120, ModoDeExecucao modo = ModoDeExecucao.Executa, int nota = 78)
    {
        var c = new Cliente
        {
            Id = NovoId(), Slug = Slugify(nome), Nome = nome, Status = "ativo", Squad = "POSEIDON", Desde = Hoje.AddDays(-diasDeCasa),
            Ficha = new() { Modalidade = "mrr", Mrr = Math.Round(budget * 0.8m) },
            Icp = new() { Tipo = "B2B", Temperatura = "morno", NivelConsciencia = "3" },
            NichoSlug = nicho, BudgetMensal = budget, TetoMql = teto, Regua = ReguaPadrao(), Modo = modo,
            Conta = new() { AdAccountId = Rnd.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(), PageId = Rnd.NextInt64(100_000_000_000_000, 999_999_999_999_999).ToString(), PageNome = nome, Link = $"https://{Slugify(nome).Replace("-", "")}.com.br", PoliticaUrl = $"https://{Slugify(nome).Replace("-", "")}.com.br/privacidade", TemLista = true, TemSiteOuTag = true, TokenValido = true, TokenConferidoEm = agora.AddHours(-1) },
        };
        c.Assets.Add(Logo(nome.Split(' ')[0], cor));
        var etapas = Estagios.EtapasExigidas(c, p);
        var lote = GeradorDemo.Gerar(c, etapas.Select(e => new GeradorDemo.Pedido(e, TipoPeca.Estatico, 1)).ToList(), null, 1, p.VaultSha);
        foreach (var peca in lote.Pecas) { peca.CrivoOk = true; peca.Aprovada = true; }
        c.Lotes.Add(lote);
        var i = 0;
        foreach (var e in etapas)
        {
            var peca = lote.Pecas.First(x => x.Etapa == e);
            var arte = ArteDemo.Gerar(c, peca, "feed", 1, i++);
            arte.Aprovada = true;
            c.Artes.Add(arte);
            c.Pilotos[e] = arte.Id;
        }
        var pub = new Publicacao { EstruturaHash = "demo-" + c.Slug, NomeDaCampanha = $"[BLUE] {nome} · Start", Simulado = true, Estado = "ok", VaultSha = p.VaultSha, CampanhaId = IdDaMeta(), FormularioId = IdDaMeta(), Em = agora.AddDays(-Math.Min(diasDeCasa - 3, 45)) };
        foreach (var e in etapas) pub.Conjuntos.Add(new ConjuntoCriado($"{e} · {nome}", e, "lookalike", IdDaMeta(), Dinheiro.CentavosDe(budget) / 30 / etapas.Count));
        c.Publicacao = pub;

        /* KPIs do mês */
        var budgetC = Dinheiro.CentavosDe(budget);
        var tetoC = Dinheiro.CentavosDe(teto);
        var gasto = (long)(budgetC * gastoFracao);
        var meta = (int)(budgetC / tetoC);
        var mql = Math.Max(0, (int)Math.Round(meta * mqlFracao));
        var leads = (int)(mql * 2.6);
        var dias = Math.Max(1, agora.Day - 1);
        c.Sinais.Kpis = new KpisDoMes($"{agora:yyyy-MM}", gasto, budgetC, DateTime.DaysInMonth(agora.Year, agora.Month), dias,
            leads, leads - 2, 2, leads - 2 - mql, mql, (int)(mql * 0.5), (int)(mql * 0.3), mql - (int)(mql * 0.5) - (int)(mql * 0.3),
            meta, mql > 0 ? gasto / mql : null, tetoC, mql == 0 ? gasto : (long)(gasto * 0.08));
        c.Sinais.NotaCs = nota;
        c.Sinais.FaixaCs = nota >= 80 ? "excelente" : nota >= 66 ? "saudavel" : nota >= 50 ? "atencao" : "critico";
        c.Sinais.RiscoChurn = nota >= 66 ? "baixo" : nota >= 50 ? "medio" : "alto";
        c.Sinais.DiasSemMensagem = Rnd.Next(0, 3);
        c.Sinais.MetaLidaEm = agora.AddMinutes(-4);
        c.Sinais.AgenteRodouEm = agora.Date.AddHours(7.5);
        c.Sinais.ConjuntosAtivos = etapas.Count;
        c.Sinais.VerbaDiariaCentavos = budgetC / 30;
        c.Sinais.LidoEm = agora.AddMinutes(-4);
        return c;
    }
}
