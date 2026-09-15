//! Audio device integration via `cpal`.
//!
//! Backends: WASAPI (Windows), ALSA / PulseAudio (Linux), CoreAudio (macOS / iOS), AAudio (Android).
//!
//! Streams are owned by a dedicated thread (some backends' streams are not `Send`). The capture
//! callback downmixes and resamples to 48 kHz mono and drives the [`CapturePipeline`] directly;
//! the playback callback pulls 10 ms stereo blocks from the [`Mixer`] through a resampler.

use std::sync::mpsc;
use std::thread::JoinHandle;

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{SampleFormat, Stream, StreamConfig};

use crate::capture::CapturePipeline;
use crate::mixer::Mixer;
use crate::resample::{PullResampler, Resampler, downmix_to_mono};
use crate::{AudioError, FRAME_SIZE, Result, SAMPLE_RATE};

/// Information about an audio device.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeviceInfo {
    /// Stable identifier (`host:device-id`), usable with [`AudioEngineConfig`].
    pub id: String,
    pub name: String,
    pub is_input: bool,
    pub is_output: bool,
    pub is_default_input: bool,
    pub is_default_output: bool,
}

fn dev_err(e: impl std::fmt::Display) -> AudioError {
    AudioError::Device(e.to_string())
}

/// Lists all devices of the default host.
pub fn list_devices() -> Result<Vec<DeviceInfo>> {
    let host = cpal::default_host();
    let default_in = host.default_input_device().and_then(|d| d.id().ok()).map(|id| id.to_string());
    let default_out = host.default_output_device().and_then(|d| d.id().ok()).map(|id| id.to_string());
    let mut out = Vec::new();
    for device in host.devices().map_err(dev_err)? {
        let Ok(id) = device.id() else { continue };
        let id = id.to_string();
        let name = device.description().map(|d| d.name().to_string()).unwrap_or_else(|_| id.clone());
        out.push(DeviceInfo {
            is_default_input: default_in.as_deref() == Some(id.as_str()),
            is_default_output: default_out.as_deref() == Some(id.as_str()),
            is_input: device.supports_input(),
            is_output: device.supports_output(),
            id,
            name,
        });
    }
    Ok(out)
}

/// Device selection for [`AudioEngine::start`].
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct AudioEngineConfig {
    /// Input device id; `None` = system default.
    pub input_device: Option<String>,
    /// Output device id; `None` = system default.
    pub output_device: Option<String>,
}

/// Running device streams. Dropping the engine stops audio.
pub struct AudioEngine {
    stop: Option<mpsc::Sender<()>>,
    thread: Option<JoinHandle<()>>,
    pub input_info: Option<String>,
    pub output_info: Option<String>,
}

impl AudioEngine {
    /// Starts capture (if `capture` is given) and playback (if `mixer` is given).
    pub fn start(config: AudioEngineConfig, capture: Option<CapturePipeline>, mixer: Option<Mixer>) -> Result<Self> {
        let (ready_tx, ready_rx) = mpsc::channel::<Result<(Option<String>, Option<String>)>>();
        let (stop_tx, stop_rx) = mpsc::channel::<()>();

        let thread = std::thread::Builder::new()
            .name("rumble-audio-devices".into())
            .spawn(move || {
                let host = cpal::default_host();
                let mut streams: Vec<Stream> = Vec::new();
                let result = (|| {
                    let mut input_info = None;
                    let mut output_info = None;
                    if let Some(capture) = capture {
                        let (s, info) = build_input(&host, config.input_device.as_deref(), capture)?;
                        streams.push(s);
                        input_info = Some(info);
                    }
                    if let Some(mixer) = mixer {
                        let (s, info) = build_output(&host, config.output_device.as_deref(), mixer)?;
                        streams.push(s);
                        output_info = Some(info);
                    }
                    for s in &streams {
                        s.play().map_err(dev_err)?;
                    }
                    Ok((input_info, output_info))
                })();
                let ok = result.is_ok();
                let _ = ready_tx.send(result);
                if ok {
                    // Park until asked to stop (or the engine is dropped).
                    let _ = stop_rx.recv();
                }
                drop(streams);
            })
            .map_err(dev_err)?;

        match ready_rx.recv() {
            Ok(Ok((input_info, output_info))) => {
                Ok(Self { stop: Some(stop_tx), thread: Some(thread), input_info, output_info })
            }
            Ok(Err(e)) => {
                let _ = thread.join();
                Err(e)
            }
            Err(_) => Err(AudioError::Device("audio thread terminated".into())),
        }
    }
}

