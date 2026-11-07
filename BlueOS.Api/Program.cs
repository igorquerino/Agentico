using System.Text.Json.Serialization;
using BlueOS.Api.Dominio;
using BlueOS.Api.Infra;
using BlueOS.Core.Copy;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;
using BlueOS.Core.Publicar;

/* ============================================================================
   A API .NET DO BLUE OS: a porta única para os dados (trilha B do plano).
   ----------------------------------------------------------------------------
   Npgsql direto no Postgres do Supabase, pelo pooler, com UM round-trip por
   tela (joins e agregações em JSON no próprio SQL). O RBAC do V1
   (lib/os/escopo-server.ts, lib/trafego/escopo.ts) vive aqui numa policy só.
   A sessão é a da pessoa: login no Supabase Auth e o JWT dela em cada chamada.
   Nenhuma chave do banco sai daqui; o desktop e a web só falam com esta API.
   ============================================================================ */
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("BLUEOS_");

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddHttpClient("supabase-mgmt");
builder.Services.AddSingleton<Db>();
builder.Services.AddHttpClient<SupabaseAuth>();
builder.Services.AddSingleton<Sessoes>();
builder.Services.AddScoped<Escopo>();
builder.Services.AddScoped<Carteira>();
builder.Services.AddScoped<PanoramaApi>();
builder.Services.AddScoped<Automacao>();
builder.Services.AddScoped<Escritas>();
builder.Services.AddHttpClient("meta", h => h.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddScoped<Meta>();
/* a escrita na Meta (trilha B, 30/10/2026): a Graph, as credenciais, o registro, o executor e o publicador */
builder.Services.AddHttpClient("meta-graph");
builder.Services.AddSingleton<MetaGraph>();
/* o MCP oficial da Meta (31/10/2026): a escrita dos squads em Meta:Mcp:Squads, com o token do squad */
builder.Services.AddHttpClient("meta-mcp");
builder.Services.AddSingleton<MetaMcp>();
builder.Services.AddScoped<MetaEscrita>();
builder.Services.AddScoped<Registro>();
builder.Services.AddScoped<Executor>();
builder.Services.AddScoped<Publicador>();
/* a geração dentro da API (31/10/2026): as chaves, o modelo de texto, o Storage, o modelo de imagem, o Copy e o Designer */
builder.Services.AddSingleton<Segredos>();
builder.Services.AddSingleton<Ia>();
builder.Services.AddHttpClient("supabase-storage");
builder.Services.AddSingleton<SupabaseStorage>();
builder.Services.AddHttpClient("imagem-ia");
builder.Services.AddSingleton<ImagemIa>();
builder.Services.AddScoped<CopyAgente>();
builder.Services.AddScoped<Designer>();
builder.Services.AddScoped<Setup>();
builder.Services.AddScoped<MotorRodada>();
builder.Services.AddScoped<Cs>();
builder.Services.AddCors(c => c.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));

var app = builder.Build();
app.UseCors();

/* ---- erro vira JSON, nunca stack trace na tela do gestor ------------------------ */
app.UseExceptionHandler(h => h.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = ex is BancoNaoConfigurado ? 503 : 500;
    ctx.Response.ContentType = "application/json; charset=utf-8";
    var msg = ex switch
    {
        BancoNaoConfigurado b => b.Message,
        Npgsql.NpgsqlException n => $"banco: {n.Message}",
        _ => ex?.Message ?? "erro",
    };
    /* Em desenvolvimento, o tipo e os primeiros quadros da pilha ajudam a achar a linha. */
    var detalhe = app.Environment.IsDevelopment() && ex is not null
        ? ex.GetType().Name + ": " + string.Join(" | ", (ex.StackTrace ?? "").Split('\n').Take(4).Select(l => l.Trim()))
        : null;
    await ctx.Response.WriteAsJsonAsync(new { error = msg, detalhe });
}));

/* ---- saúde: a API sobe mesmo sem banco configurado; o /health diz o que falta ----
   Sem sessão, só `ok` e `banco` (como o /api/health do V1): a configuração (ref, travas da Meta, contas de teste,
   squads do MCP, origem das chaves, erro do banco) só sai para quem está logado, ou em Development. */
