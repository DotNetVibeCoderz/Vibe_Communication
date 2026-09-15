//! Commands sent from the application to the session.
//!
//! Commands deserialize from JSON (`{"type":"JoinChannel","channelId":3}`), which keeps the FFI
//! surface tiny and forward compatible.

use serde::Deserialize;

use rumble_protocol::control::ControlMessage;

/// A voice target entry used by [`Command::RegisterVoiceTarget`].
#[derive(Debug, Clone, PartialEq, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct VoiceTargetEntry {
    pub sessions: Vec<u32>,
    pub channel_id: Option<u32>,
    pub group: Option<String>,
    pub links: bool,
    pub children: bool,
}

/// A ban entry used by [`Command::SetBanList`].
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BanSpec {
    pub address: String,
    pub mask: u32,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub certificate_hash: Option<String>,
    #[serde(default)]
    pub reason: Option<String>,
    #[serde(default)]
    pub duration_seconds: Option<u32>,
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(tag = "type", rename_all_fields = "camelCase")]
pub enum Command {
    Disconnect,
    JoinChannel { channel_id: u32 },
    SendTextMessage {
        #[serde(default)]
        channel_ids: Vec<u32>,
        #[serde(default)]
        sessions: Vec<u32>,
        #[serde(default)]
        tree_ids: Vec<u32>,
        message: String,
    },
    SetSelfMute { mute: bool },
    SetSelfDeaf { deaf: bool },
    SetComment { comment: String },
    SetTexture {
        #[serde(default, with = "b64_vec")]
        texture: Vec<u8>,
    },
    RegisterSelf,
    SetRecording { recording: bool },
    MoveUser { session: u32, channel_id: u32 },
    SetUserMute { session: u32, mute: bool },
    SetUserDeaf { session: u32, deaf: bool },
    SetPrioritySpeaker { session: u32, priority: bool },
    SetUserSuppressed { session: u32, suppress: bool },
    KickUser { session: u32, #[serde(default)] reason: Option<String> },
    BanUser { session: u32, #[serde(default)] reason: Option<String> },
    CreateChannel {
        parent_id: u32,
        name: String,
        #[serde(default)]
        description: Option<String>,
        #[serde(default)]
        temporary: bool,
        #[serde(default)]
        position: Option<i32>,
        #[serde(default)]
        max_users: Option<u32>,
    },
    UpdateChannel {
        channel_id: u32,
        #[serde(default)]
        name: Option<String>,
        #[serde(default)]
        description: Option<String>,
        #[serde(default)]
        parent_id: Option<u32>,
        #[serde(default)]
        position: Option<i32>,
        #[serde(default)]
        max_users: Option<u32>,
    },
    RemoveChannel { channel_id: u32 },
    LinkChannels { channel_id: u32, targets: Vec<u32> },
    UnlinkChannels { channel_id: u32, targets: Vec<u32> },
    ListenToChannels {
        #[serde(default)]
        add: Vec<u32>,
        #[serde(default)]
        remove: Vec<u32>,
    },
    RegisterVoiceTarget { id: u32, targets: Vec<VoiceTargetEntry> },
    RequestUserStats { session: u32, #[serde(default)] stats_only: bool },
    RequestBanList,
    SetBanList { bans: Vec<BanSpec> },
    RequestRegisteredUsers,
    RequestAcl { channel_id: u32 },
    QueryPermissions { channel_id: u32 },
    QueryUsers {
        #[serde(default)]
        ids: Vec<u32>,
        #[serde(default)]
        names: Vec<String>,
    },
    RequestBlob {
        #[serde(default)]
        session_textures: Vec<u32>,
        #[serde(default)]
        session_comments: Vec<u32>,
        #[serde(default)]
        channel_descriptions: Vec<u32>,
    },
    SendPluginData {
        receivers: Vec<u32>,
        data_id: String,
        #[serde(with = "b64_vec")]
        data: Vec<u8>,
    },
    ExecuteContextAction { action: String, #[serde(default)] session: Option<u32>, #[serde(default)] channel_id: Option<u32> },
    /// Escape hatch for Rust callers.
    #[serde(skip)]
    Raw(Box<ControlMessage>),
}

mod b64_vec {
    use base64::Engine as _;
    use serde::{Deserialize, Deserializer};

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Vec<u8>, D::Error> {
        let s = String::deserialize(d)?;
        base64::engine::general_purpose::STANDARD.decode(s).map_err(serde::de::Error::custom)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_json_commands() {
        let c: Command = serde_json::from_str(r#"{"type":"JoinChannel","channelId":4}"#).unwrap();
        assert_eq!(c, Command::JoinChannel { channel_id: 4 });
        let c: Command =
            serde_json::from_str(r#"{"type":"SendTextMessage","channelIds":[1],"message":"hi"}"#).unwrap();
        assert!(matches!(c, Command::SendTextMessage { ref message, .. } if message == "hi"));
        let c: Command =
            serde_json::from_str(r#"{"type":"SendPluginData","receivers":[2],"dataId":"x","data":"AQID"}"#).unwrap();
        assert!(matches!(c, Command::SendPluginData { ref data, .. } if data == &[1, 2, 3]));
        let c: Command = serde_json::from_str(r#"{"type":"Disconnect"}"#).unwrap();
        assert_eq!(c, Command::Disconnect);
    }
}
