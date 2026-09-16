//! HTTP Digest authentication for SIP (RFC 2617 / RFC 3261 §22).

use md5::{Digest, Md5};

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DigestChallenge {
    pub realm: String,
    pub nonce: String,
    pub opaque: Option<String>,
    pub algorithm: Option<String>,
    pub qop: Option<String>,
    pub stale: bool,
}

impl DigestChallenge {
    /// Parses the value of a `WWW-Authenticate` / `Proxy-Authenticate` header.
    pub fn parse(value: &str) -> Option<Self> {
        let rest = value.trim();
        let rest = rest.get(..6).filter(|p| p.eq_ignore_ascii_case("Digest")).map(|_| &rest[6..])?;
        let mut c = DigestChallenge::default();
        for (k, v) in parse_auth_params(rest) {
            match k.to_ascii_lowercase().as_str() {
                "realm" => c.realm = v,
                "nonce" => c.nonce = v,
                "opaque" => c.opaque = Some(v),
                "algorithm" => c.algorithm = Some(v),
                "qop" => c.qop = Some(v),
                "stale" => c.stale = v.eq_ignore_ascii_case("true"),
                _ => {}
            }
        }
        (!c.nonce.is_empty()).then_some(c)
    }

    /// Builds an `Authorization` header value.
    pub fn authorize(&self, method: &str, uri: &str, username: &str, password: &str, nc: u32) -> String {
        let ha1 = md5_hex(&format!("{username}:{}:{password}", self.realm));
        let ha2 = md5_hex(&format!("{method}:{uri}"));
        let wants_auth_qop = self.qop.as_deref().is_some_and(|q| q.split(',').any(|x| x.trim() == "auth"));

        let mut header = format!(
            "Digest username=\"{username}\", realm=\"{}\", nonce=\"{}\", uri=\"{uri}\"",
            self.realm, self.nonce
        );
        if wants_auth_qop {
            let cnonce = super::message::random_token(16);
            let nc = format!("{nc:08x}");
            let response = md5_hex(&format!("{ha1}:{}:{nc}:{cnonce}:auth:{ha2}", self.nonce));
            header.push_str(&format!(", response=\"{response}\", qop=auth, nc={nc}, cnonce=\"{cnonce}\""));
        } else {
            let response = md5_hex(&format!("{ha1}:{}:{ha2}", self.nonce));
            header.push_str(&format!(", response=\"{response}\""));
        }
        header.push_str(", algorithm=MD5");
        if let Some(o) = &self.opaque {
            header.push_str(&format!(", opaque=\"{o}\""));
        }
        header
    }
}

/// Validates an `Authorization` header server-side (used by the embedded test registrar).
pub fn verify_authorization(value: &str, method: &str, password: &str) -> bool {
    let Some(rest) = value.trim().strip_prefix("Digest") else { return false };
    let params = parse_auth_params(rest);
    let get = |name: &str| params.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str());
    let (Some(user), Some(realm), Some(nonce), Some(uri), Some(response)) =
        (get("username"), get("realm"), get("nonce"), get("uri"), get("response"))
    else {
        return false;
    };
    let ha1 = md5_hex(&format!("{user}:{realm}:{password}"));
    let ha2 = md5_hex(&format!("{method}:{uri}"));
    let expected = match (get("qop"), get("nc"), get("cnonce")) {
        (Some(qop), Some(nc), Some(cnonce)) => md5_hex(&format!("{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}")),
        _ => md5_hex(&format!("{ha1}:{nonce}:{ha2}")),
    };
    expected.eq_ignore_ascii_case(response)
}

pub fn auth_username(value: &str) -> Option<String> {
    let rest = value.trim().strip_prefix("Digest")?;
    parse_auth_params(rest).into_iter().find(|(k, _)| k.eq_ignore_ascii_case("username")).map(|(_, v)| v)
}

fn parse_auth_params(s: &str) -> Vec<(String, String)> {
    let mut out = Vec::new();
    let bytes = s.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        while i < bytes.len() && (bytes[i] == b',' || bytes[i].is_ascii_whitespace()) {
            i += 1;
        }
        let key_start = i;
        while i < bytes.len() && bytes[i] != b'=' {
            i += 1;
        }
        if i >= bytes.len() {
            break;
        }
        let key = s[key_start..i].trim().to_owned();
        i += 1;
        let value = if i < bytes.len() && bytes[i] == b'"' {
            i += 1;
            let start = i;
            while i < bytes.len() && bytes[i] != b'"' {
                i += 1;
            }
            let v = s[start..i].to_owned();
            i += 1;
            v
        } else {
            let start = i;
            while i < bytes.len() && bytes[i] != b',' {
                i += 1;
            }
            s[start..i].trim().to_owned()
        };
        out.push((key, value));
    }
    out
}

fn md5_hex(input: &str) -> String {
    let digest = Md5::digest(input.as_bytes());
    let mut s = String::with_capacity(32);
    for b in digest {
        s.push_str(&format!("{b:02x}"));
    }
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rfc2617_example_response() {
        // RFC 2617 §3.5 example adapted to MD5 with qop=auth and a fixed cnonce.
        let ha1 = md5_hex("Mufasa:testrealm@host.com:Circle Of Life");
        let ha2 = md5_hex("GET:/dir/index.html");
        let resp = md5_hex(&format!("{ha1}:dcd98b7102dd2f0e8b11d0f600bfb0c093:00000001:0a4f113b:auth:{ha2}"));
        assert_eq!(resp, "6629fae49393a05397450978507c4ef1");
    }

    #[test]
    fn challenge_roundtrip_verifies() {
        let c = DigestChallenge::parse("Digest realm=\"voipnet\", nonce=\"abc123\", qop=\"auth\", algorithm=MD5").unwrap();
        assert_eq!(c.realm, "voipnet");
        let header = c.authorize("REGISTER", "sip:voipnet", "alice", "pw", 1);
        assert!(verify_authorization(&header, "REGISTER", "pw"));
        assert!(!verify_authorization(&header, "REGISTER", "wrong"));
        assert_eq!(auth_username(&header).as_deref(), Some("alice"));
    }
}
