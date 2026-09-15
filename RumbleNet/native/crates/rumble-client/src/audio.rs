//! Client-side audio orchestration: owns the mixer/router pair, the capture pipeline and the
//! device engine (or a headless ticker for bots and servers without audio hardware).

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use parking_lot::{Mutex, RwLock};
use tokio::sync::mpsc;

use rumble_audio::capture::{CaptureConfig, CaptureControl, CapturePipeline, EncodedVoice, TransmitMode};
use rumble_audio::device::{AudioEngine, AudioEngineConfig};
use rumble_audio::mixer::{self, IncomingVoice, Mixer, MixerConfig, SpeakerFrame, VoiceRouter};
use rumble_audio::positional::{Listener, PositionalSettings};
use rumble_audio::{AudioError, FRAME_SIZE};

use crate::Result;

/// How audio is produced and consumed.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub enum AudioMode {
    /// Voice is ignored and nothing is transmitted.
    #[default]
    Disabled,
    /// No devices: received voice is decoded and delivered to the frame callback on a 10 ms
    /// clock; outgoing audio is pushed by the application with [`AudioSystem::push_pcm`].
    Headless,
    /// Microphone and speakers through the platform audio backend.
    Devices(AudioEngineConfig),
}

/// Callback for decoded speaker frames: `(session, samples, position, concealed)`.
pub type FrameCallback = Arc<dyn Fn(u32, &[f32], Option<[f32; 3]>, bool) + Send + Sync>;
/// In-place capture processor (e.g. .NET audio filters).
pub type CaptureCallback = Arc<dyn Fn(&mut [f32]) + Send + Sync>;

#[derive(Default)]
struct MixerSettings {
    listener: Listener,
    positional: PositionalSettings,
    positional_enabled: bool,
    master_volume: Option<f32>,
    deafened: bool,
    user_volumes: HashMap<u32, f32>,
    user_muted: HashMap<u32, bool>,
}

enum Backend {
    Disabled,
    Headless { capture: Arc<Mutex<CapturePipeline>>, stop: Arc<AtomicBool>, thread: Option<JoinHandle<()>> },
    Devices { _engine: AudioEngine },
}

impl Drop for Backend {
    fn drop(&mut self) {
        if let Backend::Headless { stop, thread, .. } = self {
            stop.store(true, Ordering::Relaxed);
            if let Some(t) = thread.take() {
                let _ = t.join();
            }
        }
    }
}

/// See module documentation.
pub struct AudioSystem {
    router: Mutex<Option<VoiceRouter>>,
    backend: Mutex<Backend>,
    mode: Mutex<AudioMode>,
    control: Arc<CaptureControl>,
    capture_config: Mutex<CaptureConfig>,
    mixer_config: Mutex<MixerConfig>,
    settings: Mutex<MixerSettings>,
    voice_tx: mpsc::Sender<EncodedVoice>,
    frame_callback: Arc<RwLock<Option<FrameCallback>>>,
    capture_callback: Arc<RwLock<Option<CaptureCallback>>>,
    position: Mutex<Option<[f32; 3]>>,
}

impl AudioSystem {
    pub(crate) fn new(voice_tx: mpsc::Sender<EncodedVoice>) -> Self {
        Self {
            router: Mutex::new(None),
            backend: Mutex::new(Backend::Disabled),
            mode: Mutex::new(AudioMode::Disabled),
            control: Arc::new(CaptureControl::default()),
            capture_config: Mutex::new(CaptureConfig::default()),
            mixer_config: Mutex::new(MixerConfig::default()),
            settings: Mutex::new(MixerSettings::default()),
            voice_tx,
            frame_callback: Arc::new(RwLock::new(None)),
            capture_callback: Arc::new(RwLock::new(None)),
            position: Mutex::new(None),
        }
    }

    pub fn mode(&self) -> AudioMode {
        self.mode.lock().clone()
    }

    /// Lock-free capture controls (transmit mode, push-to-talk, target, level meter).
    pub fn capture_control(&self) -> &Arc<CaptureControl> {
        &self.control
    }

    /// Updates capture settings; takes effect on the next [`AudioSystem::set_mode`].
    pub fn set_capture_config(&self, config: CaptureConfig) {
        *self.capture_config.lock() = config;
    }

