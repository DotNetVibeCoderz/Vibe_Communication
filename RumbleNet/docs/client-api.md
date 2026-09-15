# Client API

## Options

`RumbleClientOptions` properties, grouped by purpose:

| Group | Properties |
|---|---|
| Server | `Host`, `Port` (default 64738), `Username`, `Password`, `Tokens` (ACL access tokens) |
| Identity | `CertificatePem`, `PrivateKeyPem` (see [security.md](security.md)) |
| TLS | `TlsVerification` (`AcceptAll` \| `Pinned` \| `WebPki`), `PinnedFingerprint` |
| Resilience | `AutoReconnect`, `MaxReconnectAttempts`, `ReconnectMinDelay`, `ReconnectMaxDelay`, `ConnectTimeout`, `PingInterval`, `PingTimeout`, `CommandTimeout` |
| Voice | `ForceTcpVoice`, `PositionalTransmit`, `Audio` (see [audio.md](audio.md)) |
| Misc | `IsBot`, `ClientRelease`, `LoggerFactory`, `NativeLogLevel` |

Call `options.Validate()` to check the options early. `ConnectAsync` also validates them.

## Lifecycle

| Member | Behavior |
|---|---|
| `ConnectAsync(ct)` | Connects, authenticates and waits for synchronization. Throws `RumbleConnectionException`. |
| `DisconnectAsync(ct)` | Graceful disconnect. Automatic reconnect is not attempted. |
| `DisposeAsync()` | Disconnects, shuts down plugins and completes the event streams. |
| `State`, `IsConnected` | Connection state: `Disconnected`, `Connecting`, `Synchronizing`, `Connected` or `Reconnecting`. |

## Server model (LINQ)

```csharp
ServerModel server = client.Server;
server.Root; server.Self; server.Info;                       // ServerInfo: version, welcome text, limits…
server.GetChannel(id); server.FindChannel("Lobby"); server.FindChannelByPath("Games/Minecraft");
server.GetUser(session); server.FindUser("alice");

Channel c = server.Root!;
c.Children; c.Users; c.Parent; c.LinkedChannels; c.Path; c.Depth; c.TotalUserCount;
c.Descendants(); c.DescendantsAndSelf(); c.Ancestors();

User u = server.Self!;
u.Channel; u.IsSelf; u.IsTalking; u.IsMuted; u.IsDeafened; u.IsRegistered; u.Texture; u.CertificateHash;

var talkingInMyChannel = client.Self!.Channel!.Users.Where(x => x.IsTalking);
```

`client.GetSnapshot()` returns the native state cache as a single consistent copy.

## Operations

Methods that return `Task` wait for the server to confirm. They throw `RumblePermissionDeniedException` when an ACL denies the action and `RumbleException(Timeout)` after `CommandTimeout`.

| Category | Methods |
|---|---|
| Channels | `JoinChannelAsync(channel \| id \| nameOrPath)`, `CreateChannelAsync(parent, name, description, temporary, position, maxUsers)`, `UpdateChannel(...)`, `RemoveChannelAsync`, `LinkChannels`, `UnlinkChannels`, `ListenToChannels(add, remove)` |
| Messages | `SendChannelMessage(channel?, text)`, `SendTreeMessage`, `SendPrivateMessage`, `SendTextMessage(text, channels, users, trees)` |
| Self | `SetSelfMute`, `SetSelfDeaf`, `SetComment`, `SetAvatar`, `RegisterSelf`, `SetRecording` |
| Moderation | `MoveUser`, `MuteUser`, `DeafenUser`, `SetPrioritySpeaker`, `SuppressUser`, `KickUserAsync`, `BanUserAsync`, `GetBanListAsync`, `SetBanList` |
| Queries | `GetUserStatsAsync`, `GetRegisteredUsersAsync`, `GetAclAsync`, `GetPermissionsAsync` (returns the `Permissions` flags), `QueryUsersAsync`, `RequestBlobs` |
| Voice targets | `RegisterVoiceTarget(id 1–30, entries)`, then set `client.Audio.VoiceTarget = id` |
| Plugins and actions | `SendPluginData(dataId, bytes, receivers)`, `ExecuteContextAction(action, user, channel)` |
| Static | `RumbleClient.QueryServerAsync(host, port)` pings a server without connecting |

Messages are HTML. Encode user input with `WebUtility.HtmlEncode`. `TextMessage.PlainText` strips tags from received messages.

## Events

All events are raised on the client's event loop (a thread-pool thread), in order, after `ServerModel` has been updated. When a handler changes UI, marshal the work to the UI thread.

| Event | Args | Mumble equivalent |
|---|---|---|
| `Connected`, `Disconnected`, `Reconnecting`, `StateChanged` | `ConnectedEvent`, `DisconnectedEvent` (`WillReconnect`), `ConnectingEvent`, `ConnectionState` | |
| `Rejected`, `Kicked`, `ServerCertificateReceived` | | |
| `UserJoined` | `User` | onUserJoin |
| `UserLeft`, `UserMoved`, `UserUpdated`, `UserTalkingChanged` | rich args with `User`, `Channel` and `Actor` resolved | |
| `ChannelAdded`, `ChannelUpdated`, `ChannelRemoved` | | |
| `TextMessageReceived`, `ChannelMessageReceived`, `PrivateMessageReceived` | `TextMessage` | onChannelMessage |
| `PermissionDenied`, `ServerInfoChanged`, `PingUpdated`, `PluginDataReceived` | | |
| `Audio.AudioFrameReceived` | `in AudioFrame` (audio thread) | onAudioFrame |
| `EventReceived` | every `RumbleEvent` | |

### Streams and waiting

```csharp
await foreach (var e in client.ReadEventsAsync(ct))       // each call gets its own buffered stream
    if (e is UserTalkingEvent t) Console.WriteLine($"{t.Session} talking={t.Talking}");

var moved = await client.WaitForEventAsync<UserMovedEvent>(m => m.ToChannelId == 3, TimeSpan.FromSeconds(10));
```

## Errors

| Exception | When |
|---|---|
| `RumbleConnectionException` | Connect failed or was rejected (`RejectType`) |
| `RumblePermissionDeniedException` | An ACL denied the operation (`Denial.DenyType`, `Denial.Reason`) |
| `RumbleException` | Carries `ErrorCode`: `NotConnected`, `Timeout`, `Audio`, `Tls`, `Unsupported`… |

## Dependency injection

```csharp
services.AddLogging();
services.AddRumbleClient(o => { o.Host = "voice.example.org"; o.Username = "Service"; });
// RumbleClient is a singleton and picks up ILoggerFactory from the container.
```
