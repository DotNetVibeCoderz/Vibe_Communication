//! Session Description Protocol (RFC 4566) and offer/answer negotiation (RFC 3264).

use std::fmt::Write as _;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Direction {
    #[default]
    SendRecv,
    SendOnly,
    RecvOnly,
    Inactive,
}

impl Direction {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::SendRecv => "sendrecv",
            Self::SendOnly => "sendonly",
            Self::RecvOnly => "recvonly",
            Self::Inactive => "inactive",
        }
    }

    /// The direction the answerer must use for a given offered direction.
    pub fn reversed(self) -> Self {
        match self {
            Self::SendOnly => Self::RecvOnly,
            Self::RecvOnly => Self::SendOnly,
            d => d,
        }
    }

    pub fn can_send(self) -> bool {
        matches!(self, Self::SendRecv | Self::SendOnly)
    }

    pub fn can_receive(self) -> bool {
        matches!(self, Self::SendRecv | Self::RecvOnly)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RtpMap {
    pub payload_type: u8,
    pub encoding: String,
    pub clock_rate: u32,
    pub channels: u8,
    pub fmtp: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CryptoAttr {
    pub tag: u32,
    pub suite: String,
    pub key_params: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MediaDescription {
    pub media: String,
    pub port: u16,
    pub protocol: String,
    pub formats: Vec<RtpMap>,
    pub connection: Option<String>,
    pub direction: Direction,
    pub rtcp_mux: bool,
    pub crypto: Vec<CryptoAttr>,
    pub ice_ufrag: Option<String>,
    pub ice_pwd: Option<String>,
    pub candidates: Vec<String>,
    pub fingerprint: Option<String>,
    pub setup: Option<String>,
    pub mid: Option<String>,
    pub ptime: Option<u32>,
    pub other_attributes: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct SessionDescription {
    pub origin_user: String,
    pub session_id: u64,
    pub session_version: u64,
    pub origin_address: String,
    pub session_name: String,
    pub connection: Option<String>,
    pub ice_ufrag: Option<String>,
    pub ice_pwd: Option<String>,
    pub fingerprint: Option<String>,
    /// Session-level `a=group` values (RFC 5888), e.g. `BUNDLE 0`.
    pub groups: Vec<String>,
    /// Session-level `a=ice-lite` (RFC 8445 §2.5).
    pub ice_lite: bool,
    pub media: Vec<MediaDescription>,
}

impl SessionDescription {
    pub fn parse(text: &str) -> Option<Self> {
        let mut sdp = SessionDescription::default();
        let mut current: Option<MediaDescription> = None;

        for raw in text.lines() {
            let line = raw.trim_end();
            if line.len() < 2 || line.as_bytes()[1] != b'=' {
                continue;
            }
            let (kind, value) = (line.as_bytes()[0], &line[2..]);
            match kind {
                b'o' => {
                    let f: Vec<_> = value.split_whitespace().collect();
                    if f.len() >= 6 {
                        sdp.origin_user = f[0].to_owned();
                        sdp.session_id = f[1].parse().unwrap_or(0);
                        sdp.session_version = f[2].parse().unwrap_or(0);
                        sdp.origin_address = f[5].to_owned();
                    }
                }
                b's' => sdp.session_name = value.to_owned(),
                b'c' => {
                    let addr = value.split_whitespace().nth(2).map(|a| a.split('/').next().unwrap_or(a).to_owned());
                    match current.as_mut() {
                        Some(m) => m.connection = addr,
                        None => sdp.connection = addr,
                    }
                }
                b'm' => {
                    if let Some(m) = current.take() {
                        sdp.media.push(m);
                    }
                    let f: Vec<_> = value.split_whitespace().collect();
                    if f.len() < 3 {
                        continue;
                    }
                    let mut m = MediaDescription {
                        media: f[0].to_owned(),
                        port: f[1].split('/').next().and_then(|p| p.parse().ok()).unwrap_or(0),
                        protocol: f[2].to_owned(),
                        ..Default::default()
                    };
                    for pt in f.iter().skip(3).filter_map(|p| p.parse::<u8>().ok()) {
                        m.formats.push(static_rtpmap(pt).unwrap_or(RtpMap {
                            payload_type: pt,
                            encoding: String::new(),
                            clock_rate: 8000,
                            channels: 1,
                            fmtp: None,
                        }));
                    }
                    current = Some(m);
                }
                b'a' => {
                    let (name, val) = value.split_once(':').unwrap_or((value, ""));
                    match current.as_mut() {
                        Some(m) => apply_media_attribute(m, name, val, value),
                        None => match name {
                            "ice-ufrag" => sdp.ice_ufrag = Some(val.to_owned()),
                            "ice-pwd" => sdp.ice_pwd = Some(val.to_owned()),
                            "fingerprint" => sdp.fingerprint = Some(val.to_owned()),
                            "group" => sdp.groups.push(val.to_owned()),
                            "ice-lite" => sdp.ice_lite = true,
                            _ => {}
                        },
                    }
                }
                _ => {}
            }
        }
        if let Some(m) = current {
            sdp.media.push(m);
        }
        (!sdp.media.is_empty() || !sdp.origin_address.is_empty()).then_some(sdp)
    }

    pub fn audio(&self) -> Option<&MediaDescription> {
        self.media.iter().find(|m| m.media == "audio")
    }

    pub fn video(&self) -> Option<&MediaDescription> {
        self.media.iter().find(|m| m.media == "video")
    }

    /// Remote RTP address for a media line (media-level c= wins over session-level).
    pub fn rtp_address(&self, media: &MediaDescription) -> Option<String> {
        media.connection.clone().or_else(|| self.connection.clone())
    }

    pub fn to_string_sdp(&self) -> String {
        let mut s = String::with_capacity(512);
        let ip_kind = |a: &str| if a.contains(':') { "IP6" } else { "IP4" };
        let _ = write!(s, "v=0\r\n");
        let _ = write!(
            s,
            "o={} {} {} IN {} {}\r\n",
            if self.origin_user.is_empty() { "-" } else { &self.origin_user },
            self.session_id,
            self.session_version,
            ip_kind(&self.origin_address),
            self.origin_address
        );
        let _ = write!(s, "s={}\r\n", if self.session_name.is_empty() { "Voip.NET" } else { &self.session_name });
        if let Some(c) = &self.connection {
            let _ = write!(s, "c=IN {} {c}\r\n", ip_kind(c));
        }
        let _ = write!(s, "t=0 0\r\n");
        if let Some(u) = &self.ice_ufrag {
            let _ = write!(s, "a=ice-ufrag:{u}\r\n");
        }
        if let Some(p) = &self.ice_pwd {
            let _ = write!(s, "a=ice-pwd:{p}\r\n");
        }
        if let Some(f) = &self.fingerprint {
            let _ = write!(s, "a=fingerprint:{f}\r\n");
        }
        for g in &self.groups {
            let _ = write!(s, "a=group:{g}\r\n");
        }
        for m in &self.media {
            let pts: Vec<String> = m.formats.iter().map(|f| f.payload_type.to_string()).collect();
            // A data channel line names the SCTP application rather than payload types (RFC 8841).
            let formats = match (m.media.as_str(), pts.is_empty()) {
                ("application", true) => "webrtc-datachannel".to_owned(),
                _ => pts.join(" "),
            };
            let _ = write!(s, "m={} {} {} {}\r\n", m.media, m.port, m.protocol, formats);
            if let Some(c) = &m.connection {
                let _ = write!(s, "c=IN {} {c}\r\n", ip_kind(c));
            }
            for f in &m.formats {
                if f.channels > 1 {
                    let _ = write!(s, "a=rtpmap:{} {}/{}/{}\r\n", f.payload_type, f.encoding, f.clock_rate, f.channels);
                } else {
                    let _ = write!(s, "a=rtpmap:{} {}/{}\r\n", f.payload_type, f.encoding, f.clock_rate);
                }
                if let Some(fmtp) = &f.fmtp {
                    let _ = write!(s, "a=fmtp:{} {fmtp}\r\n", f.payload_type);
                }
            }
            if let Some(p) = m.ptime {
                let _ = write!(s, "a=ptime:{p}\r\n");
            }
            if m.rtcp_mux {
                let _ = write!(s, "a=rtcp-mux\r\n");
            }
            for c in &m.crypto {
                let _ = write!(s, "a=crypto:{} {} {}\r\n", c.tag, c.suite, c.key_params);
            }
            if let Some(u) = &m.ice_ufrag {
                let _ = write!(s, "a=ice-ufrag:{u}\r\n");
            }
            if let Some(p) = &m.ice_pwd {
                let _ = write!(s, "a=ice-pwd:{p}\r\n");
            }
            for c in &m.candidates {
                let _ = write!(s, "a=candidate:{c}\r\n");
            }
            if let Some(f) = &m.fingerprint {
                let _ = write!(s, "a=fingerprint:{f}\r\n");
            }
            if let Some(setup) = &m.setup {
                let _ = write!(s, "a=setup:{setup}\r\n");
            }
            if let Some(mid) = &m.mid {
                let _ = write!(s, "a=mid:{mid}\r\n");
            }
            for a in &m.other_attributes {
                let _ = write!(s, "a={a}\r\n");
            }
            let _ = write!(s, "a={}\r\n", m.direction.as_str());
        }
        s
    }
}

fn apply_media_attribute(m: &mut MediaDescription, name: &str, val: &str, raw: &str) {
    match name {
        "rtpmap" => {
            let Some((pt, rest)) = val.split_once(' ') else { return };
            let Ok(pt) = pt.parse::<u8>() else { return };
            let mut parts = rest.split('/');
            let encoding = parts.next().unwrap_or("").to_owned();
            let clock_rate = parts.next().and_then(|r| r.parse().ok()).unwrap_or(8000);
            let channels = parts.next().and_then(|c| c.parse().ok()).unwrap_or(1);
            if let Some(f) = m.formats.iter_mut().find(|f| f.payload_type == pt) {
                f.encoding = encoding;
                f.clock_rate = clock_rate;
                f.channels = channels;
            }
        }
        "fmtp" => {
            if let Some((pt, params)) = val.split_once(' ') {
                if let Some(f) = m.formats.iter_mut().find(|f| pt.parse() == Ok(f.payload_type)) {
                    f.fmtp = Some(params.to_owned());
                }
            }
        }
        "sendrecv" => m.direction = Direction::SendRecv,
        "sendonly" => m.direction = Direction::SendOnly,
        "recvonly" => m.direction = Direction::RecvOnly,
        "inactive" => m.direction = Direction::Inactive,
        "rtcp-mux" => m.rtcp_mux = true,
        "ptime" => m.ptime = val.trim().parse().ok(),
        "crypto" => {
            let f: Vec<_> = val.split_whitespace().collect();
            if f.len() >= 3 {
                if let Ok(tag) = f[0].parse() {
                    m.crypto.push(CryptoAttr { tag, suite: f[1].to_owned(), key_params: f[2].to_owned() });
                }
            }
        }
        "ice-ufrag" => m.ice_ufrag = Some(val.to_owned()),
        "ice-pwd" => m.ice_pwd = Some(val.to_owned()),
        "candidate" => m.candidates.push(val.to_owned()),
        "fingerprint" => m.fingerprint = Some(val.to_owned()),
        "setup" => m.setup = Some(val.to_owned()),
        "mid" => m.mid = Some(val.to_owned()),
        _ => m.other_attributes.push(raw.to_owned()),
    }
    // Normalize empty encodings of static payload types after all attributes are processed.
    for f in m.formats.iter_mut().filter(|f| f.encoding.is_empty()) {
        if let Some(s) = static_rtpmap(f.payload_type) {
            *f = s;
        }
    }
}

pub fn static_rtpmap(pt: u8) -> Option<RtpMap> {
    let (enc, rate, ch) = match pt {
        0 => ("PCMU", 8000, 1),
        3 => ("GSM", 8000, 1),
        4 => ("G723", 8000, 1),
        8 => ("PCMA", 8000, 1),
        9 => ("G722", 8000, 1),
        13 => ("CN", 8000, 1),
        18 => ("G729", 8000, 1),
        _ => return None,
    };
    Some(RtpMap { payload_type: pt, encoding: enc.to_owned(), clock_rate: rate, channels: ch, fmtp: None })
}

/// Picks the first offered codec we support (offerer preference, RFC 3264 §6.1).
/// `telephone-event` is negotiated separately and returned as the second tuple value.
pub fn negotiate(offered: &[RtpMap], supported: &[RtpMap]) -> (Option<RtpMap>, Option<RtpMap>) {
    let matches = |o: &RtpMap, s: &RtpMap| {
        o.encoding.eq_ignore_ascii_case(&s.encoding) && o.clock_rate == s.clock_rate
    };
    let codec = offered
        .iter()
        .filter(|o| !o.encoding.eq_ignore_ascii_case("telephone-event"))
        .find(|o| supported.iter().any(|s| matches(o, s)))
        .cloned();
    // RFC 4733: the event clock must equal the audio clock, so prefer the event format matching the codec.
    let events = |o: &&RtpMap| o.encoding.eq_ignore_ascii_case("telephone-event") && supported.iter().any(|s| matches(o, s));
    let dtmf = offered
        .iter()
        .filter(events)
        .find(|o| codec.as_ref().is_some_and(|c| c.clock_rate == o.clock_rate))
        .or_else(|| offered.iter().find(events))
        .cloned();
    (codec, dtmf)
}

#[cfg(test)]
mod tests {
    use super::*;

    const OFFER: &str = "v=0\r\no=alice 2890844526 2890844526 IN IP4 10.0.0.1\r\ns=-\r\nc=IN IP4 10.0.0.1\r\nt=0 0\r\n\
m=audio 49170 RTP/AVP 111 0 8 101\r\na=rtpmap:111 opus/48000/2\r\na=fmtp:111 minptime=10;useinbandfec=1\r\n\
a=rtpmap:101 telephone-event/8000\r\na=fmtp:101 0-16\r\na=crypto:1 AES_CM_128_HMAC_SHA1_80 inline:WVNfX19zZW1jdGwgKCkgewkyMjA7fQp9CnVubGVz\r\n\
a=sendonly\r\nm=video 51372 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n";

    #[test]
    fn parses_offer() {
        let sdp = SessionDescription::parse(OFFER).unwrap();
        let audio = sdp.audio().unwrap();
        assert_eq!(audio.port, 49170);
        assert_eq!(audio.formats.len(), 4);
        assert_eq!(audio.formats[0].encoding, "opus");
        assert_eq!(audio.formats[0].channels, 2);
        assert_eq!(audio.formats[1].encoding, "PCMU");
        assert_eq!(audio.direction, Direction::SendOnly);
        assert_eq!(audio.crypto.len(), 1);
        assert_eq!(sdp.rtp_address(audio).as_deref(), Some("10.0.0.1"));
        assert_eq!(sdp.video().unwrap().formats[0].encoding, "H264");
    }

    #[test]
    fn negotiates_by_offerer_preference() {
        let sdp = SessionDescription::parse(OFFER).unwrap();
        let supported = vec![static_rtpmap(8).unwrap(), static_rtpmap(0).unwrap(), RtpMap {
            payload_type: 101,
            encoding: "telephone-event".into(),
            clock_rate: 8000,
            channels: 1,
            fmtp: None,
        }];
        let (codec, dtmf) = negotiate(&sdp.audio().unwrap().formats, &supported);
        assert_eq!(codec.unwrap().encoding, "PCMU");
        assert_eq!(dtmf.unwrap().payload_type, 101);
    }

    #[test]
    fn serializes_and_reparses() {
        let sdp = SessionDescription::parse(OFFER).unwrap();
        let again = SessionDescription::parse(&sdp.to_string_sdp()).unwrap();
        assert_eq!(sdp.media, again.media);
    }
}
