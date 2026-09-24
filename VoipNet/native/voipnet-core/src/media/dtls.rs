//! DTLS-SRTP keying (RFC 5763/5764) on the dimpl sans-IO DTLS 1.2 engine.
//!
//! Certificates are self-signed; the peer is authenticated by comparing its certificate with the
//! `a=fingerprint` received over signaling.

use std::sync::Arc;
use std::time::Instant;

use dimpl::{Config, Dtls, DtlsCertificate, Output};

use crate::sip::tls::fingerprint_sha256;
use crate::srtp::{SrtpContext, SrtpProfile};

/// A local DTLS certificate shared by every call of an endpoint.
pub struct DtlsIdentity {
    certificate: Vec<u8>,
    private_key: Vec<u8>,
    fingerprint: String,
}

impl DtlsIdentity {
    /// Generates an ECDSA P-256 certificate with a random serial (Firefox rejects reused serials).
    pub fn generate() -> Result<Arc<Self>, String> {
        let key = rcgen::KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256).map_err(|e| e.to_string())?;
        let mut params = rcgen::CertificateParams::new(Vec::<String>::new()).map_err(|e| e.to_string())?;
        params.distinguished_name.push(rcgen::DnType::CommonName, "Voip.NET");
        params.serial_number = Some(rand::random::<[u8; 16]>().to_vec().into());
        let cert = params.self_signed(&key).map_err(|e| e.to_string())?;
        let certificate = cert.der().to_vec();
        Ok(Arc::new(Self { fingerprint: fingerprint_sha256(&certificate), certificate, private_key: key.serialize_der() }))
    }

    /// `AA:BB:…` SHA-256 fingerprint for `a=fingerprint:sha-256 …`.
    pub fn fingerprint(&self) -> &str {
        &self.fingerprint
    }
}

impl std::fmt::Debug for DtlsIdentity {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DtlsIdentity").field("fingerprint", &self.fingerprint).finish()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DtlsRole {
    /// Sends the ClientHello (`a=setup:active`).
    Client,
    /// Waits for the ClientHello (`a=setup:passive`).
    Server,
}

pub enum DtlsEvent {
    Send(Vec<u8>),
    /// Handshake finished and the peer certificate matched: (outbound, inbound, profile).
    Keys(SrtpContext, SrtpContext, SrtpProfile),
    /// The tunnel is up, so application data — SCTP, for a data channel — can flow through it.
    Connected,
    /// Plaintext that arrived inside the tunnel, which for WebRTC is an SCTP packet (RFC 8261).
    Data(Vec<u8>),
    Failed(String),
}

pub struct DtlsTransport {
    dtls: Dtls,
    role: DtlsRole,
    remote_fingerprint: String,
    next_timeout: Option<Instant>,
    cert_ok: bool,
    done: bool,
    buf: Vec<u8>,
}

fn normalize(fp: &str) -> String {
    // Accept "sha-256 AA:BB…" or a bare hex string.
    let value = fp.trim().rsplit(' ').next().unwrap_or(fp);
    value.chars().filter(|c| c.is_ascii_hexdigit()).map(|c| c.to_ascii_uppercase()).collect()
}

impl DtlsTransport {
    pub fn new(identity: &DtlsIdentity, role: DtlsRole, remote_fingerprint: &str, now: Instant) -> Result<Self, String> {
        let config = Config::builder().mtu(1200).build().map_err(|e| e.to_string())?;
        let cert = DtlsCertificate { certificate: identity.certificate.clone(), private_key: identity.private_key.clone() };
        let mut dtls = Dtls::new_12(Arc::new(config), cert, now);
        dtls.set_active(role == DtlsRole::Client);
        Ok(Self {
            dtls,
            role,
            remote_fingerprint: normalize(remote_fingerprint),
            next_timeout: None,
            cert_ok: false,
            done: false,
            buf: vec![0u8; 2048],
        })
    }

    pub fn role(&self) -> DtlsRole {
        self.role
    }

    pub fn is_done(&self) -> bool {
        self.done
    }

    /// Starts the engine; the client role sends its ClientHello. Must run before any packet.
    pub fn start(&mut self, now: Instant, events: &mut Vec<DtlsEvent>) {
        self.handle_timeout(now, events);
    }

    pub fn handle_packet(&mut self, data: &[u8], events: &mut Vec<DtlsEvent>) {
        match self.dtls.handle_packet(data) {
            Ok(()) => self.drain(events),
            Err(e) => self.fail(format!("DTLS: {e}"), events),
        }
    }

    /// Sends application data through the tunnel. Fails quietly before the handshake finishes;
    /// the data channel queues its own packets and resends them, so nothing is lost by dropping here.
    pub fn send_data(&mut self, data: &[u8], events: &mut Vec<DtlsEvent>) {
        match self.dtls.send_application_data(data) {
            Ok(()) => self.drain(events),
            // Before the handshake finishes there is nothing to send through; the data channel
            // holds on to its packets and sends them again, so dropping one here loses nothing.
            Err(_) => {}
        }
    }

    pub fn poll_timeout(&mut self, now: Instant, events: &mut Vec<DtlsEvent>) {
        if self.next_timeout.is_some_and(|t| now >= t) {
            self.handle_timeout(now, events);
        }
    }

    fn handle_timeout(&mut self, now: Instant, events: &mut Vec<DtlsEvent>) {
        self.next_timeout = None;
        match self.dtls.handle_timeout(now) {
            Ok(()) => self.drain(events),
            Err(e) => self.fail(format!("DTLS: {e}"), events),
        }
    }

