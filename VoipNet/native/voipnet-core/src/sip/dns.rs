//! DNS client and SIP server resolution (RFC 3263).
//!
//! Just enough DNS to find a provider's servers: NAPTR to pick a transport, SRV for the hosts and
//! ports behind a domain, and A/AAAA for the addresses. Queries go to the system's resolvers over
//! UDP; anything that does not answer in time is simply left out, and the caller falls back to the
//! plain address lookup it would have done anyway.

use std::collections::HashMap;
use std::net::{IpAddr, SocketAddr, ToSocketAddrs, UdpSocket};
use std::time::{Duration, Instant};

use parking_lot::Mutex;

pub const TYPE_A: u16 = 1;
pub const TYPE_CNAME: u16 = 5;
pub const TYPE_AAAA: u16 = 28;
pub const TYPE_SRV: u16 = 33;
pub const TYPE_NAPTR: u16 = 35;

const QUERY_TIMEOUT: Duration = Duration::from_millis(1500);
/// How long answers are reused. Short enough to follow a provider moving a host, long enough that a
/// burst of calls does not re-query for every one of them.
const CACHE_TTL: Duration = Duration::from_secs(60);
/// How long an address that failed is skipped when something else is available.
const PENALTY: Duration = Duration::from_secs(60);

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SrvRecord {
    pub priority: u16,
    pub weight: u16,
    pub port: u16,
    pub target: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NaptrRecord {
    pub order: u16,
    pub preference: u16,
    pub flags: String,
    pub service: String,
    pub replacement: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Record {
    Address(IpAddr),
    Srv(SrvRecord),
    Naptr(NaptrRecord),
    Cname(String),
}

/// Builds a query for one name and type, with recursion desired.
pub fn encode_query(id: u16, name: &str, qtype: u16) -> Vec<u8> {
    let mut out = Vec::with_capacity(32 + name.len());
    out.extend_from_slice(&id.to_be_bytes());
    out.extend_from_slice(&0x0100u16.to_be_bytes()); // standard query, recursion desired
    out.extend_from_slice(&1u16.to_be_bytes()); // one question
    out.extend_from_slice(&[0, 0, 0, 0, 0, 0]); // no answers, authorities or additionals
    for label in name.trim_end_matches('.').split('.').filter(|l| !l.is_empty()) {
        let bytes = &label.as_bytes()[..label.len().min(63)];
        out.push(bytes.len() as u8);
        out.extend_from_slice(bytes);
    }
    out.push(0);
    out.extend_from_slice(&qtype.to_be_bytes());
    out.extend_from_slice(&1u16.to_be_bytes()); // class IN
    out
}

/// Reads a possibly compressed name, returning it and the offset just past it.
fn read_name(msg: &[u8], mut at: usize) -> Option<(String, usize)> {
    let mut name = String::new();
    let mut end = None;
    // A compression pointer may only point backwards, so a bounded walk cannot loop forever.
    for _ in 0..64 {
        let len = *msg.get(at)? as usize;
        match len {
            0 => {
                let past = at + 1;
                return Some((name, end.unwrap_or(past)));
            }
            _ if len & 0xC0 == 0xC0 => {
                let pointer = ((len & 0x3F) << 8) | *msg.get(at + 1)? as usize;
                if pointer >= at {
                    return None;
                }
                end = end.or(Some(at + 2));
                at = pointer;
            }
            _ => {
                let label = msg.get(at + 1..at + 1 + len)?;
                if !name.is_empty() {
                    name.push('.');
                }
                name.push_str(&String::from_utf8_lossy(label));
                at += 1 + len;
            }
        }
    }
    None
}

/// Reads a length-prefixed character string (NAPTR flags, service and regexp).
fn read_string(msg: &[u8], at: usize) -> Option<(String, usize)> {
    let len = *msg.get(at)? as usize;
    let bytes = msg.get(at + 1..at + 1 + len)?;
    Some((String::from_utf8_lossy(bytes).into_owned(), at + 1 + len))
}

/// Pulls the answer records out of a response, ignoring types this engine does not use.
pub fn decode_answers(msg: &[u8], id: u16) -> Option<Vec<Record>> {
    if msg.len() < 12 || u16::from_be_bytes([msg[0], msg[1]]) != id || msg[2] & 0x80 == 0 {
        return None;
    }
    if msg[3] & 0x0F != 0 {
        // A name error or server failure has no answers to offer.
        return Some(Vec::new());
    }
    let questions = u16::from_be_bytes([msg[4], msg[5]]);
    let answers = u16::from_be_bytes([msg[6], msg[7]]);
    let mut at = 12;
    for _ in 0..questions {
        at = read_name(msg, at)?.1 + 4;
    }
    let mut out = Vec::new();
    for _ in 0..answers {
        let (_, next) = read_name(msg, at)?;
        let rtype = u16::from_be_bytes([*msg.get(next)?, *msg.get(next + 1)?]);
        let rdlength = u16::from_be_bytes([*msg.get(next + 8)?, *msg.get(next + 9)?]) as usize;
        let rdata = next + 10;
        let body = msg.get(rdata..rdata + rdlength)?;
        match rtype {
            TYPE_A if rdlength == 4 => out.push(Record::Address(IpAddr::from([body[0], body[1], body[2], body[3]]))),
            TYPE_AAAA if rdlength == 16 => {
                let octets: [u8; 16] = body.try_into().ok()?;
                out.push(Record::Address(IpAddr::from(octets)));
            }
            TYPE_CNAME => out.push(Record::Cname(read_name(msg, rdata)?.0)),
            TYPE_SRV if rdlength >= 7 => out.push(Record::Srv(SrvRecord {
                priority: u16::from_be_bytes([body[0], body[1]]),
                weight: u16::from_be_bytes([body[2], body[3]]),
                port: u16::from_be_bytes([body[4], body[5]]),
                target: read_name(msg, rdata + 6)?.0,
            })),
            TYPE_NAPTR if rdlength >= 7 => {
                let (flags, at) = read_string(msg, rdata + 4)?;
                let (service, at) = read_string(msg, at)?;
                let (_regexp, at) = read_string(msg, at)?;
                out.push(Record::Naptr(NaptrRecord {
                    order: u16::from_be_bytes([body[0], body[1]]),
                    preference: u16::from_be_bytes([body[2], body[3]]),
                    flags,
                    service,
                    replacement: read_name(msg, at)?.0,
                }));
            }
            _ => {}
        }
        at = rdata + rdlength;
    }
    Some(out)
}

/// Asks one server and waits for its answer. Returns `None` when the query goes unanswered.
fn ask(server: SocketAddr, name: &str, qtype: u16) -> Option<Vec<Record>> {
    let socket = UdpSocket::bind(if server.is_ipv6() { "[::]:0" } else { "0.0.0.0:0" }).ok()?;
    socket.set_read_timeout(Some(QUERY_TIMEOUT)).ok()?;
    let id = rand::random::<u16>();
    socket.send_to(&encode_query(id, name, qtype), server).ok()?;
    let mut buf = [0u8; 4096];
    let deadline = Instant::now() + QUERY_TIMEOUT;
    while Instant::now() < deadline {
        let (n, from) = socket.recv_from(&mut buf).ok()?;
        if from.ip() != server.ip() {
            continue;
        }
        if let Some(records) = decode_answers(&buf[..n], id) {
            return Some(records);
        }
    }
    None
}

/// The resolvers this machine uses. Empty when they cannot be found, which turns SRV lookups off.
pub fn system_nameservers() -> Vec<IpAddr> {
    #[cfg(windows)]
    {
        windows_nameservers()
    }
    #[cfg(not(windows))]
    {
        std::fs::read_to_string("/etc/resolv.conf")
            .map(|text| {
                text.lines()
                    .filter_map(|line| line.split_whitespace().next().filter(|k| *k == "nameserver").and(line.split_whitespace().nth(1)))
                    .filter_map(|addr| addr.split('%').next().unwrap_or(addr).parse().ok())
                    .collect()
            })
            .unwrap_or_default()
    }
}

#[cfg(windows)]
fn windows_nameservers() -> Vec<IpAddr> {
    use std::net::{Ipv4Addr, Ipv6Addr};
    use windows_sys::Win32::NetworkManagement::IpHelper::{
        GetAdaptersAddresses, GAA_FLAG_SKIP_ANYCAST, GAA_FLAG_SKIP_FRIENDLY_NAME, GAA_FLAG_SKIP_MULTICAST, GAA_FLAG_SKIP_UNICAST,
        IP_ADAPTER_ADDRESSES_LH,
    };
    use windows_sys::Win32::Networking::WinSock::{AF_UNSPEC, SOCKADDR_IN, SOCKADDR_IN6, AF_INET, AF_INET6};

    let flags = GAA_FLAG_SKIP_UNICAST | GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_FRIENDLY_NAME;
    let mut size: u32 = 16 * 1024;
    let mut buffer = vec![0u8; size as usize];
    // SAFETY: the buffer is at least `size` bytes and the call only writes inside it. A resize is
    // retried once, which is what the API asks for when it reports the buffer is too small.
    let mut code = unsafe { GetAdaptersAddresses(AF_UNSPEC as u32, flags, std::ptr::null_mut(), buffer.as_mut_ptr().cast(), &mut size) };
    if code == 111 {
        buffer = vec![0u8; size as usize];
        code = unsafe { GetAdaptersAddresses(AF_UNSPEC as u32, flags, std::ptr::null_mut(), buffer.as_mut_ptr().cast(), &mut size) };
    }
    if code != 0 {
        return Vec::new();
    }

    let mut out = Vec::new();
    let mut adapter = buffer.as_ptr() as *const IP_ADAPTER_ADDRESSES_LH;
    // SAFETY: the API returns a linked list inside `buffer`, terminated by a null Next pointer.
    unsafe {
        while !adapter.is_null() {
            let mut server = (*adapter).FirstDnsServerAddress;
            while !server.is_null() {
                let sockaddr = (*server).Address.lpSockaddr;
                if !sockaddr.is_null() {
                    match (*sockaddr).sa_family {
                        f if f == AF_INET => {
                            let v4 = &*(sockaddr as *const SOCKADDR_IN);
                            out.push(IpAddr::V4(Ipv4Addr::from(u32::from_be(v4.sin_addr.S_un.S_addr))));
                        }
                        f if f == AF_INET6 => {
                            let v6 = &*(sockaddr as *const SOCKADDR_IN6);
                            out.push(IpAddr::V6(Ipv6Addr::from(v6.sin6_addr.u.Byte)));
                        }
                        _ => {}
                    }
                }
                server = (*server).Next;
            }
            adapter = (*adapter).Next;
        }
    }
    out.retain(|ip| !ip.is_unspecified() && !matches!(ip, IpAddr::V6(v6) if v6.segments()[0] == 0xfec0));
    out.dedup();
    out
}

/// The transports a SIP URI can be resolved for, named as RFC 3263 does.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DnsTransport {
    Udp,
    Tcp,
    Tls,
    /// SIP over WebSocket (RFC 7118).
    Ws,
    Wss,
}

impl DnsTransport {
    /// The SRV prefix for this transport (`_sip._udp`, `_sips._tcp`, ...).
    fn srv_prefix(self) -> &'static str {
        match self {
            Self::Udp => "_sip._udp",
            Self::Tcp => "_sip._tcp",
            Self::Tls => "_sips._tcp",
            Self::Ws => "_sip._ws",
            Self::Wss => "_sips._wss",
        }
    }

    /// The NAPTR service field that selects this transport.
    fn naptr_service(self) -> &'static str {
        match self {
            Self::Udp => "SIP+D2U",
            Self::Tcp => "SIP+D2T",
            Self::Tls => "SIPS+D2T",
            Self::Ws => "SIP+D2W",
            Self::Wss => "SIPS+D2W",
        }
    }

    /// A `sips:` URI must be reached over TLS even when the endpoint's own transport is plainer.
    pub fn max_secure(self, secure: bool) -> Self {
        match (self, secure) {
            (Self::Udp | Self::Tcp, true) => Self::Tls,
            (Self::Ws, true) => Self::Wss,
            (kind, _) => kind,
        }
    }

    pub fn default_port(self) -> u16 {
        match self {
            Self::Tls => 5061,
            Self::Ws => 80,
            Self::Wss => 443,
            _ => 5060,
        }
    }
}

