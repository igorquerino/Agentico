using BlueOS.Web.Components;
using BlueOS.Shared.Services;
using BlueOS.Web.Services;
using BlueOS.Core.Api;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add device-specific services used by the BlueOS.Shared project
builder.Services.AddSingleton<IFormFactor, FormFactor>();

/* A mesma fonte de dados do desktop. No piloto web é um singleton por processo: a
   versão web de verdade (Blazor Interactive Server contra a API .NET) troca isto por
   um serviço por circuito, com a sessão de cada pessoa. */
builder.Services.AddSingleton<FonteDeDados>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(
        typeof(BlueOS.Shared._Imports).Assembly);

app.Run();
