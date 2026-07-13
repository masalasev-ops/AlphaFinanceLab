using AlphaLab.Core.Arenas;
using AlphaLab.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The client's only config is the non-secret Arenas registry (FR-37 / D71), loaded from
// wwwroot/appsettings.json. It never loads secrets. The active arena defaults to the first entry;
// its baseUrl is the API address (there is no bare Api:BaseUrl key).
var arenas = builder.Configuration.GetSection("Arenas").Get<ArenaEntry[]>();
var registry = ArenaRegistry.FromEntries(arenas, builder.HostEnvironment.BaseAddress);

builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(registry.Active);

// Cross-origin HTTP client pointed at the active arena's API (CORS is allowed by the API).
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(registry.Active.BaseUrl) });
builder.Services.AddScoped<ReadModelClient>();

await builder.Build().RunAsync();