app.MapGet("/api/health", async (HttpContext http, Db db, IConfiguration cfg, MetaGraph graph, MetaMcp mcp, Ia ia, ImagemIa imagem) =>
{
    var banco = await db.ConfereAsync();
    if (!app.Environment.IsDevelopment() && await UsuarioDaSessaoAsync(http) is null)
        return Results.Ok(new { ok = banco.Ok, banco = banco.Ok ? "conectado" : "indisponível" });
    return Results.Ok(new
    {
        ok = banco.Ok,
        banco = banco.Ok ? "conectado" : banco.Erro,
        transporte = db.Transporte,
        projeto = db.Ref,
        producao_bloqueada = Db.RefProducao,
        supabase = string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) ? "falta Supabase:Url" : cfg["Supabase:Url"],
        anon_key = string.IsNullOrWhiteSpace(cfg["Supabase:AnonKey"]) ? "falta Supabase:AnonKey" : "ok",
        meta = graph.EstadoDaTrava(),
        meta_mcp = mcp.Estado(),
        ia = ia.Estado(),
        imagem = imagem.Estado(),
    });
});

/* ---- login: usuário ou e-mail + senha, como o /api/os/login do V1 ---------------- */
app.MapPost("/api/login", async (LoginPedido p, Db db, SupabaseAuth auth, Sessoes sessoes, Escopo escopo) =>
{
    if (string.IsNullOrWhiteSpace(p.Login) || string.IsNullOrWhiteSpace(p.Senha)) return Results.BadRequest(new { error = "Informe usuario e senha." });
    var termo = p.Login.Trim().ToLowerInvariant();
    var u = await db.UmAsync(Sql.Fmt($"""
        select u.user_id::text as user_id, u.user_nome, u.user_username, u.user_ativo, au.email
        from public.users u join auth.users au on au.id = u.user_id
        where lower(u.user_username) = {termo} or lower(u.user_email) = {termo}
        limit 1
        """), r => new { Id = r.S("user_id")!, Nome = r.S("user_nome") ?? "", Username = r.S("user_username") ?? "", Ativo = r.B("user_ativo") ?? false, Email = r.S("email") ?? "" });
    if (u is null) return Results.Json(new { error = "Usuario ou senha invalidos." }, statusCode: 401);
    if (!u.Ativo) return Results.Json(new { error = "Conta inativa." }, statusCode: 403);
    var sessao = await auth.EntrarAsync(u.Email, p.Senha);
    if (sessao is null) return Results.Json(new { error = "Usuario ou senha invalidos." }, statusCode: 401);
    sessoes.Guardar(sessao.Token, sessao.UserId, sessao.ExpiraEm);
    var esc = await escopo.DoUsuarioAsync(sessao.UserId);
    return Results.Ok(new
    {
        ok = true, token = sessao.Token, expira_em = sessao.ExpiraEm,
        usuario = new { user_id = u.Id, nome = u.Nome, username = u.Username },
        escopo = new { global = esc.Global, squads = esc.SquadIds?.Count, papeis = esc.Papeis },
    });
});

/* ---- a partir daqui, tudo exige a sessão ------------------------------------------
   A sessão é conferida no GoTrue (JWT válido e conta não banida) e guardada por até 5 min, sem renovar no acerto.
   O cargo que só lê (o `rh`, lib/os/somente-leitura.ts do V1) não escreve em nada: todo POST, PUT, PATCH ou DELETE
   de quem só tem cargos de leitura leva 403 aqui, antes da rota. */
var api = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
{
    var http = ctx.HttpContext;
    if (TokenDe(http) == "") return Results.Json(new { error = "sem sessão" }, statusCode: 401);
    var userId = await UsuarioDaSessaoAsync(http);
    if (userId is null) return Results.Json(new { error = "sem sessão: faça o login de novo." }, statusCode: 401);
    http.Items["userId"] = userId;
    if (Escopo.EhMetodoDeEscrita(http.Request.Method))
    {
        var esc = await http.RequestServices.GetRequiredService<Escopo>().DoUsuarioAsync(userId);
        if (esc.SomenteLeitura) return Results.Json(new { error = Escopo.MotivoSomenteLeitura }, statusCode: 403);
    }
    return await next(ctx);
});

static string UserId(HttpContext c) => (string)c.Items["userId"]!;

static string TokenDe(HttpContext http)
{
    var cab = http.Request.Headers.Authorization.ToString();
    return cab.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? cab[7..].Trim() : "";
}

/* O usuário da sessão: o cache só responde o que o GoTrue conferiu há menos de 5 min (e antes do `exp` do JWT);
   fora disso, o GoTrue confere de novo, e só então a sessão volta ao cache. */
