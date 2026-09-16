using Spectre.Console;
using Spectre.Console.Rendering;

namespace VoipNet.Cli;

/// <summary>
/// Live-updating output on an interactive terminal; when output is redirected (CI, log files)
/// the final view is printed once instead, because live rendering needs a real console.
/// </summary>
internal static class ConsoleLive
{
    public static async Task RunAsync(IRenderable initial, Func<Action<IRenderable>, Task> work)
    {
        if (Console.IsOutputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
        {
            IRenderable last = initial;
            await work(view => last = view);
            AnsiConsole.Write(last);
            return;
        }

        await AnsiConsole.Live(initial).StartAsync(context => work(view => context.UpdateTarget(view)));
    }
}
