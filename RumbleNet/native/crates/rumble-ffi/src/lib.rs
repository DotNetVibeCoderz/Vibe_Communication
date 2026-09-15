//! # rumble-ffi
//!
//! Stable C ABI over the Rumble.Net Rust core. Design rules:
//!
//! * Every function returns an `i32` status (`RUMBLE_OK` = 0, negative = error) unless noted;
//!   the message of the last error on the calling thread is available via `rumble_last_error`.
//! * Rich data crosses the boundary as UTF-8 JSON (config, commands, events, snapshots), keeping
//!   the ABI small and forward compatible. Hot paths (audio) use raw pointers only.
//! * Buffers allocated by Rust are released with `rumble_free`.
//! * Panics never unwind into the host; they are converted into `RUMBLE_ERR_PANIC`.
//! * Callbacks run on native threads and must return quickly. They are guaranteed not to be
//!   invoked after `rumble_client_destroy` returns.

#![allow(clippy::missing_safety_doc)]

use std::cell::RefCell;
use std::ffi::{c_char, c_void};
use std::fmt::Write as _;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::{Arc, Once, OnceLock};
use std::time::{Duration, Instant};

use parking_lot::RwLock;
use tracing_subscriber::layer::{Context, SubscriberExt};

use rumble_client::audio_core::capture::{CaptureConfig, TransmitMode};
use rumble_client::audio_core::codec::{OpusApplication, OpusDecoder, OpusEncoder};
use rumble_client::audio_core::device::{AudioEngineConfig, list_devices};
use rumble_client::audio_core::positional::{Listener, PositionalSettings};
use rumble_client::{AudioMode, Client, ClientConfig, ClientError, Command, Event, EventHandler};

// ------------------------------------------------------------------------------------------
// Status codes
// ------------------------------------------------------------------------------------------

pub const RUMBLE_OK: i32 = 0;
pub const RUMBLE_ERR_INVALID_ARGUMENT: i32 = -1;
pub const RUMBLE_ERR_NOT_CONNECTED: i32 = -2;
pub const RUMBLE_ERR_NETWORK: i32 = -3;
pub const RUMBLE_ERR_AUDIO: i32 = -4;
pub const RUMBLE_ERR_TLS: i32 = -5;
pub const RUMBLE_ERR_JSON: i32 = -6;
pub const RUMBLE_ERR_PANIC: i32 = -7;
pub const RUMBLE_ERR_UNSUPPORTED: i32 = -8;
pub const RUMBLE_ERR_TIMEOUT: i32 = -9;
pub const RUMBLE_ERR_INTERNAL: i32 = -10;

type FfiResult<T> = Result<T, (i32, String)>;

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

fn set_error(code: i32, message: String) -> i32 {
    LAST_ERROR.with(|e| *e.borrow_mut() = message);
    code
}

fn guard(f: impl FnOnce() -> FfiResult<i32>) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(v)) => v,
        Ok(Err((code, message))) => set_error(code, message),
        Err(panic) => {
            let msg = panic
                .downcast_ref::<&str>()
                .map(|s| s.to_string())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "unknown panic".into());
            set_error(RUMBLE_ERR_PANIC, format!("panic in native code: {msg}"))
        }
    }
}

fn invalid(msg: &str) -> (i32, String) {
    (RUMBLE_ERR_INVALID_ARGUMENT, msg.to_string())
}

fn client_err(e: ClientError) -> (i32, String) {
    let code = match &e {
        ClientError::Io(_) | ClientError::Resolve(_) | ClientError::Protocol(_) => RUMBLE_ERR_NETWORK,
        ClientError::Tls(_) | ClientError::Certificate(_) => RUMBLE_ERR_TLS,
        ClientError::Audio(rumble_client::audio_core::AudioError::Unsupported(_)) => RUMBLE_ERR_UNSUPPORTED,
        ClientError::Audio(_) => RUMBLE_ERR_AUDIO,
        ClientError::Timeout => RUMBLE_ERR_TIMEOUT,
        ClientError::InvalidConfig(_) => RUMBLE_ERR_INVALID_ARGUMENT,
        ClientError::NotConnected | ClientError::Closed => RUMBLE_ERR_NOT_CONNECTED,
    };
    (code, e.to_string())
}

fn json_err(e: serde_json::Error) -> (i32, String) {
    (RUMBLE_ERR_JSON, format!("invalid JSON: {e}"))
}

unsafe fn slice<'a, T>(ptr: *const T, len: usize) -> FfiResult<&'a [T]> {
    if len == 0 {
        Ok(&[])
    } else if ptr.is_null() {
        Err(invalid("null pointer with non-zero length"))
    } else {
        // SAFETY: caller guarantees `ptr` is valid for `len` elements.
        Ok(unsafe { std::slice::from_raw_parts(ptr, len) })
    }
}

