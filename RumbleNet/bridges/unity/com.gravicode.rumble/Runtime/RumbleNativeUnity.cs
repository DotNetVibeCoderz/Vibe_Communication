// Rumble.Net Unity bridge — low-level bindings to rumble_native (see native/include/rumble.h).
// Unity (Mono/IL2CPP) cannot consume the .NET 10 Rumble.Net assembly, so this bridge talks to the
// same C ABI directly using DllImport + MonoPInvokeCallback, which works with both scripting backends.
// Made by Gravicode Studios, led by Kang Fadhil.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Gravicode.Rumble
{
    public static unsafe class RumbleNativeUnity
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string Lib = "__Internal";
#else
        private const string Lib = "rumble_native";
#endif

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void EventCallback(IntPtr userData, byte* json, UIntPtr length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void FrameCallback(IntPtr userData, uint session, float* samples, UIntPtr count, float* position, int concealed);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr rumble_version();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rumble_last_error(byte* buffer, UIntPtr capacity);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void rumble_free(byte* ptr, UIntPtr len);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rumble_client_create(byte* configJson, UIntPtr configLen, EventCallback callback, IntPtr userData, out IntPtr client);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void rumble_client_destroy(IntPtr client);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rumble_client_disconnect(IntPtr client);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rumble_client_state(IntPtr client);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rumble_client_send_command(IntPtr client, byte* json, UIntPtr len);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int rumble_audio_set_mode(IntPtr client, int mode, byte* inputId, UIntPtr inputLen, byte* outputId, UIntPtr outputLen);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rumble_audio_set_frame_callback(IntPtr client, FrameCallback callback, IntPtr userData);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rumble_audio_push_pcm_f32(IntPtr client, float* samples, UIntPtr count);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rumble_audio_set_transmit_mode(IntPtr client, int mode);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rumble_audio_set_push_to_talk(IntPtr client, int pressed);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rumble_audio_set_positional(IntPtr client, int enabled, float minDistance, float maxDistance, float minVolume, float rearAttenuation);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int rumble_audio_set_listener(IntPtr client, float* pose);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern float rumble_audio_input_level(IntPtr client);

        public static string LastError()
        {
            var buffer = new byte[1024];
            fixed (byte* p = buffer)
            {
                var n = rumble_last_error(p, (UIntPtr)buffer.Length);
                return Encoding.UTF8.GetString(buffer, 0, Math.Min(Math.Max(n, 0), buffer.Length));
            }
        }

        public static void Check(int status)
        {
            if (status < 0)
            {
                throw new InvalidOperationException("Rumble native error " + status + ": " + LastError());
            }
        }

        public static int WithUtf8(string value, Func<IntPtr, int, int> call)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            fixed (byte* p = bytes)
            {
                return call((IntPtr)p, bytes.Length);
            }
        }
    }
}
