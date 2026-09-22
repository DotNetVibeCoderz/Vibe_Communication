//! C ABI used by the .NET bindings.
//!
//! All functions return `0` on success or a negative [`error codes`](self) value.
//! Handles are opaque pointers owned by the caller until `voipnet_endpoint_destroy`.

use std::ffi::{c_char, c_int, c_void, CStr, CString};
use std::sync::Arc;

use crate::media::{AudioDirection, DtmfSource, MediaStats};
use crate::sip::endpoint::{Endpoint, EndpointConfig, EndpointError, EndpointHandler, Event};

pub const VN_OK: c_int = 0;
pub const VN_ERR_INVALID_ARGUMENT: c_int = -1;
pub const VN_ERR_INVALID_CONFIG: c_int = -2;
pub const VN_ERR_IO: c_int = -3;
pub const VN_ERR_NOT_FOUND: c_int = -4;
pub const VN_ERR_INVALID_STATE: c_int = -5;
pub const VN_ERR_INTERNAL: c_int = -6;

pub type VnEventCallback = extern "C" fn(user: *mut c_void, json: *const c_char);
pub type VnAudioCallback =
    extern "C" fn(user: *mut c_void, call_id: u64, direction: c_int, sample_rate: u32, pcm: *const i16, samples: c_int);
pub type VnDtmfCallback = extern "C" fn(user: *mut c_void, call_id: u64, digit: u32, source: c_int);
pub type VnEncodedCallback = extern "C" fn(
    user: *mut c_void,
    call_id: u64,
    payload_type: u8,
    timestamp: u32,
    marker: c_int,
    data: *const u8,
    len: c_int,
);

pub type VnVideoCallback =
    extern "C" fn(user: *mut c_void, call_id: u64, timestamp: u32, keyframe: c_int, data: *const u8, len: c_int);

#[repr(C)]
#[derive(Clone, Copy)]
pub struct VnCallbacks {
    pub on_event: Option<VnEventCallback>,
    pub on_audio: Option<VnAudioCallback>,
    pub on_dtmf: Option<VnDtmfCallback>,
    pub on_encoded: Option<VnEncodedCallback>,
    pub on_video: Option<VnVideoCallback>,
    pub user_data: *mut c_void,
}

// The .NET side keeps its delegates alive for the lifetime of the endpoint and its
// user pointer refers to a pinned GCHandle, so the callbacks are safe to share.
unsafe impl Send for VnCallbacks {}
unsafe impl Sync for VnCallbacks {}

struct FfiHandler {
    cb: VnCallbacks,
}

impl EndpointHandler for FfiHandler {
    fn on_event(&self, event: &Event) {
        let Some(f) = self.cb.on_event else { return };
        let json = serde_json::to_string(event).unwrap_or_else(|_| "{\"type\":\"log\"}".into());
        if let Ok(c) = CString::new(json) {
            f(self.cb.user_data, c.as_ptr());
        }
    }

    fn on_audio(&self, call_id: u64, direction: AudioDirection, sample_rate: u32, pcm: &[i16]) {
        if let Some(f) = self.cb.on_audio {
            f(self.cb.user_data, call_id, direction as c_int, sample_rate, pcm.as_ptr(), pcm.len() as c_int);
        }
    }

    fn on_dtmf(&self, call_id: u64, digit: char, source: DtmfSource) {
        if let Some(f) = self.cb.on_dtmf {
            f(self.cb.user_data, call_id, digit as u32, source as c_int);
        }
    }

    fn on_encoded(&self, call_id: u64, payload_type: u8, timestamp: u32, marker: bool, payload: &[u8]) {
        if let Some(f) = self.cb.on_encoded {
            f(self.cb.user_data, call_id, payload_type, timestamp, c_int::from(marker), payload.as_ptr(), payload.len() as c_int);
        }
    }

    fn on_video_frame(&self, call_id: u64, timestamp: u32, keyframe: bool, frame: &[u8]) {
        if let Some(f) = self.cb.on_video {
            f(self.cb.user_data, call_id, timestamp, c_int::from(keyframe), frame.as_ptr(), frame.len() as c_int);
        }
    }
}

fn map_error(e: EndpointError) -> c_int {
    match e {
        EndpointError::Io(_) => VN_ERR_IO,
        EndpointError::InvalidArgument(_) => VN_ERR_INVALID_ARGUMENT,
        EndpointError::NotFound => VN_ERR_NOT_FOUND,
        EndpointError::InvalidState(_) => VN_ERR_INVALID_STATE,
    }
}