static async Task<string?> UsuarioDaSessaoAsync(HttpContext http)
{
    var token = TokenDe(http);
    if (token == "") return null;
    var sessoes = http.RequestServices.GetRequiredService<Sessoes>();
    if (sessoes.UserIdDe(token) is { } guardado) return guardado;
    var userId = await http.RequestServices.GetRequiredService<SupabaseAuth>().UserIdDoTokenAsync(token);
    if (userId is not null) sessoes.Guardar(token, userId, DateTime.UtcNow.Add(Sessoes.Janela));
    return userId;
}

api.MapGet("/carteira", async (HttpContext c, Escopo escopo, Carteira carteira, Meta meta) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var lista = await carteira.ListarAsync(esc);
    /* o token do squad conferido na Meta é o que tira o cliente de Assets: 1 chamada por token, com cache */
    try { await meta.MarcarTokensAsync(lista); } catch (Exception e) { app.Logger.LogWarning("tokens dos squads não conferidos: {Motivo}", e.Message); }
    return Results.Ok(lista);
});

api.MapGet("/clientes/{slug}", async (string slug, HttpContext c, Escopo escopo, Carteira carteira, Meta meta) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    try { await meta.MarcarTokensAsync([cli]); } catch (Exception e) { app.Logger.LogWarning("token do squad não conferido: {Motivo}", e.Message); }
    return Results.Ok(cli);
});

api.MapGet("/clientes/{slug}/estrutura", async (string slug, HttpContext c, Escopo escopo, Carteira carteira, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var p = ParametrosMotor.PadraoDoVault;
    if (cli.BudgetMensal is null or <= 0) return Results.Ok(new { ok = false, falta = "budget", motivo = "sem budget mensal de ads: preencha no card Tráfego do cliente para o Start montar a estrutura." });
    /* o nicho do código ou o do catálogo ativo (trafego_nicho), como o nichoValido do V1 */
    if (!await escritas.NichoValidoAsync(cli.NichoSlug, naDuvida: true)) return Results.Ok(new { ok = false, falta = "nicho", motivo = "sem nicho no card Tráfego: é ele que nomeia os conjuntos e orienta a copy." });
    var e = Estrutura.Montar(cli.BudgetMensal.Value, cli.Conta.TemLista, cli.Conta.TemSiteOuTag, cli.TetoMql, cli.NichoSlug, p, DateOnly.FromDateTime(DateTime.Now));
    return Results.Ok(new { ok = true, estrutura = e, vault_sha = p.VaultSha });
});

api.MapGet("/clientes/{slug}/ongoing", async (string slug, HttpContext c, Escopo escopo, Carteira carteira, Automacao automacao) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    return Results.Ok(await automacao.OngoingAsync(cli));
});

api.MapGet("/clientes/{slug}/formularios", async (string slug, HttpContext c, Escopo escopo, Carteira carteira, Meta meta) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    try { return Results.Ok(await meta.FormulariosAsync(cli)); }
    catch (InvalidOperationException e) { return Results.Json(new { error = e.Message }, statusCode: 422); }
});

api.MapGet("/clientes/{slug}/recomendacoes", async (string slug, HttpContext c, Escopo escopo, Automacao automacao) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    return Results.Ok(new { ok = true, recomendacoes = await automacao.RecomendacoesAsync(slug), pode_aceitar = esc.PodeAplicarNaMeta });
});

/* ---- a decisão do gestor: negar, ou aceitar e APLICAR na Meta pelo executor (trilha B) ---- */
api.MapPost("/clientes/{slug}/recomendacoes/{id}/decisao", async (string slug, string id, DecisaoPedido p, HttpContext c, Escopo escopo, Executor executor) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var (status, corpo) = await executor.DecidirAsync(esc, slug, id, p.Aceita, p.Motivo);
    return Results.Json(corpo, statusCode: status);
});

/* ---- o Publicar do Start: a estrutura vira campanha, formulário, conjuntos e anúncios na Meta, pausados ---- */
api.MapPost("/clientes/{slug}/publicar", async (string slug, PedidoDoStart p, HttpContext c, Escopo escopo, Carteira carteira, Publicador publicador, Registro registro) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    if (!esc.PodeAplicarNaMeta)
    {
        await registro.TrilhaAsync("criou_campanha", Guid.Parse(esc.UserId), slug, "negado", "cargo sem permissão de escrita na Meta", "campanha", null, null);
        return Results.Json(new { error = "publicar na Meta é de head pra cima" }, statusCode: 403);
    }
    var r = await publicador.PublicarAsync(cli, p, Guid.Parse(esc.UserId));
    var status = r.Ok || r.Simulado ? 200 : r.Codigo switch { "meta" => 502, "em_andamento" => 409, _ => 422 };
    return Results.Json(r, statusCode: status);
});