/// Caches lookups and remembers which addresses just failed, so the next attempt tries another one.
pub struct Resolver {
    servers: Vec<SocketAddr>,
    cache: Mutex<HashMap<(String, u16), (Instant, Vec<Record>)>>,
    penalties: Mutex<HashMap<SocketAddr, Instant>>,
}

impl Default for Resolver {
    fn default() -> Self {
        Self::new(Vec::new())
    }
}

impl Resolver {
    /// `servers` overrides the machine's resolvers; an empty list uses those, on port 53.
    pub fn new(servers: Vec<SocketAddr>) -> Self {
        let servers = if servers.is_empty() {
            system_nameservers().into_iter().map(|ip| SocketAddr::new(ip, 53)).collect()
        } else {
            servers
        };
        Self { servers, cache: Mutex::new(HashMap::new()), penalties: Mutex::new(HashMap::new()) }
    }

    pub fn nameservers(&self) -> &[SocketAddr] {
        &self.servers
    }

    fn lookup(&self, name: &str, qtype: u16) -> Vec<Record> {
        let key = (name.to_ascii_lowercase(), qtype);
        if let Some((at, records)) = self.cache.lock().get(&key) {
            if at.elapsed() < CACHE_TTL {
                return records.clone();
            }
        }
        let mut records = Vec::new();
        for server in &self.servers {
            if let Some(answer) = ask(*server, name, qtype) {
                records = answer;
                break;
            }
        }
        let mut cache = self.cache.lock();
        if cache.len() > 256 {
            cache.clear();
        }
        cache.insert(key, (Instant::now(), records.clone()));
        records
    }

