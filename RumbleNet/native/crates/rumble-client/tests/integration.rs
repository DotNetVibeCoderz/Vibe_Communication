//! End-to-end tests against the in-process mock Mumble server.

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use rumble_client::audio_core::capture::TransmitMode;
use rumble_client::{AudioMode, Client, ClientConfig, Command, Event};
use rumble_mock_server::{ECHO_BOT_SESSION, LOBBY_CHANNEL, MockServer, MockServerConfig};
use tokio::sync::mpsc;

const TIMEOUT: Duration = Duration::from_secs(8);

fn config(port: u16, name: &str) -> ClientConfig {
    ClientConfig {
        host: "127.0.0.1".into(),
        port,
        username: name.into(),
        ping_interval_ms: 300,
        ping_timeout_ms: 5_000,
        reconnect_min_delay_ms: 100,
        reconnect_max_delay_ms: 500,
        ..Default::default()
    }
}

fn connect(cfg: ClientConfig) -> (Client, mpsc::UnboundedReceiver<Event>) {
    let (tx, rx) = mpsc::unbounded_channel();
    let client = Client::connect(
        &tokio::runtime::Handle::current(),
        cfg,
        Arc::new(move |e| {
            let _ = tx.send(e);
        }),
    )
    .unwrap();
    (client, rx)
}

