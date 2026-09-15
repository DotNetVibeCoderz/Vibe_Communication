//! Standalone mock server: `rumble-mock-server [bind-address] [password]`.
//!
//! Example: `rumble-mock-server 127.0.0.1:64738`

use rumble_mock_server::{MockServer, MockServerConfig};

#[tokio::main]
async fn main() -> std::io::Result<()> {
    let mut args = std::env::args().skip(1);
    let bind = args.next().unwrap_or_else(|| "127.0.0.1:64738".into());
    let password = args.next();
    let config = MockServerConfig {
        bind: bind.parse().map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidInput, e))?,
        password,
        ..Default::default()
    };
    let server = MockServer::start(config).await?;
    println!("Rumble mock server listening on {} (TCP+UDP). Press Ctrl+C to stop.", server.addr());
    tokio::signal::ctrl_c().await?;
    Ok(())
}