    /// Marks an address as failed so it goes last for a while.
    pub fn penalize(&self, addr: SocketAddr) {
        let mut penalties = self.penalties.lock();
        penalties.retain(|_, at| at.elapsed() < PENALTY);
        penalties.insert(addr, Instant::now());
    }

    /// Resolves a SIP host into the addresses to try, best first (RFC 3263 §4). An explicit port or a
    /// numeric host short-circuits the lookups, exactly as the RFC requires.
    pub fn resolve(&self, host: &str, port: Option<u16>, transport: DnsTransport) -> Vec<SocketAddr> {
        let default_port = transport.default_port();
        if let Ok(ip) = host.parse::<IpAddr>() {
            return vec![SocketAddr::new(ip, port.unwrap_or(default_port))];
        }
        let mut addresses = Vec::new();
        if port.is_none() && !self.servers.is_empty() {
            for srv in self.service_records(host, transport) {
                let targets = self.addresses_of(&srv.target);
                addresses.extend(targets.into_iter().map(|ip| SocketAddr::new(ip, srv.port)));
            }
        }
        if addresses.is_empty() {
            // No SRV records (or no resolvers): the host itself, with the port we were given.
            let wanted = port.unwrap_or(default_port);
            addresses = self.addresses_of(host).into_iter().map(|ip| SocketAddr::new(ip, wanted)).collect();
            if addresses.is_empty() {
                addresses = (host, wanted).to_socket_addrs().map(|it| it.collect()).unwrap_or_default();
            }
        }
        addresses.dedup();
        // Anything that failed recently goes last, but is still worth a try if nothing else answers.
        let penalties = self.penalties.lock();
        addresses.sort_by_key(|addr| penalties.get(addr).is_some_and(|at| at.elapsed() < PENALTY));
        addresses
    }

