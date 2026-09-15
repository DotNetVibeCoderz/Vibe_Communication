/*
 * Rumble.Net native C API
 * Made by Gravicode Studios, led by Kang Fadhil.
 *
 * Conventions
 *  - Functions return RUMBLE_OK (0) or a negative RUMBLE_ERR_* code unless documented otherwise.
 *    Call rumble_last_error() on the same thread to obtain the message.
 *  - Strings are UTF-8 with an explicit length (not NUL-terminated).
 *  - Buffers returned through (uint8_t** out_ptr, size_t* out_len) must be released with rumble_free().
 *  - Callbacks run on native threads and must return quickly. They are never invoked after
 *    rumble_client_destroy() returns. Do not call rumble_client_destroy() from inside a callback.
 *  - Audio is always 48 kHz; speaker frames are 10 ms (480 samples) mono float.
 */
#ifndef RUMBLE_H
#define RUMBLE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define RUMBLE_OK                     0
#define RUMBLE_ERR_INVALID_ARGUMENT  -1
#define RUMBLE_ERR_NOT_CONNECTED     -2
#define RUMBLE_ERR_NETWORK           -3
#define RUMBLE_ERR_AUDIO             -4
#define RUMBLE_ERR_TLS               -5
#define RUMBLE_ERR_JSON              -6
#define RUMBLE_ERR_PANIC             -7
#define RUMBLE_ERR_UNSUPPORTED       -8
#define RUMBLE_ERR_TIMEOUT           -9
#define RUMBLE_ERR_INTERNAL         -10

typedef enum RumbleConnectionState {
    RUMBLE_STATE_DISCONNECTED  = 0,
    RUMBLE_STATE_CONNECTING    = 1,
    RUMBLE_STATE_SYNCHRONIZING = 2,
    RUMBLE_STATE_CONNECTED     = 3,
    RUMBLE_STATE_RECONNECTING  = 4
} RumbleConnectionState;

typedef enum RumbleAudioMode {
    RUMBLE_AUDIO_DISABLED = 0,
    RUMBLE_AUDIO_HEADLESS = 1,
    RUMBLE_AUDIO_DEVICES  = 2
} RumbleAudioMode;

typedef enum RumbleTransmitMode {
    RUMBLE_TRANSMIT_CONTINUOUS     = 0,
    RUMBLE_TRANSMIT_VOICE_ACTIVITY = 1,
    RUMBLE_TRANSMIT_PUSH_TO_TALK   = 2
} RumbleTransmitMode;

typedef struct RumbleClient RumbleClient;
typedef struct RumbleOpusEncoder RumbleOpusEncoder;
typedef struct RumbleOpusDecoder RumbleOpusDecoder;
typedef struct RumbleMockServer RumbleMockServer;

/* JSON event, e.g. {"type":"UserJoined","user":{...}} */
typedef void (*rumble_event_callback)(void *user_data, const uint8_t *json, size_t json_len);
/* Decoded 10 ms speaker frame. position points to 3 floats (x, y, z) or is NULL. */
typedef void (*rumble_frame_callback)(void *user_data, uint32_t session, const float *samples, size_t count,
                                      const float *position, int32_t concealed);
/* In-place capture processor (mono 48 kHz, 480 samples). */
typedef void (*rumble_capture_callback)(void *user_data, float *samples, size_t count);
/* level: 0 trace, 1 debug, 2 info, 3 warn, 4 error */
typedef void (*rumble_log_callback)(void *user_data, int32_t level, const uint8_t *target, size_t target_len,
                                    const uint8_t *message, size_t message_len);

/* ---- general ---- */
const char *rumble_version(void);
int32_t rumble_last_error(uint8_t *buffer, size_t capacity);
void rumble_free(uint8_t *ptr, size_t len);
int32_t rumble_set_log_callback(rumble_log_callback callback, void *user_data, int32_t min_level);

/* ---- client ---- */
int32_t rumble_client_create(const uint8_t *config_json, size_t config_len, rumble_event_callback callback,
                             void *user_data, RumbleClient **out_client);