unsafe fn slice_mut<'a, T>(ptr: *mut T, len: usize) -> FfiResult<&'a mut [T]> {
    if len == 0 {
        Ok(&mut [])
    } else if ptr.is_null() {
        Err(invalid("null pointer with non-zero length"))
    } else {
        // SAFETY: caller guarantees `ptr` is valid and exclusive for `len` elements.
        Ok(unsafe { std::slice::from_raw_parts_mut(ptr, len) })
    }
}

unsafe fn utf8<'a>(ptr: *const u8, len: usize) -> FfiResult<&'a str> {
    // SAFETY: forwarded to caller contract.
    let bytes = unsafe { slice(ptr, len) }?;
    std::str::from_utf8(bytes).map_err(|_| invalid("string is not valid UTF-8"))
}

unsafe fn write_out(data: Vec<u8>, out_ptr: *mut *mut u8, out_len: *mut usize) -> FfiResult<()> {
    if out_ptr.is_null() || out_len.is_null() {
        return Err(invalid("output pointers must not be null"));
    }
    let boxed = data.into_boxed_slice();
    let len = boxed.len();
    let ptr = Box::into_raw(boxed) as *mut u8;
    // SAFETY: output pointers checked above.
    unsafe {
        *out_ptr = ptr;
        *out_len = len;
    }
    Ok(())
}

/// Opaque user pointer forwarded to callbacks.
#[derive(Clone, Copy)]
struct UserData(*mut c_void);
// SAFETY: the pointer is only handed back to the host, which owns its thread-safety contract.
unsafe impl Send for UserData {}
unsafe impl Sync for UserData {}
impl UserData {
    fn get(self) -> *mut c_void {
        self.0
    }
}

fn runtime() -> &'static tokio::runtime::Runtime {
    static RT: OnceLock<tokio::runtime::Runtime> = OnceLock::new();
    RT.get_or_init(|| {
        let workers = std::thread::available_parallelism().map_or(2, |n| n.get().clamp(2, 8));
        tokio::runtime::Builder::new_multi_thread()
            .worker_threads(workers)
            .thread_name("rumble-io")
            .enable_all()
            .build()
            .expect("failed to build tokio runtime")
    })
}

// ------------------------------------------------------------------------------------------
// General
// ------------------------------------------------------------------------------------------

/// Returns the native library version as a static NUL-terminated string.
#[unsafe(no_mangle)]
pub extern "C" fn rumble_version() -> *const c_char {
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr() as *const c_char
}

/// Copies the last error message of this thread into `buffer` (UTF-8, not NUL-terminated).
/// Returns the full message length (which may exceed `capacity`).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_last_error(buffer: *mut u8, capacity: usize) -> i32 {
    LAST_ERROR.with(|e| {
        let msg = e.borrow();
        let n = msg.len().min(capacity);
        if n > 0 && !buffer.is_null() {
            // SAFETY: caller provides `capacity` writable bytes.
            unsafe { std::ptr::copy_nonoverlapping(msg.as_ptr(), buffer, n) };
        }
        msg.len() as i32
    })
}

/// Frees a buffer returned by this library.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_free(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        // SAFETY: ptr/len were produced by `write_out` from a boxed slice.
        drop(unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len)) });
    }
}

// ------------------------------------------------------------------------------------------
// Logging
// ------------------------------------------------------------------------------------------

/// `level`: 0 trace, 1 debug, 2 info, 3 warn, 4 error.
pub type LogCallback = Option<unsafe extern "C" fn(*mut c_void, i32, *const u8, usize, *const u8, usize)>;

static LOGGER: RwLock<Option<(unsafe extern "C" fn(*mut c_void, i32, *const u8, usize, *const u8, usize), UserData)>> =
    RwLock::new(None);
static LOG_LEVEL: AtomicI32 = AtomicI32::new(2);
static LOG_INIT: Once = Once::new();

fn level_num(level: &tracing::Level) -> i32 {
    match *level {
        tracing::Level::TRACE => 0,
        tracing::Level::DEBUG => 1,
        tracing::Level::INFO => 2,
        tracing::Level::WARN => 3,
        tracing::Level::ERROR => 4,
    }
}

struct FfiLogLayer;

struct MessageVisitor(String);

impl tracing::field::Visit for MessageVisitor {
    fn record_debug(&mut self, field: &tracing::field::Field, value: &dyn std::fmt::Debug) {
        if field.name() == "message" {
            let _ = write!(self.0, "{value:?}");
        } else {
            let _ = write!(self.0, " {}={value:?}", field.name());
        }
    }

    fn record_str(&mut self, field: &tracing::field::Field, value: &str) {
        if field.name() == "message" {
            self.0.push_str(value);
        } else {
            let _ = write!(self.0, " {}={value}", field.name());
        }
    }
}