    /// NAPTR first (which names the SRV record to use), then SRV for the transport, in RFC order.
    fn service_records(&self, host: &str, transport: DnsTransport) -> Vec<SrvRecord> {
        let mut naptrs: Vec<NaptrRecord> = self
            .lookup(host, TYPE_NAPTR)
            .into_iter()
            .filter_map(|r| match r {
                Record::Naptr(n) if n.service.eq_ignore_ascii_case(transport.naptr_service()) => Some(n),
                _ => None,
            })
            .collect();
        naptrs.sort_by_key(|n| (n.order, n.preference));

        let names: Vec<String> = if naptrs.is_empty() {
            vec![format!("{}.{host}", transport.srv_prefix())]
        } else {
            naptrs.into_iter().map(|n| n.replacement).collect()
        };
        let mut srvs: Vec<SrvRecord> = names
            .iter()
            .flat_map(|name| self.lookup(name, TYPE_SRV))
            .filter_map(|r| match r {
                // "." as a target means the service is explicitly not offered here (RFC 2782).
                Record::Srv(s) if !s.target.is_empty() && s.port != 0 => Some(s),
                _ => None,
            })
            .collect();
        // Lowest priority first; within a priority, heavier weights come first so load lands where the
        // operator wants it.
        srvs.sort_by_key(|s| (s.priority, u16::MAX - s.weight));
        srvs
    }