    pub fn capture_config(&self) -> CaptureConfig {
        *self.capture_config.lock()
    }

    pub fn set_mixer_config(&self, config: MixerConfig) {
        *self.mixer_config.lock() = config;
    }

    /// Installs (or clears) the decoded-frame callback. Effective immediately.
    pub fn set_frame_callback(&self, callback: Option<FrameCallback>) {
        *self.frame_callback.write() = callback;
    }

    /// Installs (or clears) the capture processor callback. Effective immediately.
    pub fn set_capture_callback(&self, callback: Option<CaptureCallback>) {
        *self.capture_callback.write() = callback;
    }

    /// Switches audio mode, rebuilding the pipeline.
    pub fn set_mode(&self, mode: AudioMode) -> Result<()> {
        // Tear down the previous backend first (releases devices).
        *self.backend.lock() = Backend::Disabled;
        *self.router.lock() = None;

        if mode == AudioMode::Disabled {
            *self.mode.lock() = mode;
            self.control.set_muted(self.control.is_muted());
            return Ok(());
        }

        let (mut router, mut mixer) = mixer::channel(*self.mixer_config.lock());
        self.apply_settings(&mut router);
        self.install_frame_sink(&mut mixer);
        let capture = self.build_capture()?;

        let backend = match &mode {
            AudioMode::Disabled => unreachable!(),
            AudioMode::Headless => {
                let stop = Arc::new(AtomicBool::new(false));
                let thread = spawn_headless_clock(mixer, stop.clone())?;
                Backend::Headless { capture: Arc::new(Mutex::new(capture)), stop, thread: Some(thread) }
            }
            AudioMode::Devices(cfg) => {
                let engine = AudioEngine::start(cfg.clone(), Some(capture), Some(mixer))?;
                tracing::info!(input = ?engine.input_info, output = ?engine.output_info, "audio devices started");
                Backend::Devices { _engine: engine }
            }
        };

        *self.router.lock() = Some(router);
        *self.backend.lock() = backend;
        *self.mode.lock() = mode;
        Ok(())
    }

    fn build_capture(&self) -> Result<CapturePipeline> {
        let tx = self.voice_tx.clone();
        let sink = Box::new(move |packet: EncodedVoice| {
            // Never block the audio thread: drop packets if the network side is congested.
            let _ = tx.try_send(packet);
        });
        let mut capture = CapturePipeline::new(*self.capture_config.lock(), self.control.clone(), sink)?;
        let cb = self.capture_callback.clone();
        capture.add_processor(Box::new(move |frame: &mut [f32]| {
            if let Some(guard) = cb.try_read() {
                if let Some(f) = guard.as_ref() {
                    f(frame);
                }
            }
        }));
        Ok(capture)
    }

