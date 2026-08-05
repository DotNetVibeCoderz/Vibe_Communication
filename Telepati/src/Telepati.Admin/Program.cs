using Microsoft.AspNetCore.Authentication.Cookies;
using Telepati.Admin;
using Telepati.Bot;
using Telepati.Admin.Components;
using Telepati.Client.Core;
using Telepati.Infrastructure;
using Telepati.Shared.Configuration;
using Telepati.UI;

var builder = WebApplication.CreateBuilder(args);

// Static web assets (the _framework scripts and every RCL's _content folder) are only wired up
// automatically in Development. Without this, running the app in any other environment serves
// 500s for blazor.web.js and the app silently never becomes interactive. A published app has
// the files copied into wwwroot, where this call is a harmless no-op.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// The console reads and writes the database directly rather than going through the
// messaging API — it needs administrative reach the client transports do not expose.
builder.Services.AddTelepatiInfrastructure(builder.Configuration);

// Only for ThemeState, which reads the active theme through the same client the
// messenger uses, so admin and messenger always agree on what is active.
builder.Services.AddTelepatiClient(builder.Configuration);
builder.Services.AddTelepatiUI();

// The MCP gallery dials servers to test them, which is the bot's connection code — so the
// console registers the bot services too rather than duplicating the transport logic.
builder.Services.AddTelepatiBot();

// The signed-in admin lives in a cookie, not in circuit state. Holding it in a scoped service
// meant every full-page navigation and every refresh started a new circuit and logged the
// admin straight back out.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "telepati.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/login";
        options.LogoutPath = AuthEndpoints.SignOutPath;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddScoped<AdminSession>();

var app = builder.Build();

// Sharing one database with the server means both may try to create it; the operation
// is idempotent, so whichever starts first wins and the other is a no-op.
await app.Services.InitializeTelepatiDatabaseAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.MapStaticAssets();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapAdminAuthEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
