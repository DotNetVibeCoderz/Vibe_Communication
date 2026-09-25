using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VoipNet.Video.MediaFoundation;

/// <summary>
/// The slice of Media Foundation needed to put a picture through a codec, called through its COM
/// vtables directly.
/// </summary>
/// <remarks>
/// The interfaces used here — IMFTransform, IMFMediaType, IMFSample, IMFMediaBuffer, ICodecAPI — are
/// called by slot rather than through a COM interop layer, which keeps the dependency list of this
/// package empty and the calls explicit. Each slot number below is the method's position in its
/// interface, counting the three IUnknown methods first.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Mf
{
    internal const uint Version = 0x0002_0070; // MF_SDK_VERSION 2, MF_API_VERSION 0x70
    internal const int SOk = 0;
    internal const int NeedMoreInput = unchecked((int)0xC00D_6D72);
    internal const int StreamChange = unchecked((int)0xC00D_6D61);
    internal const int TransformTypeNotSet = unchecked((int)0xC00D_6D3A);

    // IMFTransform::ProcessMessage
    internal const uint MessageCommandFlush = 0x0000_0000;
    internal const uint MessageCommandDrain = 0x0000_0001;
    internal const uint MessageNotifyBeginStreaming = 0x1000_0000;
    internal const uint MessageNotifyEndStreaming = 0x1000_0001;
    internal const uint MessageNotifyStartOfStream = 0x1000_0002;
    internal const uint MessageNotifyEndOfStream = 0x1000_0003;

    /// <summary>The output stream hands back samples of its own, so the caller allocates none.</summary>
    internal const uint OutputStreamProvidesSamples = 0x0000_0100;

    internal static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    internal static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    internal static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    internal static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    internal static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid VideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
    internal static readonly Guid VideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");

    internal static readonly Guid H264EncoderClass = new("6ca50344-051a-4ded-9779-a43305165e35");
    internal static readonly Guid H264DecoderClass = new("62ce7e72-4c71-4d20-b15d-452831a87d9d");
    internal static readonly Guid TransformInterface = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    internal static readonly Guid CodecApiInterface = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    internal static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    internal static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    internal static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    internal static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    internal static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    [LibraryImport("mfplat.dll")]
    internal static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateMediaType(out nint type);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateSample(out nint sample);

    [LibraryImport("mfplat.dll")]
    internal static partial int MFCreateMemoryBuffer(uint maxLength, out nint buffer);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    /// <summary>One entry of <c>MFT_OUTPUT_DATA_BUFFER</c>, which ProcessOutput fills in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OutputDataBuffer
    {
        internal uint StreamId;
        private readonly uint _padding;
        internal nint Sample;
        internal uint Status;
        private readonly uint _padding2;
        internal nint Events;
    }

    /// <summary><c>MFT_OUTPUT_STREAM_INFO</c>: whether the transform allocates, and how much.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OutputStreamInfo
    {
        internal uint Flags;
        internal uint Size;
        internal uint Alignment;
    }

    /// <summary>Throws when a call failed, naming what was being done at the time.</summary>
    internal static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
        }
    }

    private static void** Vtable(nint instance) => *(void***)instance;

    // ---- IUnknown -----------------------------------------------------------------------------------

    internal static int QueryInterface(nint instance, in Guid iid, out nint result)
    {
        fixed (Guid* id = &iid)
        fixed (nint* target = &result)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtable(instance)[0])(instance, id, target);
        }
    }

    internal static void Release(ref nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Vtable(instance)[2])(instance);
            instance = 0;
        }
    }

    // ---- IMFAttributes, which IMFMediaType and IMFSample both extend --------------------------------

    internal static int SetUInt32(nint attributes, in Guid key, uint value)
    {
        fixed (Guid* id = &key)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Vtable(attributes)[21])(attributes, id, value);
        }
    }

    internal static int SetUInt64(nint attributes, in Guid key, ulong value)
    {
        fixed (Guid* id = &key)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Vtable(attributes)[22])(attributes, id, value);
        }
    }

    internal static int SetGuid(nint attributes, in Guid key, in Guid value)
    {
        fixed (Guid* id = &key)
        fixed (Guid* v = &value)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Vtable(attributes)[24])(attributes, id, v);
        }
    }

    internal static int GetUInt64(nint attributes, in Guid key, out ulong value)
    {
        fixed (Guid* id = &key)
        fixed (ulong* v = &value)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Vtable(attributes)[8])(attributes, id, v);
        }
    }

    /// <summary>The GUID stored under a key, or an empty one when there is none.</summary>
    internal static Guid GetItemGuid(nint attributes, in Guid key)
    {
        Guid value = default;
        fixed (Guid* id = &key)
        {
            var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Vtable(attributes)[10])(attributes, id, &value);
            return hr == SOk ? value : default;
        }
    }

    internal static int GetUInt32(nint attributes, in Guid key, out uint value)
    {
        fixed (Guid* id = &key)
        fixed (uint* v = &value)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Vtable(attributes)[7])(attributes, id, v);
        }
    }

    // ---- IMFSample ----------------------------------------------------------------------------------

    internal static int SetSampleTime(nint sample, long time) =>
        ((delegate* unmanaged[Stdcall]<nint, long, int>)Vtable(sample)[36])(sample, time);

    internal static int SetSampleDuration(nint sample, long duration) =>
        ((delegate* unmanaged[Stdcall]<nint, long, int>)Vtable(sample)[38])(sample, duration);

    internal static int GetSampleTime(nint sample, out long time)
    {
        fixed (long* value = &time)
        {
            return ((delegate* unmanaged[Stdcall]<nint, long*, int>)Vtable(sample)[35])(sample, value);
        }
    }

    internal static int AddBuffer(nint sample, nint buffer) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int>)Vtable(sample)[42])(sample, buffer);

    internal static int ConvertToContiguousBuffer(nint sample, out nint buffer)
    {
        fixed (nint* result = &buffer)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtable(sample)[41])(sample, result);
        }
    }

    // ---- IMFMediaBuffer -----------------------------------------------------------------------------

    internal static int Lock(nint buffer, out byte* data, out uint maxLength, out uint currentLength)
    {
        fixed (byte** target = &data)
        fixed (uint* max = &maxLength)
        fixed (uint* current = &currentLength)
        {
            return ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Vtable(buffer)[3])(buffer, target, max, current);
        }
    }

    internal static int Unlock(nint buffer) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Vtable(buffer)[4])(buffer);

    internal static int SetCurrentLength(nint buffer, uint length) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Vtable(buffer)[6])(buffer, length);

    // ---- IMFTransform -------------------------------------------------------------------------------

    internal static int GetOutputStreamInfo(nint transform, uint stream, out OutputStreamInfo info)
    {
        fixed (OutputStreamInfo* value = &info)
        {
            return ((delegate* unmanaged[Stdcall]<nint, uint, OutputStreamInfo*, int>)Vtable(transform)[7])(transform, stream, value);
        }
    }

    internal static int GetAttributes(nint transform, out nint attributes)
    {
        fixed (nint* value = &attributes)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtable(transform)[8])(transform, value);
        }
    }

    internal static int GetOutputAvailableType(nint transform, uint stream, uint index, out nint type)
    {
        fixed (nint* value = &type)
        {
            return ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Vtable(transform)[14])(transform, stream, index, value);
        }
    }

    internal static int SetInputType(nint transform, uint stream, nint type, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Vtable(transform)[15])(transform, stream, type, flags);

    internal static int SetOutputType(nint transform, uint stream, nint type, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Vtable(transform)[16])(transform, stream, type, flags);

    internal static int GetOutputCurrentType(nint transform, uint stream, out nint type)
    {
        fixed (nint* value = &type)
        {
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtable(transform)[18])(transform, stream, value);
        }
    }

    internal static int ProcessMessage(nint transform, uint message, nuint parameter) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Vtable(transform)[23])(transform, message, parameter);

    internal static int ProcessInput(nint transform, uint stream, nint sample, uint flags) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Vtable(transform)[24])(transform, stream, sample, flags);

    internal static int ProcessOutput(nint transform, uint flags, ref OutputDataBuffer buffer, out uint status)
    {
        fixed (OutputDataBuffer* buffers = &buffer)
        fixed (uint* value = &status)
        {
            return ((delegate* unmanaged[Stdcall]<nint, uint, uint, OutputDataBuffer*, uint*, int>)Vtable(transform)[25])(transform, flags, 1, buffers, value);
        }
    }

    // ---- ICodecAPI ----------------------------------------------------------------------------------

    /// <summary>A VARIANT holding a 32-bit unsigned value, which is all the codec settings need.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)]
        internal ushort Type;

        [FieldOffset(8)]
        internal uint Value;
    }

    private const ushort VtUi4 = 19;
    private const ushort VtBool = 11;

    internal static int SetCodecUInt32(nint codec, in Guid property, uint value)
    {
        var variant = new Variant { Type = VtUi4, Value = value };
        fixed (Guid* id = &property)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)Vtable(codec)[9])(codec, id, &variant);
        }
    }

    internal static int SetCodecBool(nint codec, in Guid property, bool value)
    {
        // VARIANT_TRUE is -1, not 1, and lives in the same two bytes a short would.
        var variant = new Variant { Type = VtBool, Value = value ? 0xFFFFu : 0u };
        fixed (Guid* id = &property)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)Vtable(codec)[9])(codec, id, &variant);
        }
    }

    /// <summary>A frame size or rate, which Media Foundation stores as two 32-bit halves of one value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// <summary>Starts Media Foundation once per process, and keeps it up for as long as the process runs.</summary>
    internal static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            Check(MFStartup(Version, 0), "MFStartup");
        }
    }

    private static int _started;
}
