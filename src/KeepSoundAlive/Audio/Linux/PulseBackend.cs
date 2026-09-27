using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KeepSoundAlive.Audio.Linux;

/// <summary>
/// PulseAudio backend. Also covers PipeWire, which ships a PulseAudio-compatible server (pipewire-pulse)
/// on practically every desktop distribution.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class PulseBackend : IAudioBackend
{
    private const int SampleRate = 48000;
    private const int Channels = 2;

    public string Name => "PulseAudio";

    // Since PulseAudio 15 (and always in PipeWire) a stream without an explicit sink follows the default sink.
    public bool FollowsDefaultDeviceAutomatically => true;

    public static bool IsAvailable()
    {
        if (!NativeLibrary.TryLoad(PulseNative.SimpleLib, out _) || !NativeLibrary.TryLoad(PulseNative.Lib, out _))
        {
            return false;
        }

        try
        {
            return PulseQuery.Run(out _, out _);
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<AudioDevice> GetDevices()
    {
        if (!PulseQuery.Run(out var sinks, out _))
        {
            throw new InvalidOperationException("Cannot connect to the PulseAudio/PipeWire server.");
        }

        return sinks;
    }

    public string? GetDefaultDeviceId() => PulseQuery.Run(out _, out var defaultSink) ? defaultSink : null;

    public IAudioOutput Open(string? deviceId, SignalSource source)
    {
        string name;
        if (deviceId is null)
        {
            PulseQuery.Run(out var sinks, out var defaultSink);
            var description = sinks.FirstOrDefault(s => s.Id == defaultSink)?.Name;
            name = description ?? "default";
        }
        else
        {
            name = GetDevices().FirstOrDefault(d => d.Id == deviceId)?.Name ?? deviceId;
        }

        return new PulseOutput(deviceId, name, source);
    }

    public void Dispose()
    {
    }

    private sealed unsafe class PulseOutput : IAudioOutput
    {
        private readonly nint _stream;
        private readonly Thread _thread;
        private readonly SignalGenerator _generator;
        private volatile bool _stop;
        private volatile string? _failure;

        public PulseOutput(string? deviceId, string deviceName, SignalSource source)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
            _generator = new SignalGenerator(source, SampleRate, Channels);

            var spec = new PulseNative.SampleSpec
            {
                Format = PulseNative.SampleFormatFloat32Le,
                Rate = SampleRate,
                Channels = Channels,
            };

            // ~200 ms of target latency keeps the CPU wake-ups rare.
            var bytesPerSecond = (uint)(SampleRate * Channels * sizeof(float));
            var attr = new PulseNative.BufferAttr
            {
                MaxLength = uint.MaxValue,
                TLength = bytesPerSecond / 5,
                PreBuf = uint.MaxValue,
                MinReq = uint.MaxValue,
                FragSize = uint.MaxValue,
            };

            int error;
            _stream = PulseNative.pa_simple_new(null, "KeepSoundAlive", PulseNative.StreamPlayback, deviceId,
                "Keep-alive signal", &spec, null, &attr, &error);
            if (_stream == 0)
            {
                throw new InvalidOperationException(PulseNative.ErrorText(error));
            }

            _thread = new Thread(Run) { IsBackground = true, Name = "KeepSoundAlive PulseAudio" };
            _thread.Start();
        }

        public string? DeviceId { get; }

        public string DeviceName { get; }

        public string? Failure => _failure;

        private void Run()
        {
            var buffer = new float[SampleRate / 20 * Channels]; // 50 ms
            fixed (float* ptr = buffer)
            {
                while (!_stop)
                {
                    _generator.Fill(buffer);
                    int error;
                    if (PulseNative.pa_simple_write(_stream, ptr, (nuint)(buffer.Length * sizeof(float)), &error) < 0)
                    {
                        _failure = PulseNative.ErrorText(error);
                        return;
                    }
                }
            }
        }

        public void Dispose()
        {
            _failure ??= "Closed";
            _stop = true;
            if (_thread.Join(TimeSpan.FromSeconds(3)))
            {
                PulseNative.pa_simple_free(_stream);
            }
        }
    }
}

/// <summary>One-shot query of the sink list and the default sink through the asynchronous libpulse API.</summary>
[SupportedOSPlatform("linux")]
internal static unsafe class PulseQuery
{
    private sealed class State
    {
        public readonly List<AudioDevice> Sinks = [];
        public string? DefaultSink;
    }

    public static bool Run(out IReadOnlyList<AudioDevice> sinks, out string? defaultSink)
    {
        sinks = [];
        defaultSink = null;

        var mainloop = PulseNative.pa_mainloop_new();
        if (mainloop == 0)
        {
            return false;
        }

        var context = PulseNative.pa_context_new(PulseNative.pa_mainloop_get_api(mainloop), "KeepSoundAlive");
        var state = new State();
        var handle = GCHandle.Alloc(state);
        try
        {
            if (context == 0 || PulseNative.pa_context_connect(context, null, PulseNative.ContextNoAutoSpawn, null) < 0)
            {
                return false;
            }

            var deadline = Environment.TickCount64 + 3000;
            if (!Iterate(mainloop, deadline, () =>
                {
                    var s = PulseNative.pa_context_get_state(context);
                    if (s is PulseNative.ContextFailed or PulseNative.ContextTerminated)
                    {
                        throw new OperationCanceledException();
                    }

                    return s == PulseNative.ContextReady;
                }))
            {
                return false;
            }

            var userdata = (void*)GCHandle.ToIntPtr(handle);
            var serverOp = PulseNative.pa_context_get_server_info(context, &OnServerInfo, userdata);
            var sinkOp = PulseNative.pa_context_get_sink_info_list(context, &OnSinkInfo, userdata);
            try
            {
                if (!Iterate(mainloop, deadline, () =>
                        PulseNative.pa_operation_get_state(serverOp) != PulseNative.OperationRunning &&
                        PulseNative.pa_operation_get_state(sinkOp) != PulseNative.OperationRunning))
                {
                    return false;
                }
            }
            finally
            {
                PulseNative.pa_operation_unref(serverOp);
                PulseNative.pa_operation_unref(sinkOp);
            }

            sinks = state.Sinks;
            defaultSink = state.DefaultSink;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (context != 0)
            {
                PulseNative.pa_context_disconnect(context);
                PulseNative.pa_context_unref(context);
            }

            PulseNative.pa_mainloop_free(mainloop);
            handle.Free();
        }
    }

    private static bool Iterate(nint mainloop, long deadline, Func<bool> done)
    {
        while (!done())
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            if (PulseNative.pa_mainloop_iterate(mainloop, 0, null) < 0)
            {
                return false;
            }

            Thread.Sleep(2);
        }

        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSinkInfo(nint context, PulseNative.SinkInfoHead* info, int eol, void* userdata)
    {
        if (eol != 0 || info == null)
        {
            return;
        }

        var state = (State)GCHandle.FromIntPtr((nint)userdata).Target!;
        var name = Marshal.PtrToStringUTF8(info->Name);
        if (name is not null)
        {
            state.Sinks.Add(new AudioDevice(name, Marshal.PtrToStringUTF8(info->Description) ?? name));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnServerInfo(nint context, PulseNative.ServerInfoHead* info, void* userdata)
    {
        if (info == null)
        {
            return;
        }

        var state = (State)GCHandle.FromIntPtr((nint)userdata).Target!;
        state.DefaultSink = Marshal.PtrToStringUTF8(info->DefaultSinkName);
    }
}

[SupportedOSPlatform("linux")]
internal static unsafe partial class PulseNative
{
    public const string Lib = "libpulse.so.0";
    public const string SimpleLib = "libpulse-simple.so.0";

    public const int StreamPlayback = 1;
    public const int SampleFormatFloat32Le = 5;
    public const int ContextNoAutoSpawn = 1;
    public const int ContextReady = 4;
    public const int ContextFailed = 5;
    public const int ContextTerminated = 6;
    public const int OperationRunning = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    /// <summary>The leading fields of pa_sink_info (the only ones we read).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SinkInfoHead
    {
        public nint Name;
        public uint Index;
        public nint Description;
    }

    /// <summary>The leading fields of pa_server_info (the only ones we read).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServerInfoHead
    {
        public nint UserName;
        public nint HostName;
        public nint ServerVersion;
        public nint ServerName;
        public SampleSpec SampleSpec;
        public nint DefaultSinkName;
    }

    public static string ErrorText(int error) => Marshal.PtrToStringUTF8(pa_strerror(error)) ?? $"PulseAudio error {error}";

    [LibraryImport(SimpleLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pa_simple_new(string? server, string name, int dir, string? dev, string streamName,
        SampleSpec* spec, void* channelMap, BufferAttr* attr, int* error);

    [LibraryImport(SimpleLib)]
    public static partial int pa_simple_write(nint s, void* data, nuint bytes, int* error);

    [LibraryImport(SimpleLib)]
    public static partial void pa_simple_free(nint s);

    [LibraryImport(Lib)]
    public static partial nint pa_strerror(int error);

    [LibraryImport(Lib)]
    public static partial nint pa_mainloop_new();

    [LibraryImport(Lib)]
    public static partial nint pa_mainloop_get_api(nint mainloop);

    [LibraryImport(Lib)]
    public static partial int pa_mainloop_iterate(nint mainloop, int block, int* retval);

    [LibraryImport(Lib)]
    public static partial void pa_mainloop_free(nint mainloop);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pa_context_new(nint api, string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int pa_context_connect(nint context, string? server, int flags, void* spawnApi);

    [LibraryImport(Lib)]
    public static partial int pa_context_get_state(nint context);

    [LibraryImport(Lib)]
    public static partial void pa_context_disconnect(nint context);

    [LibraryImport(Lib)]
    public static partial void pa_context_unref(nint context);

    [LibraryImport(Lib)]
    public static partial nint pa_context_get_sink_info_list(nint context,
        delegate* unmanaged[Cdecl]<nint, SinkInfoHead*, int, void*, void> callback, void* userdata);

    [LibraryImport(Lib)]
    public static partial nint pa_context_get_server_info(nint context,
        delegate* unmanaged[Cdecl]<nint, ServerInfoHead*, void*, void> callback, void* userdata);

    [LibraryImport(Lib)]
    public static partial int pa_operation_get_state(nint operation);

    [LibraryImport(Lib)]
    public static partial void pa_operation_unref(nint operation);
}