impl<S: tracing::Subscriber> tracing_subscriber::Layer<S> for FfiLogLayer {
    fn register_callsite(&self, _: &'static tracing::Metadata<'static>) -> tracing::subscriber::Interest {
        tracing::subscriber::Interest::sometimes()
    }

    fn enabled(&self, metadata: &tracing::Metadata<'_>, _: Context<'_, S>) -> bool {
        level_num(metadata.level()) >= LOG_LEVEL.load(Ordering::Relaxed) && LOGGER.read().is_some()
    }

    fn on_event(&self, event: &tracing::Event<'_>, _: Context<'_, S>) {
        let guard = LOGGER.read();
        let Some((cb, ud)) = *guard else { return };
        let mut visitor = MessageVisitor(String::new());
        event.record(&mut visitor);
        let target = event.metadata().target();
        let level = level_num(event.metadata().level());
        // SAFETY: host-provided callback; strings are valid for the duration of the call.
        unsafe { cb(ud.get(), level, target.as_ptr(), target.len(), visitor.0.as_ptr(), visitor.0.len()) };
    }
}

/// Installs a log callback (pass null to remove) and the minimum level.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_set_log_callback(callback: LogCallback, user_data: *mut c_void, min_level: i32) -> i32 {
    guard(|| {
        LOG_INIT.call_once(|| {
            let subscriber = tracing_subscriber::registry().with(FfiLogLayer);
            let _ = tracing::subscriber::set_global_default(subscriber);
        });
        LOG_LEVEL.store(min_level.clamp(0, 5), Ordering::Relaxed);
        *LOGGER.write() = callback.map(|cb| (cb, UserData(user_data)));
        tracing::callsite::rebuild_interest_cache();
        Ok(RUMBLE_OK)
    })
}

// ------------------------------------------------------------------------------------------
// Client
// ------------------------------------------------------------------------------------------

/// Receives UTF-8 JSON events.
pub type EventCallback = Option<unsafe extern "C" fn(*mut c_void, *const u8, usize)>;

/// Opaque client handle.
pub struct RumbleClient {
    client: Client,
    alive: Arc<RwLock<bool>>,
}

unsafe fn client_ref<'a>(client: *const RumbleClient) -> FfiResult<&'a RumbleClient> {
    // SAFETY: caller passes a handle obtained from `rumble_client_create`.
    unsafe { client.as_ref() }.ok_or_else(|| invalid("client handle is null"))
}

/// Creates a client from a JSON `ClientConfig` and starts connecting.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_client_create(
    config_json: *const u8,
    config_len: usize,
    callback: EventCallback,
    user_data: *mut c_void,
    out_client: *mut *mut RumbleClient,
) -> i32 {
    guard(|| {
        if out_client.is_null() {
            return Err(invalid("out_client is null"));
        }
        // SAFETY: caller contract.
        let json = unsafe { utf8(config_json, config_len) }?;
        let config: ClientConfig = serde_json::from_str(json).map_err(json_err)?;
        let alive = Arc::new(RwLock::new(true));
        let alive_cb = alive.clone();
        let ud = UserData(user_data);
        let handler: EventHandler = Arc::new(move |event: Event| {
            let Some(cb) = callback else { return };
            let alive = alive_cb.read();
            if !*alive {
                return;
            }
            let json = event.to_json();
            // SAFETY: host callback; JSON buffer valid during the call.
            unsafe { cb(ud.get(), json.as_ptr(), json.len()) };
        });
        let client = Client::connect(runtime().handle(), config, handler).map_err(client_err)?;
        // SAFETY: checked non-null above.
        unsafe { *out_client = Box::into_raw(Box::new(RumbleClient { client, alive })) };
        Ok(RUMBLE_OK)
    })
}

/// Disconnects and destroys the client. No callbacks are invoked after this returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_client_destroy(client: *mut RumbleClient) {
    if client.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: handle created by `rumble_client_create`, destroyed once.
        let handle = unsafe { Box::from_raw(client) };
        *handle.alive.write() = false;
        let audio = handle.client.audio();
        audio.set_frame_callback(None);
        audio.set_capture_callback(None);
        let _ = audio.set_mode(AudioMode::Disabled);
        drop(handle);
    }));
}

/// Requests a graceful disconnect (no automatic reconnect).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_client_disconnect(client: *const RumbleClient) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        unsafe { client_ref(client) }?.client.disconnect();
        Ok(RUMBLE_OK)
    })
}

/// Returns the connection state (0 disconnected … 4 reconnecting) or a negative error.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_client_state(client: *const RumbleClient) -> i32 {
    // SAFETY: caller contract.
    guard(|| Ok(unsafe { client_ref(client) }?.client.connection_state() as i32))
}

/// Sends a JSON command (see `Command`).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_client_send_command(client: *const RumbleClient, json: *const u8, len: usize) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let c = unsafe { client_ref(client) }?;
        // SAFETY: caller contract.
        let text = unsafe { utf8(json, len) }?;
        let cmd: Command = serde_json::from_str(text).map_err(json_err)?;
        c.client.send(cmd).map_err(client_err)?;
        Ok(RUMBLE_OK)
    })
}

