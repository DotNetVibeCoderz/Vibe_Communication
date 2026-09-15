using System.Runtime.InteropServices;

namespace Rumble.Net.Interop;

/// <summary>
/// Owns a native client. The P/Invoke marshaller add-refs the handle for the duration of every
/// call, so disposing concurrently with an in-flight call defers destruction until it returns.
/// </summary>
internal sealed class RumbleClientSafeHandle : SafeHandle
{
    public RumbleClientSafeHandle()
        : base(0, ownsHandle: true)
    {
    }

    /// <summary>GCHandle passed as callback user data; freed after the native client is destroyed.</summary>
    internal GCHandle CallbackHandle { get; set; }

    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        // Native guarantees no callbacks run after destroy returns, so freeing the GCHandle afterwards is safe.
        NativeMethods.rumble_client_destroy(handle);
        if (CallbackHandle.IsAllocated)
        {
            CallbackHandle.Free();
        }

        return true;
    }
}
