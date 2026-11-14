using Microsoft.Extensions.Logging;
namespace CarAgency.Maui;
public static class MauiProgram { public static MauiApp CreateMauiApp(){var b=MauiApp.CreateBuilder();b.UseMauiApp<App>();b.Services.AddMauiBlazorWebView();return b.Build();}}
