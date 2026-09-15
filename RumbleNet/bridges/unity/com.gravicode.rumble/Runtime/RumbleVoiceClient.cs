// Rumble.Net Unity bridge — drop-in MonoBehaviour for Mumble voice chat with positional audio.
// Made by Gravicode Studios, led by Kang Fadhil.

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;
using UnityEngine.Events;

namespace Gravicode.Rumble
{
    [Serializable]
    public class RumbleStringEvent : UnityEvent<string> { }

    /// <summary>
    /// Connects to a Mumble server, plays voice through the OS audio device (Rust core) and keeps
    /// the listener pose in sync with <see cref="listenerTransform"/> for positional audio.
    /// </summary>
    public sealed unsafe class RumbleVoiceClient : MonoBehaviour
    {
        [Header("Server")]
        public string host = "localhost";
        public int port = 64738;
        public string username = "UnityPlayer";
        public string password = "";
        public bool connectOnStart = true;

        [Header("Voice")]
        public bool pushToTalk = true;
        public KeyCode pushToTalkKey = KeyCode.V;
        public bool positionalAudio = true;
        public Transform listenerTransform;
        public float minDistance = 1f;
        public float maxDistance = 25f;

        [Header("Events (raw JSON from the native core)")]
        public RumbleStringEvent onEvent = new RumbleStringEvent();

        private static readonly ConcurrentDictionary<IntPtr, RumbleVoiceClient> Instances = new ConcurrentDictionary<IntPtr, RumbleVoiceClient>();
        private static int _nextId;
        private readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();
        private IntPtr _client;
        private IntPtr _id;
        private bool _lastPtt;

        public bool IsConnected => _client != IntPtr.Zero && RumbleNativeUnity.rumble_client_state(_client) == 3;

        private void Start()
        {
            if (connectOnStart)
            {
                Connect();
            }
        }

        public void Connect()
        {
            if (_client != IntPtr.Zero)
            {
                return;
            }

            _id = (IntPtr)System.Threading.Interlocked.Increment(ref _nextId);
            Instances[_id] = this;
            var json = "{\"host\":\"" + Escape(host) + "\",\"port\":" + port + ",\"username\":\"" + Escape(username) + "\"" +
                       (string.IsNullOrEmpty(password) ? "" : ",\"password\":\"" + Escape(password) + "\"") +
                       ",\"positionalTransmit\":" + (positionalAudio ? "true" : "false") + "}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            fixed (byte* p = bytes)
            {
                RumbleNativeUnity.Check(RumbleNativeUnity.rumble_client_create(p, (UIntPtr)bytes.Length, OnEvent, _id, out _client));
            }

            RumbleNativeUnity.Check(RumbleNativeUnity.rumble_audio_set_transmit_mode(_client, pushToTalk ? 2 : 1));
            RumbleNativeUnity.Check(RumbleNativeUnity.rumble_audio_set_positional(_client, positionalAudio ? 1 : 0, minDistance, maxDistance, 0.1f, 0.25f));
            RumbleNativeUnity.Check(RumbleNativeUnity.rumble_audio_set_mode(_client, 2, null, UIntPtr.Zero, null, UIntPtr.Zero));
        }

        public void Disconnect()
        {
            if (_client == IntPtr.Zero)
            {
                return;
            }

            RumbleNativeUnity.rumble_client_destroy(_client);
            _client = IntPtr.Zero;
            Instances.TryRemove(_id, out _);
        }

        public void JoinChannel(uint channelId) => SendCommand("{\"type\":\"JoinChannel\",\"channelId\":" + channelId + "}");

        public void SendChannelMessage(uint channelId, string message) =>
            SendCommand("{\"type\":\"SendTextMessage\",\"channelIds\":[" + channelId + "],\"message\":\"" + Escape(message) + "\"}");

        public void SendCommand(string json)
        {
            if (_client == IntPtr.Zero)
            {
                return;
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            fixed (byte* p = bytes)
            {
                RumbleNativeUnity.Check(RumbleNativeUnity.rumble_client_send_command(_client, p, (UIntPtr)bytes.Length));
            }
        }

        private void Update()
        {
            while (_pending.TryDequeue(out var json))
            {
                onEvent.Invoke(json);
            }

            if (_client == IntPtr.Zero)
            {
                return;
            }

            var ptt = pushToTalk && Input.GetKey(pushToTalkKey);
            if (ptt != _lastPtt)
            {
                _lastPtt = ptt;
                RumbleNativeUnity.rumble_audio_set_push_to_talk(_client, ptt ? 1 : 0);
            }

            if (positionalAudio)
            {
                var t = listenerTransform != null ? listenerTransform : transform;
                // Unity and Mumble are both left-handed with +Y up and +Z forward.
                var pose = stackalloc float[9]
                {
                    t.position.x, t.position.y, t.position.z,
                    t.forward.x, t.forward.y, t.forward.z,
                    t.up.x, t.up.y, t.up.z,
                };
                RumbleNativeUnity.rumble_audio_set_listener(_client, pose);
            }
        }

        private void OnDestroy() => Disconnect();

        private void OnApplicationQuit() => Disconnect();

        [MonoPInvokeCallback(typeof(RumbleNativeUnity.EventCallback))]
        private static void OnEvent(IntPtr userData, byte* json, UIntPtr length)
        {
            if (Instances.TryGetValue(userData, out var instance))
            {
                instance._pending.Enqueue(System.Text.Encoding.UTF8.GetString(json, (int)length));
            }
        }

        private static string Escape(string s) => (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
