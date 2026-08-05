using Telepati.Client.Core;
using Telepati.Shared.Configuration;
using Telepati.UI;
using Telepati.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Static web assets (the _framework scripts and every RCL's _content folder) are only wired up
// automatically in Development. Without this, running the app in any other environment serves
// 500s for blazor.web.js and the app silently never becomes interactive. A published app has
// the files copied into wwwroot, where this call is a harmless no-op.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// The messenger owns no database — it talks to the server through the transport the
// user picked, exactly like the desktop and mobile apps do.
builder.Services.AddTelepatiClient(builder.Configuration);
builder.Services.AddTelepatiUI();

// Chat threads are persisted in IndexedDB so a page reload paints instantly.
builder.Services.AddTelepatiBrowserStore();

// Branding and feature flags are read from the same section the server uses, so a
// single appsettings edit keeps every surface consistent.
builder.Services.AddSingleton(
    builder.Configuration.GetSection(TelepatiOptions.SectionName).Get<TelepatiOptions>() ?? new TelepatiOptions());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Serves wwwroot, the _framework scripts and every RCL's _content assets. UseStaticFiles
// alone leaves the last two 404ing outside Development, which silently breaks interactivity.
app.MapStaticAssets();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    // Routable pages live in Telepati.UI. The Router's AdditionalAssemblies only covers
    // client-side navigation; endpoint discovery needs this as well, or every URL 404s.
    .AddAdditionalAssemblies(typeof(Telepati.UI.Pages.Messenger).Assembly);

app.Run();