    fn fail(&mut self, reason: String, events: &mut Vec<DtlsEvent>) {
        if !self.done {
            self.done = true;
            events.push(DtlsEvent::Failed(reason));
        }
    }

    fn drain(&mut self, events: &mut Vec<DtlsEvent>) {
        loop {
            match self.dtls.poll_output(&mut self.buf) {
                Output::Packet(p) => events.push(DtlsEvent::Send(p.to_vec())),
                Output::BufferTooSmall { needed } => self.buf.resize(needed, 0),
                Output::Timeout(t) => {
                    self.next_timeout = Some(t);
                    break;
                }
                Output::PeerCert(der) => {
                    let actual = fingerprint_sha256(der).replace(':', "");
                    if actual == self.remote_fingerprint {
                        self.cert_ok = true;
                    } else {
                        let reason = format!("DTLS certificate fingerprint {actual} does not match signaling");
                        self.fail(reason, events);
                    }
                }
                Output::KeyingMaterial(material, profile) => {
                    if !self.cert_ok || self.done {
                        continue;
                    }
                    let profile = match profile {
                        dimpl::SrtpProfile::AES128_CM_SHA1_80 => SrtpProfile::AesCm128HmacSha1_80,
                        dimpl::SrtpProfile::AEAD_AES_128_GCM => SrtpProfile::AeadAes128Gcm,
                        dimpl::SrtpProfile::AEAD_AES_256_GCM => SrtpProfile::AeadAes256Gcm,
                        other => {
                            self.fail(format!("unsupported SRTP profile {other}"), events);
                            continue;
                        }
                    };
                    match SrtpContext::from_dtls(profile, &material, self.role == DtlsRole::Client) {
                        Ok((outbound, inbound)) => {
                            self.done = true;
                            events.push(DtlsEvent::Keys(outbound, inbound, profile));
                        }
                        Err(e) => self.fail(format!("DTLS-SRTP keys: {e:?}"), events),
                    }
                }
                Output::Connected => events.push(DtlsEvent::Connected),
                Output::ApplicationData(data) => events.push(DtlsEvent::Data(data.to_vec())),
                Output::CloseNotify => self.fail("DTLS closed by peer".into(), events),
                _ => {}
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pump(a: &mut DtlsTransport, b: &mut DtlsTransport, mut pending: Vec<DtlsEvent>) -> (Vec<DtlsEvent>, Vec<DtlsEvent>) {
        let (mut done_a, mut done_b) = (Vec::new(), Vec::new());
        let mut from_a = true;
        for _ in 0..40 {
            let mut next = Vec::new();
            for e in pending {
                match e {
                    DtlsEvent::Send(p) if from_a => b.handle_packet(&p, &mut next),
                    DtlsEvent::Send(p) => a.handle_packet(&p, &mut next),
                    other if from_a => done_a.push(other),
                    other => done_b.push(other),
                }
            }
            pending = next;
            from_a = !from_a;
            if pending.is_empty() {
                break;
            }
        }
        (done_a, done_b)
    }

    #[test]
    fn handshake_exports_matching_srtp_keys() {
        let (ia, ib) = (DtlsIdentity::generate().unwrap(), DtlsIdentity::generate().unwrap());
        let now = Instant::now();
        let mut client = DtlsTransport::new(&ia, DtlsRole::Client, &format!("sha-256 {}", ib.fingerprint()), now).unwrap();
        let mut server = DtlsTransport::new(&ib, DtlsRole::Server, ia.fingerprint(), now).unwrap();
        let mut start = Vec::new();
        client.start(now, &mut start);
        server.start(now, &mut Vec::new());
        let (ca, cb) = pump(&mut client, &mut server, start);
        let mut keys: Vec<_> = ca.into_iter().chain(cb).filter_map(|e| if let DtlsEvent::Keys(o, i, p) = e { Some((o, i, p)) } else { None }).collect();
        assert_eq!(keys.len(), 2, "both sides derive keys");
        let (mut client_out, _, profile) = keys.remove(0);
        let (_, mut server_in, _) = keys.remove(0);
        let mut p = vec![0x80, 0, 0, 1, 0, 0, 0, 1, 1, 2, 3, 4, 9, 9, 9];
        client_out.protect_rtp(&mut p).unwrap();
        server_in.unprotect_rtp(&mut p).unwrap();
        assert_eq!(&p[12..], &[9, 9, 9], "{profile:?}");
    }

    #[test]
    fn fingerprint_mismatch_fails() {
        let (ia, ib) = (DtlsIdentity::generate().unwrap(), DtlsIdentity::generate().unwrap());
        let now = Instant::now();
        let mut client = DtlsTransport::new(&ia, DtlsRole::Client, "sha-256 00:11:22", now).unwrap();
        let mut server = DtlsTransport::new(&ib, DtlsRole::Server, ia.fingerprint(), now).unwrap();
        let mut start = Vec::new();
        client.start(now, &mut start);
        server.start(now, &mut Vec::new());
        let (ca, cb) = pump(&mut client, &mut server, start);
        assert!(ca.iter().any(|e| matches!(e, DtlsEvent::Failed(_))));
        assert!(!ca.iter().any(|e| matches!(e, DtlsEvent::Keys(..))));
        let _ = cb;
    }
}