api.MapPost("/clientes/{slug}/trafego", async (string slug, CardTrafegoPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    if (!esc.PodeEditarMql) return Results.Json(new { error = Escopo.MotivoSemMql }, statusCode: 403);
    /* campo ausente não mexe; null limpa (o `undefined` do V1) */
    var (nicho, erroNicho) = Escritas.TextoDoCorpo(p.NichoSlug, "nicho");
    var (budget, erroBudget) = Escritas.ReaisDoCorpo(p.BudgetMensal, "budget mensal");
    if ((erroNicho ?? erroBudget) is { } erro) return Results.Json(new { error = erro }, statusCode: 400);
    var r = await escritas.CardTrafegoAsync(esc, slug, nicho, budget);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

api.MapPost("/clientes/{slug}/mql", async (string slug, MqlPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    if (!esc.PodeEditarMql) return Results.Json(new { error = Escopo.MotivoSemMql }, statusCode: 403);
    var (regua, erroRegua) = Escritas.ReguaDoCorpo(p.Regua);
    var (teto, erroTeto) = Escritas.ReaisDoCorpo(p.TetoMql, "teto por MQL");
    if ((erroRegua ?? erroTeto) is { } erro) return Results.Json(new { error = erro }, statusCode: 400);
    var r = await escritas.CriterioMqlAsync(esc, slug, regua, teto);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

api.MapPatch("/clientes/{slug}/ficha", async (string slug, FichaPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var r = await escritas.FichaAsync(esc, slug, p);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

/* ---- o Agente de Copy dentro da API: gerar o lote e ajustar uma peça (31/10/2026) ---- */
api.MapPost("/clientes/{slug}/copy/gerar", async (string slug, CopyPedido p, HttpContext c, Escopo escopo, Carteira carteira, CopyAgente copy) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var itens = (p.Itens ?? []).Select(i => new ItemDoPedido((i.Tipo ?? "estatico").ToLowerInvariant(), (i.Funil ?? "mofu").ToLowerInvariant(), i.NPecas ?? i.N_Pecas ?? 3)).ToList();
    var (status, corpo) = await copy.GerarAsync(cli, new PedidoDeCopy(itens, p.Briefing, p.Referencia, p.Nome), Guid.Parse(esc.UserId), c.RequestAborted);
    return Results.Json(corpo, statusCode: status);
});

api.MapPost("/clientes/{slug}/pecas/{id}/ajustar", async (string slug, string id, AjustePedido p, HttpContext c, Escopo escopo, Carteira carteira, CopyAgente copy) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var (status, corpo) = await copy.AjustarAsync(cli, id, p.Instrucao ?? "", p.Campos, Guid.Parse(esc.UserId), c.RequestAborted);
    return Results.Json(corpo, statusCode: status);
});

/* ---- o setup do cliente e o motor, dentro da API (31/10/2026): o que sobrava no V1 ---- */
api.MapPost("/clientes", async (NovoClientePedido p, HttpContext c, Escopo escopo, Setup setup) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    /* podeCriarCliente do V1 (cargos-admin.ts): tier <= 2, coordenador e acima; head não cria */
    if (!BlueOS.Core.Cs.CsRegras.PodeCriarCliente(esc.Papeis))
        return Results.Json(new { error = "Cadastrar cliente na carteira é só de coordenador e acima. Peça a um coordenador para criar o cliente e te vincular ao squad." }, statusCode: 403);
    var (status, corpo) = await setup.CriarClienteAsync(p.Nome ?? "", p.Squad, Guid.Parse(esc.UserId));
    return Results.Json(corpo, statusCode: status);
});

api.MapPost("/clientes/{slug}/assets", async (string slug, AssetPedido p, HttpContext c, Escopo escopo, Carteira carteira, Setup setup) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    byte[] bytes;
    try { bytes = Convert.FromBase64String(p.Base64 ?? ""); } catch { return Results.Json(new { error = "o arquivo não veio em base64 válido." }, statusCode: 400); }
    var (status, corpo) = await setup.SubirAssetAsync(cli, (p.Tipo ?? "tela").Trim().ToLowerInvariant(), p.Nome ?? "arquivo", (p.Mime ?? "application/octet-stream").Trim().ToLowerInvariant(), bytes, Guid.Parse(esc.UserId));
    return Results.Json(corpo, statusCode: status);
});

api.MapGet("/clientes/{slug}/assets/urls", async (string slug, HttpContext c, Escopo escopo, Carteira carteira, Setup setup) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    return Results.Ok(await setup.UrlsDosAssetsAsync(cli));
});

api.MapDelete("/clientes/{slug}/assets/{id}", async (string slug, string id, HttpContext c, Escopo escopo, Carteira carteira, Setup setup) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var (status, corpo) = await setup.RemoverAssetAsync(cli, id, Guid.Parse(esc.UserId));
    return Results.Json(corpo, statusCode: status);
});