/// Writes a JSON snapshot of the cached server state. Free with `rumble_free`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_client_snapshot(client: *const RumbleClient, out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let c = unsafe { client_ref(client) }?;
        let json = serde_json::to_vec(&c.client.snapshot()).map_err(json_err)?;
        // SAFETY: caller contract.
        unsafe { write_out(json, out_ptr, out_len) }?;
        Ok(RUMBLE_OK)
    })
}

// ------------------------------------------------------------------------------------------
// Audio
// ------------------------------------------------------------------------------------------

/// Decoded speaker frame: `(user, session, samples, count, position_xyz_or_null, concealed)`.
pub type FrameCallback = Option<unsafe extern "C" fn(*mut c_void, u32, *const f32, usize, *const f32, i32)>;
/// Capture processor operating in place: `(user, samples, count)`.
pub type CaptureCallback = Option<unsafe extern "C" fn(*mut c_void, *mut f32, usize)>;

/// Mode: 0 disabled, 1 headless, 2 devices. Device ids are optional (null/0 = default).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_mode(
    client: *const RumbleClient,
    mode: i32,
    input_id: *const u8,
    input_len: usize,
    output_id: *const u8,
    output_len: usize,
) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let c = unsafe { client_ref(client) }?;
        let mode = match mode {
            0 => AudioMode::Disabled,
            1 => AudioMode::Headless,
            2 => {
                // SAFETY: caller contract.
                let input = unsafe { utf8(input_id, input_len) }?;
                // SAFETY: caller contract.
                let output = unsafe { utf8(output_id, output_len) }?;
                AudioMode::Devices(AudioEngineConfig {
                    input_device: (!input.is_empty()).then(|| input.to_string()),
                    output_device: (!output.is_empty()).then(|| output.to_string()),
                })
            }
            _ => return Err(invalid("unknown audio mode")),
        };
        c.client.audio().set_mode(mode).map_err(client_err)?;
        Ok(RUMBLE_OK)
    })
}

#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct CaptureConfigDto {
    bitrate: i32,
    frames_per_packet: usize,
    complexity: i32,
    inband_fec: bool,
    expected_packet_loss: i32,
    vad_threshold_db: f32,
    vad_hold_frames: u32,
    noise_gate_db: Option<f32>,
    dc_filter: bool,
}

impl Default for CaptureConfigDto {
    fn default() -> Self {
        let d = CaptureConfig::default();
        Self {
            bitrate: d.bitrate,
            frames_per_packet: d.frames_per_packet,
            complexity: d.complexity,
            inband_fec: d.inband_fec,
            expected_packet_loss: d.expected_packet_loss,
            vad_threshold_db: d.vad_threshold_db,
            vad_hold_frames: d.vad_hold_frames,
            noise_gate_db: d.noise_gate_db,
            dc_filter: d.dc_filter,
        }
    }
}

/// Sets capture options from JSON; applied the next time the audio mode is set.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_capture_config(client: *const RumbleClient, json: *const u8, len: usize) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let c = unsafe { client_ref(client) }?;
        // SAFETY: caller contract.
        let dto: CaptureConfigDto = serde_json::from_str(unsafe { utf8(json, len) }?).map_err(json_err)?;
        c.client.audio().set_capture_config(CaptureConfig {
            bitrate: dto.bitrate.clamp(8_000, 510_000),
            frames_per_packet: dto.frames_per_packet,
            complexity: dto.complexity,
            inband_fec: dto.inband_fec,
            expected_packet_loss: dto.expected_packet_loss,
            vad_threshold_db: dto.vad_threshold_db,
            vad_hold_frames: dto.vad_hold_frames,
            noise_gate_db: dto.noise_gate_db,
            dc_filter: dto.dc_filter,
            ..CaptureConfig::default()
        });
        Ok(RUMBLE_OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_frame_callback(client: *const RumbleClient, callback: FrameCallback, user_data: *mut c_void) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let c = unsafe { client_ref(client) }?;
        let audio = c.client.audio();
        match callback {
            None => audio.set_frame_callback(None),
            Some(cb) => {
                let ud = UserData(user_data);
                let alive = c.alive.clone();
                audio.set_frame_callback(Some(Arc::new(move |session, samples: &[f32], pos: Option<[f32; 3]>, concealed| {
                    let Some(alive) = alive.try_read() else { return };
                    if !*alive {
                        return;
                    }
                    let p = pos.as_ref().map_or(std::ptr::null(), |p| p.as_ptr());
                    // SAFETY: host callback; buffers valid during the call.
                    unsafe { cb(ud.get(), session, samples.as_ptr(), samples.len(), p, concealed as i32) };
                })));
            }
        }
        Ok(RUMBLE_OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_capture_callback(
    client: *const RumbleClient,
    callback: CaptureCallback,
    user_data: *mut c_void,
) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let c = unsafe { client_ref(client) }?;
        let audio = c.client.audio();
        match callback {
            None => audio.set_capture_callback(None),
            Some(cb) => {
                let ud = UserData(user_data);
                let alive = c.alive.clone();
                audio.set_capture_callback(Some(Arc::new(move |samples: &mut [f32]| {
                    let Some(alive) = alive.try_read() else { return };
                    if !*alive {
                        return;
                    }
                    // SAFETY: host callback; buffer valid and exclusive during the call.
                    unsafe { cb(ud.get(), samples.as_mut_ptr(), samples.len()) };
                })));
            }
        }
        Ok(RUMBLE_OK)
    })
}