async fn wait_for(rx: &mut mpsc::UnboundedReceiver<Event>, what: &str, pred: impl Fn(&Event) -> bool) -> Event {
    let mut seen = Vec::new();
    let result = tokio::time::timeout(TIMEOUT, async {
        while let Some(e) = rx.recv().await {
            if pred(&e) {
                return Some(e);
            }
            seen.push(format!("{e:?}").chars().take(120).collect::<String>());
        }
        None
    })
    .await;
    match result {
        Ok(Some(e)) => e,
        _ => panic!("timed out waiting for {what}; received:\n{}", seen.join("\n")),
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn connect_sync_chat_voice_and_channels() {
    let server = MockServer::start(MockServerConfig::default()).await.unwrap();
    let (client, mut rx) = connect(config(server.port(), "tester"));

    let bot_frames = Arc::new(AtomicUsize::new(0));
    let counter = bot_frames.clone();
    client.audio().set_mode(AudioMode::Headless).unwrap();
    client.audio().set_frame_callback(Some(Arc::new(move |session, samples, _, concealed| {
        let energy: f32 = samples.iter().map(|s| s * s).sum();
        if session == ECHO_BOT_SESSION && !concealed && energy > 0.01 {
            counter.fetch_add(1, Ordering::Relaxed);
        }
    })));

    let connected = wait_for(&mut rx, "Connected", |e| matches!(e, Event::Connected { .. })).await;
    let Event::Connected { session: me, welcome_text, .. } = connected else { unreachable!() };
    assert!(welcome_text.contains("mock server"));

    let snapshot = client.snapshot();
    assert!(snapshot.synchronized);
    assert_eq!(snapshot.session, Some(me));
    assert!(snapshot.channels.iter().any(|c| c.name == "Lobby"));
    assert!(snapshot.users.iter().any(|u| u.name == "EchoBot"));
    assert!(snapshot.users.iter().any(|u| u.name == "tester"));

    // UDP becomes active after the first encrypted ping round-trip.
    wait_for(&mut rx, "UDP active", |e| matches!(e, Event::PingUpdated { udp_active: true, .. })).await;

    client.send(Command::JoinChannel { channel_id: LOBBY_CHANNEL }).unwrap();
    wait_for(&mut rx, "UserMoved", |e| {
        matches!(e, Event::UserMoved { session, to_channel_id, .. } if *session == me && *to_channel_id == LOBBY_CHANNEL)
    })
    .await;

    client
        .send(Command::SendTextMessage {
            channel_ids: vec![LOBBY_CHANNEL],
            sessions: vec![],
            tree_ids: vec![],
            message: "halo".into(),
        })
        .unwrap();
    wait_for(&mut rx, "echo text", |e| {
        matches!(e, Event::TextMessage { actor: Some(a), message, .. } if *a == ECHO_BOT_SESSION && message == "echo: halo")
    })
    .await;

    // Talk for one second; the echo bot plays it back.
    client.audio().set_transmit_mode(TransmitMode::Continuous);
    let chunk: Vec<f32> = (0..960).map(|i| (i as f32 * 440.0 * std::f32::consts::TAU / 48_000.0).sin() * 0.5).collect();
    let mut ticker = tokio::time::interval(Duration::from_millis(20));
    for _ in 0..50 {
        ticker.tick().await;
        client.audio().push_pcm(&chunk).unwrap();
    }
    client.audio().end_transmission();

    wait_for(&mut rx, "bot talking", |e| matches!(e, Event::UserTalking { session, talking: true } if *session == ECHO_BOT_SESSION)).await;
    let deadline = tokio::time::Instant::now() + TIMEOUT;
    while bot_frames.load(Ordering::Relaxed) < 20 && tokio::time::Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    assert!(server.voice_packets() > 10, "server relayed {} packets", server.voice_packets());
    assert!(bot_frames.load(Ordering::Relaxed) >= 20, "decoded echo frames: {}", bot_frames.load(Ordering::Relaxed));

    client
        .send(Command::CreateChannel {
            parent_id: 0,
            name: "Rust Room".into(),
            description: Some("made by a test".into()),
            temporary: true,
            position: None,
            max_users: None,
        })
        .unwrap();
    let added = wait_for(&mut rx, "ChannelAdded", |e| matches!(e, Event::ChannelAdded { channel } if channel.name == "Rust Room")).await;
    let Event::ChannelAdded { channel } = added else { unreachable!() };
    client.send(Command::RemoveChannel { channel_id: channel.id }).unwrap();
    wait_for(&mut rx, "ChannelRemoved", |e| matches!(e, Event::ChannelRemoved { channel_id } if *channel_id == channel.id)).await;

    client.send(Command::RequestUserStats { session: ECHO_BOT_SESSION, stats_only: false }).unwrap();
    wait_for(&mut rx, "UserStats", |e| matches!(e, Event::UserStats { .. })).await;

    client.disconnect();
    wait_for(&mut rx, "Disconnected", |e| matches!(e, Event::Disconnected { will_reconnect: false, .. })).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn reconnects_after_connection_loss() {
    let server = MockServer::start(MockServerConfig::default()).await.unwrap();
    let (client, mut rx) = connect(config(server.port(), "phoenix"));
    wait_for(&mut rx, "Connected", |e| matches!(e, Event::Connected { .. })).await;

    server.drop_all_connections();
    wait_for(&mut rx, "Disconnected (retry)", |e| matches!(e, Event::Disconnected { will_reconnect: true, .. })).await;
    wait_for(&mut rx, "Connecting attempt", |e| matches!(e, Event::Connecting { .. })).await;
    wait_for(&mut rx, "Reconnected", |e| matches!(e, Event::Connected { .. })).await;
    assert!(client.snapshot().synchronized);
    client.shutdown().await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn wrong_password_is_rejected_without_retry() {
    let server =
        MockServer::start(MockServerConfig { password: Some("secret".into()), ..Default::default() }).await.unwrap();
    let mut cfg = config(server.port(), "intruder");
    cfg.password = Some("nope".into());
    let (_client, mut rx) = connect(cfg);
    let rejected = wait_for(&mut rx, "Rejected", |e| matches!(e, Event::Rejected { .. })).await;
    assert!(matches!(rejected, Event::Rejected { ref reject_type, .. } if reject_type == "WrongServerPw"));
    wait_for(&mut rx, "Disconnected (final)", |e| matches!(e, Event::Disconnected { will_reconnect: false, .. })).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn voice_over_tcp_tunnel() {
    let server = MockServer::start(MockServerConfig::default()).await.unwrap();
    let mut cfg = config(server.port(), "tunnel");
    cfg.force_tcp_voice = true;
    let (client, mut rx) = connect(cfg);
    let frames = Arc::new(AtomicUsize::new(0));
    let f = frames.clone();
    client.audio().set_mode(AudioMode::Headless).unwrap();
    client.audio().set_frame_callback(Some(Arc::new(move |session, _, _, concealed| {
        if session == ECHO_BOT_SESSION && !concealed {
            f.fetch_add(1, Ordering::Relaxed);
        }
    })));
    wait_for(&mut rx, "Connected", |e| matches!(e, Event::Connected { .. })).await;
    client.send(Command::JoinChannel { channel_id: LOBBY_CHANNEL }).unwrap();
    wait_for(&mut rx, "UserMoved", |e| matches!(e, Event::UserMoved { .. })).await;

    client.audio().set_transmit_mode(TransmitMode::Continuous);
    let chunk = vec![0.25f32; 960];
    for _ in 0..30 {
        client.audio().push_pcm(&chunk).unwrap();
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    let deadline = tokio::time::Instant::now() + TIMEOUT;
    while frames.load(Ordering::Relaxed) < 10 && tokio::time::Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(50)).await;
    }
    assert!(frames.load(Ordering::Relaxed) >= 10);
    assert!(!client.snapshot().udp_active);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn server_query_ping() {
    let server = MockServer::start(MockServerConfig::default()).await.unwrap();
    let info = rumble_client::query::query_server("127.0.0.1", server.port(), Duration::from_secs(3)).await.unwrap();
    assert_eq!(info.version, "1.5.0");
    assert_eq!(info.max_users, 100);
}
