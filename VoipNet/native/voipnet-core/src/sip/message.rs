//! SIP message model, parser and serializer (RFC 3261 §7).

use std::fmt::{self, Write as _};

use super::uri::{parse_params, NameAddr, SipUri};

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub enum Method {
    Register,
    Invite,
    Ack,
    Bye,
    Cancel,
    Options,
    Refer,
    Notify,
    Info,
    Message,
    Update,
    Subscribe,
    /// Acknowledges a reliable provisional response (RFC 3262).
    Prack,
    Other(String),
}

impl Method {
    pub fn parse(s: &str) -> Self {
        match s {
            "REGISTER" => Self::Register,
            "INVITE" => Self::Invite,
            "ACK" => Self::Ack,
            "BYE" => Self::Bye,
            "CANCEL" => Self::Cancel,
            "OPTIONS" => Self::Options,
            "REFER" => Self::Refer,
            "NOTIFY" => Self::Notify,
            "INFO" => Self::Info,
            "MESSAGE" => Self::Message,
            "UPDATE" => Self::Update,
            "SUBSCRIBE" => Self::Subscribe,
            "PRACK" => Self::Prack,
            other => Self::Other(other.to_owned()),
        }
    }

    pub fn as_str(&self) -> &str {
        match self {
            Self::Register => "REGISTER",
            Self::Invite => "INVITE",
            Self::Ack => "ACK",
            Self::Bye => "BYE",
            Self::Cancel => "CANCEL",
            Self::Options => "OPTIONS",
            Self::Refer => "REFER",
            Self::Notify => "NOTIFY",
            Self::Info => "INFO",
            Self::Message => "MESSAGE",
            Self::Update => "UPDATE",
            Self::Subscribe => "SUBSCRIBE",
            Self::Prack => "PRACK",
            Self::Other(s) => s,
        }
    }
}

impl fmt::Display for Method {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StartLine {
    Request { method: Method, uri: String },
    Response { code: u16, reason: String },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SipMessage {
    pub start: StartLine,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ParseError {
    Incomplete,
    InvalidStartLine,
    InvalidHeader,
}

impl fmt::Display for ParseError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}

impl std::error::Error for ParseError {}

pub const SIP_VERSION: &str = "SIP/2.0";

impl SipMessage {
    pub fn request(method: Method, uri: impl Into<String>) -> Self {
        Self { start: StartLine::Request { method, uri: uri.into() }, headers: Vec::with_capacity(12), body: Vec::new() }
    }

    pub fn response(code: u16, reason: Option<&str>) -> Self {
        let reason = reason.unwrap_or_else(|| reason_phrase(code)).to_owned();
        Self { start: StartLine::Response { code, reason }, headers: Vec::with_capacity(10), body: Vec::new() }
    }

    /// Parses a single message from a datagram or stream buffer.
    /// Returns the message and the number of bytes consumed.
    pub fn parse(data: &[u8]) -> Result<(Self, usize), ParseError> {
        let (head_end, sep_len) = find_header_end(data).ok_or(ParseError::Incomplete)?;
        let head = std::str::from_utf8(&data[..head_end]).map_err(|_| ParseError::InvalidHeader)?;
        let mut lines = head.split('\n').map(|l| l.strip_suffix('\r').unwrap_or(l));

        // Skip leading keep-alive CRLFs.
        let first = lines.by_ref().find(|l| !l.is_empty()).ok_or(ParseError::InvalidStartLine)?;
        let start = parse_start_line(first)?;

        let mut headers: Vec<(String, String)> = Vec::with_capacity(16);
        for line in lines {
            if line.starts_with(' ') || line.starts_with('\t') {
                // Header folding (obsolete but still seen in the wild).
                let (_, v) = headers.last_mut().ok_or(ParseError::InvalidHeader)?;
                v.push(' ');
                v.push_str(line.trim());
                continue;
            }
            if line.is_empty() {
                continue;
            }
            let (name, value) = line.split_once(':').ok_or(ParseError::InvalidHeader)?;
            headers.push((expand_compact(name.trim()).to_owned(), value.trim().to_owned()));
        }

        let body_start = head_end + sep_len;
        let content_length = headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case("Content-Length"))
            .and_then(|(_, v)| v.parse::<usize>().ok());
        let body_len = content_length.unwrap_or(data.len() - body_start);
        if data.len() < body_start + body_len {
            return Err(ParseError::Incomplete);
        }
        let body = data[body_start..body_start + body_len].to_vec();
        Ok((Self { start, headers, body }, body_start + body_len))
    }

