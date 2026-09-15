# Native C API

The header is [`native/include/rumble.h`](../native/include/rumble.h), and the library is `rumble_native` (`.dll`, `.so` or `.dylib`, or `.a` for iOS).

## Conventions

- Functions return `RUMBLE_OK` (0) or a negative `RUMBLE_ERR_*` code. Call `rumble_last_error(buf, cap)` on the same thread to get the message.
- Strings are UTF-8 with an explicit length.
- Rich data (config, commands, events, snapshots) crosses the boundary as JSON. Audio uses raw `float*` or `int16_t*` pointers.
- Free buffers returned through `(uint8_t** out_ptr, size_t* out_len)` with `rumble_free`.
- Callbacks run on native threads. None are invoked after `rumble_client_destroy` returns, and you must not call destroy from inside a callback.

## Example

```c
#include "rumble.h"
#include <stdio.h>
#include <string.h>

static void on_event(void *ud, const uint8_t *json, size_t len) {
    printf("%.*s\n", (int)len, (const char *)json);   /* {"type":"UserJoined",...} */
}

int main(void) {
    const char *cfg = "{\"host\":\"voice.example.org\",\"port\":64738,\"username\":\"c-client\"}";
    RumbleClient *client = NULL;
    if (rumble_client_create((const uint8_t *)cfg, strlen(cfg), on_event, NULL, &client) != RUMBLE_OK) {
        char err[256]; int n = rumble_last_error((uint8_t *)err, sizeof err);
        fprintf(stderr, "error: %.*s\n", n, err);
        return 1;
    }
    rumble_audio_set_mode(client, RUMBLE_AUDIO_DEVICES, NULL, 0, NULL, 0);

    /* …wait for {"type":"Connected"} … */
    const char *cmd = "{\"type\":\"JoinChannel\",\"channelId\":1}";
    rumble_client_send_command(client, (const uint8_t *)cmd, strlen(cmd));

    rumble_client_destroy(client);
    return 0;
}
```

## JSON reference

**Config** (camelCase; every field except `host` and `username` is optional):

`host, port, username, password, tokens[], certificatePem, privateKeyPem, tlsVerification ("AcceptAll"|"Pinned"|"WebPki"), pinnedFingerprint, autoReconnect, maxReconnectAttempts, reconnectMinDelayMs, reconnectMaxDelayMs, connectTimeoutMs, pingIntervalMs, pingTimeoutMs, forceTcpVoice, isBot, clientRelease, positionalTransmit`

**Commands** use the form `{"type": "...", ...}`:

| Type | Fields |
|---|---|
| `Disconnect` | none |
| `JoinChannel` | `channelId` |
| `SendTextMessage` | `channelIds`, `sessions`, `treeIds`, `message` |
| `SetSelfMute` | `mute` |
| `SetSelfDeaf` | `deaf` |
| `SetComment` | `comment` |
| `SetTexture` | `texture` (base64) |
| `RegisterSelf` | none |
| `SetRecording` | `recording` |
| `MoveUser` | `session`, `channelId` |
| `SetUserMute` | `session`, `mute` |
| `SetUserDeaf` | `session`, `deaf` |
| `SetPrioritySpeaker` | `session`, `priority` |
| `SetUserSuppressed` | `session`, `suppress` |
| `KickUser`, `BanUser` | `session`, `reason` |
| `CreateChannel` | `parentId`, `name`, `description`, `temporary`, `position`, `maxUsers` |
| `UpdateChannel` | `channelId` plus any of `name`, `description`, `parentId`, `position`, `maxUsers` |
| `RemoveChannel` | `channelId` |
| `LinkChannels`, `UnlinkChannels` | `channelId`, `targets` |
| `ListenToChannels` | `add`, `remove` |
| `RegisterVoiceTarget` | `id`, `targets[{sessions, channelId, group, links, children}]` |
| `RequestUserStats` | `session`, `statsOnly` |
| `RequestBanList` | none |
| `SetBanList` | `bans[{address, mask, name, certificateHash, reason, durationSeconds}]` |
| `RequestRegisteredUsers` | none |
| `RequestAcl` | `channelId` |
| `QueryPermissions` | `channelId` |
| `QueryUsers` | `ids`, `names` |
| `RequestBlob` | `sessionTextures`, `sessionComments`, `channelDescriptions` |
| `SendPluginData` | `receivers`, `dataId`, `data` (base64) |
| `ExecuteContextAction` | `action`, `session`, `channelId` |

**Events** use the same `{"type": ...}` shape. The types are:

`StateChanged, Connecting, ServerCertificate, Connected, Disconnected, Rejected, Kicked, ChannelAdded, ChannelUpdated, ChannelRemoved, UserJoined, UserUpdated, UserMoved, UserLeft, UserTalking, TextMessage, PermissionDenied, PermissionsUpdated, ServerConfigUpdated, PingUpdated, UserStats, BanList, RegisteredUsers, Acl, UsersQueried, ContextActionModified, PluginData`

Their fields match the .NET records in `src/Rumble.Net/Events/RumbleEvents.cs`, in camelCase.

## Other functions

- **Audio:** `rumble_audio_*` covers mode, capture config, frame and capture callbacks, PCM push, transmit mode, PTT, voice target, volumes, positional settings and the listener pose (9 floats), input level, and device list.
- **Codec:** `rumble_opus_encoder_*` and `rumble_opus_decoder_*` wrap the Opus codec.
- **Utilities:** `rumble_generate_certificate`, `rumble_query_server`, `rumble_run_benchmarks`, `rumble_set_log_callback`.
- **Testing:** `rumble_mock_server_start`, `_drop_connections` and `_stop`. These need the `mock-server` feature, which is on by default. Build with `--no-default-features` to leave them out.
