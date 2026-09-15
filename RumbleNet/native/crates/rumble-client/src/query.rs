//! Unconnected server queries (the "server browser" ping).

use std::time::{Duration, Instant};

use serde::Serialize;
use tokio::net::UdpSocket;

use rumble_protocol::voice::{decode_server_info_response, encode_server_info_request};

use crate::{ClientError, Result};

/// Result of [`query_server`].
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerQueryResult {
    pub version: String,
    pub users: u32,
    pub max_users: u32,
    pub max_bandwidth: u32,
    pub ping_ms: f32,
}

/// Sends a legacy UDP info ping to a Mumble server and waits for the reply.
pub async fn query_server(host: &str, port: u16, timeout: Duration) -> Result<ServerQueryResult> {
    let addr = tokio::net::lookup_host((host, port))
        .await?
        .next()
        .ok_or_else(|| ClientError::Resolve(host.to_string()))?;
    let socket = UdpSocket::bind(if addr.is_ipv4() { "0.0.0.0:0" } else { "[::]:0" }).await?;
    socket.connect(addr).await?;

    let ident: u64 = rand::random();
    let start = Instant::now();
    socket.send(&encode_server_info_request(ident)).await?;

    let mut buf = [0u8; 64];
    tokio::time::timeout(timeout, async {
        loop {
            let n = socket.recv(&mut buf).await?;
            if let Ok(info) = decode_server_info_response(&buf[..n]) {
                if info.ident == ident {
                    return Ok(ServerQueryResult {
                        version: info.version.to_string(),
                        users: info.users,
                        max_users: info.max_users,
                        max_bandwidth: info.max_bandwidth,
                        ping_ms: start.elapsed().as_secs_f32() * 1000.0,
                    });
                }
            }
        }
    })
    .await?
}
