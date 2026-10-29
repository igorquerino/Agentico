using Microsoft.Extensions.Logging;
using BlueOS.Shared.Services;
using BlueOS.Services;
using BlueOS.Core.Api;

namespace BlueOS;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        // Add device-specific services used by the BlueOS.Shared project
        builder.Services.AddSingleton<IFormFactor, FormFactor>();

        /* A fonte de dados da tela: demonstração em memória ou o V1 pela sessão da pessoa.
           Singleton: a troca de fonte vale para a janela inteira. O desktop nunca guarda
           chave do banco nem a service role (plano de migração, "Riscos e cuidados"). */
        builder.Services.AddSingleton<FonteDeDados>();

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
