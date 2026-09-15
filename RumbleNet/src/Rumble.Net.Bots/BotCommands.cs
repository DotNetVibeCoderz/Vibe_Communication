using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Rumble.Net.Events;
using Rumble.Net.Models;

namespace Rumble.Net.Bots;

/// <summary>Marks a bot method as a chat command.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BotCommandAttribute(string name) : Attribute
{
    /// <summary>Command name (without prefix).</summary>
    public string Name { get; } = name;

    /// <summary>Help text.</summary>
    public string? Description { get; set; }

    /// <summary>Usage hint, e.g. <c>&lt;a&gt; &lt;b&gt;</c>.</summary>
    public string? Usage { get; set; }

    /// <summary>Alternative names.</summary>
    public string[] Aliases { get; set; } = [];
}

/// <summary>Context passed to command handlers.</summary>
public sealed class BotCommandContext
{
    internal BotCommandContext(RumbleClient client, TextMessage message, string command, IReadOnlyList<string> arguments, string argumentText)
    {
        Client = client;
        Message = message;
        Command = command;
        Arguments = arguments;
        ArgumentText = argumentText;
    }

    /// <summary>The bot's client.</summary>
    public RumbleClient Client { get; }

    /// <summary>The message that triggered the command.</summary>
    public TextMessage Message { get; }

    /// <summary>Sender of the command.</summary>
    public User? Sender => Message.Sender;

    /// <summary>Command name as typed (lower case).</summary>
    public string Command { get; }

    /// <summary>Parsed arguments (quotes supported).</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Raw text after the command name.</summary>
    public string ArgumentText { get; }

    /// <summary>Replies privately to private commands, otherwise in the originating channel.</summary>
    public void Reply(string text)
    {
        if (Message.IsPrivate && Sender is not null)
        {
            Client.SendPrivateMessage(Sender, text);
        }
        else if (Message.Channels.Count > 0)
        {
            Client.SendChannelMessage(Message.Channels[0], text);
        }
        else
        {
            Client.SendChannelMessage(text);
        }
    }
}

/// <summary>Command handler delegate.</summary>
public delegate ValueTask BotCommandHandler(BotCommandContext context, CancellationToken cancellationToken);

/// <summary>A registered command.</summary>
public sealed record BotCommandInfo(string Name, string? Description, string? Usage, IReadOnlyList<string> Aliases, BotCommandHandler Handler);

/// <summary>Parses chat messages and dispatches commands such as <c>!roll 20</c>.</summary>
public sealed class BotCommandRouter
{
    private readonly Dictionary<string, BotCommandInfo> _commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a router with a built-in <c>help</c> command.</summary>
    public BotCommandRouter()
    {
        Map("help", ctx =>
        {
            var sb = new StringBuilder("<b>Commands</b><br/>");
            foreach (var c in Commands.OrderBy(c => c.Name))
            {
                sb.Append(Prefix).Append(c.Name);
                if (c.Usage is not null)
                {
                    sb.Append(' ').Append(System.Net.WebUtility.HtmlEncode(c.Usage));
                }

                if (c.Description is not null)
                {
                    sb.Append(" — ").Append(System.Net.WebUtility.HtmlEncode(c.Description));
                }

                sb.Append("<br/>");
            }

            ctx.Reply(sb.ToString());
        }, "Lists commands");
    }

    /// <summary>Command prefix (default <c>!</c>).</summary>
    public string Prefix { get; set; } = "!";

    /// <summary>Registered commands.</summary>
    public IEnumerable<BotCommandInfo> Commands => _commands.Values.Distinct();

    /// <summary>Registers an async command.</summary>
    public BotCommandRouter Map(string name, BotCommandHandler handler, string? description = null, string? usage = null, params string[] aliases)
    {
        var info = new BotCommandInfo(name, description, usage, aliases, handler);
        foreach (var key in aliases.Prepend(name))
        {
            _commands[key] = info;
        }

        return this;
    }

    /// <summary>Registers a synchronous command.</summary>
    public BotCommandRouter Map(string name, Action<BotCommandContext> handler, string? description = null, string? usage = null, params string[] aliases)
        => Map(name, (ctx, _) =>
        {
            handler(ctx);
            return ValueTask.CompletedTask;
        }, description, usage, aliases);

    /// <summary>Registers every method marked with <see cref="BotCommandAttribute"/> on <paramref name="target"/>.</summary>
    public BotCommandRouter MapFromObject([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type, object target)
    {
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.GetCustomAttribute<BotCommandAttribute>() is not { } attr)
            {
                continue;
            }

            var parameters = method.GetParameters();
            var takesToken = parameters.Length == 2 && parameters[1].ParameterType == typeof(CancellationToken);
            if (parameters.Length == 0 || parameters[0].ParameterType != typeof(BotCommandContext) || parameters.Length > 2 || (parameters.Length == 2 && !takesToken))
            {
                throw new InvalidOperationException($"Command method {type.Name}.{method.Name} must take (BotCommandContext[, CancellationToken]).");
            }

            Map(attr.Name, async (ctx, ct) =>
            {
                var result = method.Invoke(target, takesToken ? [ctx, ct] : [ctx]);
                switch (result)
                {
                    case Task t:
                        await t.ConfigureAwait(false);
                        break;
                    case ValueTask vt:
                        await vt.ConfigureAwait(false);
                        break;
                }
            }, attr.Description, attr.Usage, attr.Aliases);
        }

        return this;
    }

    /// <summary>Splits a command line into name and arguments (supports double quotes).</summary>
    public bool TryParse(string text, out string command, out IReadOnlyList<string> arguments, out string argumentText)
    {
        command = string.Empty;
        arguments = [];
        argumentText = string.Empty;
        var plain = HtmlText.ToPlainText(text).Trim();
        if (!plain.StartsWith(Prefix, StringComparison.Ordinal) || plain.Length <= Prefix.Length)
        {
            return false;
        }

        var body = plain[Prefix.Length..];
        var space = body.IndexOf(' ');
        command = (space < 0 ? body : body[..space]).ToLowerInvariant();
        argumentText = space < 0 ? string.Empty : body[(space + 1)..].Trim();

        var args = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var ch in argumentText)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        arguments = args;
        return true;
    }

    /// <summary>Dispatches a message. Returns true if it was a known command.</summary>
    public async ValueTask<bool> DispatchAsync(RumbleClient client, TextMessage message, CancellationToken cancellationToken = default)
    {
        if (!TryParse(message.Message, out var name, out var args, out var argText) || !_commands.TryGetValue(name, out var info))
        {
            return false;
        }

        var context = new BotCommandContext(client, message, name, args, argText);
        try
        {
            await info.Handler(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException
                                       || ex.InnerException is FormatException or ArgumentException or ArgumentOutOfRangeException)
        {
            // Mumble messages are HTML: encode so usage hints like "<sides>" are not swallowed as tags.
            context.Reply(System.Net.WebUtility.HtmlEncode($"Usage: {Prefix}{info.Name} {info.Usage ?? string.Empty}".TrimEnd()));
        }

        return true;
    }
}
