//! SIP URI (RFC 3261 §19.1) and name-addr parsing.

use std::fmt;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SipUri {
    pub secure: bool,
    pub user: Option<String>,
    pub host: String,
    pub port: Option<u16>,
    pub params: Vec<(String, Option<String>)>,
}

impl SipUri {
    pub fn new(user: Option<&str>, host: &str, port: Option<u16>) -> Self {
        Self { secure: false, user: user.map(str::to_owned), host: host.to_owned(), port, params: Vec::new() }
    }

    pub fn parse(input: &str) -> Option<Self> {
        let s = input.trim();
        let (secure, rest) = if let Some(r) = strip_prefix_ci(s, "sips:") {
            (true, r)
        } else if let Some(r) = strip_prefix_ci(s, "sip:") {
            (false, r)
        } else if let Some(r) = strip_prefix_ci(s, "tel:") {
            // Represent tel: URIs as a user part with an empty host.
            return Some(Self { secure: false, user: Some(r.to_owned()), host: String::new(), port: None, params: Vec::new() });
        } else {
            return None;
        };

        // Strip URI headers (?a=b) – we never need them in the engine.
        let rest = rest.split('?').next().unwrap_or(rest);
        let mut parts = rest.split(';');
        let addr = parts.next()?;
        let params = parts
            .filter(|p| !p.is_empty())
            .map(|p| match p.split_once('=') {
                Some((k, v)) => (k.to_owned(), Some(v.to_owned())),
                None => (p.to_owned(), None),
            })
            .collect();

        let (user, hostport) = match addr.rsplit_once('@') {
            Some((u, h)) => {
                let user = u.split(':').next().unwrap_or(u); // drop password
                (Some(user.to_owned()), h)
            }
            None => (None, addr),
        };

        let (host, port) = split_host_port(hostport)?;
        if host.is_empty() {
            return None;
        }
        Some(Self { secure, user, host, port, params })
    }

    pub fn param(&self, name: &str) -> Option<&str> {
        self.params
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_deref().unwrap_or(""))
    }

    /// Host and port for transport resolution (defaults to 5060/5061).
    pub fn host_port(&self) -> (String, u16) {
        (self.host.clone(), self.port.unwrap_or(if self.secure { 5061 } else { 5060 }))
    }

    /// Address-of-record form without parameters.
    pub fn aor(&self) -> String {
        let mut s = String::from(if self.secure { "sips:" } else { "sip:" });
        if let Some(u) = &self.user {
            s.push_str(u);
            s.push('@');
        }
        s.push_str(&self.host);
        s
    }
}

impl fmt::Display for SipUri {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(if self.secure { "sips:" } else { "sip:" })?;
        if let Some(u) = &self.user {
            write!(f, "{u}@")?;
        }
        if self.host.contains(':') && !self.host.starts_with('[') {
            write!(f, "[{}]", self.host)?;
        } else {
            f.write_str(&self.host)?;
        }
        if let Some(p) = self.port {
            write!(f, ":{p}")?;
        }
        for (k, v) in &self.params {
            match v {
                Some(v) => write!(f, ";{k}={v}")?,
                None => write!(f, ";{k}")?,
            }
        }
        Ok(())
    }
}

/// `"Display Name" <sip:uri>;tag=abc` or `sip:uri;tag=abc`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NameAddr {
    pub display: Option<String>,
    pub uri: SipUri,
    pub params: Vec<(String, Option<String>)>,
}

impl NameAddr {
    pub fn new(uri: SipUri) -> Self {
        Self { display: None, uri, params: Vec::new() }
    }