/// Pushes mono 48 kHz float PCM (headless mode).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_push_pcm_f32(client: *const RumbleClient, samples: *const f32, count: usize) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let c = unsafe { client_ref(client) }?;
        // SAFETY: caller contract.
        c.client.audio().push_pcm(unsafe { slice(samples, count) }?).map_err(client_err)?;
        Ok(RUMBLE_OK)
    })
}

/// Pushes mono 48 kHz 16-bit PCM (headless mode).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_push_pcm_i16(client: *const RumbleClient, samples: *const i16, count: usize) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let c = unsafe { client_ref(client) }?;
        // SAFETY: caller contract.
        c.client.audio().push_pcm_i16(unsafe { slice(samples, count) }?).map_err(client_err)?;
        Ok(RUMBLE_OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_end_transmission(client: *const RumbleClient) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        unsafe { client_ref(client) }?.client.audio().end_transmission();
        Ok(RUMBLE_OK)
    })
}

/// 0 continuous, 1 voice activity, 2 push-to-talk.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_transmit_mode(client: *const RumbleClient, mode: i32) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let c = unsafe { client_ref(client) }?;
        c.client.audio().set_transmit_mode(TransmitMode::from(mode.clamp(0, 2) as u8));
        Ok(RUMBLE_OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_push_to_talk(client: *const RumbleClient, pressed: i32) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        unsafe { client_ref(client) }?.client.audio().set_push_to_talk(pressed != 0);
        Ok(RUMBLE_OK)
    })
}

/// 0 = normal talking, 1..30 = registered voice targets, 31 = server loopback.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_voice_target(client: *const RumbleClient, target: i32) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        unsafe { client_ref(client) }?.client.audio().set_voice_target(target.clamp(0, 31) as u8);
        Ok(RUMBLE_OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_master_volume(client: *const RumbleClient, volume: f32) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        unsafe { client_ref(client) }?.client.audio().set_master_volume(volume);
        Ok(RUMBLE_OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_user_volume(client: *const RumbleClient, session: u32, volume: f32) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        unsafe { client_ref(client) }?.client.audio().set_user_volume(session, volume);
        Ok(RUMBLE_OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_user_muted(client: *const RumbleClient, session: u32, muted: i32) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        unsafe { client_ref(client) }?.client.audio().set_user_muted(session, muted != 0);
        Ok(RUMBLE_OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_positional(
    client: *const RumbleClient,
    enabled: i32,
    min_distance: f32,
    max_distance: f32,
    min_volume: f32,
    rear_attenuation: f32,
) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let audio = unsafe { client_ref(client) }?.client.audio();
        audio.set_positional_settings(PositionalSettings {
            min_distance: min_distance.max(0.0),
            max_distance: max_distance.max(min_distance + 0.01),
            min_volume: min_volume.clamp(0.0, 1.0),
            rear_attenuation: rear_attenuation.clamp(0.0, 1.0),
        });
        audio.set_positional_enabled(enabled != 0);
        Ok(RUMBLE_OK)
    })
}

/// Sets the listener pose: `pose` points to 9 floats (position, forward, up).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_set_listener(client: *const RumbleClient, pose: *const f32) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let c = unsafe { client_ref(client) }?;
        // SAFETY: caller provides 9 floats.
        let p = unsafe { slice(pose, 9) }?;
        c.client.audio().set_listener(Listener {
            position: [p[0], p[1], p[2]],
            forward: [p[3], p[4], p[5]],
            up: [p[6], p[7], p[8]],
        });
        Ok(RUMBLE_OK)
    })
}

/// Current microphone level in dBFS (−96 … 0). Returns −96 for invalid handles.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_input_level(client: *const RumbleClient) -> f32 {
    // SAFETY: caller contract.
    unsafe { client.as_ref() }.map_or(-96.0, |c| c.client.audio().capture_control().level_db())
}

/// 1 if currently transmitting, 0 otherwise.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_is_transmitting(client: *const RumbleClient) -> i32 {
    // SAFETY: caller contract.
    unsafe { client.as_ref() }.map_or(0, |c| c.client.audio().capture_control().is_transmitting() as i32)
}

