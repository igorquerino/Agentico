namespace BlueOS;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage())
        {
            Title = "Blue OS · Gestor de Tráfego",
            Width = 1440,
            Height = 900,
            MinimumWidth = 1024,
            MinimumHeight = 640,
        };
    }
}