api.MapPatch("/clientes/{slug}/conta", async (string slug, ContaPedido p, HttpContext c, Escopo escopo, Carteira carteira, Setup setup) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var (status, corpo) = await setup.ConectarContaAsync(cli, p.AdAccountId, p.PageId, p.PageNome, p.Desconectar ?? false, Guid.Parse(esc.UserId));
    return Results.Json(corpo, statusCode: status);
});

api.MapGet("/squads/{nome}/token", async (string nome, HttpContext c, Escopo escopo, Setup setup) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var squad = await setup.SquadAsync(nome);
    if (squad is null) return Results.Json(new { error = $"squad {nome} não existe" }, statusCode: 404);
    if (!esc.Global && (esc.SquadIds is null || !esc.SquadIds.Contains(squad.Value.Id ?? ""))) return Results.Json(new { error = $"o squad {nome} não está no seu escopo." }, statusCode: 403);
    return Results.Ok(await setup.ConferirTokenAsync(squad.Value.Nome));
});

api.MapPost("/squads/{nome}/token", async (string nome, TokenPedido p, HttpContext c, Escopo escopo, Setup setup) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var squad = await setup.SquadAsync(nome);
    if (squad is null) return Results.Json(new { error = $"squad {nome} não existe" }, statusCode: 404);
    if (!esc.Global && (esc.SquadIds is null || !esc.SquadIds.Contains(squad.Value.Id ?? ""))) return Results.Json(new { error = $"o squad {nome} não está no seu escopo." }, statusCode: 403);
    var (status, corpo) = await setup.GravarTokenAsync(squad.Value.Nome, p.Token ?? "", Guid.Parse(esc.UserId), squad.Value.Id);
    return Results.Json(corpo, statusCode: status);
});

api.MapPost("/clientes/{slug}/motor/rodar", async (string slug, HttpContext c, Escopo escopo, Carteira carteira, MotorRodada motor) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var (status, corpo) = await motor.RodarAsync(cli, Guid.Parse(esc.UserId));
    return Results.Json(corpo, statusCode: status);
});

/* ---- o Agente Designer dentro da API: as artes das peças escolhidas, uma chamada por peça (31/10/2026) ---- */
api.MapPost("/clientes/{slug}/artes/gerar", async (string slug, ArtePedido p, HttpContext c, Escopo escopo, Carteira carteira, Designer designer) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var (status, corpo) = await designer.GerarAsync(cli, new PedidoDeArte(p.PecaIds ?? [], p.Formatos ?? [], p.Template, p.DesignerLoteId, p.Encerrar ?? false, p.Ajuste), Guid.Parse(esc.UserId), c.RequestAborted);
    return Results.Json(corpo, statusCode: status);
});