    /// A and AAAA for a name, following one CNAME if that is all we get.
    fn addresses_of(&self, name: &str) -> Vec<IpAddr> {
        let mut records = self.lookup(name, TYPE_A);
        records.extend(self.lookup(name, TYPE_AAAA));
        let mut out: Vec<IpAddr> = records
            .iter()
            .filter_map(|r| if let Record::Address(ip) = r { Some(*ip) } else { None })
            .collect();
        if out.is_empty() {
            if let Some(Record::Cname(alias)) = records.iter().find(|r| matches!(r, Record::Cname(_))) {
                let alias = alias.clone();
                out = self
                    .lookup(&alias, TYPE_A)
                    .into_iter()
                    .filter_map(|r| if let Record::Address(ip) = r { Some(ip) } else { None })
                    .collect();
            }
        }
        out
    }
}

/// A DNS server for tests, shared with the endpoint tests so they can exercise real resolution.
#[cfg(test)]
pub(crate) mod testing {
    use super::*;

    /// Writes a name into a message, without compression.
    pub(crate) fn name_bytes(name: &str) -> Vec<u8> {
        let mut out = Vec::new();
        for label in name.split('.').filter(|l| !l.is_empty()) {
            out.push(label.len() as u8);
            out.extend_from_slice(label.as_bytes());
        }
        out.push(0);
        out
    }

    pub(crate) fn srv_rdata(priority: u16, weight: u16, port: u16, target: &str) -> Vec<u8> {
        let mut out = Vec::new();
        out.extend_from_slice(&priority.to_be_bytes());
        out.extend_from_slice(&weight.to_be_bytes());
        out.extend_from_slice(&port.to_be_bytes());
        out.extend_from_slice(&name_bytes(target));
        out
    }

    pub(crate) fn naptr_rdata(order: u16, preference: u16, service: &str, replacement: &str) -> Vec<u8> {
        let mut out = Vec::new();
        out.extend_from_slice(&order.to_be_bytes());
        out.extend_from_slice(&preference.to_be_bytes());
        out.push(1);
        out.extend_from_slice(b"s");
        out.push(service.len() as u8);
        out.extend_from_slice(service.as_bytes());
        out.push(0); // empty regexp
        out.extend_from_slice(&name_bytes(replacement));
        out
    }