/// Lists audio devices as JSON. Free with `rumble_free`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_audio_list_devices(out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    guard(|| {
        let devices = list_devices().map_err(|e| (RUMBLE_ERR_AUDIO, e.to_string()))?;
        let json: Vec<serde_json::Value> = devices
            .into_iter()
            .map(|d| {
                serde_json::json!({
                    "id": d.id, "name": d.name, "isInput": d.is_input, "isOutput": d.is_output,
                    "isDefaultInput": d.is_default_input, "isDefaultOutput": d.is_default_output,
                })
            })
            .collect();
        // SAFETY: caller contract.
        unsafe { write_out(serde_json::to_vec(&json).map_err(json_err)?, out_ptr, out_len) }?;
        Ok(RUMBLE_OK)
    })
}

// ------------------------------------------------------------------------------------------
// Utilities
// ------------------------------------------------------------------------------------------

/// Generates a self-signed client certificate; returns JSON `{certificatePem, privateKeyPem, sha1Fingerprint}`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_generate_certificate(
    common_name: *const u8,
    len: usize,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let cn = unsafe { utf8(common_name, len) }?;
        let cert = rumble_client::tls::generate_certificate(cn).map_err(client_err)?;
        let json = serde_json::json!({
            "certificatePem": cert.certificate_pem,
            "privateKeyPem": cert.private_key_pem,
            "sha1Fingerprint": cert.sha1_fingerprint,
        });
        // SAFETY: caller contract.
        unsafe { write_out(serde_json::to_vec(&json).map_err(json_err)?, out_ptr, out_len) }?;
        Ok(RUMBLE_OK)
    })
}

/// Queries server info without connecting (blocking). Returns JSON.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_query_server(
    host: *const u8,
    host_len: usize,
    port: u16,
    timeout_ms: u32,
    out_ptr: *mut *mut u8,
    out_len: *mut usize,
) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let host = unsafe { utf8(host, host_len) }?.to_string();
        let result = runtime()
            .block_on(rumble_client::query::query_server(&host, port, Duration::from_millis(timeout_ms as u64)))
            .map_err(client_err)?;
        // SAFETY: caller contract.
        unsafe { write_out(serde_json::to_vec(&result).map_err(json_err)?, out_ptr, out_len) }?;
        Ok(RUMBLE_OK)
    })
}

// ------------------------------------------------------------------------------------------
// Opus codec (standalone, for recording, file streaming and tests)
// ------------------------------------------------------------------------------------------

pub struct RumbleOpusEncoder(OpusEncoder);
pub struct RumbleOpusDecoder(OpusDecoder);

/// `application`: 0 voip, 1 audio, 2 low delay.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_opus_encoder_create(
    channels: i32,
    application: i32,
    bitrate: i32,
    out: *mut *mut RumbleOpusEncoder,
) -> i32 {
    guard(|| {
        if out.is_null() {
            return Err(invalid("out is null"));
        }
        let app = match application {
            1 => OpusApplication::Audio,
            2 => OpusApplication::LowDelay,
            _ => OpusApplication::Voip,
        };
        let mut enc = OpusEncoder::new(channels as u16, app).map_err(|e| (RUMBLE_ERR_AUDIO, e.to_string()))?;
        enc.set_bitrate(bitrate).map_err(|e| (RUMBLE_ERR_AUDIO, e.to_string()))?;
        // SAFETY: checked non-null.
        unsafe { *out = Box::into_raw(Box::new(RumbleOpusEncoder(enc))) };
        Ok(RUMBLE_OK)
    })
}