api.MapPatch("/clientes/{slug}/pecas/{id}", async (string slug, string id, PecaPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var r = await escritas.AprovarPecaAsync(slug, id, p.Aprovada);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

api.MapPatch("/clientes/{slug}/modo", async (string slug, ModoPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    if (!esc.PodeAplicarNaMeta) return Results.Json(new { error = "mudar o modo do motor ou a chave geral é de head pra cima." }, statusCode: 403);
    var r = await escritas.ModoAsync(esc, slug, p.Modo);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

api.MapGet("/motor/chave-geral", async (Escritas escritas) =>
{
    var (ligada, motivo, por, mudadoEm) = await escritas.ChaveGeralAsync();
    return Results.Ok(new { ligada, motivo, por, mudadoEm });
});
api.MapPatch("/motor/chave-geral", async (ChaveGeralPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!esc.PodeAplicarNaMeta) return Results.Json(new { error = "mudar o modo do motor ou a chave geral é de head pra cima." }, statusCode: 403);
    var r = await escritas.MudarChaveGeralAsync(esc, p.Ligada, p.Motivo);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

/* ============================================================================
   O AGENTE DE CS (30/10/2026): a base das telas dos coordenadores. Só endpoints.
   Porte de app/api/os/me e app/api/cs/* do V1 — ver Dominio/Cs.cs.
   ============================================================================ */
static IResult RespostaCs((object? Corpo, Cs.Erro? Erro) r) =>
    r.Erro is null ? Results.Ok(r.Corpo) : Results.Json(r.Corpo ?? new { error = r.Erro.Mensagem }, statusCode: r.Erro.Http);


/* ---- o Panorama: o mapa estelar do squad numa leitura ------------------------------ */
api.MapGet("/panorama", async (string? squad, HttpContext c, Escopo escopo, PanoramaApi panorama) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    return Results.Ok(await panorama.LerAsync(esc, UserId(c), squad));
});
api.MapPost("/panorama/visto", async (PanoramaApi.VistoPedido p, HttpContext c, Escopo escopo, PanoramaApi panorama) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, p.ClienteSlug)) return Results.NotFound(new { error = "cliente fora do seu alcance" });
    await panorama.MarcarVistoAsync(UserId(c), p);
    return Results.Ok(new { ok = true });
});
api.MapPost("/panorama/a-caminho", async (PanoramaApi.ACaminhoPedido p, HttpContext c, Escopo escopo, PanoramaApi panorama) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, p.ClienteSlug)) return Results.NotFound(new { error = "cliente fora do seu alcance" });
    await panorama.RegistrarACaminhoAsync(UserId(c), p);
    return Results.Ok(new { ok = true });
});

api.MapGet("/me", async (HttpContext c, Cs cs) =>
    await cs.MeAsync(UserId(c)) is { } me ? Results.Ok(me) : Results.Json(new { error = "usuário não encontrado" }, statusCode: 404));

/* a carteira (lista) ou, com ?cliente=, o dossiê completo daquele cliente no squad pedido/principal */
api.MapGet("/cs/carteira", async (string? cliente, string? squad, string? grupo, HttpContext c, Escopo escopo, Cs cs) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!string.IsNullOrWhiteSpace(cliente)) return RespostaCs(await cs.DossieAsync(esc, cliente, squad));
    return Results.Ok(await cs.CarteiraAsync(esc, squad, grupo));
});

api.MapGet("/cs/risco", async (string? todos, HttpContext c, Escopo escopo, Cs cs) =>
    Results.Ok(await cs.RiscoAsync(await escopo.DoUsuarioAsync(UserId(c)), todos == "1")));

api.MapGet("/cs/anotacao", async (string? cliente_id, HttpContext c, Escopo escopo, Cs cs) =>
    RespostaCs(await cs.AnotacoesAsync(await escopo.DoUsuarioAsync(UserId(c)), cliente_id)));
api.MapPost("/cs/anotacao", async (AnotacaoPedido p, HttpContext c, Escopo escopo, Cs cs) =>
    RespostaCs(await cs.AnotarAsync(await escopo.DoUsuarioAsync(UserId(c)), p.ClienteId, p.Texto)));
api.MapPatch("/cs/anotacao", async (AnotacaoPedido p, HttpContext c, Escopo escopo, Cs cs) =>
    RespostaCs(await cs.ArquivarAnotacaoAsync(await escopo.DoUsuarioAsync(UserId(c)), p.AnotacaoId)));

api.MapGet("/cs/upsell-vendido", async (string? cliente_id, HttpContext c, Escopo escopo, Cs cs) =>
    RespostaCs(await cs.UpsellsVendidosAsync(await escopo.DoUsuarioAsync(UserId(c)), cliente_id)));
api.MapPost("/cs/upsell-vendido", async (UpsellPedido p, HttpContext c, Escopo escopo, Cs cs) =>
    RespostaCs(await cs.RegistrarUpsellAsync(await escopo.DoUsuarioAsync(UserId(c)), p.ClienteId, p.UpsellSlug, p.UpsellNome, p.Valor, p.VendidoEm, p.Observacao)));