void rumble_client_destroy(RumbleClient *client);
int32_t rumble_client_disconnect(const RumbleClient *client);
int32_t rumble_client_state(const RumbleClient *client);
int32_t rumble_client_send_command(const RumbleClient *client, const uint8_t *json, size_t len);
int32_t rumble_client_snapshot(const RumbleClient *client, uint8_t **out_ptr, size_t *out_len);

/* ---- audio ---- */
int32_t rumble_audio_set_mode(const RumbleClient *client, int32_t mode, const uint8_t *input_id, size_t input_len,
                              const uint8_t *output_id, size_t output_len);
int32_t rumble_audio_set_capture_config(const RumbleClient *client, const uint8_t *json, size_t len);
int32_t rumble_audio_set_frame_callback(const RumbleClient *client, rumble_frame_callback callback, void *user_data);
int32_t rumble_audio_set_capture_callback(const RumbleClient *client, rumble_capture_callback callback,
                                          void *user_data);
int32_t rumble_audio_push_pcm_f32(const RumbleClient *client, const float *samples, size_t count);
int32_t rumble_audio_push_pcm_i16(const RumbleClient *client, const int16_t *samples, size_t count);
int32_t rumble_audio_end_transmission(const RumbleClient *client);
int32_t rumble_audio_set_transmit_mode(const RumbleClient *client, int32_t mode);
int32_t rumble_audio_set_push_to_talk(const RumbleClient *client, int32_t pressed);
int32_t rumble_audio_set_voice_target(const RumbleClient *client, int32_t target);
int32_t rumble_audio_set_master_volume(const RumbleClient *client, float volume);
int32_t rumble_audio_set_user_volume(const RumbleClient *client, uint32_t session, float volume);
int32_t rumble_audio_set_user_muted(const RumbleClient *client, uint32_t session, int32_t muted);
int32_t rumble_audio_set_positional(const RumbleClient *client, int32_t enabled, float min_distance,
                                    float max_distance, float min_volume, float rear_attenuation);
/* pose: 9 floats = position xyz, forward xyz, up xyz */
int32_t rumble_audio_set_listener(const RumbleClient *client, const float *pose);
float rumble_audio_input_level(const RumbleClient *client);
int32_t rumble_audio_is_transmitting(const RumbleClient *client);
int32_t rumble_audio_list_devices(uint8_t **out_ptr, size_t *out_len);

/* ---- utilities ---- */
int32_t rumble_generate_certificate(const uint8_t *common_name, size_t len, uint8_t **out_ptr, size_t *out_len);
int32_t rumble_query_server(const uint8_t *host, size_t host_len, uint16_t port, uint32_t timeout_ms,
                            uint8_t **out_ptr, size_t *out_len);
int32_t rumble_run_benchmarks(uint8_t **out_ptr, size_t *out_len);

/* ---- opus ---- */
int32_t rumble_opus_encoder_create(int32_t channels, int32_t application, int32_t bitrate,
                                   RumbleOpusEncoder **out_encoder);
int32_t rumble_opus_encode(RumbleOpusEncoder *encoder, const float *pcm, size_t pcm_len, uint8_t *out,
                           size_t out_capacity);
void rumble_opus_encoder_destroy(RumbleOpusEncoder *encoder);
int32_t rumble_opus_decoder_create(int32_t channels, RumbleOpusDecoder **out_decoder);
int32_t rumble_opus_decode(RumbleOpusDecoder *decoder, const uint8_t *packet, size_t packet_len, float *pcm,
                           size_t pcm_capacity);
void rumble_opus_decoder_destroy(RumbleOpusDecoder *decoder);

/* ---- embedded mock server (feature "mock-server") ---- */
int32_t rumble_mock_server_start(const uint8_t *bind, size_t bind_len, const uint8_t *password, size_t password_len,
                                 uint16_t *out_port, RumbleMockServer **out_server);
int32_t rumble_mock_server_drop_connections(const RumbleMockServer *server);
void rumble_mock_server_stop(RumbleMockServer *server);

#ifdef __cplusplus
}
#endif

#endif /* RUMBLE_H */