/// Encodes interleaved float PCM (a valid Opus frame size). Returns the packet length.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_opus_encode(
    encoder: *mut RumbleOpusEncoder,
    pcm: *const f32,
    pcm_len: usize,
    out: *mut u8,
    out_capacity: usize,
) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let enc = unsafe { encoder.as_mut() }.ok_or_else(|| invalid("encoder is null"))?;
        // SAFETY: caller contract.
        let (pcm, out) = unsafe { (slice(pcm, pcm_len)?, slice_mut(out, out_capacity)?) };
        enc.0.encode_float(pcm, out).map(|n| n as i32).map_err(|e| (RUMBLE_ERR_AUDIO, e.to_string()))
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_opus_encoder_destroy(encoder: *mut RumbleOpusEncoder) {
    if !encoder.is_null() {
        // SAFETY: created by `rumble_opus_encoder_create`.
        drop(unsafe { Box::from_raw(encoder) });
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_opus_decoder_create(channels: i32, out: *mut *mut RumbleOpusDecoder) -> i32 {
    guard(|| {
        if out.is_null() {
            return Err(invalid("out is null"));
        }
        let dec = OpusDecoder::new(channels as u16).map_err(|e| (RUMBLE_ERR_AUDIO, e.to_string()))?;
        // SAFETY: checked non-null.
        unsafe { *out = Box::into_raw(Box::new(RumbleOpusDecoder(dec))) };
        Ok(RUMBLE_OK)
    })
}

/// Decodes a packet (null/0 = packet loss concealment of `pcm_capacity / channels` samples).
/// Returns decoded samples per channel.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_opus_decode(
    decoder: *mut RumbleOpusDecoder,
    packet: *const u8,
    packet_len: usize,
    pcm: *mut f32,
    pcm_capacity: usize,
) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        let dec = unsafe { decoder.as_mut() }.ok_or_else(|| invalid("decoder is null"))?;
        // SAFETY: caller contract.
        let out = unsafe { slice_mut(pcm, pcm_capacity) }?;
        let result = if packet.is_null() || packet_len == 0 {
            dec.0.conceal(out)
        } else {
            // SAFETY: caller contract.
            dec.0.decode_float(unsafe { slice(packet, packet_len) }?, out)
        };
        result.map(|n| n as i32).map_err(|e| (RUMBLE_ERR_AUDIO, e.to_string()))
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_opus_decoder_destroy(decoder: *mut RumbleOpusDecoder) {
    if !decoder.is_null() {
        // SAFETY: created by `rumble_opus_decoder_create`.
        drop(unsafe { Box::from_raw(decoder) });
    }
}

// ------------------------------------------------------------------------------------------
// Benchmarks
// ------------------------------------------------------------------------------------------

/// Runs the built-in native micro benchmarks and returns JSON results.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_run_benchmarks(out_ptr: *mut *mut u8, out_len: *mut usize) -> i32 {
    guard(|| {
        let results = benchmarks::run();
        // SAFETY: caller contract.
        unsafe { write_out(serde_json::to_vec(&results).map_err(json_err)?, out_ptr, out_len) }?;
        Ok(RUMBLE_OK)
    })
}

mod benchmarks {
    use super::*;
    use bytes::Bytes;
    use rumble_client::audio_core::FRAME_SIZE;
    use rumble_client::audio_core::codec::StreamCodec;
    use rumble_client::audio_core::mixer::{self, IncomingVoice, MixerConfig};
    use rumble_client::protocol::crypt::CryptState;

    fn measure(name: &str, unit: &str, iterations: u32, mut f: impl FnMut()) -> serde_json::Value {
        for _ in 0..iterations / 10 {
            f();
        }
        let start = Instant::now();
        for _ in 0..iterations {
            f();
        }
        let total = start.elapsed();
        let per_op_ns = total.as_nanos() as f64 / iterations as f64;
        serde_json::json!({
            "name": name, "unit": unit, "iterations": iterations,
            "nanosecondsPerOperation": per_op_ns,
            "operationsPerSecond": 1e9 / per_op_ns,
        })
    }

    pub fn run() -> Vec<serde_json::Value> {
        let mut results = Vec::new();

        let mut a = CryptState::new();
        let mut b = CryptState::new();
        a.set_key(&[3; 16], &[1; 16], &[2; 16]);
        b.set_key(&[3; 16], &[2; 16], &[1; 16]);
        let plain = [0x5Au8; 96];
        let mut enc = [0u8; 100];
        let mut dec = [0u8; 96];
        results.push(measure("OCB2-AES128 encrypt + decrypt (96 B voice packet)", "packet", 200_000, || {
            let n = a.encrypt(&plain, &mut enc).unwrap();
            b.decrypt(&enc[..n], &mut dec).unwrap();
        }));

        let mut encoder = OpusEncoder::new(1, OpusApplication::Voip).unwrap();
        encoder.set_bitrate(48_000).unwrap();
        let frame: Vec<f32> = (0..FRAME_SIZE).map(|i| (i as f32 * 0.03).sin() * 0.4).collect();
        let mut packet = [0u8; 1500];
        results.push(measure("Opus encode 10 ms mono @ 48 kbit/s", "frame", 3_000, || {
            encoder.encode_float(&frame, &mut packet).unwrap();
        }));

        let n = encoder.encode_float(&frame, &mut packet).unwrap();
        let mut decoder = OpusDecoder::new(1).unwrap();
        let mut pcm = vec![0f32; 5760];
        results.push(measure("Opus decode 10 ms mono", "frame", 10_000, || {
            decoder.decode_float(&packet[..n], &mut pcm).unwrap();
        }));

        let (mut router, mut mix) = mixer::channel(MixerConfig::default());
        let payload = Bytes::copy_from_slice(&packet[..n]);
        let mut out = vec![0f32; FRAME_SIZE * 2];
        let mut seq = 0u64;
        results.push(measure("Mixer: 8 speakers jitter + decode + spatial mix (10 ms)", "frame", 2_000, || {
            for s in 0..8u32 {
                router.route(
                    s + 1,
                    IncomingVoice {
                        codec: StreamCodec::Opus,
                        sequence: seq,
                        payload: payload.clone(),
                        position: Some([s as f32 - 4.0, 0.0, 2.0]),
                        is_terminator: false,
                        volume_adjustment: 0.0,
                        arrival_us: seq as i64 * 10_000,
                    },
                );
            }
            seq += 1;
            mix.mix(&mut out, 2);
        }));

        results
    }
}

