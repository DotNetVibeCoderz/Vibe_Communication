using Microsoft.AspNetCore.StaticFiles;
using VoipNet.CallCenter.Components;
using VoipNet.CallCenter.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<ContactCentre>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ContactCentre>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();

// Recorded calls are played straight from disk by the browser's audio element.
app.MapGet("/recordings/{**file}", (string file, ContactCentre centre) =>
{
    var root = Path.GetFullPath(centre.RecordingsFolder);
    var path = Path.GetFullPath(Path.Combine(root, file));
    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
    {
        return Results.NotFound();
    }

    var type = new FileExtensionContentTypeProvider().TryGetContentType(path, out var contentType) ? contentType : "application/octet-stream";
    return Results.File(path, type, enableRangeProcessing: true);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
