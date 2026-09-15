//! # rumble-client
//!
//! Async Mumble client built on `tokio` and `rustls`.
//!
//! * One **supervisor task** per client owns the connection lifecycle: connect → authenticate →
//!   run session → on failure back off and reconnect (configurable).
//! * The **session loop** multiplexes the TLS control channel, the encrypted UDP voice channel,
//!   heartbeats, outgoing voice and application commands in a single `select!`.
//! * Server state (channel tree, users, server configuration) is cached in [`state::ServerState`]
//!   and every change is published as an [`event::Event`].
//! * Voice is routed into the lock-free [`rumble_audio::mixer`] through [`audio::AudioSystem`].

pub mod audio;
pub mod command;
pub mod config;
pub mod event;
pub mod query;
pub mod state;
pub mod tls;

mod session;

use std::sync::Arc;
use std::sync::atomic::{AtomicU8, Ordering};
use std::time::Duration;

use parking_lot::RwLock;
use tokio::runtime::Handle;
use tokio::sync::mpsc;
use tokio::task::JoinHandle;

pub use audio::{AudioMode, AudioSystem};
pub use command::Command;
pub use config::{ClientConfig, TlsVerification};
pub use event::{ConnectionState, Event};
pub use rumble_audio as audio_core;
pub use rumble_protocol as protocol;
pub use state::{Channel, ServerInfo, ServerState, Snapshot, User};

/// Errors surfaced by the client API.
#[derive(Debug, thiserror::Error)]
pub enum ClientError {
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
    #[error("TLS error: {0}")]
    Tls(String),
    #[error("protocol error: {0}")]
    Protocol(#[from] rumble_protocol::ProtocolError),
    #[error("audio error: {0}")]
    Audio(#[from] rumble_audio::AudioError),
    #[error("operation timed out")]
    Timeout,
    #[error("could not resolve host {0}")]
    Resolve(String),
    #[error("invalid configuration: {0}")]
    InvalidConfig(String),
    #[error("certificate error: {0}")]
    Certificate(String),
    #[error("not connected")]
    NotConnected,
    #[error("client has been shut down")]
    Closed,
}

impl From<tokio::time::error::Elapsed> for ClientError {
    fn from(_: tokio::time::error::Elapsed) -> Self {
        ClientError::Timeout
    }
}

pub type Result<T> = std::result::Result<T, ClientError>;

/// Callback receiving client events. Invoked from runtime worker threads; must not block.
pub type EventHandler = Arc<dyn Fn(Event) + Send + Sync>;

pub(crate) struct Shared {
    pub config: ClientConfig,
    pub handler: EventHandler,
    pub state: RwLock<ServerState>,
    pub connection_state: AtomicU8,
    pub audio: Arc<AudioSystem>,
}

impl Shared {
    pub fn emit(&self, event: Event) {
        (self.handler)(event);
    }

    pub fn set_connection_state(&self, state: ConnectionState) {
        let prev = self.connection_state.swap(state as u8, Ordering::Relaxed);
        if prev != state as u8 {
            self.emit(Event::StateChanged { state });
        }
    }
}

/// A Mumble client. Dropping it disconnects.
pub struct Client {
    shared: Arc<Shared>,
    commands: mpsc::UnboundedSender<Command>,
    task: Option<JoinHandle<()>>,
}

impl Client {
    /// Creates a client and starts connecting on the given runtime.
    pub fn connect(runtime: &Handle, config: ClientConfig, handler: EventHandler) -> Result<Self> {
        config.validate()?;
        let (voice_tx, voice_rx) = mpsc::channel(256);
        let audio = Arc::new(AudioSystem::new(voice_tx));
        let shared = Arc::new(Shared {
            config,
            handler,
            state: RwLock::new(ServerState::default()),
            connection_state: AtomicU8::new(ConnectionState::Disconnected as u8),
            audio,
        });
        let (tx, rx) = mpsc::unbounded_channel();
        let task = runtime.spawn(session::supervise(shared.clone(), rx, voice_rx));
        Ok(Self { shared, commands: tx, task: Some(task) })
    }

    pub fn config(&self) -> &ClientConfig {
        &self.shared.config
    }

    pub fn connection_state(&self) -> ConnectionState {
        ConnectionState::from(self.shared.connection_state.load(Ordering::Relaxed))
    }

    /// Consistent snapshot of the cached server state.
    pub fn snapshot(&self) -> Snapshot {
        self.shared.state.read().snapshot()
    }

    /// Runs a closure with read access to the cached state (no copy).
    pub fn with_state<R>(&self, f: impl FnOnce(&ServerState) -> R) -> R {
        f(&self.shared.state.read())
    }

    /// Audio subsystem (devices, capture, mixer controls).
    pub fn audio(&self) -> &Arc<AudioSystem> {
        &self.shared.audio
    }

    /// Queues a command for the session. Fails if the client is not connected.
    pub fn send(&self, command: Command) -> Result<()> {
        if self.connection_state() != ConnectionState::Connected && !matches!(command, Command::Disconnect) {
            return Err(ClientError::NotConnected);
        }
        self.commands.send(command).map_err(|_| ClientError::Closed)
    }

    /// Requests a graceful disconnect (no reconnect).
    pub fn disconnect(&self) {
        let _ = self.commands.send(Command::Disconnect);
    }

    /// Disconnects and waits for the supervisor to finish.
    pub async fn shutdown(mut self) {
        self.disconnect();
        if let Some(task) = self.task.take() {
            let _ = tokio::time::timeout(Duration::from_secs(5), task).await;
        }
    }
}

impl Drop for Client {
    fn drop(&mut self) {
        let _ = self.commands.send(Command::Disconnect);
        self.shared.audio.set_mode(AudioMode::Disabled).ok();
    }
}
