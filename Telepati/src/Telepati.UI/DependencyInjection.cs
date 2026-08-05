using Microsoft.Extensions.DependencyInjection;
using Telepati.UI.Services;

namespace Telepati.UI;

public static class DependencyInjection
{
    /// <summary>
    /// Shared UI services. Call after <c>AddTelepatiClient</c> — <see cref="ChatState"/> and
    /// <see cref="ThemeState"/> both bind to whichever transport was selected.
    /// </summary>
    public static IServiceCollection AddTelepatiUI(this IServiceCollection services)
    {
        services.AddSingleton<MarkdownRenderer>();
        services.AddScoped<MediaResolver>();
        services.AddScoped<ThemeState>();
        services.AddScoped<ChatState>();
        return services;
    }

    /// <summary>
    /// Adds IndexedDB persistence for the web app. Desktop and mobile call
    /// <c>AddTelepatiLocalStore</c> instead — they have a real database file and do not need to
    /// cross a JS interop boundary to reach it.
    /// </summary>
    public static IServiceCollection AddTelepatiBrowserStore(this IServiceCollection services)
    {
        services.AddScoped<BrowserStore>();
        services.AddScoped<Telepati.Client.Core.ILocalCacheInfo>(sp => sp.GetRequiredService<BrowserStore>());
        return services;
    }
}