    pub fn to_bytes(&self) -> Vec<u8> {
        let mut out = String::with_capacity(512 + self.body.len());
        match &self.start {
            StartLine::Request { method, uri } => {
                let _ = write!(out, "{method} {uri} {SIP_VERSION}\r\n");
            }
            StartLine::Response { code, reason } => {
                let _ = write!(out, "{SIP_VERSION} {code} {reason}\r\n");
            }
        }
        for (k, v) in &self.headers {
            if k.eq_ignore_ascii_case("Content-Length") {
                continue;
            }
            let _ = write!(out, "{k}: {v}\r\n");
        }
        let _ = write!(out, "Content-Length: {}\r\n\r\n", self.body.len());
        let mut bytes = out.into_bytes();
        bytes.extend_from_slice(&self.body);
        bytes
    }

    pub fn is_request(&self) -> bool {
        matches!(self.start, StartLine::Request { .. })
    }

    pub fn method(&self) -> Option<&Method> {
        match &self.start {
            StartLine::Request { method, .. } => Some(method),
            StartLine::Response { .. } => None,
        }
    }

    pub fn request_uri(&self) -> Option<&str> {
        match &self.start {
            StartLine::Request { uri, .. } => Some(uri),
            StartLine::Response { .. } => None,
        }
    }

    pub fn status(&self) -> Option<u16> {
        match &self.start {
            StartLine::Response { code, .. } => Some(*code),
            StartLine::Request { .. } => None,
        }
    }

    pub fn reason(&self) -> Option<&str> {
        match &self.start {
            StartLine::Response { reason, .. } => Some(reason),
            StartLine::Request { .. } => None,
        }
    }

    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }

    pub fn headers_named<'a>(&'a self, name: &'a str) -> impl Iterator<Item = &'a str> + 'a {
        self.headers.iter().filter(move |(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }

    pub fn add_header(&mut self, name: &str, value: impl Into<String>) -> &mut Self {
        self.headers.push((name.to_owned(), value.into()));
        self
    }

    pub fn set_header(&mut self, name: &str, value: impl Into<String>) -> &mut Self {
        let value = value.into();
        if let Some(slot) = self.headers.iter_mut().find(|(k, _)| k.eq_ignore_ascii_case(name)) {
            slot.1 = value;
        } else {
            self.headers.push((name.to_owned(), value));
        }
        self
    }

    pub fn remove_header(&mut self, name: &str) {
        self.headers.retain(|(k, _)| !k.eq_ignore_ascii_case(name));
    }

    pub fn set_body(&mut self, content_type: &str, body: Vec<u8>) {
        self.set_header("Content-Type", content_type);
        self.body = body;
    }

    pub fn call_id(&self) -> Option<&str> {
        self.header("Call-ID")
    }

    pub fn cseq(&self) -> Option<(u32, Method)> {
        let v = self.header("CSeq")?;
        let (n, m) = v.split_once(char::is_whitespace)?;
        Some((n.trim().parse().ok()?, Method::parse(m.trim())))
    }

    pub fn from(&self) -> Option<NameAddr> {
        self.header("From").and_then(NameAddr::parse)
    }

    pub fn to(&self) -> Option<NameAddr> {
        self.header("To").and_then(NameAddr::parse)
    }

    pub fn contact(&self) -> Option<NameAddr> {
        self.header("Contact").and_then(|c| NameAddr::parse(split_list(c).next()?))
    }

    /// Top-most Via header branch parameter.
    pub fn via_branch(&self) -> Option<String> {
        let via = split_list(self.header("Via")?).next()?.to_owned();
        let (_, params) = via.split_once(';')?;
        parse_params(params)
            .into_iter()
            .find(|(k, _)| k.eq_ignore_ascii_case("branch"))
            .and_then(|(_, v)| v)
    }

    /// Transaction key (branch + method, with ACK/CANCEL mapped for server matching).
    pub fn transaction_key(&self) -> Option<String> {
        let branch = self.via_branch()?;
        let (_, method) = self.cseq()?;
        Some(format!("{branch}|{method}"))
    }

    pub fn record_routes(&self) -> Vec<String> {
        self.headers_named("Record-Route").flat_map(split_list).map(str::to_owned).collect()
    }

    pub fn expires(&self) -> Option<u32> {
        self.header("Expires").and_then(|v| v.trim().parse().ok())
    }

    pub fn body_str(&self) -> &str {
        std::str::from_utf8(&self.body).unwrap_or("")
    }

    pub fn content_type(&self) -> Option<&str> {
        self.header("Content-Type")
    }
}

impl fmt::Display for SipMessage {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&String::from_utf8_lossy(&self.to_bytes()))
    }
}

