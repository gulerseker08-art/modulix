using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Modulix.Web;
using Modulix.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Arayüz API ile aynı adresten sunulur; oturum çerezi tarayıcı tarafından eklenir.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<SessionState>();
builder.Services.AddScoped<LookupCatalog>();
builder.Services.AddScoped<FormLayouts>();
builder.Services.AddScoped<AppNavigation>();
builder.Services.AddTransient<CrmLookups>();

await builder.Build().RunAsync();
