//! Client configuration (serializable so it can cross the FFI boundary as JSON).

use serde::{Deserialize, Serialize};

use crate::{ClientError, Result};

/// How the server certificate is validated.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub enum TlsVerification {
    /// Accept any certificate (Mumble servers are usually self-signed). The certificate
    /// fingerprint is reported through `Event::ServerCertificate` for trust-on-first-use.
    #[default]
    AcceptAll,
    /// Only accept a certificate whose SHA-1 fingerprint equals `pinned_fingerprint`.
    Pinned,
    /// Validate against the Mozilla root store (servers with CA-issued certificates).
    WebPki,
}

/// Connection options.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ClientConfig {
    pub host: String,
    pub port: u16,
    pub username: String,
    pub password: Option<String>,
    /// Access tokens for ACL groups.
    pub tokens: Vec<String>,
    /// PEM encoded client certificate (identity for registered users).
    pub certificate_pem: Option<String>,
    /// PEM encoded private key matching `certificate_pem`.
    pub private_key_pem: Option<String>,
    pub tls_verification: TlsVerification,
    /// Hex SHA-1 fingerprint used with [`TlsVerification::Pinned`].
    pub pinned_fingerprint: Option<String>,
    pub auto_reconnect: bool,
    /// 0 = unlimited.
    pub max_reconnect_attempts: u32,
    pub reconnect_min_delay_ms: u64,
    pub reconnect_max_delay_ms: u64,
    pub connect_timeout_ms: u64,
    pub ping_interval_ms: u64,
    /// Session is considered dead when no TCP ping reply arrives within this time.
    pub ping_timeout_ms: u64,
    /// Always tunnel voice through TCP (for networks blocking UDP).
    pub force_tcp_voice: bool,
    /// Identify as a bot (`client_type = 1`).
    pub is_bot: bool,
    /// Client release string shown to other users.
    pub client_release: String,
    /// Include the listener position in outgoing voice packets.
    pub positional_transmit: bool,
}

impl Default for ClientConfig {
    fn default() -> Self {
        Self {
            host: "localhost".into(),
            port: 64738,
            username: "RumbleUser".into(),
            password: None,
            tokens: Vec::new(),
            certificate_pem: None,
            private_key_pem: None,
            tls_verification: TlsVerification::AcceptAll,
            pinned_fingerprint: None,
            auto_reconnect: true,
            max_reconnect_attempts: 0,
            reconnect_min_delay_ms: 1_000,
            reconnect_max_delay_ms: 30_000,
            connect_timeout_ms: 10_000,
            ping_interval_ms: 5_000,
            ping_timeout_ms: 30_000,
            force_tcp_voice: false,
            is_bot: false,
            client_release: concat!("Rumble.Net ", env!("CARGO_PKG_VERSION")).into(),
            positional_transmit: false,
        }
    }
}

impl ClientConfig {
    pub fn validate(&self) -> Result<()> {
        if self.host.trim().is_empty() {
            return Err(ClientError::InvalidConfig("host must not be empty".into()));
        }
        if self.username.trim().is_empty() {
            return Err(ClientError::InvalidConfig("username must not be empty".into()));
        }
        if self.port == 0 {
            return Err(ClientError::InvalidConfig("port must not be 0".into()));
        }
        if self.certificate_pem.is_some() != self.private_key_pem.is_some() {
            return Err(ClientError::InvalidConfig("certificate and private key must be provided together".into()));
        }
        if self.tls_verification == TlsVerification::Pinned && self.pinned_fingerprint.is_none() {
            return Err(ClientError::InvalidConfig("pinned verification requires pinnedFingerprint".into()));
        }
        if self.ping_interval_ms == 0 || self.ping_timeout_ms <= self.ping_interval_ms {
            return Err(ClientError::InvalidConfig("pingTimeoutMs must be greater than pingIntervalMs".into()));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn json_defaults() {
        let cfg: ClientConfig = serde_json::from_str(r#"{"host":"voice.example.org","username":"bob"}"#).unwrap();
        assert_eq!(cfg.port, 64738);
        assert!(cfg.auto_reconnect);
        cfg.validate().unwrap();
    }

    #[test]
    fn validation() {
        let cfg = ClientConfig { username: "".into(), ..Default::default() };
        assert!(cfg.validate().is_err());
        let cfg = ClientConfig { tls_verification: TlsVerification::Pinned, ..Default::default() };
        assert!(cfg.validate().is_err());
    }
}