/// Splits a comma separated header value while respecting quotes and angle brackets.
pub fn split_list(value: &str) -> impl Iterator<Item = &str> {
    let mut parts = Vec::new();
    let (mut depth, mut quoted, mut start) = (0i32, false, 0usize);
    for (i, c) in value.char_indices() {
        match c {
            '"' => quoted = !quoted,
            '<' if !quoted => depth += 1,
            '>' if !quoted => depth -= 1,
            ',' if !quoted && depth == 0 => {
                parts.push(value[start..i].trim());
                start = i + 1;
            }
            _ => {}
        }
    }
    parts.push(value[start..].trim());
    parts.into_iter().filter(|p| !p.is_empty())
}

fn find_header_end(data: &[u8]) -> Option<(usize, usize)> {
    if let Some(i) = data.windows(4).position(|w| w == b"\r\n\r\n") {
        return Some((i, 4));
    }
    data.windows(2).position(|w| w == b"\n\n").map(|i| (i, 2))
}

fn parse_start_line(line: &str) -> Result<StartLine, ParseError> {
    if let Some(rest) = line.strip_prefix(SIP_VERSION) {
        let rest = rest.trim_start();
        let (code, reason) = rest.split_once(' ').unwrap_or((rest, ""));
        let code = code.parse::<u16>().map_err(|_| ParseError::InvalidStartLine)?;
        if !(100..700).contains(&code) {
            return Err(ParseError::InvalidStartLine);
        }
        return Ok(StartLine::Response { code, reason: reason.to_owned() });
    }
    let mut it = line.split(' ');
    let method = it.next().filter(|m| !m.is_empty()).ok_or(ParseError::InvalidStartLine)?;
    let uri = it.next().ok_or(ParseError::InvalidStartLine)?;
    match it.next() {
        Some(v) if v == SIP_VERSION => {}
        _ => return Err(ParseError::InvalidStartLine),
    }
    Ok(StartLine::Request { method: Method::parse(method), uri: uri.to_owned() })
}

fn expand_compact(name: &str) -> &str {
    if name.len() != 1 {
        return name;
    }
    match name.as_bytes()[0].to_ascii_lowercase() {
        b'i' => "Call-ID",
        b'm' => "Contact",
        b'e' => "Content-Encoding",
        b'l' => "Content-Length",
        b'c' => "Content-Type",
        b'f' => "From",
        b's' => "Subject",
        b'k' => "Supported",
        b't' => "To",
        b'v' => "Via",
        b'o' => "Event",
        b'r' => "Refer-To",
        b'b' => "Referred-By",
        b'x' => "Session-Expires",
        _ => name,
    }
}