api.MapPatch("/cs/upsell-vendido", async (UpsellPedido p, HttpContext c, Escopo escopo, Cs cs) =>
    RespostaCs(await cs.ArquivarUpsellAsync(await escopo.DoUsuarioAsync(UserId(c)), p.VendaId)));

api.MapPatch("/cs/passo-decisao", async (DecisaoHumanaPedido p, HttpContext c, Escopo escopo, Cs cs) =>
    RespostaCs(await cs.DecidirPassoAsync(await escopo.DoUsuarioAsync(UserId(c)), p.AnaliseId, p.Indice, p.Status, p.Justificativa)));
api.MapPatch("/cs/promessa-status", async (DecisaoHumanaPedido p, HttpContext c, Escopo escopo, Cs cs) =>
    RespostaCs(await cs.DecidirPromessaAsync(await escopo.DoUsuarioAsync(UserId(c)), p.AnaliseId, p.Indice, p.Status, p.Justificativa)));

api.MapPost("/cs/sem-performance", async (SemPerformancePedido p, HttpContext c, Escopo escopo, Cs cs) =>
    RespostaCs(await cs.SemPerformanceAsync(await escopo.DoUsuarioAsync(UserId(c)), p.ClienteId, p.SquadId, p.Ligado)));

api.MapGet("/cs/membros", async (string? cliente_id, HttpContext c, Escopo escopo, Cs cs) =>
    RespostaCs(await cs.MembrosAsync(await escopo.DoUsuarioAsync(UserId(c)), cliente_id)));
api.MapPost("/cs/passo", async (PassoPedido p, HttpContext c, Escopo escopo, Cs cs, CancellationToken ct) =>
    RespostaCs(await cs.PassoParaTaskAsync(await escopo.DoUsuarioAsync(UserId(c)), p, ct)));

app.Run();

public sealed record LoginPedido(string Login, string Senha);
/* JsonElement e não string?/decimal?: o campo AUSENTE (Undefined) não mexe, o null limpa, como o `undefined` do V1 */
public sealed record CardTrafegoPedido(System.Text.Json.JsonElement NichoSlug, System.Text.Json.JsonElement BudgetMensal);
public sealed record MqlPedido(System.Text.Json.JsonElement Regua, System.Text.Json.JsonElement TetoMql);
public sealed record ModoPedido(ModoDeExecucao Modo);
public sealed record ChaveGeralPedido(bool Ligada, string? Motivo);
public sealed record DecisaoPedido(bool Aceita, string? Motivo);
public sealed record PecaPedido(bool Aprovada);
public sealed record ItemDeCopyPedido(string? Tipo, string? Funil, int? NPecas, int? N_Pecas);
public sealed record CopyPedido(List<ItemDeCopyPedido>? Itens, string? Briefing, string? Referencia, string? Nome);
public sealed record AjustePedido(string? Instrucao, List<string>? Campos);
public sealed record NovoClientePedido(string? Nome, string? Squad);
public sealed record AssetPedido(string? Tipo, string? Nome, string? Mime, string? Base64);
public sealed record ContaPedido(string? AdAccountId, string? PageId, string? PageNome, bool? Desconectar);
public sealed record TokenPedido(string? Token);
public sealed record ArtePedido(List<string>? PecaIds, List<string>? Formatos, string? Template, string? DesignerLoteId, bool? Encerrar, string? Ajuste);
public sealed record FichaPedido(FichaCadastral? Ficha, Icp? Icp, Produto? Produto, Estrategia? Estrategia);
public sealed record AnotacaoPedido([property: JsonPropertyName("cliente_id")] string? ClienteId, string? Texto, [property: JsonPropertyName("anotacao_id")] string? AnotacaoId);
public sealed record UpsellPedido([property: JsonPropertyName("cliente_id")] string? ClienteId, [property: JsonPropertyName("upsell_slug")] string? UpsellSlug, [property: JsonPropertyName("upsell_nome")] string? UpsellNome,
    System.Text.Json.JsonElement? Valor, [property: JsonPropertyName("vendido_em")] string? VendidoEm, string? Observacao, [property: JsonPropertyName("venda_id")] string? VendaId);
public sealed record DecisaoHumanaPedido([property: JsonPropertyName("analise_id")] string? AnaliseId, int? Indice, string? Status, string? Justificativa);
public sealed record SemPerformancePedido([property: JsonPropertyName("cliente_id")] string? ClienteId, [property: JsonPropertyName("squad_id")] string? SquadId, bool? Ligado);
