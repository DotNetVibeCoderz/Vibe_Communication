//! Cached server state: channel tree, users and server information.

use std::collections::HashMap;

use rumble_protocol::proto;
use rumble_protocol::version::Version;
use serde::Serialize;

pub(crate) mod b64 {
    use base64::Engine as _;
    use serde::Serializer;

    pub fn opt<S: Serializer>(v: &Option<Vec<u8>>, s: S) -> Result<S::Ok, S::Error> {
        match v {
            Some(bytes) => s.serialize_some(&base64::engine::general_purpose::STANDARD.encode(bytes)),
            None => s.serialize_none(),
        }
    }

    pub fn bytes<S: Serializer>(v: &[u8], s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&base64::engine::general_purpose::STANDARD.encode(v))
    }
}

/// A channel in the server tree.
#[derive(Debug, Clone, PartialEq, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Channel {
    pub id: u32,
    pub parent_id: Option<u32>,
    pub name: String,
    pub description: String,
    /// Hex SHA-1 of the description when it was not sent inline (request it with `RequestBlob`).
    pub description_hash: Option<String>,
    pub position: i32,
    pub temporary: bool,
    pub max_users: u32,
    pub links: Vec<u32>,
    pub is_enter_restricted: bool,
    pub can_enter: bool,
    /// Effective permissions of the local user (bit flags), if queried.
    pub permissions: Option<u32>,
}

/// A connected user.
#[derive(Debug, Clone, PartialEq, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct User {
    pub session: u32,
    /// Registered user id, if registered.
    pub user_id: Option<u32>,
    pub name: String,
    pub channel_id: u32,
    pub mute: bool,
    pub deaf: bool,
    pub suppress: bool,
    pub self_mute: bool,
    pub self_deaf: bool,
    pub priority_speaker: bool,
    pub recording: bool,
    pub comment: String,
    pub comment_hash: Option<String>,
    #[serde(serialize_with = "b64::opt")]
    pub texture: Option<Vec<u8>>,
    pub texture_hash: Option<String>,
    /// Hex SHA-1 hash of the user's certificate.
    pub certificate_hash: Option<String>,
    pub listening_channels: Vec<u32>,
}

/// Server level information.
#[derive(Debug, Clone, PartialEq, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ServerInfo {
    pub version: String,
    pub release: String,
    pub os: String,
    pub os_version: String,
    pub welcome_text: String,
    pub max_bandwidth: u32,
    pub max_users: u32,
    pub allow_html: bool,
    pub message_length: u32,
    pub image_message_length: u32,
    pub recording_allowed: bool,
    pub suggested_positional: Option<bool>,
    pub suggested_push_to_talk: Option<bool>,
    pub opus: bool,
    pub root_permissions: u64,
    pub certificate_fingerprint: Option<String>,
}

/// Serializable copy of the whole state.
#[derive(Debug, Clone, PartialEq, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub session: Option<u32>,
    pub synchronized: bool,
    pub server: ServerInfo,
    pub channels: Vec<Channel>,
    pub users: Vec<User>,
    pub tcp_ping_ms: f32,
    pub udp_ping_ms: f32,
    pub udp_active: bool,
}

/// Cached server state.
#[derive(Debug, Clone, Default)]
pub struct ServerState {
    pub session: Option<u32>,
    pub synchronized: bool,
    pub server_version: Version,
    pub server: ServerInfo,
    pub channels: HashMap<u32, Channel>,
    pub users: HashMap<u32, User>,
    pub tcp_ping_ms: f32,
    pub udp_ping_ms: f32,
    pub udp_active: bool,
}

fn hex_opt(v: Option<&[u8]>) -> Option<String> {
    v.filter(|b| !b.is_empty()).map(hex::encode)
}

impl ServerState {
    pub fn reset(&mut self) {
        *self = ServerState::default();
    }

    pub fn snapshot(&self) -> Snapshot {
        let mut channels: Vec<Channel> = self.channels.values().cloned().collect();
        channels.sort_by(|a, b| a.position.cmp(&b.position).then_with(|| a.name.cmp(&b.name)));
        let mut users: Vec<User> = self.users.values().cloned().collect();
        users.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
        Snapshot {
            session: self.session,
            synchronized: self.synchronized,
            server: self.server.clone(),
            channels,
            users,
            tcp_ping_ms: self.tcp_ping_ms,
            udp_ping_ms: self.udp_ping_ms,
            udp_active: self.udp_active,
        }
    }

    /// The local user, once synchronized.
    pub fn me(&self) -> Option<&User> {
        self.session.and_then(|s| self.users.get(&s))
    }

    /// Users currently in a channel.
    pub fn users_in(&self, channel_id: u32) -> impl Iterator<Item = &User> {
        self.users.values().filter(move |u| u.channel_id == channel_id)
    }

    /// Direct children of a channel.
    pub fn children_of(&self, channel_id: u32) -> impl Iterator<Item = &Channel> {
        self.channels.values().filter(move |c| c.parent_id == Some(channel_id))
    }

    /// Finds a channel by case-insensitive name.
    pub fn find_channel(&self, name: &str) -> Option<&Channel> {
        self.channels.values().find(|c| c.name.eq_ignore_ascii_case(name))
    }

