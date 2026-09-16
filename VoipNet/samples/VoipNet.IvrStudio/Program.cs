using VoipNet.IvrStudio.Components;
using VoipNet.IvrStudio.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<FlowStore>();

// One test line per browser circuit, so each tab dials its own IVR.
builder.Services.AddScoped<TestLine>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/flow.json", (FlowStore store) => Results.Text(store.Load().ToJson(), "application/json"));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