/// # Safety
/// `ptr` must be null or a valid NUL-terminated UTF-8 string.
unsafe fn str_from(ptr: *const c_char) -> Option<&'static str> {
    if ptr.is_null() {
        return None;
    }
    CStr::from_ptr(ptr).to_str().ok()
}

unsafe fn write_error(buf: *mut c_char, len: c_int, message: &str) {
    if buf.is_null() || len <= 1 {
        return;
    }
    let bytes = message.as_bytes();
    let n = bytes.len().min(len as usize - 1);
    std::ptr::copy_nonoverlapping(bytes.as_ptr(), buf as *mut u8, n);
    *buf.add(n) = 0;
}

unsafe fn endpoint<'a>(handle: *mut c_void) -> Option<&'a Endpoint> {
    (!handle.is_null()).then(|| &*(handle as *const Endpoint))
}

/// Creates and starts an endpoint from a JSON configuration.
///
/// # Safety
/// `config_json` must be a valid UTF-8 C string; `out_handle` must be a valid pointer.
#[no_mangle]
pub unsafe extern "C" fn voipnet_endpoint_create(
    config_json: *const c_char,
    callbacks: VnCallbacks,
    out_handle: *mut *mut c_void,
    error_buf: *mut c_char,
    error_len: c_int,
) -> c_int {
    if out_handle.is_null() {
        return VN_ERR_INVALID_ARGUMENT;
    }
    *out_handle = std::ptr::null_mut();
    let json = str_from(config_json).unwrap_or("{}");
    let cfg: EndpointConfig = match serde_json::from_str(json) {
        Ok(c) => c,
        Err(e) => {
            write_error(error_buf, error_len, &format!("invalid configuration: {e}"));
            return VN_ERR_INVALID_CONFIG;
        }
    };
    let handler = Arc::new(FfiHandler { cb: callbacks });
    match Endpoint::start(cfg, handler) {
        Ok(ep) => {
            *out_handle = Box::into_raw(Box::new(ep)) as *mut c_void;
            VN_OK
        }
        Err(e) => {
            write_error(error_buf, error_len, &e.to_string());
            map_error(e)
        }
    }
}

/// # Safety
/// `handle` must come from [`voipnet_endpoint_create`] and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn voipnet_endpoint_destroy(handle: *mut c_void) -> c_int {
    if handle.is_null() {
        return VN_ERR_INVALID_ARGUMENT;
    }
    let ep = Box::from_raw(handle as *mut Endpoint);
    ep.shutdown();
    drop(ep);
    VN_OK
}

