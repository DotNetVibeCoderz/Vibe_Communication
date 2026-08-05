using Microsoft.Extensions.DependencyInjection;
using Telepati.Bot.Plugins;

namespace Telepati.Bot;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Kang Bacot. Call after <c>AddTelepatiInfrastructure</c> — the plugins depend on
    /// storage and the options singletons it registers.
    /// </summary>
    public static IServiceCollection AddTelepatiBot(this IServiceCollection services)
    {
        services.AddHttpClient();

        services.AddSingleton<Workspace>();
        services.AddSingleton<TimePlugin>();
        services.AddSingleton<MathPlugin>();
        services.AddScoped<WebPlugin>();
        services.AddScoped<FilePlugin>();
        services.AddScoped<ScriptPlugin>();
        services.AddScoped<SkillsPlugin>();

        // One provider for the whole process: an MCP stdio server is a child process, and
        // starting one per conversation turn would leak them.
        services.AddSingleton<IMcpToolProvider, McpToolProvider>();

        services.AddScoped<IKernelFactory, KernelFactory>();
        services.AddScoped<IBotService, BotService>();

        return services;
    }
}
