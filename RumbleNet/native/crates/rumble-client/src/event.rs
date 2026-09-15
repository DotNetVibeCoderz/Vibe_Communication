//! Events published by the client.
//!
//! Events serialize to JSON as `{"type":"UserJoined", ...camelCase fields}` which the .NET layer
//! deserializes with source-generated `System.Text.Json` polymorphism.

use serde::Serialize;

use crate::state::{Channel, ServerInfo, User, b64};

/// Connection lifecycle state.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub enum ConnectionState {
    Disconnected = 0,
    Connecting = 1,
    /// TLS established, authenticating and synchronizing state.
    Synchronizing = 2,
    Connected = 3,
    Reconnecting = 4,
}

impl From<u8> for ConnectionState {
    fn from(v: u8) -> Self {
        match v {
            1 => ConnectionState::Connecting,
            2 => ConnectionState::Synchronizing,
            3 => ConnectionState::Connected,
            4 => ConnectionState::Reconnecting,
            _ => ConnectionState::Disconnected,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BanEntry {
    /// IP address as text.
    pub address: String,
    pub mask: u32,
    pub name: Option<String>,
    pub certificate_hash: Option<String>,
    pub reason: Option<String>,
    pub start: Option<String>,
    pub duration_seconds: Option<u32>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RegisteredUser {
    pub user_id: u32,
    pub name: Option<String>,
    pub last_seen: Option<String>,
    pub last_channel: Option<u32>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AclGroup {
    pub name: String,
    pub inherited: bool,
    pub inherit: bool,
    pub inheritable: bool,
    pub add: Vec<u32>,
    pub remove: Vec<u32>,
    pub inherited_members: Vec<u32>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AclEntry {
    pub apply_here: bool,
    pub apply_subs: bool,
    pub inherited: bool,
    pub user_id: Option<u32>,
    pub group: Option<String>,
    pub grant: u32,
    pub deny: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct PacketStats {
    pub good: u32,
    pub late: u32,
    pub lost: u32,
    pub resync: u32,
}

/// All client events.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "type", rename_all_fields = "camelCase")]
pub enum Event {
    StateChanged { state: ConnectionState },
    Connecting { host: String, port: u16, attempt: u32 },
    /// The server certificate was received (use for trust-on-first-use).
    ServerCertificate { sha1_fingerprint: String, sha256_fingerprint: String },
    /// Authentication succeeded and the initial state is synchronized.
    Connected { session: u32, welcome_text: String, server: ServerInfo },
    Disconnected { reason: String, will_reconnect: bool },
    Rejected { reject_type: String, reason: String },
    Kicked { actor: Option<u32>, reason: String, ban: bool },

    ChannelAdded { channel: Channel },
    ChannelUpdated { channel: Channel },
    ChannelRemoved { channel_id: u32 },

    UserJoined { user: User },
    UserUpdated { user: User, actor: Option<u32> },
    UserMoved { session: u32, from_channel_id: u32, to_channel_id: u32, actor: Option<u32> },
    UserLeft { session: u32, actor: Option<u32>, reason: Option<String>, ban: bool },
    UserTalking { session: u32, talking: bool },

    TextMessage { actor: Option<u32>, sessions: Vec<u32>, channel_ids: Vec<u32>, tree_ids: Vec<u32>, message: String },
    PermissionDenied {
        deny_type: String,
        reason: Option<String>,
        permission: Option<u32>,
        channel_id: Option<u32>,
        session: Option<u32>,
        name: Option<String>,
    },
    PermissionsUpdated { channel_id: Option<u32>, permissions: Option<u32>, flush: bool },
    ServerConfigUpdated { server: ServerInfo },
    PingUpdated { tcp_ping_ms: f32, udp_ping_ms: f32, udp_active: bool, local: PacketStats, remote: PacketStats },

    UserStats {
        session: u32,
        stats_only: bool,
        tcp_ping_ms: f32,
        udp_ping_ms: f32,
        online_seconds: Option<u32>,
        idle_seconds: Option<u32>,
        bandwidth: Option<u32>,
        version: Option<String>,
        os: Option<String>,
        address: Option<String>,
        strong_certificate: bool,
        opus: bool,
        from_client: PacketStats,
        from_server: PacketStats,
    },
    BanList { bans: Vec<BanEntry> },
    RegisteredUsers { users: Vec<RegisteredUser> },
    Acl { channel_id: u32, inherit_acls: bool, groups: Vec<AclGroup>, acls: Vec<AclEntry> },
    UsersQueried { ids: Vec<u32>, names: Vec<String> },
    ContextActionModified { action: String, text: Option<String>, context: u32, remove: bool },
    PluginData {
        sender_session: Option<u32>,
        data_id: String,
        #[serde(serialize_with = "b64::bytes")]
        data: Vec<u8>,
    },
}

impl Event {
    /// JSON representation used by the FFI layer.
    pub fn to_json(&self) -> String {
        serde_json::to_string(self).unwrap_or_else(|_| String::from("{\"type\":\"Unknown\"}"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn json_shape() {
        let json = Event::UserTalking { session: 3, talking: true }.to_json();
        assert_eq!(json, r#"{"type":"UserTalking","session":3,"talking":true}"#);
        let json = Event::PluginData { sender_session: None, data_id: "x".into(), data: vec![1, 2, 3] }.to_json();
        assert!(json.contains(r#""data":"AQID""#), "{json}");
    }
}