    /// Builds a response echoing the query's id and question.
    pub(crate) fn response(query: &[u8], answers: &[(&str, u16, Vec<u8>)]) -> Vec<u8> {
        let mut out = query[..2].to_vec();
        out.extend_from_slice(&0x8180u16.to_be_bytes()); // response, recursion available
        out.extend_from_slice(&1u16.to_be_bytes());
        out.extend_from_slice(&(answers.len() as u16).to_be_bytes());
        out.extend_from_slice(&[0, 0, 0, 0]);
        out.extend_from_slice(&query[12..]); // echo the question
        for (name, rtype, rdata) in answers {
            out.extend_from_slice(&name_bytes(name));
            out.extend_from_slice(&rtype.to_be_bytes());
            out.extend_from_slice(&1u16.to_be_bytes());
            out.extend_from_slice(&60u32.to_be_bytes());
            out.extend_from_slice(&(rdata.len() as u16).to_be_bytes());
            out.extend_from_slice(rdata);
        }
        out
    }

    /// Answers from a fixed table on a loopback port, which the test hands to a [`Resolver`].
    pub(crate) fn start_server(table: Vec<(String, u16, Vec<u8>)>) -> u16 {
        let socket = UdpSocket::bind("127.0.0.1:0").expect("bind");
        let port = socket.local_addr().unwrap().port();
        std::thread::spawn(move || {
            let mut buf = [0u8; 2048];
            while let Ok((n, from)) = socket.recv_from(&mut buf) {
                let query = &buf[..n];
                let Some((name, at)) = read_name(query, 12) else { continue };
                let qtype = u16::from_be_bytes([query[at], query[at + 1]]);
                let answers: Vec<(&str, u16, Vec<u8>)> = table
                    .iter()
                    .filter(|(n, t, _)| n.eq_ignore_ascii_case(&name) && *t == qtype)
                    .map(|(n, t, d)| (n.as_str(), *t, d.clone()))
                    .collect();
                let _ = socket.send_to(&response(query, &answers), from);
            }
        });
        port
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    use super::testing::{naptr_rdata, response, srv_rdata, start_server};

    /// A resolver pointed at a DNS server that answers from a fixed table.
    fn with_server(table: Vec<(String, u16, Vec<u8>)>) -> Resolver {
        Resolver::new(vec![SocketAddr::from(([127, 0, 0, 1], start_server(table)))])
    }

    #[test]
    fn srv_records_are_tried_by_priority_then_weight() {
        let resolver = with_server(vec![
            ("_sip._udp.example.com".into(), TYPE_SRV, srv_rdata(20, 0, 5060, "backup.example.com")),
            ("_sip._udp.example.com".into(), TYPE_SRV, srv_rdata(10, 10, 5080, "light.example.com")),
            ("_sip._udp.example.com".into(), TYPE_SRV, srv_rdata(10, 90, 5070, "heavy.example.com")),
            ("heavy.example.com".into(), TYPE_A, vec![203, 0, 113, 1]),
            ("light.example.com".into(), TYPE_A, vec![203, 0, 113, 2]),
            ("backup.example.com".into(), TYPE_A, vec![203, 0, 113, 3]),
        ]);
        let addresses = resolver.resolve("example.com", None, DnsTransport::Udp);
        assert_eq!(
            addresses,
            vec![
                SocketAddr::from(([203, 0, 113, 1], 5070)),
                SocketAddr::from(([203, 0, 113, 2], 5080)),
                SocketAddr::from(([203, 0, 113, 3], 5060)),
            ]
        );

        // The address that just failed goes last, but stays on the list.
        resolver.penalize(SocketAddr::from(([203, 0, 113, 1], 5070)));
        let after = resolver.resolve("example.com", None, DnsTransport::Udp);
        assert_eq!(after.last(), Some(&SocketAddr::from(([203, 0, 113, 1], 5070))));
        assert_eq!(after.len(), 3);
    }

    #[test]
    fn naptr_picks_the_srv_record_for_the_transport() {
        let resolver = with_server(vec![
            ("example.com".into(), TYPE_NAPTR, naptr_rdata(10, 10, "SIPS+D2T", "_sips._tcp.secure.example.com")),
            ("example.com".into(), TYPE_NAPTR, naptr_rdata(20, 10, "SIP+D2U", "_sip._udp.plain.example.com")),
            ("_sips._tcp.secure.example.com".into(), TYPE_SRV, srv_rdata(10, 10, 5061, "tls.example.com")),
            ("_sip._udp.plain.example.com".into(), TYPE_SRV, srv_rdata(10, 10, 5060, "udp.example.com")),
            ("tls.example.com".into(), TYPE_A, vec![198, 51, 100, 1]),
            ("udp.example.com".into(), TYPE_A, vec![198, 51, 100, 2]),
        ]);
        assert_eq!(
            resolver.resolve("example.com", None, DnsTransport::Tls),
            vec![SocketAddr::from(([198, 51, 100, 1], 5061))]
        );
        assert_eq!(
            resolver.resolve("example.com", None, DnsTransport::Udp),
            vec![SocketAddr::from(([198, 51, 100, 2], 5060))]
        );
    }

    #[test]
    fn an_explicit_port_or_address_skips_the_lookups() {
        let resolver = with_server(vec![
            ("_sip._udp.example.com".into(), TYPE_SRV, srv_rdata(10, 10, 5070, "srv.example.com")),
            ("srv.example.com".into(), TYPE_A, vec![203, 0, 113, 9]),
            ("example.com".into(), TYPE_A, vec![203, 0, 113, 8]),
        ]);
        // A port in the URI means "this host, this port" (RFC 3263 4.2).
        assert_eq!(
            resolver.resolve("example.com", Some(5062), DnsTransport::Udp),
            vec![SocketAddr::from(([203, 0, 113, 8], 5062))]
        );
        // A numeric host needs no DNS at all.
        assert_eq!(
            resolver.resolve("192.0.2.5", None, DnsTransport::Tls),
            vec![SocketAddr::from(([192, 0, 2, 5], 5061))]
        );
    }

    #[test]
    fn a_domain_without_srv_records_falls_back_to_its_address() {
        let resolver = with_server(vec![("example.com".into(), TYPE_A, vec![192, 0, 2, 10])]);
        assert_eq!(
            resolver.resolve("example.com", None, DnsTransport::Tcp),
            vec![SocketAddr::from(([192, 0, 2, 10], 5060))]
        );
        // Nothing at all: the caller gets an empty list and can fall back to its own default.
        assert!(resolver.resolve("nothing.example.com", None, DnsTransport::Udp).is_empty());
    }

    #[test]
    fn a_query_and_its_answer_survive_encoding() {
        let query = encode_query(0x1234, "sip.example.com", TYPE_A);
        let (name, at) = read_name(&query, 12).expect("question name");
        assert_eq!(name, "sip.example.com");
        assert_eq!(u16::from_be_bytes([query[at], query[at + 1]]), TYPE_A);
        let msg = response(&query, &[("sip.example.com", TYPE_A, vec![203, 0, 113, 7])]);
        let records = decode_answers(&msg, 0x1234).expect("answers");
        assert_eq!(records, vec![Record::Address(IpAddr::V4(std::net::Ipv4Addr::new(203, 0, 113, 7)))]);
        // An answer to somebody else's question is not ours to read.
        assert!(decode_answers(&msg, 0x9999).is_none());
    }

    #[test]
    fn compressed_names_are_followed() {
        let query = encode_query(1, "example.com", TYPE_SRV);
        // Point the SRV target at the question's name instead of spelling it out again.
        let mut rdata = vec![0, 10, 0, 20, 0x13, 0xC4];
        rdata.extend_from_slice(&[0xC0, 12]);
        let msg = response(&query, &[("example.com", TYPE_SRV, rdata)]);
        let records = decode_answers(&msg, 1).expect("answers");
        assert_eq!(
            records,
            vec![Record::Srv(SrvRecord { priority: 10, weight: 20, port: 5060, target: "example.com".into() })]
        );
    }
}