// ------------------------------------------------------------------------------------------
// Mock server
// ------------------------------------------------------------------------------------------

#[cfg(feature = "mock-server")]
pub struct RumbleMockServer(rumble_mock_server::MockServer);

/// Starts the embedded mock Mumble server. `bind` e.g. "127.0.0.1:0". Password optional.
#[cfg(feature = "mock-server")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_mock_server_start(
    bind: *const u8,
    bind_len: usize,
    password: *const u8,
    password_len: usize,
    out_port: *mut u16,
    out_server: *mut *mut RumbleMockServer,
) -> i32 {
    guard(|| {
        if out_server.is_null() || out_port.is_null() {
            return Err(invalid("output pointers must not be null"));
        }
        // SAFETY: caller contract.
        let bind = unsafe { utf8(bind, bind_len) }?;
        // SAFETY: caller contract.
        let password = unsafe { utf8(password, password_len) }?;
        let config = rumble_mock_server::MockServerConfig {
            bind: if bind.is_empty() { "127.0.0.1:0" } else { bind }.parse().map_err(|_| invalid("invalid bind address"))?,
            password: (!password.is_empty()).then(|| password.to_string()),
            ..Default::default()
        };
        let server = runtime()
            .block_on(async { rumble_mock_server::MockServer::start(config).await })
            .map_err(|e| (RUMBLE_ERR_NETWORK, e.to_string()))?;
        // SAFETY: checked non-null.
        unsafe {
            *out_port = server.port();
            *out_server = Box::into_raw(Box::new(RumbleMockServer(server)));
        }
        Ok(RUMBLE_OK)
    })
}

#[cfg(feature = "mock-server")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_mock_server_drop_connections(server: *const RumbleMockServer) -> i32 {
    guard(|| {
        // SAFETY: caller contract.
        unsafe { server.as_ref() }.ok_or_else(|| invalid("server is null"))?.0.drop_all_connections();
        Ok(RUMBLE_OK)
    })
}

#[cfg(feature = "mock-server")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rumble_mock_server_stop(server: *mut RumbleMockServer) {
    if !server.is_null() {
        // Drop inside the runtime context so spawned tasks can observe shutdown.
        let _guard = runtime().enter();
        // SAFETY: created by `rumble_mock_server_start`.
        drop(unsafe { Box::from_raw(server) });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn errors_are_reported() {
        let mut out: *mut RumbleClient = std::ptr::null_mut();
        let bad = b"{not json";
        // SAFETY: valid buffers.
        let rc = unsafe { rumble_client_create(bad.as_ptr(), bad.len(), None, std::ptr::null_mut(), &mut out) };
        assert_eq!(rc, RUMBLE_ERR_JSON);
        let mut buf = [0u8; 256];
        // SAFETY: valid buffer.
        let n = unsafe { rumble_last_error(buf.as_mut_ptr(), buf.len()) };
        assert!(std::str::from_utf8(&buf[..n as usize]).unwrap().contains("invalid JSON"));
    }

    #[test]
    fn opus_roundtrip_via_ffi() {
        let mut enc = std::ptr::null_mut();
        let mut dec = std::ptr::null_mut();
        // SAFETY: valid pointers throughout.
        unsafe {
            assert_eq!(rumble_opus_encoder_create(1, 0, 32_000, &mut enc), RUMBLE_OK);
            assert_eq!(rumble_opus_decoder_create(1, &mut dec), RUMBLE_OK);
            let pcm = [0.1f32; 480];
            let mut packet = [0u8; 512];
            let n = rumble_opus_encode(enc, pcm.as_ptr(), pcm.len(), packet.as_mut_ptr(), packet.len());
            assert!(n > 0);
            let mut out = [0f32; 5760];
            assert_eq!(rumble_opus_decode(dec, packet.as_ptr(), n as usize, out.as_mut_ptr(), out.len()), 480);
            rumble_opus_encoder_destroy(enc);
            rumble_opus_decoder_destroy(dec);
        }
    }

    #[test]
    fn benchmarks_produce_json() {
        let mut ptr = std::ptr::null_mut();
        let mut len = 0;
        // SAFETY: valid out pointers.
        unsafe {
            assert_eq!(rumble_run_benchmarks(&mut ptr, &mut len), RUMBLE_OK);
            let text = std::str::from_utf8(std::slice::from_raw_parts(ptr, len)).unwrap();
            assert!(text.contains("nanosecondsPerOperation"));
            rumble_free(ptr, len);
        }
    }
}
