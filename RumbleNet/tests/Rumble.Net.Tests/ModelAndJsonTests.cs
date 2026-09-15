using System.Text;
using System.Text.Json;
using Rumble.Net.Events;
using Rumble.Net.Interop;
using Rumble.Net.Models;

namespace Rumble.Net.Tests;

public class ModelAndJsonTests
{
    private static RumbleEvent Parse(string json) => RumbleClient.ParseEvent(Encoding.UTF8.GetBytes(json))!;

    [Fact]
    public void Deserializes_events_emitted_by_native_core()
    {
        var joined = Assert.IsType<UserJoinedEvent>(Parse(
            """{"type":"UserJoined","user":{"session":7,"userId":null,"name":"alice","channelId":1,"mute":false,"deaf":false,"suppress":false,"selfMute":true,"selfDeaf":false,"prioritySpeaker":false,"recording":false,"comment":"","commentHash":null,"texture":"AQID","textureHash":null,"certificateHash":null,"listeningChannels":[2]}}"""));
        Assert.Equal("alice", joined.User.Name);
        Assert.True(joined.User.SelfMute);
        Assert.Equal(new byte[] { 1, 2, 3 }, joined.User.Texture);
        Assert.Equal([2u], joined.User.ListeningChannels);

        var state = Assert.IsType<StateChangedEvent>(Parse("""{"type":"StateChanged","state":"Reconnecting"}"""));
        Assert.Equal(ConnectionState.Reconnecting, state.State);

        var text = Assert.IsType<TextMessageEvent>(Parse("""{"type":"TextMessage","actor":3,"sessions":[],"channelIds":[1],"treeIds":[],"message":"<b>hi</b>"}"""));
        Assert.False(text.IsPrivate);
        Assert.Equal("hi", HtmlText.ToPlainText(text.Message));
    }

    [Fact]
    public void Unknown_event_types_are_tolerated()
    {
        var e = Assert.IsType<UnknownEvent>(Parse("""{"type":"SomethingNew","foo":1}"""));
        Assert.Equal("SomethingNew", e.Type);
    }

    [Fact]
    public void Commands_serialize_to_native_shape()
    {
        var json = Native.Json<RumbleCommand>(new JoinChannelCommand(4), RumbleJsonContext.Default.RumbleCommand);
        Assert.Equal("""{"type":"JoinChannel","channelId":4}""", Encoding.UTF8.GetString(json.Span));

        json = Native.Json<RumbleCommand>(new KickUserCommand(9, null), RumbleJsonContext.Default.RumbleCommand);
        Assert.Equal("""{"type":"KickUser","session":9}""", Encoding.UTF8.GetString(json.Span));

        json = Native.Json<RumbleCommand>(new DisconnectCommand(), RumbleJsonContext.Default.RumbleCommand);
        Assert.Equal("""{"type":"Disconnect"}""", Encoding.UTF8.GetString(json.Span));
    }

    [Fact]
    public void Client_config_uses_native_field_names()
    {
        var options = new RumbleClientOptions { Host = "voice.example", Username = "bob", PingInterval = TimeSpan.FromSeconds(2) };
        var json = Encoding.UTF8.GetString(Native.Json(options.ToNative(), RumbleJsonContext.Default.NativeClientConfig).Span);
        Assert.Contains("\"host\":\"voice.example\"", json);
        Assert.Contains("\"pingIntervalMs\":2000", json);
        Assert.Contains("\"tlsVerification\":\"AcceptAll\"", json);
    }

    [Fact]
    public void Model_builds_navigable_tree()
    {
        var model = new ServerModel();
        model.Apply(new ChannelAddedEvent(new Channel { Id = 0, Name = "Root" }));
        model.Apply(new ChannelAddedEvent(new Channel { Id = 1, ParentId = 0, Name = "Games", Position = 1 }));
        model.Apply(new ChannelAddedEvent(new Channel { Id = 2, ParentId = 1, Name = "Minecraft" }));
        model.Apply(new ChannelAddedEvent(new Channel { Id = 3, ParentId = 0, Name = "AFK", Position = 2 }));
        model.Apply(new UserJoinedEvent(new User { Session = 10, Name = "alice", ChannelId = 2 }));
        model.Apply(new UserJoinedEvent(new User { Session = 11, Name = "bob", ChannelId = 3 }));
        model.Apply(new ConnectedEvent(10, "hi", new ServerInfo()));

        Assert.Equal("Root/Games/Minecraft", model.GetChannel(2)!.Path);
        Assert.Equal(["Games", "AFK"], model.Root!.Children.Select(c => c.Name));
        Assert.Equal(2, model.Root.TotalUserCount);
        Assert.Same(model.GetChannel(2), model.FindChannelByPath("Games/Minecraft"));
        Assert.True(model.FindUser("ALICE")!.IsSelf);
        Assert.Equal("Minecraft", model.Self!.Channel!.Name);
        Assert.Equal(["Minecraft"], model.Root.Descendants().Where(c => c.Users.Any(u => u.Name == "alice")).Select(c => c.Name));

        model.Apply(new UserTalkingEvent(11, true));
        Assert.True(model.GetUser(11)!.IsTalking);
        model.Apply(new UserLeftEvent(11, null, null, false));
        Assert.Null(model.GetUser(11));

        model.Apply(new ChannelRemovedEvent(3));
        Assert.Single(model.Root.Children);
    }

    [Fact]
    public void Options_validation()
    {
        Assert.Throws<ArgumentException>(() => new RumbleClientOptions { Username = "" }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RumbleClientOptions { Port = 70000 }.Validate());
        Assert.Throws<ArgumentException>(() => new RumbleClientOptions { TlsVerification = TlsVerification.Pinned }.Validate());
        new RumbleClientOptions().Validate();
    }
}
