namespace CarAgency.Maui;
public partial class App : Application
{
    public App() { InitializeComponent(); MainPage = new MainPage(); }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);
#if WINDOWS
        window.Created += (_, _) =>
        {
            if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                var presenter = nativeWindow.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
                presenter?.Maximize();
            }
        };
#endif
        return window;
    }
}
