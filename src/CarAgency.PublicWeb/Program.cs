using CarAgency.Web.Components;
var builder=WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient("catalog", c=>c.BaseAddress=new Uri(builder.Configuration["ApiBaseUrl"]??"http://localhost:5080"));
var app=builder.Build();app.UseStaticFiles();app.UseAntiforgery();app.MapRazorComponents<App>().AddInteractiveServerRenderMode();app.Run();