    /// Applies a `ChannelState` message. Returns the updated channel and whether it is new.
    pub fn apply_channel_state(&mut self, msg: &proto::ChannelState) -> Option<(Channel, bool)> {
        let id = msg.channel_id?;
        let is_new = !self.channels.contains_key(&id);
        let ch = self.channels.entry(id).or_insert_with(|| Channel { id, can_enter: true, ..Default::default() });
        if let Some(p) = msg.parent {
            ch.parent_id = (p != id).then_some(p);
        }
        if let Some(n) = &msg.name {
            ch.name.clone_from(n);
        }
        if let Some(d) = &msg.description {
            ch.description.clone_from(d);
            ch.description_hash = None;
        }
        if let Some(h) = &msg.description_hash {
            ch.description_hash = hex_opt(Some(h));
        }
        if let Some(t) = msg.temporary {
            ch.temporary = t;
        }
        if let Some(p) = msg.position {
            ch.position = p;
        }
        if let Some(m) = msg.max_users {
            ch.max_users = m;
        }
        if !msg.links.is_empty() {
            ch.links.clone_from(&msg.links);
        }
        for l in &msg.links_add {
            if !ch.links.contains(l) {
                ch.links.push(*l);
            }
        }
        ch.links.retain(|l| !msg.links_remove.contains(l));
        if let Some(r) = msg.is_enter_restricted {
            ch.is_enter_restricted = r;
        }
        if let Some(c) = msg.can_enter {
            ch.can_enter = c;
        }
        Some((ch.clone(), is_new))
    }

    /// Removes a channel and its links from other channels.
    pub fn remove_channel(&mut self, id: u32) -> Option<Channel> {
        let removed = self.channels.remove(&id);
        for ch in self.channels.values_mut() {
            ch.links.retain(|l| *l != id);
        }
        removed
    }

    /// Applies a `UserState` message. Returns `(user, is_new, previous_channel)`.
    pub fn apply_user_state(&mut self, msg: &proto::UserState) -> Option<(User, bool, Option<u32>)> {
        let session = msg.session?;
        let is_new = !self.users.contains_key(&session);
        let u = self.users.entry(session).or_insert_with(|| User { session, ..Default::default() });
        let prev_channel = u.channel_id;

        if let Some(n) = &msg.name {
            u.name.clone_from(n);
        }
        if let Some(id) = msg.user_id {
            u.user_id = Some(id);
        }
        if let Some(c) = msg.channel_id {
            u.channel_id = c;
        }
        macro_rules! flag {
            ($($field:ident),*) => { $( if let Some(v) = msg.$field { u.$field = v; } )* };
        }
        flag!(mute, deaf, suppress, self_mute, self_deaf, priority_speaker, recording);
        if let Some(t) = &msg.texture {
            u.texture = (!t.is_empty()).then(|| t.to_vec());
            u.texture_hash = None;
        }
        if let Some(h) = &msg.texture_hash {
            u.texture_hash = hex_opt(Some(h));
        }
        if let Some(c) = &msg.comment {
            u.comment.clone_from(c);
            u.comment_hash = None;
        }
        if let Some(h) = &msg.comment_hash {
            u.comment_hash = hex_opt(Some(h));
        }
        if let Some(h) = &msg.hash {
            u.certificate_hash = (!h.is_empty()).then(|| h.clone());
        }
        for c in &msg.listening_channel_add {
            if !u.listening_channels.contains(c) {
                u.listening_channels.push(*c);
            }
        }
        u.listening_channels.retain(|c| !msg.listening_channel_remove.contains(c));

        let moved = (!is_new && prev_channel != u.channel_id).then_some(prev_channel);
        Some((u.clone(), is_new, moved))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn channel_tree_and_users() {
        let mut st = ServerState::default();
        st.apply_channel_state(&proto::ChannelState { channel_id: Some(0), name: Some("Root".into()), ..Default::default() });
        let (lobby, new) = st
            .apply_channel_state(&proto::ChannelState {
                channel_id: Some(1),
                parent: Some(0),
                name: Some("Lobby".into()),
                ..Default::default()
            })
            .unwrap();
        assert!(new);
        assert_eq!(lobby.parent_id, Some(0));
        assert_eq!(st.children_of(0).count(), 1);

        let (u, new, moved) = st
            .apply_user_state(&proto::UserState { session: Some(5), name: Some("alice".into()), ..Default::default() })
            .unwrap();
        assert!(new && moved.is_none());
        assert_eq!(u.channel_id, 0);
        let (_, new, moved) =
            st.apply_user_state(&proto::UserState { session: Some(5), channel_id: Some(1), ..Default::default() }).unwrap();
        assert!(!new);
        assert_eq!(moved, Some(0));
        assert_eq!(st.users_in(1).count(), 1);
        assert_eq!(st.find_channel("lobby").unwrap().id, 1);

        st.apply_channel_state(&proto::ChannelState { channel_id: Some(0), links_add: vec![1], ..Default::default() });
        assert_eq!(st.channels[&0].links, vec![1]);
        st.remove_channel(1);
        assert!(st.channels[&0].links.is_empty());
    }
}
