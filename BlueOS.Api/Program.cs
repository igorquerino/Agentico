using System.Text.Json.Serialization;
using BlueOS.Api.Dominio;
using BlueOS.Api.Infra;
using BlueOS.Core.Modelos;
using BlueOS.Core.Motor;

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
builder.Services.AddSingleton<Db>();
builder.Services.AddHttpClient<SupabaseAuth>();
builder.Services.AddSingleton<Sessoes>();
builder.Services.AddScoped<Escopo>();
builder.Services.AddScoped<Carteira>();
builder.Services.AddScoped<Automacao>();
builder.Services.AddScoped<Escritas>();
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
    await ctx.Response.WriteAsJsonAsync(new { error = msg });
}));

/* ---- saúde: a API sobe mesmo sem banco configurado; o /health diz o que falta ---- */
app.MapGet("/api/health", async (Db db, IConfiguration cfg) =>
{
    var banco = await db.ConfereAsync();
    return Results.Ok(new
    {
        ok = banco.Ok,
        banco = banco.Ok ? "conectado" : banco.Erro,
        supabase = string.IsNullOrWhiteSpace(cfg["Supabase:Url"]) ? "falta Supabase:Url" : cfg["Supabase:Url"],
        anon_key = string.IsNullOrWhiteSpace(cfg["Supabase:AnonKey"]) ? "falta Supabase:AnonKey" : "ok",
    });
});

/* ---- login: usuário ou e-mail + senha, como o /api/os/login do V1 ---------------- */
app.MapPost("/api/login", async (LoginPedido p, Db db, SupabaseAuth auth, Sessoes sessoes, Escopo escopo) =>
{
    if (string.IsNullOrWhiteSpace(p.Login) || string.IsNullOrWhiteSpace(p.Senha)) return Results.BadRequest(new { error = "Informe usuario e senha." });
    var termo = p.Login.Trim().ToLowerInvariant();
    var u = await db.UmAsync("""
        select u.user_id::text, u.user_nome, u.user_username, u.user_ativo, au.email
        from public.users u join auth.users au on au.id = u.user_id
        where lower(u.user_username) = @t or lower(u.user_email) = @t
        limit 1
        """, r => new { Id = r.GetString(0), Nome = r.GetString(1), Username = r.GetString(2), Ativo = r.GetBoolean(3), Email = r.GetString(4) }, ("t", termo));
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

/* ---- a partir daqui, tudo exige a sessão ------------------------------------------ */
var api = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
{
    var sessoes = ctx.HttpContext.RequestServices.GetRequiredService<Sessoes>();
    var auth = ctx.HttpContext.RequestServices.GetRequiredService<SupabaseAuth>();
    var cab = ctx.HttpContext.Request.Headers.Authorization.ToString();
    var token = cab.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? cab[7..].Trim() : "";
    if (token == "") return Results.Json(new { error = "sem sessão" }, statusCode: 401);
    var userId = sessoes.UserIdDe(token) ?? await auth.UserIdDoTokenAsync(token);
    if (userId is null) return Results.Json(new { error = "sem sessão: faça o login de novo." }, statusCode: 401);
    sessoes.Guardar(token, userId, DateTime.UtcNow.AddMinutes(5));
    ctx.HttpContext.Items["userId"] = userId;
    return await next(ctx);
});

static string UserId(HttpContext c) => (string)c.Items["userId"]!;

api.MapGet("/carteira", async (HttpContext c, Escopo escopo, Carteira carteira) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    return Results.Ok(await carteira.ListarAsync(esc));
});

api.MapGet("/clientes/{slug}", async (string slug, HttpContext c, Escopo escopo, Carteira carteira) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    return cli is null ? Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403) : Results.Ok(cli);
});

api.MapGet("/clientes/{slug}/estrutura", async (string slug, HttpContext c, Escopo escopo, Carteira carteira) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    var cli = await carteira.ObterAsync(esc, slug);
    if (cli is null) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var p = ParametrosMotor.PadraoDoVault;
    if (cli.BudgetMensal is null or <= 0) return Results.Ok(new { ok = false, falta = "budget", motivo = "sem budget mensal de ads: preencha no card Tráfego do cliente para o Start montar a estrutura." });
    if (!BlueOS.Core.Setup.Nichos.Valido(cli.NichoSlug)) return Results.Ok(new { ok = false, falta = "nicho", motivo = "sem nicho no card Tráfego: é ele que nomeia os conjuntos e orienta a copy." });
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

api.MapGet("/clientes/{slug}/recomendacoes", async (string slug, HttpContext c, Escopo escopo, Automacao automacao) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    return Results.Ok(new { ok = true, recomendacoes = await automacao.RecomendacoesAsync(slug), pode_aceitar = esc.PodeAplicarNaMeta });
});

api.MapPost("/clientes/{slug}/recomendacoes/{id}/decisao", async (string slug, string id, DecisaoPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var r = await escritas.DecidirAsync(esc, slug, id, p.Aceita, p.Motivo);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

api.MapPost("/clientes/{slug}/trafego", async (string slug, CardTrafegoPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    if (!esc.PodeEditarMql) return Results.Json(new { error = Escopo.MotivoSemMql }, statusCode: 403);
    var r = await escritas.CardTrafegoAsync(esc, slug, p.NichoSlug, p.BudgetMensal);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

api.MapPost("/clientes/{slug}/mql", async (string slug, MqlPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    if (!esc.PodeEditarMql) return Results.Json(new { error = Escopo.MotivoSemMql }, statusCode: 403);
    var r = await escritas.CriterioMqlAsync(esc, slug, p.Regua, p.TetoMql);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

api.MapPatch("/clientes/{slug}/ficha", async (string slug, FichaPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    var r = await escritas.FichaAsync(esc, slug, p);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

api.MapPatch("/clientes/{slug}/modo", async (string slug, ModoPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!await escopo.AlcancaAsync(esc, slug)) return Results.Json(new { error = "cliente fora do seu escopo" }, statusCode: 403);
    if (!esc.PodeAplicarNaMeta) return Results.Json(new { error = "mudar o modo do motor ou a chave geral é de head pra cima." }, statusCode: 403);
    var r = await escritas.ModoAsync(slug, p.Modo);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

api.MapGet("/motor/chave-geral", async (Escritas escritas) => Results.Ok(await escritas.ChaveGeralAsync()));
api.MapPatch("/motor/chave-geral", async (ChaveGeralPedido p, HttpContext c, Escopo escopo, Escritas escritas) =>
{
    var esc = await escopo.DoUsuarioAsync(UserId(c));
    if (!esc.PodeAplicarNaMeta) return Results.Json(new { error = "mudar o modo do motor ou a chave geral é de head pra cima." }, statusCode: 403);
    var r = await escritas.MudarChaveGeralAsync(esc, p.Ligada, p.Motivo);
    return r.Ok ? Results.Ok(new { ok = true }) : Results.Json(new { error = r.Erro }, statusCode: r.Status);
});

app.Run();

public sealed record LoginPedido(string Login, string Senha);
public sealed record CardTrafegoPedido(string? NichoSlug, decimal? BudgetMensal);
public sealed record MqlPedido(ReguaV2? Regua, decimal? TetoMql);
public sealed record ModoPedido(ModoDeExecucao Modo);
public sealed record ChaveGeralPedido(bool Ligada, string? Motivo);
public sealed record DecisaoPedido(bool Aceita, string? Motivo);
public sealed record FichaPedido(FichaCadastral? Ficha, Icp? Icp, Produto? Produto, Estrategia? Estrategia);
