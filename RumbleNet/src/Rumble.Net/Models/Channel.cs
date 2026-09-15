using System.Text.Json.Serialization;

namespace Rumble.Net.Models;

/// <summary>
/// A channel in the server tree. Instances are immutable snapshots; navigation properties
/// (<see cref="Parent"/>, <see cref="Children"/>, <see cref="Users"/>) always query the live model,
/// which makes the tree convenient to explore with LINQ.
/// </summary>
public sealed class Channel
{
    /// <summary>Channel id (0 = root).</summary>
    public uint Id { get; init; }

    /// <summary>Parent channel id, or <c>null</c> for the root.</summary>
    public uint? ParentId { get; init; }

    /// <summary>Channel name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Description (may be HTML).</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>SHA-1 of a large description that was not sent inline.</summary>
    public string? DescriptionHash { get; init; }

    /// <summary>Sort position.</summary>
    public int Position { get; init; }

    /// <summary>Temporary channels are removed when empty.</summary>
    public bool Temporary { get; init; }

    /// <summary>Maximum users (0 = server default).</summary>
    public uint MaxUsers { get; init; }

    /// <summary>Ids of linked channels.</summary>
    public IReadOnlyList<uint> Links { get; init; } = [];

    /// <summary>Whether entering is restricted by ACL.</summary>
    public bool IsEnterRestricted { get; init; }

    /// <summary>Whether the local user may enter.</summary>
    public bool CanEnter { get; init; } = true;

    /// <summary>Local user's permission bits in this channel, if queried.</summary>
    public uint? Permissions { get; init; }

    [JsonIgnore]
    internal ServerModel? Model { get; set; }

    /// <summary>True for the root channel.</summary>
    [JsonIgnore]
    public bool IsRoot => ParentId is null;

    /// <summary>The parent channel.</summary>
    [JsonIgnore]
    public Channel? Parent => ParentId is { } p ? Model?.GetChannel(p) : null;

    /// <summary>Direct child channels ordered by position then name.</summary>
    [JsonIgnore]
    public IEnumerable<Channel> Children => Model is null
        ? []
        : Model.Channels.Where(c => c.ParentId == Id).OrderBy(c => c.Position).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Users currently in this channel ordered by name.</summary>
    [JsonIgnore]
    public IEnumerable<User> Users => Model is null
        ? []
        : Model.Users.Where(u => u.ChannelId == Id).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Linked channels.</summary>
    [JsonIgnore]
    public IEnumerable<Channel> LinkedChannels => Model is null ? [] : Links.Select(Model.GetChannel).OfType<Channel>();

    /// <summary>Path from the root, e.g. <c>Root/Games/Minecraft</c>.</summary>
    [JsonIgnore]
    public string Path => string.Join('/', AncestorsAndSelf().Reverse().Select(c => c.Name));

    /// <summary>Depth in the tree (root = 0).</summary>
    [JsonIgnore]
    public int Depth => Ancestors().Count();

    /// <summary>Walks up to the root (excluding this channel).</summary>
    public IEnumerable<Channel> Ancestors()
    {
        var seen = new HashSet<uint> { Id };
        for (var p = Parent; p is not null && seen.Add(p.Id); p = p.Parent)
        {
            yield return p;
        }
    }

    /// <summary>This channel followed by its ancestors.</summary>
    public IEnumerable<Channel> AncestorsAndSelf() => Ancestors().Prepend(this);

    /// <summary>All descendants, depth first.</summary>
    public IEnumerable<Channel> Descendants()
    {
        var stack = new Stack<Channel>(Children.Reverse());
        var seen = new HashSet<uint> { Id };
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            if (!seen.Add(c.Id))
            {
                continue;
            }

            yield return c;
            foreach (var child in c.Children.Reverse())
            {
                stack.Push(child);
            }
        }
    }

    /// <summary>This channel followed by all descendants.</summary>
    public IEnumerable<Channel> DescendantsAndSelf() => Descendants().Prepend(this);

    /// <summary>Number of users in this channel and all sub-channels.</summary>
    [JsonIgnore]
    public int TotalUserCount => DescendantsAndSelf().Sum(c => c.Users.Count());

    internal Channel WithModel(ServerModel model)
    {
        Model = model;
        return this;
    }

    /// <inheritdoc />
    public override string ToString() => $"#{Id} {Name}";
}
