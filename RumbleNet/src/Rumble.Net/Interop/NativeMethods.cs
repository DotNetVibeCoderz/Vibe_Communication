using System.Runtime.InteropServices;

namespace Rumble.Net.Interop;

/// <summary>
/// Raw P/Invoke declarations for <c>rumble_native</c>. All signatures are blittable, so the
/// source generator emits direct calls without marshalling stubs.
/// </summary>
internal static unsafe partial class NativeMethods
{
    internal const string Library = "rumble_native";

    // ---- general ----
    [LibraryImport(Library)]
    internal static partial byte* rumble_version();

    [LibraryImport(Library)]
    internal static partial int rumble_last_error(byte* buffer, nuint capacity);

    [LibraryImport(Library)]
    internal static partial void rumble_free(byte* ptr, nuint len);

    [LibraryImport(Library)]
    internal static partial int rumble_set_log_callback(
        delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, byte*, nuint, void> callback,
        nint userData,
        int minLevel);

    // ---- client ----
    [LibraryImport(Library)]
    internal static partial int rumble_client_create(
        byte* configJson,
        nuint configLen,
        delegate* unmanaged[Cdecl]<nint, byte*, nuint, void> callback,
        nint userData,
        out RumbleClientSafeHandle client);

    [LibraryImport(Library)]
    internal static partial void rumble_client_destroy(nint handle);

    [LibraryImport(Library)]
    internal static partial int rumble_client_disconnect(RumbleClientSafeHandle client);

    [LibraryImport(Library)]
    internal static partial int rumble_client_state(RumbleClientSafeHandle client);

    [LibraryImport(Library)]
    internal static partial int rumble_client_send_command(RumbleClientSafeHandle client, byte* json, nuint len);

    [LibraryImport(Library)]
    internal static partial int rumble_client_snapshot(RumbleClientSafeHandle client, out byte* ptr, out nuint len);

    // ---- audio ----
    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_mode(RumbleClientSafeHandle client, int mode, byte* inputId, nuint inputLen, byte* outputId, nuint outputLen);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_capture_config(RumbleClientSafeHandle client, byte* json, nuint len);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_frame_callback(
        RumbleClientSafeHandle client,
        delegate* unmanaged[Cdecl]<nint, uint, float*, nuint, float*, int, void> callback,
        nint userData);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_capture_callback(
        RumbleClientSafeHandle client,
        delegate* unmanaged[Cdecl]<nint, float*, nuint, void> callback,
        nint userData);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_push_pcm_f32(RumbleClientSafeHandle client, float* samples, nuint count);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_push_pcm_i16(RumbleClientSafeHandle client, short* samples, nuint count);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_end_transmission(RumbleClientSafeHandle client);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_transmit_mode(RumbleClientSafeHandle client, int mode);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_push_to_talk(RumbleClientSafeHandle client, int pressed);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_voice_target(RumbleClientSafeHandle client, int target);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_master_volume(RumbleClientSafeHandle client, float volume);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_user_volume(RumbleClientSafeHandle client, uint session, float volume);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_user_muted(RumbleClientSafeHandle client, uint session, int muted);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_positional(RumbleClientSafeHandle client, int enabled, float minDistance, float maxDistance, float minVolume, float rearAttenuation);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_set_listener(RumbleClientSafeHandle client, float* pose);

    [LibraryImport(Library)]
    internal static partial float rumble_audio_input_level(RumbleClientSafeHandle client);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_is_transmitting(RumbleClientSafeHandle client);

    [LibraryImport(Library)]
    internal static partial int rumble_audio_list_devices(out byte* ptr, out nuint len);

    // ---- utilities ----
    [LibraryImport(Library)]
    internal static partial int rumble_generate_certificate(byte* commonName, nuint len, out byte* ptr, out nuint outLen);

    [LibraryImport(Library)]
    internal static partial int rumble_query_server(byte* host, nuint hostLen, ushort port, uint timeoutMs, out byte* ptr, out nuint len);

    [LibraryImport(Library)]
    internal static partial int rumble_run_benchmarks(out byte* ptr, out nuint len);

    // ---- opus ----
    [LibraryImport(Library)]
    internal static partial int rumble_opus_encoder_create(int channels, int application, int bitrate, out nint encoder);

    [LibraryImport(Library)]
    internal static partial int rumble_opus_encode(nint encoder, float* pcm, nuint pcmLen, byte* output, nuint capacity);

    [LibraryImport(Library)]
    internal static partial void rumble_opus_encoder_destroy(nint encoder);

    [LibraryImport(Library)]
    internal static partial int rumble_opus_decoder_create(int channels, out nint decoder);

    [LibraryImport(Library)]
    internal static partial int rumble_opus_decode(nint decoder, byte* packet, nuint packetLen, float* pcm, nuint capacity);

    [LibraryImport(Library)]
    internal static partial void rumble_opus_decoder_destroy(nint decoder);

    // ---- mock server ----
    [LibraryImport(Library)]
    internal static partial int rumble_mock_server_start(byte* bind, nuint bindLen, byte* password, nuint passwordLen, out ushort port, out nint server);

    [LibraryImport(Library)]
    internal static partial int rumble_mock_server_drop_connections(nint server);

    [LibraryImport(Library)]
    internal static partial void rumble_mock_server_stop(nint server);
}