macro_rules! with_endpoint {
    ($handle:expr, $ep:ident => $body:expr) => {{
        match endpoint($handle) {
            Some($ep) => match $body {
                Ok(_) => VN_OK,
                Err(e) => map_error(e),
            },
            None => VN_ERR_INVALID_ARGUMENT,
        }
    }};
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_register(handle: *mut c_void) -> c_int {
    with_endpoint!(handle, ep => ep.register())
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_unregister(handle: *mut c_void) -> c_int {
    with_endpoint!(handle, ep => ep.unregister())
}

/// # Safety
/// `handle` must be live, `target` a UTF-8 C string and `out_call_id` writable.
#[no_mangle]
pub unsafe extern "C" fn voipnet_make_call(handle: *mut c_void, target: *const c_char, out_call_id: *mut u64) -> c_int {
    let Some(ep) = endpoint(handle) else { return VN_ERR_INVALID_ARGUMENT };
    let Some(target) = str_from(target) else { return VN_ERR_INVALID_ARGUMENT };
    match ep.make_call(target) {
        Ok(id) => {
            if !out_call_id.is_null() {
                *out_call_id = id;
            }
            VN_OK
        }
        Err(e) => map_error(e),
    }
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_answer(handle: *mut c_void, call_id: u64) -> c_int {
    with_endpoint!(handle, ep => ep.answer(call_id))
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_reject(handle: *mut c_void, call_id: u64, status_code: u16) -> c_int {
    with_endpoint!(handle, ep => ep.reject(call_id, status_code))
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_hangup(handle: *mut c_void, call_id: u64) -> c_int {
    with_endpoint!(handle, ep => ep.hangup(call_id))
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_set_hold(handle: *mut c_void, call_id: u64, hold: c_int) -> c_int {
    with_endpoint!(handle, ep => ep.set_hold(call_id, hold != 0))
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_set_mute(handle: *mut c_void, call_id: u64, mute: c_int) -> c_int {
    with_endpoint!(handle, ep => ep.set_mute(call_id, mute != 0))
}

/// Re-reads the TLS certificate, key and CA files; new connections use them.
///
/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_reload_tls(handle: *mut c_void) -> c_int {
    with_endpoint!(handle, ep => ep.reload_tls())
}

/// Restarts ICE on an established call (re-INVITE with new credentials).
///
/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_restart_ice(handle: *mut c_void, call_id: u64) -> c_int {
    with_endpoint!(handle, ep => ep.restart_ice(call_id))
}

/// # Safety
/// `handle` must be live and `target` a UTF-8 C string.
#[no_mangle]
pub unsafe extern "C" fn voipnet_transfer(handle: *mut c_void, call_id: u64, target: *const c_char) -> c_int {
    let Some(target) = str_from(target) else { return VN_ERR_INVALID_ARGUMENT };
    with_endpoint!(handle, ep => ep.transfer(call_id, target))
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_transfer_attended(handle: *mut c_void, call_id: u64, consult_call_id: u64) -> c_int {
    with_endpoint!(handle, ep => ep.transfer_attended(call_id, consult_call_id))
}

/// # Safety
/// `handle` must be live and `digits` a UTF-8 C string.
#[no_mangle]
pub unsafe extern "C" fn voipnet_send_dtmf(handle: *mut c_void, call_id: u64, digits: *const c_char, duration_ms: u32) -> c_int {
    let Some(digits) = str_from(digits) else { return VN_ERR_INVALID_ARGUMENT };
    with_endpoint!(handle, ep => ep.send_dtmf(call_id, digits, duration_ms))
}

/// Queues PCM for transmission; writes the queued backlog in milliseconds to `out_queued_ms`.
///
/// # Safety
/// `pcm` must point to `samples` readable `i16` values.
#[no_mangle]
pub unsafe extern "C" fn voipnet_send_audio(
    handle: *mut c_void,
    call_id: u64,
    pcm: *const i16,
    samples: c_int,
    sample_rate: u32,
    out_queued_ms: *mut u32,
) -> c_int {
    let Some(ep) = endpoint(handle) else { return VN_ERR_INVALID_ARGUMENT };
    if pcm.is_null() || samples < 0 {
        return VN_ERR_INVALID_ARGUMENT;
    }
    let slice = std::slice::from_raw_parts(pcm, samples as usize);
    match ep.send_audio(call_id, slice, sample_rate) {
        Ok(queued) => {
            if !out_queued_ms.is_null() {
                *out_queued_ms = queued;
            }
            VN_OK
        }
        Err(e) => map_error(e),
    }
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_clear_audio(handle: *mut c_void, call_id: u64) -> c_int {
    with_endpoint!(handle, ep => ep.clear_audio(call_id))
}

/// # Safety
/// `data` must point to `len` readable bytes.
#[no_mangle]
pub unsafe extern "C" fn voipnet_send_encoded(
    handle: *mut c_void,
    call_id: u64,
    payload_type: u8,
    timestamp: u32,
    marker: c_int,
    data: *const u8,
    len: c_int,
) -> c_int {
    let Some(ep) = endpoint(handle) else { return VN_ERR_INVALID_ARGUMENT };
    if data.is_null() || len < 0 {
        return VN_ERR_INVALID_ARGUMENT;
    }
    let slice = std::slice::from_raw_parts(data, len as usize);
    match ep.send_encoded(call_id, payload_type, timestamp, marker != 0, slice) {
        Ok(()) => VN_OK,
        Err(e) => map_error(e),
    }
}

/// Sends one encoded video frame (H.264 Annex B access unit or VP8 frame) on the call's video stream.
///
/// # Safety
/// `data` must point to `len` readable bytes.
#[no_mangle]
pub unsafe extern "C" fn voipnet_send_video_frame(
    handle: *mut c_void,
    call_id: u64,
    timestamp: u32,
    data: *const u8,
    len: c_int,
) -> c_int {
    let Some(ep) = endpoint(handle) else { return VN_ERR_INVALID_ARGUMENT };
    if data.is_null() || len < 0 {
        return VN_ERR_INVALID_ARGUMENT;
    }
    let slice = std::slice::from_raw_parts(data, len as usize);
    match ep.send_video_frame(call_id, timestamp, slice) {
        Ok(()) => VN_OK,
        Err(e) => map_error(e),
    }
}

/// Reports the audio device's round trip in milliseconds to the echo canceller.
///
/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_set_stream_delay(handle: *mut c_void, call_id: u64, delay_ms: u32) -> c_int {
    with_endpoint!(handle, ep => ep.set_stream_delay(call_id, delay_ms))
}

/// Asks the peer for a video keyframe: a Full Intra Request when `full` is non-zero, otherwise a
/// Picture Loss Indication.
///
/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_request_keyframe(handle: *mut c_void, call_id: u64, full: c_int) -> c_int {
    with_endpoint!(handle, ep => ep.request_keyframe(call_id, full != 0))
}

/// Writes the call's negotiated video codec into `out` (empty when the call has no video stream).
///
/// # Safety
/// `out` must point to `len` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn voipnet_video_codec(handle: *mut c_void, call_id: u64, out: *mut c_char, len: c_int) -> c_int {
    let Some(ep) = endpoint(handle) else { return VN_ERR_INVALID_ARGUMENT };
    match ep.video_codec(call_id) {
        Ok(codec) => {
            write_error(out, len, codec.as_deref().unwrap_or(""));
            VN_OK
        }
        Err(e) => map_error(e),
    }
}

/// # Safety
/// `out_stats` must point to a writable [`MediaStats`].
#[no_mangle]
pub unsafe extern "C" fn voipnet_call_stats(handle: *mut c_void, call_id: u64, out_stats: *mut MediaStats) -> c_int {
    let Some(ep) = endpoint(handle) else { return VN_ERR_INVALID_ARGUMENT };
    if out_stats.is_null() {
        return VN_ERR_INVALID_ARGUMENT;
    }
    match ep.call_stats(call_id) {
        Ok(s) => {
            *out_stats = s;
            VN_OK
        }
        Err(e) => map_error(e),
    }
}

fn to_c_string(value: String) -> *mut c_char {
    CString::new(value).map(CString::into_raw).unwrap_or(std::ptr::null_mut())
}

/// Returns a JSON description of one call, or null. Free with [`voipnet_string_free`].
///
/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_call_info_json(handle: *mut c_void, call_id: u64) -> *mut c_char {
    let Some(ep) = endpoint(handle) else { return std::ptr::null_mut() };
    match ep.call_info(call_id).ok().and_then(|i| serde_json::to_string(&i).ok()) {
        Some(json) => to_c_string(json),
        None => std::ptr::null_mut(),
    }
}

/// Returns a JSON array of all calls. Free with [`voipnet_string_free`].
///
/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_calls_json(handle: *mut c_void) -> *mut c_char {
    let Some(ep) = endpoint(handle) else { return std::ptr::null_mut() };
    to_c_string(serde_json::to_string(&ep.calls()).unwrap_or_else(|_| "[]".into()))
}

/// # Safety
/// `s` must come from this library.
#[no_mangle]
pub unsafe extern "C" fn voipnet_string_free(s: *mut c_char) {
    if !s.is_null() {
        drop(CString::from_raw(s));
    }
}

/// # Safety
/// `handle` must be live and `target` a UTF-8 C string.
#[no_mangle]
pub unsafe extern "C" fn voipnet_send_options(handle: *mut c_void, target: *const c_char, out_request_id: *mut u64) -> c_int {
    let Some(ep) = endpoint(handle) else { return VN_ERR_INVALID_ARGUMENT };
    let Some(target) = str_from(target) else { return VN_ERR_INVALID_ARGUMENT };
    match ep.send_options(target) {
        Ok(id) => {
            if !out_request_id.is_null() {
                *out_request_id = id;
            }
            VN_OK
        }
        Err(e) => map_error(e),
    }
}

/// # Safety
/// `handle` must be live; `target`, `content_type` and `body` UTF-8 C strings.
#[no_mangle]
pub unsafe extern "C" fn voipnet_send_message(
    handle: *mut c_void,
    target: *const c_char,
    content_type: *const c_char,
    body: *const c_char,
    out_request_id: *mut u64,
) -> c_int {
    let Some(ep) = endpoint(handle) else { return VN_ERR_INVALID_ARGUMENT };
    let (Some(target), Some(body)) = (str_from(target), str_from(body)) else { return VN_ERR_INVALID_ARGUMENT };
    let ct = str_from(content_type).unwrap_or("text/plain");
    match ep.send_message(target, ct, body) {
        Ok(id) => {
            if !out_request_id.is_null() {
                *out_request_id = id;
            }
            VN_OK
        }
        Err(e) => map_error(e),
    }
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_conference_create(handle: *mut c_void, out_conference_id: *mut u64) -> c_int {
    let Some(ep) = endpoint(handle) else { return VN_ERR_INVALID_ARGUMENT };
    if out_conference_id.is_null() {
        return VN_ERR_INVALID_ARGUMENT;
    }
    *out_conference_id = ep.conference_create();
    VN_OK
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_conference_add(handle: *mut c_void, conference_id: u64, call_id: u64) -> c_int {
    with_endpoint!(handle, ep => ep.conference_add(conference_id, call_id))
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_conference_remove(handle: *mut c_void, call_id: u64) -> c_int {
    with_endpoint!(handle, ep => ep.conference_remove(call_id))
}

/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_conference_destroy(handle: *mut c_void, conference_id: u64) -> c_int {
    let Some(ep) = endpoint(handle) else { return VN_ERR_INVALID_ARGUMENT };
    ep.conference_destroy(conference_id);
    VN_OK
}

/// Local SIP address (`ip:port`) the endpoint is bound to. Free with [`voipnet_string_free`].
///
/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_local_address(handle: *mut c_void) -> *mut c_char {
    let Some(ep) = endpoint(handle) else { return std::ptr::null_mut() };
    to_c_string(ep.local_address().to_string())
}

/// SHA-256 fingerprint of the local TLS certificate, or null when the transport is not TLS.
/// Free with `voipnet_string_free`.
///
/// # Safety
/// `handle` must be a live endpoint handle.
#[no_mangle]
pub unsafe extern "C" fn voipnet_tls_fingerprint(handle: *mut c_void) -> *mut c_char {
    let Some(ep) = endpoint(handle) else { return std::ptr::null_mut() };
    ep.tls_fingerprint().map_or(std::ptr::null_mut(), to_c_string)
}

/// Engine version string. Static, must not be freed.
#[no_mangle]
pub extern "C" fn voipnet_version() -> *const c_char {
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr() as *const c_char
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    static EVENTS: AtomicU32 = AtomicU32::new(0);

    extern "C" fn count_events(_user: *mut c_void, json: *const c_char) {
        assert!(!json.is_null());
        EVENTS.fetch_add(1, Ordering::Relaxed);
    }

    #[test]
    fn create_call_and_destroy_through_ffi() {
        let cfg = CString::new(r#"{"bindAddress":"127.0.0.1","sipPort":0,"username":"ffi","rtpPortMin":41000,"rtpPortMax":42000}"#).unwrap();
        let cbs = VnCallbacks {
            on_event: Some(count_events),
            on_audio: None,
            on_dtmf: None,
            on_encoded: None,
            on_video: None,
            user_data: std::ptr::null_mut(),
        };
        let mut handle = std::ptr::null_mut();
        let mut err = [0i8; 256];
        unsafe {
            assert_eq!(voipnet_endpoint_create(cfg.as_ptr(), cbs, &mut handle, err.as_mut_ptr(), 256), VN_OK);
            let addr = voipnet_local_address(handle);
            let addr_str = CStr::from_ptr(addr).to_str().unwrap().to_owned();
            voipnet_string_free(addr);
            assert!(addr_str.starts_with("127.0.0.1:"));

            let target = CString::new(format!("sip:nobody@{addr_str}")).unwrap();
            let mut call = 0u64;
            assert_eq!(voipnet_make_call(handle, target.as_ptr(), &mut call), VN_OK);
            assert!(call > 0);
            let json = voipnet_calls_json(handle);
            let calls = CStr::from_ptr(json).to_str().unwrap().to_owned();
            voipnet_string_free(json);
            assert!(calls.contains("\"callId\""), "{calls}");

            let mut stats = MediaStats::default();
            assert_eq!(voipnet_call_stats(handle, call, &mut stats), VN_OK);
            assert_eq!(voipnet_hangup(handle, call), VN_OK);
            assert_eq!(voipnet_endpoint_destroy(handle), VN_OK);
        }
        // Events arrive on the dispatcher thread, which destroy does not wait for.
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(3);
        while EVENTS.load(Ordering::Relaxed) == 0 && std::time::Instant::now() < deadline {
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
        assert!(EVENTS.load(Ordering::Relaxed) > 0);
    }

    #[test]
    fn invalid_configuration_is_reported() {
        let cfg = CString::new(r#"{"sipPort":"not-a-number"}"#).unwrap();
        let cbs = VnCallbacks { on_event: None, on_audio: None, on_dtmf: None, on_encoded: None, on_video: None, user_data: std::ptr::null_mut() };
        let mut handle = std::ptr::null_mut();
        let mut err = [0i8; 256];
        unsafe {
            assert_eq!(voipnet_endpoint_create(cfg.as_ptr(), cbs, &mut handle, err.as_mut_ptr(), 256), VN_ERR_INVALID_CONFIG);
            assert!(!CStr::from_ptr(err.as_ptr()).to_str().unwrap().is_empty());
            assert!(handle.is_null());
        }
    }
}