pub fn reason_phrase(code: u16) -> &'static str {
    match code {
        100 => "Trying",
        180 => "Ringing",
        181 => "Call Is Being Forwarded",
        182 => "Queued",
        183 => "Session Progress",
        200 => "OK",
        202 => "Accepted",
        301 => "Moved Permanently",
        302 => "Moved Temporarily",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        407 => "Proxy Authentication Required",
        408 => "Request Timeout",
        415 => "Unsupported Media Type",
        480 => "Temporarily Unavailable",
        481 => "Call/Transaction Does Not Exist",
        486 => "Busy Here",
        487 => "Request Terminated",
        488 => "Not Acceptable Here",
        491 => "Request Pending",
        500 => "Server Internal Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        504 => "Server Time-out",
        600 => "Busy Everywhere",
        603 => "Decline",
        _ => "Unknown",
    }
}

/// Generates a random token suitable for tags, branches and Call-IDs.
pub fn random_token(len: usize) -> String {
    use rand::distributions::Alphanumeric;
    use rand::Rng;
    rand::thread_rng().sample_iter(&Alphanumeric).take(len).map(char::from).collect()
}

pub fn new_branch() -> String {
    format!("z9hG4bK{}", random_token(16))
}

#[allow(dead_code)]
pub(crate) fn uri_of(value: &str) -> Option<SipUri> {
    NameAddr::parse(value).map(|n| n.uri).or_else(|| SipUri::parse(value))
}

#[cfg(test)]
mod tests {
    use super::*;

    const INVITE: &str = "INVITE sip:bob@biloxi.com SIP/2.0\r\n\
Via: SIP/2.0/UDP pc33.atlanta.com;branch=z9hG4bK776asdhds\r\n\
Max-Forwards: 70\r\n\
To: Bob <sip:bob@biloxi.com>\r\n\
f: Alice <sip:alice@atlanta.com>;tag=1928301774\r\n\
i: a84b4c76e66710@pc33.atlanta.com\r\n\
CSeq: 314159 INVITE\r\n\
Contact: <sip:alice@pc33.atlanta.com>\r\n\
Content-Type: application/sdp\r\n\
Content-Length: 4\r\n\r\nv=0\n";

    #[test]
    fn parses_request_with_compact_headers() {
        let (msg, used) = SipMessage::parse(INVITE.as_bytes()).unwrap();
        assert_eq!(used, INVITE.len());
        assert_eq!(msg.method(), Some(&Method::Invite));
        assert_eq!(msg.call_id(), Some("a84b4c76e66710@pc33.atlanta.com"));
        assert_eq!(msg.cseq(), Some((314159, Method::Invite)));
        assert_eq!(msg.from().unwrap().tag(), Some("1928301774"));
        assert_eq!(msg.via_branch().as_deref(), Some("z9hG4bK776asdhds"));
        assert_eq!(msg.body_str(), "v=0\n");
    }

    #[test]
    fn roundtrips_response() {
        let mut r = SipMessage::response(486, None);
        r.add_header("Call-ID", "abc").add_header("CSeq", "1 INVITE");
        let bytes = r.to_bytes();
        let (parsed, _) = SipMessage::parse(&bytes).unwrap();
        assert_eq!(parsed.status(), Some(486));
        assert_eq!(parsed.reason(), Some("Busy Here"));
        assert_eq!(parsed.header("content-length"), Some("0"));
    }

    #[test]
    fn detects_incomplete_body() {
        let truncated = &INVITE.as_bytes()[..INVITE.len() - 2];
        assert_eq!(SipMessage::parse(truncated).unwrap_err(), ParseError::Incomplete);
    }

    #[test]
    fn splits_lists_respecting_quotes() {
        let items: Vec<_> = split_list("\"Doe, John\" <sip:a@b>, <sip:c@d;x=1,2>").collect();
        assert_eq!(items, vec!["\"Doe, John\" <sip:a@b>", "<sip:c@d;x=1,2>"]);
    }
}
