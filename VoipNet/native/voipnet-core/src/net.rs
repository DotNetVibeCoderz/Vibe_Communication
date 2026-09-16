//! Network helpers.

use std::net::{IpAddr, Ipv4Addr, SocketAddr, ToSocketAddrs, UdpSocket};

/// Determines the local interface address used for the default route.
/// No packets are sent: connecting a UDP socket only selects a route.
pub fn primary_local_ip() -> IpAddr {
    UdpSocket::bind("0.0.0.0:0")
        .and_then(|s| {
            s.connect("192.0.2.1:9")?;
            s.local_addr()
        })
        .map(|a| a.ip())
        .ok()
        .filter(|ip| !ip.is_unspecified())
        .unwrap_or(IpAddr::V4(Ipv4Addr::LOCALHOST))
}

/// Resolves `host:port` (A/AAAA). Prefers IPv4 when available.
pub fn resolve(host: &str, port: u16) -> Option<SocketAddr> {
    if let Ok(ip) = host.trim_matches(|c| c == '[' || c == ']').parse::<IpAddr>() {
        return Some(SocketAddr::new(ip, port));
    }
    let addrs: Vec<SocketAddr> = (host, port).to_socket_addrs().ok()?.collect();
    addrs.iter().find(|a| a.is_ipv4()).or_else(|| addrs.first()).copied()
}
