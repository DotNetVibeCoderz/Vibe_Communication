using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using Rumble.Net;
using Rumble.Net.Testing;

namespace RumbleGallery.Infrastructure;

/// <summary>Marks a static class with <c>public static Task RunAsync(SampleContext)</c> as a gallery sample.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SampleAttribute(string category, string title, string summary) : Attribute
{
    public string Category { get; } = category;
    public string Title { get; } = title;
    public string Summary { get; } = summary;

    /// <summary>Longer explanation shown above the code.</summary>
    public string? Description { get; init; }

    /// <summary>Sort order within the gallery.</summary>
    public int Order { get; init; }
}

/// <summary>A discovered sample.</summary>
public sealed class GallerySample
{
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Description { get; init; }
    public required string FileName { get; init; }
    public required string Code { get; init; }
    public required int Order { get; init; }
    public required Func<SampleContext, Task> RunAsync { get; init; }

    public string CategoryUpper => Category.ToUpperInvariant();
}

public enum OutputKind
{
    Info,
    Success,
    Warning,
    Error,
    Muted,
}

/// <summary>A line in the output console.</summary>
public sealed record OutputLine(DateTime Time, string Text, OutputKind Kind)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");

    public IBrush Foreground => Kind switch
    {
        OutputKind.Success => new SolidColorBrush(Color.Parse("#7FC7A1")),
        OutputKind.Warning => new SolidColorBrush(Color.Parse("#F0B233")),
        OutputKind.Error => new SolidColorBrush(Color.Parse("#E88A76")),
        OutputKind.Muted => new SolidColorBrush(Color.Parse("#7D8B8F")),
        _ => new SolidColorBrush(Color.Parse("#E6EAE3")),
    };
}

/// <summary>Everything a sample needs: the demo server, logging and cancellation.</summary>
public sealed class SampleContext(MockMumbleServer server, Action<string, OutputKind> write, CancellationToken token)
{
    /// <summary>The Mumble server built into the gallery (with EchoBot in the Lobby).</summary>
    public MockMumbleServer Server { get; } = server;

    /// <summary>Cancelled when the user presses Stop.</summary>
    public CancellationToken Token { get; } = token;

    /// <summary>Creates client options for the gallery server with fast reconnects.</summary>
    public RumbleClientOptions CreateOptions(string username)
    {
        var options = Server.CreateClientOptions(username);
        options.ReconnectMinDelay = TimeSpan.FromMilliseconds(250);
        options.ReconnectMaxDelay = TimeSpan.FromSeconds(2);
        options.PingInterval = TimeSpan.FromSeconds(1);
        options.PingTimeout = TimeSpan.FromSeconds(10);
        return options;
    }

    public void Log(string text) => write(text, OutputKind.Info);

    public void Success(string text) => write(text, OutputKind.Success);

    public void Warn(string text) => write(text, OutputKind.Warning);

    public void Note(string text) => write(text, OutputKind.Muted);

    /// <summary>A logger factory that writes into the gallery console.</summary>
    public ILoggerFactory CreateLoggerFactory(LogLevel minimum = LogLevel.Information) =>
        LoggerFactory.Create(b => b.SetMinimumLevel(minimum).AddProvider(new ConsoleProvider(write)));

    private sealed class ConsoleProvider(Action<string, OutputKind> write) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName, write);

        public void Dispose()
        {
        }
    }

    private sealed class ConsoleLogger(string category, Action<string, OutputKind> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var kind = logLevel switch
            {
                >= LogLevel.Error => OutputKind.Error,
                LogLevel.Warning => OutputKind.Warning,
                <= LogLevel.Debug => OutputKind.Muted,
                _ => OutputKind.Info,
            };
            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            write($"[{logLevel.ToString()[..4].ToLowerInvariant()}] {shortCategory}: {formatter(state, exception)}", kind);
        }
    }
}

/// <summary>Discovers samples by reflection and loads their embedded source.</summary>
public static class SampleCatalog
{
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Sample types are part of this assembly.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Sample types are part of this assembly.")]
    public static IReadOnlyList<GallerySample> Discover()
    {
        var assembly = typeof(SampleCatalog).Assembly;
        var samples = new List<GallerySample>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<SampleAttribute>() is not { } attr)
            {
                continue;
            }

            var method = type.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static, [typeof(SampleContext)])
                         ?? throw new InvalidOperationException($"{type.Name} must declare public static Task RunAsync(SampleContext).");
            var (fileName, code) = LoadSource(assembly, type.Name);
            samples.Add(new GallerySample
            {
                Category = attr.Category,
                Title = attr.Title,
                Summary = attr.Summary,
                Description = attr.Description ?? attr.Summary,
                FileName = fileName,
                Code = code,
                Order = attr.Order,
                RunAsync = method.CreateDelegate<Func<SampleContext, Task>>(),
            });
        }

        return [.. samples.OrderBy(s => s.Order).ThenBy(s => s.Title)];
    }

    /// <summary>Finds the embedded file that declares <paramref name="typeName"/> (a file may hold several samples).</summary>
    private static (string FileName, string Code) LoadSource(Assembly assembly, string typeName)
    {
        var candidates = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Samples.", StringComparison.Ordinal) && n.EndsWith(".cs", StringComparison.Ordinal))
            .OrderByDescending(n => n == $"Samples.{typeName}.cs");
        foreach (var name in candidates)
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var code = reader.ReadToEnd();
            if (code.Contains($"class {typeName}", StringComparison.Ordinal))
            {
                return (name["Samples.".Length..], code);
            }
        }

        return ($"{typeName}.cs", $"// Source for {typeName} is not embedded.");
    }
}
