using Android.App;
using Android.Runtime;
using Java.Interop;
using Rumble.Net;

namespace RumbleApp;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	public override void OnCreate()
	{
		base.OnCreate();

		// The native audio backend reads the device list and the mixer burst size from Android's
		// AudioManager over JNI, and .NET loads the library with dlopen, so JNI_OnLoad never runs.
		// Without this the audio thread dies as soon as it tries to open a device.
		RumbleNative.InitializeAndroid(JniRuntime.CurrentRuntime.InvocationPointer, Handle);
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