    pub fn parse(input: &str) -> Option<Self> {
        let s = input.trim();
        if let Some(lt) = s.find('<') {
            let gt = s[lt..].find('>')? + lt;
            let display = s[..lt].trim().trim_matches('"').trim();
            let uri = SipUri::parse(&s[lt + 1..gt])?;
            let params = parse_params(&s[gt + 1..]);
            Some(Self { display: (!display.is_empty()).then(|| display.to_owned()), uri, params })
        } else {
            // Without angle brackets, parameters belong to the header, not the URI.
            let (uri_part, rest) = match s.find(';') {
                Some(i) => (&s[..i], &s[i..]),
                None => (s, ""),
            };
            let uri = SipUri::parse(uri_part)?;
            Some(Self { display: None, uri, params: parse_params(rest) })
        }
    }

    pub fn param(&self, name: &str) -> Option<&str> {
        self.params
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_deref().unwrap_or(""))
    }

    pub fn tag(&self) -> Option<&str> {
        self.param("tag")
    }

    pub fn set_param(&mut self, name: &str, value: &str) {
        self.params.retain(|(k, _)| !k.eq_ignore_ascii_case(name));
        self.params.push((name.to_owned(), Some(value.to_owned())));
    }
}

impl fmt::Display for NameAddr {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        if let Some(d) = &self.display {
            write!(f, "\"{d}\" ")?;
        }
        write!(f, "<{}>", self.uri)?;
        for (k, v) in &self.params {
            match v {
                Some(v) => write!(f, ";{k}={v}")?,
                None => write!(f, ";{k}")?,
            }
        }
        Ok(())
    }
}

pub(crate) fn parse_params(s: &str) -> Vec<(String, Option<String>)> {
    s.split(';')
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .map(|p| match p.split_once('=') {
            Some((k, v)) => (k.trim().to_owned(), Some(v.trim().trim_matches('"').to_owned())),
            None => (p.to_owned(), None),
        })
        .collect()
}

pub(crate) fn split_host_port(s: &str) -> Option<(String, Option<u16>)> {
    if let Some(rest) = s.strip_prefix('[') {
        let end = rest.find(']')?;
        let host = rest[..end].to_owned();
        let port = rest[end + 1..].strip_prefix(':').and_then(|p| p.parse().ok());
        return Some((host, port));
    }
    match s.rsplit_once(':') {
        Some((h, p)) if !h.contains(':') => Some((h.to_owned(), Some(p.parse().ok()?))),
        _ => Some((s.to_owned(), None)),
    }
}

fn strip_prefix_ci<'a>(s: &'a str, prefix: &str) -> Option<&'a str> {
    (s.len() >= prefix.len() && s[..prefix.len()].eq_ignore_ascii_case(prefix)).then(|| &s[prefix.len()..])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_full_uri() {
        let u = SipUri::parse("sip:alice:secret@example.com:5070;transport=udp;lr").unwrap();
        assert_eq!(u.user.as_deref(), Some("alice"));
        assert_eq!(u.host, "example.com");
        assert_eq!(u.port, Some(5070));
        assert_eq!(u.param("transport"), Some("udp"));
        assert_eq!(u.param("lr"), Some(""));
        assert_eq!(u.to_string(), "sip:alice@example.com:5070;transport=udp;lr");
    }

    #[test]
    fn parses_ipv6_and_sips() {
        let u = SipUri::parse("sips:bob@[2001:db8::1]:5061").unwrap();
        assert!(u.secure);
        assert_eq!(u.host, "2001:db8::1");
        assert_eq!(u.port, Some(5061));
    }

    #[test]
    fn parses_name_addr() {
        let n = NameAddr::parse("\"Alice Smith\" <sip:alice@atlanta.com>;tag=1928301774").unwrap();
        assert_eq!(n.display.as_deref(), Some("Alice Smith"));
        assert_eq!(n.uri.user.as_deref(), Some("alice"));
        assert_eq!(n.tag(), Some("1928301774"));

        let bare = NameAddr::parse("sip:bob@biloxi.com;tag=a6c85cf").unwrap();
        assert_eq!(bare.tag(), Some("a6c85cf"));
        assert!(bare.uri.params.is_empty());
    }
}