    fn install_frame_sink(&self, mixer: &mut Mixer) {
        let cb = self.frame_callback.clone();
        mixer.set_frame_sink(Some(Box::new(move |frame: &SpeakerFrame<'_>| {
            if let Some(guard) = cb.try_read() {
                if let Some(f) = guard.as_ref() {
                    f(frame.session, frame.samples, frame.position, frame.concealed);
                }
            }
        })));
    }

    fn apply_settings(&self, router: &mut VoiceRouter) {
        let s = self.settings.lock();
        router.set_listener(s.listener);
        router.set_positional_settings(s.positional);
        router.set_positional_enabled(s.positional_enabled);
        if let Some(v) = s.master_volume {
            router.set_master_volume(v);
        }
        router.set_deafened(s.deafened);
        for (session, v) in &s.user_volumes {
            router.set_user_volume(*session, *v);
        }
        for (session, m) in &s.user_muted {
            router.set_user_muted(*session, *m);
        }
    }

    fn with_router(&self, f: impl FnOnce(&mut VoiceRouter)) {
        if let Some(r) = self.router.lock().as_mut() {
            f(r);
        }
    }

    pub(crate) fn route(&self, session: u32, build: impl FnOnce(i64) -> IncomingVoice) {
        if let Some(r) = self.router.lock().as_mut() {
            let now = r.now_us();
            r.route(session, build(now));
        }
    }

    pub(crate) fn remove_speaker(&self, session: u32) {
        self.with_router(|r| r.remove_speaker(session));
        let mut s = self.settings.lock();
        s.user_volumes.remove(&session);
        s.user_muted.remove(&session);
    }

    pub(crate) fn clear_speakers(&self) {
        self.with_router(|r| r.clear());
    }

    /// Pushes mono 48 kHz PCM for transmission (headless mode only).
    pub fn push_pcm(&self, samples: &[f32]) -> Result<()> {
        match &*self.backend.lock() {
            Backend::Headless { capture, .. } => {
                capture.lock().push(samples);
                Ok(())
            }
            _ => Err(AudioError::Unsupported("push_pcm requires headless audio mode").into()),
        }
    }

    /// Pushes mono 48 kHz `i16` PCM (headless mode only).
    pub fn push_pcm_i16(&self, samples: &[i16]) -> Result<()> {
        match &*self.backend.lock() {
            Backend::Headless { capture, .. } => {
                capture.lock().push_i16(samples);
                Ok(())
            }
            _ => Err(AudioError::Unsupported("push_pcm requires headless audio mode").into()),
        }
    }

    /// Ends the current outgoing transmission (sends a terminator packet).
    pub fn end_transmission(&self) {
        if let Backend::Headless { capture, .. } = &*self.backend.lock() {
            capture.lock().end_transmission();
        }
    }

    pub fn set_transmit_mode(&self, mode: TransmitMode) {
        self.control.set_mode(mode);
    }

    pub fn set_push_to_talk(&self, pressed: bool) {
        self.control.set_push_to_talk(pressed);
    }

    pub fn set_voice_target(&self, target: u8) {
        self.control.set_target(target);
    }

    pub(crate) fn set_capture_muted(&self, muted: bool) {
        self.control.set_muted(muted);
    }

    pub fn set_deafened(&self, deafened: bool) {
        self.settings.lock().deafened = deafened;
        self.with_router(|r| r.set_deafened(deafened));
    }

    pub fn set_user_volume(&self, session: u32, volume: f32) {
        self.settings.lock().user_volumes.insert(session, volume);
        self.with_router(|r| r.set_user_volume(session, volume));
    }

    pub fn set_user_muted(&self, session: u32, muted: bool) {
        self.settings.lock().user_muted.insert(session, muted);
        self.with_router(|r| r.set_user_muted(session, muted));
    }

    pub fn set_master_volume(&self, volume: f32) {
        self.settings.lock().master_volume = Some(volume);
        self.with_router(|r| r.set_master_volume(volume));
    }

    pub fn set_positional_enabled(&self, enabled: bool) {
        self.settings.lock().positional_enabled = enabled;
        self.with_router(|r| r.set_positional_enabled(enabled));
    }

    pub fn set_positional_settings(&self, settings: PositionalSettings) {
        self.settings.lock().positional = settings;
        self.with_router(|r| r.set_positional_settings(settings));
    }

    /// Updates the listener pose; the position is also used for outgoing positional audio.
    pub fn set_listener(&self, listener: Listener) {
        self.settings.lock().listener = listener;
        *self.position.lock() = Some(listener.position);
        self.with_router(|r| r.set_listener(listener));
    }

    pub(crate) fn outgoing_position(&self) -> Option<[f32; 3]> {
        *self.position.lock()
    }
}

fn spawn_headless_clock(mut mixer: Mixer, stop: Arc<AtomicBool>) -> Result<JoinHandle<()>> {
    std::thread::Builder::new()
        .name("rumble-audio-clock".into())
        .spawn(move || {
            let period = Duration::from_millis(10);
            let mut buf = vec![0f32; FRAME_SIZE * 2];
            let mut next = Instant::now();
            while !stop.load(Ordering::Relaxed) {
                let now = Instant::now();
                if now.duration_since(next) > Duration::from_millis(200) {
                    next = now; // fell far behind (suspend / debugger): resync
                }
                while next <= now {
                    mixer.mix(&mut buf, 2);
                    next += period;
                }
                std::thread::sleep(next.saturating_duration_since(Instant::now()));
            }
        })
        .map_err(|e| AudioError::Device(e.to_string()).into())
}