impl Drop for AudioEngine {
    fn drop(&mut self) {
        self.stop.take();
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

fn find_device(host: &cpal::Host, id: Option<&str>, input: bool) -> Result<cpal::Device> {
    if let Some(id) = id {
        let parsed = id.parse().map_err(dev_err)?;
        if let Some(d) = host.device_by_id(&parsed) {
            return Ok(d);
        }
    }
    let d = if input { host.default_input_device() } else { host.default_output_device() };
    d.ok_or(AudioError::NoDevice(if input { "input" } else { "output" }))
}

fn describe(device: &cpal::Device, config: &StreamConfig, format: SampleFormat) -> String {
    let name = device.description().map(|d| d.name().to_string()).unwrap_or_default();
    format!("{name} ({} Hz, {} ch, {format:?})", config.sample_rate, config.channels)
}

fn build_input(host: &cpal::Host, id: Option<&str>, mut capture: CapturePipeline) -> Result<(Stream, String)> {
    let device = find_device(host, id, true)?;
    let supported = device.default_input_config().map_err(dev_err)?;
    let format = supported.sample_format();
    let config: StreamConfig = supported.config();
    let info = describe(&device, &config, format);
    let channels = config.channels as usize;

    let mut resampler = Resampler::new(config.sample_rate, SAMPLE_RATE, 1);
    let mut mono = Vec::with_capacity(FRAME_SIZE * 4);
    let mut resampled = Vec::with_capacity(FRAME_SIZE * 4);
    let mut float_buf: Vec<f32> = Vec::new();
    let err_fn = |e: cpal::Error| tracing::warn!("input stream error: {e}");

    let mut handle = move |data: &[f32]| {
        mono.clear();
        resampled.clear();
        downmix_to_mono(data, channels, &mut mono);
        resampler.process(&mono, &mut resampled);
        capture.push(&resampled);
    };

    let stream = match format {
        SampleFormat::F32 => device
            .build_input_stream::<f32, _, _>(config, move |data: &[f32], _| handle(data), err_fn, None)
            .map_err(dev_err)?,
        SampleFormat::I16 => device
            .build_input_stream::<i16, _, _>(
                config,
                move |data: &[i16], _| {
                    float_buf.resize(data.len(), 0.0);
                    crate::dsp::i16_to_f32(data, &mut float_buf);
                    handle(&float_buf);
                },
                err_fn,
                None,
            )
            .map_err(dev_err)?,
        SampleFormat::I32 => device
            .build_input_stream::<i32, _, _>(
                config,
                move |data: &[i32], _| {
                    float_buf.clear();
                    float_buf.extend(data.iter().map(|s| *s as f32 / 2_147_483_648.0));
                    handle(&float_buf);
                },
                err_fn,
                None,
            )
            .map_err(dev_err)?,
        _ => return Err(AudioError::Unsupported("input sample format")),
    };
    Ok((stream, info))
}

fn build_output(host: &cpal::Host, id: Option<&str>, mut mixer: Mixer) -> Result<(Stream, String)> {
    let device = find_device(host, id, false)?;
    let supported = device.default_output_config().map_err(dev_err)?;
    let format = supported.sample_format();
    let config: StreamConfig = supported.config();
    let info = describe(&device, &config, format);
    let channels = config.channels as usize;

    let mut resampler = PullResampler::new(SAMPLE_RATE, config.sample_rate, 2, FRAME_SIZE);
    let mut stereo: Vec<f32> = Vec::new();
    let err_fn = |e: cpal::Error| tracing::warn!("output stream error: {e}");

    // Renders `frames` device frames into `out` (interleaved, device channel count).
    let mut render = move |out: &mut [f32]| {
        let frames = out.len() / channels;
        stereo.resize(frames * 2, 0.0);
        resampler.fill(&mut stereo, |block| {
            mixer.mix(block, 2);
        });
        match channels {
            1 => {
                for (o, s) in out.iter_mut().zip(stereo.chunks_exact(2)) {
                    *o = 0.5 * (s[0] + s[1]);
                }
            }
            2 => out.copy_from_slice(&stereo),
            _ => {
                for (o, s) in out.chunks_exact_mut(channels).zip(stereo.chunks_exact(2)) {
                    o.fill(0.0);
                    o[0] = s[0];
                    o[1] = s[1];
                }
            }
        }
    };

    let mut float_buf: Vec<f32> = Vec::new();
    let stream = match format {
        SampleFormat::F32 => device
            .build_output_stream::<f32, _, _>(config, move |data: &mut [f32], _| render(data), err_fn, None)
            .map_err(dev_err)?,
        SampleFormat::I16 => device
            .build_output_stream::<i16, _, _>(
                config,
                move |data: &mut [i16], _| {
                    float_buf.resize(data.len(), 0.0);
                    render(&mut float_buf);
                    crate::dsp::f32_to_i16(&float_buf, data);
                },
                err_fn,
                None,
            )
            .map_err(dev_err)?,
        SampleFormat::I32 => device
            .build_output_stream::<i32, _, _>(
                config,
                move |data: &mut [i32], _| {
                    float_buf.resize(data.len(), 0.0);
                    render(&mut float_buf);
                    for (o, s) in data.iter_mut().zip(&float_buf) {
                        *o = (s.clamp(-1.0, 1.0) * 2_147_483_647.0) as i32;
                    }
                },
                err_fn,
                None,
            )
            .map_err(dev_err)?,
        _ => return Err(AudioError::Unsupported("output sample format")),
    };
    Ok((stream, info))
}
